local crpLog = require('crp_log')
local runtimeContext = require('runtime_context')
local recordBuilder = require('record_builder')
local campaignStateFactory = require('campaign_state')
local statusWriterFactory = require('status_writer')
local snapshotSamplerFactory = require('snapshot_sampler')

local coordinator = {}

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function positiveNumber(value, fallback)
  local numberValue = tonumber(value)
  if numberValue and numberValue > 0 then return numberValue end
  return fallback
end

local function clampInteger(value, fallback, minimum, maximum)
  local numberValue = math.floor(tonumber(value) or fallback)
  if numberValue < minimum then numberValue = minimum end
  if numberValue > maximum then numberValue = maximum end
  return numberValue
end

local function objectFingerprint(safe, obj)
  if not safe.isValidObject(obj) then return '' end
  local text, err = safe.getFullName(obj)
  if err then return '' end
  return safe.fingerprintValue(text or '')
end

local function localPlayerStateScope(safe)
  local controller, controllerErr = safe.findFirst('CrabPC')
  if controllerErr or not safe.isValidObject(controller) then return '', nil end
  local playerState, playerStateErr = safe.getProperty(controller, 'PlayerState')
  if playerStateErr or not safe.isValidObject(playerState) then return '', nil end
  return objectFingerprint(safe, playerState), playerState
end

local function worldFingerprint(safe)
  local gameState, gameStateErr = safe.findFirst('CrabGS')
  if gameStateErr or not safe.isValidObject(gameState) then return '' end
  return objectFingerprint(safe, gameState)
end

local function observedRoleFromAuthority(authorityStatus)
  if authorityStatus == 'runtime-non-authority' then return 'joined-client' end
  if authorityStatus == 'runtime-authority' then return 'host' end
  return 'unknown'
end

local function safetyConfigurationValid(config)
  return tostring(config.mode or '') == 'observe'
    and config.snapshotSamplerEnabled == true
    and config.allowWriteProbes ~= true
    and config.allowRpcProbes ~= true
    and config.allowHudTickHook ~= true
    and config.allowRawIdentityEvidence ~= true
    and config.allowDeepArrayProbes ~= true
    and config.allowPassiveObservationHooks ~= true
    and config.allowFullObserveInventoryStages ~= true
    and config.allowFullObserveRuntimeDiscovery ~= true
end

local function validOpaqueId(value, minimumLength, maximumLength)
  local text = tostring(value or '')
  return text ~= '' and text ~= 'unassigned' and #text >= minimumLength and #text <= maximumLength
    and text:match('^[%w_%-]+$') ~= nil
end

local function campaignIdentityValid(config)
  local generation = tonumber(config.campaignGeneration)
  local role = tostring(config.selectedRole or ''):lower():gsub('%s+', '-')
  return validOpaqueId(config.campaignId, 1, 128)
    and validOpaqueId(config.campaignSessionId, 8, 96)
    and validOpaqueId(config.machineId, 8, 96)
    and (role == 'host' or role == 'joined-client')
    and generation ~= nil and generation >= 1 and math.floor(generation) == generation
end

local function lifecycleShape(state, lifecycleState, lifecycleGeneration, context, stable)
  return {
    state = tostring(lifecycleState or state.lifecycleState or 'unknown'),
    generation = math.max(0, math.floor(tonumber(lifecycleGeneration) or state.lifecycleGeneration or 0)),
    context = tostring(context or state.context or 'unknown'),
    stable = stable == true
  }
end

local function loadCatalog()
  local ok, value = pcall(function() return require('crabsync_catalog') end)
  if not ok or type(value) ~= 'table' then
    return { schemaVersion = 'unavailable', catalogHash = '', hooks = {} }, tostring(value)
  end
  if type(value.hooks) ~= 'table' then value.hooks = {} end
  return value, nil
end

function coordinator.new(sessionId, config, safe, evidenceWriter)
  local catalog, catalogErr = loadCatalog()
  local state = campaignStateFactory.new(sessionId, config, catalog)
  local statusWriter = statusWriterFactory.new(sessionId, config)
  state:setStatusWriter(statusWriter)
  local snapshots = snapshotSamplerFactory.new(config, safe, evidenceWriter, state)
  local o = {
    sessionId = sessionId,
    config = config,
    safe = safe,
    evidenceWriter = evidenceWriter,
    catalog = catalog,
    catalogError = catalogErr,
    state = state,
    snapshots = snapshots,
    active = false,
    lastHeartbeatAt = nil,
    lastRuntimePollAt = nil,
    heartbeatSeconds = positiveNumber(config.fullObserveHeartbeatSeconds, 1),
    runtimePollSeconds = 1,
    stableSamplesRequired = clampInteger(config.snapshotStableSamplesRequired, 10, 10, 120),
    stableDwellSecondsRequired = clampInteger(config.snapshotStableDwellSeconds, 30, 30, 600),
    stableCandidateKey = '',
    stableCandidateStartedAt = nil,
    stableConsecutiveSamples = 0,
    stableReady = false,
    stabilityResetReason = 'startup',
    lastContext = 'unknown',
    lastPlayerStatePresent = false,
    lifecycleTransitionListener = nil
  }

  function o:resetStability(reason)
    self.stableCandidateKey = ''
    self.stableCandidateStartedAt = nil
    self.stableConsecutiveSamples = 0
    self.stableReady = false
    self.stabilityResetReason = tostring(reason or 'unstable')
    self.state:setStability({
      ready = false,
      consecutiveSamples = 0,
      requiredSamples = self.stableSamplesRequired,
      dwellSeconds = 0,
      requiredDwellSeconds = self.stableDwellSecondsRequired,
      candidateFingerprint = '',
      resetReason = self.stabilityResetReason
    })
  end

  -- The hook-free baseline does not know what a profile-specific listener
  -- does. Readiness supplies one from its separate module so Normal Play
  -- Guide never imports paired-readiness code.
  function o:setLifecycleTransitionListener(listener)
    self.lifecycleTransitionListener = type(listener) == 'function' and listener or nil
  end

  function o:notifyLifecycleTransition(priorLifecycle, nextLifecycle, reason)
    if type(self.lifecycleTransitionListener) ~= 'function' then return end
    local ok, err = pcall(self.lifecycleTransitionListener, priorLifecycle, nextLifecycle, reason)
    if not ok then
      crpLog.line('[CrabRuntimeProbe] lifecycle transition listener failed: ' .. tostring(err))
    end
  end

  function o:observeStableCandidate(candidateKey)
    local now = os.time()
    if candidateKey ~= self.stableCandidateKey then
      self.stableCandidateKey = candidateKey
      self.stableCandidateStartedAt = now
      self.stableConsecutiveSamples = 1
      self.stableReady = false
    else
      self.stableConsecutiveSamples = self.stableConsecutiveSamples + 1
    end
    local dwellSeconds = now - (self.stableCandidateStartedAt or now)
    self.stableReady = self.stableConsecutiveSamples >= self.stableSamplesRequired
      and dwellSeconds >= self.stableDwellSecondsRequired
    local candidateFingerprint = self.safe.fingerprintValue(candidateKey)
    self.state:setStability({
      ready = self.stableReady,
      consecutiveSamples = self.stableConsecutiveSamples,
      requiredSamples = self.stableSamplesRequired,
      dwellSeconds = dwellSeconds,
      requiredDwellSeconds = self.stableDwellSecondsRequired,
      candidateFingerprint = candidateFingerprint,
      resetReason = self.stableReady and '' or self.stabilityResetReason
    })
    self.state.lifecycleState = self.stableReady and 'stable' or 'warming'
  end

  function o:writeCoordinatorEvidence(eventName, result, details)
    local base = recordBuilder.fullObserveBase(self.config, self.state, eventName)
    local row = recordBuilder.merge(base, {
      timestamp = utcNow(),
      sequence = self.state:nextSequence(),
      category = 'snapshot-runtime',
      symbol = 'Runtime.SnapshotObserver',
      result = result,
      runtimeStatus = result == 'ok' and 'SNAPSHOT_CAMPAIGN' or 'UNSUPPORTED',
      catalogSchemaVersion = tostring(self.catalog.schemaVersion or ''),
      catalogHash = tostring(self.catalog.catalogHash or ''),
      details = details or {},
      safetyClassification = 'snapshot-read-only-reviewed-paths',
      observationSemantics = 'state-delta-candidate-not-exact-call-proof',
      hooksDisabled = true,
      runtimeDiscoveryDisabled = true,
      inventoryStagesDisabled = true,
      noWrites = true,
      noRpcs = true,
      noMutation = true,
      noHud = true,
      rawIdentityEvidence = false,
      passiveOnly = true
    })
    if self.config.progressiveHooksArmed == true then row.hooksDisabled = false end
    local writeOk = self.evidenceWriter:writeEvidence(row)
    self.state:noteWriteResult(writeOk)
  end

  function o:start()
    if not safetyConfigurationValid(self.config) or not campaignIdentityValid(self.config) then
      self.state:tripCircuit('snapshot-runtime',
        'unsafe, legacy-observer-enabled, or unassigned campaign configuration rejected', 'rejected-unsafe')
      self:writeCoordinatorEvidence('SnapshotRuntime.StartRejected', 'unsupported', {
        reason = 'snapshot sampler requires safe gates, legacy observers disabled, and assigned campaign identity'
      })
      self.state:flushStatus('start-rejected')
      return false
    end

    self.active = true
    self.state.lifecycleState = 'warming'
    -- The readiness coordinator reuses this bounded baseline but owns its
    -- profile.  Do not briefly relabel a paired readiness run as Normal Play
    -- Guide while emitting its startup/status evidence.
    if self.config.progressiveObservationEnabled ~= true
      and self.config.readinessCampaignEnabled ~= true then
      self.state.activeProfile = 'normal-play-guide'
      self.state.workflow = 'normal-play-guide'
    end
    self.state.collectionReadiness = 'warming'
    self.state.probeStage = 'snapshot:waiting-for-stable-game'
    self:resetStability('startup stability barrier required')
    self.snapshots:setActive(true)
    self.snapshots:resetLifecycle(self.state.lifecycleGeneration)

    if self.catalogError then
      self.state.dirtyEvidence = true
      self.state.evidenceHealth = 'catalog-unavailable'
      self:writeCoordinatorEvidence('SnapshotRuntime.CatalogUnavailable', 'unsupported', { error = self.catalogError })
    else
      self:writeCoordinatorEvidence('SnapshotRuntime.Started', 'ok', {
        snapshotSchema = 'snapshot-observation-v1',
        stableSamplesRequired = self.stableSamplesRequired,
        stableDwellSecondsRequired = self.stableDwellSecondsRequired,
        sampleIntervalSeconds = self.snapshots.sampleIntervalSeconds,
        unchangedHeartbeatSeconds = self.snapshots.unchangedHeartbeatSeconds,
        catalogCandidateCount = #(self.catalog.hooks or {}),
        hooksEnabled = false,
        runtimeDiscoveryEnabled = false,
        inventoryStagesEnabled = false,
        checklistQualificationOwner = 'desktop-gui'
      })
    end
    self.state:flushStatus('started')
    crpLog.line('[CrabRuntimeProbe] snapshot-first observer started; waiting for stable game state')
    return true
  end

  function o:refreshRuntime(runnerState)
    local facts = runtimeContext.snapshot(self.safe, runnerState or {})
    local playerStateFingerprint, playerState = localPlayerStateScope(self.safe)
    local currentWorldFingerprint = worldFingerprint(self.safe)
    local playerStatePresent = playerStateFingerprint ~= ''
    local nextContext = tostring(facts.context or 'unknown')
    local candidateValid = playerStatePresent
      and currentWorldFingerprint ~= ''
      and facts.playerStateValid == true
      and nextContext ~= 'traveling'
      and nextContext ~= 'unstable'
      and nextContext ~= 'dead-or-respawning'
      and nextContext ~= 'menu'
      and nextContext ~= 'lobby'
      and nextContext ~= 'unknown'
    local candidateKey = candidateValid
      and (currentWorldFingerprint .. '|' .. playerStateFingerprint .. '|' .. nextContext)
      or ''
    local contextChangedAfterStable = self.stableReady
      and (not candidateValid or candidateKey ~= self.stableCandidateKey)
    local forceTransition = (self.lastPlayerStatePresent and not playerStatePresent)
      or contextChangedAfterStable
    local wasStable = self.stableReady and self.state.lifecycleState == 'stable'
    local nextLifecycleState = 'warming'
    if not candidateValid and (nextContext == 'traveling'
      or nextContext == 'dead-or-respawning' or nextContext == 'unstable') then
      nextLifecycleState = nextContext
    end
    local priorLifecycle = lifecycleShape(self.state, self.state.lifecycleState,
      self.state.lifecycleGeneration, self.state.context, wasStable)
    local nextLifecycle = lifecycleShape(self.state, nextLifecycleState,
      (tonumber(self.state.lifecycleGeneration) or 0) + (forceTransition and 1 or 0), nextContext, false)
    if wasStable and forceTransition then
      self:notifyLifecycleTransition(priorLifecycle, nextLifecycle,
        'stable-scope-transition:' .. tostring(nextContext))
    end
    local authorityStatus = self.safe.authorityStatus(playerState)
    local observedRole = observedRoleFromAuthority(authorityStatus)
    local selectedRole = tostring(self.state.selectedRole):lower():gsub('%s+', '-')
    if (selectedRole == 'host' and observedRole == 'joined-client')
      or (selectedRole == 'joined-client' and observedRole == 'host') then
      self.state.dirtyEvidence = true
      self.state.evidenceHealth = 'role-mismatch'
    end

    local changed = self.state:updateRuntime({
      worldFingerprint = currentWorldFingerprint,
      localPlayerStateFingerprint = playerStateFingerprint,
      context = nextContext,
      lifecycleState = nextLifecycleState,
      observedRole = observedRole,
      authorityStatus = authorityStatus,
      forceLifecycleTransition = forceTransition
    })
    self.lastPlayerStatePresent = playerStatePresent
    self.lastContext = nextContext

    if changed then
      self:resetStability('polled fingerprint or context transition')
      self.snapshots:resetLifecycle(self.state.lifecycleGeneration)
    end
    if candidateValid then
      self:observeStableCandidate(candidateKey)
    else
      self:resetStability('valid current CrabGS and CrabPC.PlayerState stable sample unavailable')
      if nextContext == 'traveling' or nextContext == 'dead-or-respawning' or nextContext == 'unstable' then
        self.state.lifecycleState = nextContext
      else
        self.state.lifecycleState = 'warming'
      end
      self.state.probeStage = 'snapshot:waiting-for-stable-game'
    end

    if changed then
      self:writeCoordinatorEvidence('SnapshotRuntime.LifecycleTransition', 'ok', {
        lifecycleGeneration = self.state.lifecycleGeneration,
        lifecycleState = self.state.lifecycleState,
        context = nextContext,
        transitionSource = 'stable-polling',
        stableSamples = self.stableConsecutiveSamples,
        stableSamplesRequired = self.stableSamplesRequired,
        stableDwellSecondsRequired = self.stableDwellSecondsRequired
      })
      self.state:flushStatus('lifecycle-transition')
    end
  end

  function o:onTick(runnerState)
    if not self.active then return end
    local now = os.time()
    if self.lastRuntimePollAt == nil or (now - self.lastRuntimePollAt) >= self.runtimePollSeconds then
      self.lastRuntimePollAt = now
      self:refreshRuntime(runnerState)
    end

    if self.state:checkStopMarker() then
      self.active = false
      self.snapshots:setActive(false)
      self:writeCoordinatorEvidence('SnapshotRuntime.StopRequested', 'ok', {
        marker = 'results/dashboard_stop_requested.json'
      })
      self.state:flushStatus('stopped')
      return
    end

    if self.stableReady and self.state.lifecycleState == 'stable' then
      local outcome = self.snapshots:onTick()
      if type(self.snapshots.isBaselineReady) == 'function' and self.snapshots:isBaselineReady() then
        self.state.collectionReadiness = self.config.progressiveHooksArmed == true and 'collecting' or 'ready'
      end
      if outcome and outcome.scopeLost == true then
        self:resetStability('snapshot scope changed during category read')
        self.state.lifecycleState = 'warming'
        self.state.probeStage = 'snapshot:waiting-for-stable-game'
      end
    end

    if self.lastHeartbeatAt == nil or (now - self.lastHeartbeatAt) >= self.heartbeatSeconds then
      self.lastHeartbeatAt = now
      self.state.lastHeartbeatAt = utcNow()
      self.state:flushStatus('heartbeat')
    end
  end

  return o
end

return coordinator
