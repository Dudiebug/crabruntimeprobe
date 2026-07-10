local crpLog = require('crp_log')
local runtimeContext = require('runtime_context')
local recordBuilder = require('record_builder')
local campaignStateFactory = require('campaign_state')
local statusWriterFactory = require('status_writer')
local passiveHookManagerFactory = require('passive_hook_manager')
local inventoryStageManagerFactory = require('inventory_stage_manager')

local coordinator = {}

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function positiveNumber(value, fallback)
  if type(value) == 'number' and value > 0 then return value end
  return fallback
end

local function clampInteger(value, fallback, minimum, maximum)
  local numberValue = math.floor(tonumber(value) or fallback)
  if numberValue < minimum then numberValue = minimum end
  if numberValue > maximum then numberValue = maximum end
  return numberValue
end

local function safeFingerprint(safe, obj)
  if not safe.isValidObject(obj) then return '' end
  local fullName = safe.getFullName(obj)
  local fingerprint = safe.fingerprintValue(tostring(fullName or '') .. '|' .. tostring(obj))
  return fingerprint
end

local function localPlayerStateFingerprint(safe)
  local controller = safe.findFirst('CrabPC')
  if not safe.isValidObject(controller) then return '', nil end
  local playerState = safe.getProperty(controller, 'PlayerState')
  if not safe.isValidObject(playerState) then return '', nil end
  return safeFingerprint(safe, playerState), playerState
end

local function worldFingerprint(safe)
  local gameState = safe.findFirst('GameStateBase')
  if not safe.isValidObject(gameState) then gameState = safe.findFirst('CrabGS') end
  return safeFingerprint(safe, gameState)
end

local function visibleCrabPlayerStateCount(safe)
  local values, err = safe.findAll('CrabPS')
  if err or type(values) ~= 'table' then return 0 end
  local count = 0
  safe.forEachArrayLimited(values, 16, function(_, wrapped)
    local candidate = wrapped
    if not safe.isValidObject(candidate) then candidate = safe.unwrapKnownValue(wrapped) end
    if safe.isValidObject(candidate) then count = count + 1 end
  end)
  return count
end

local function observedRoleFromFacts(authorityStatus, visiblePlayerStates)
  if authorityStatus == 'runtime-non-authority' then return 'joined-client' end
  if authorityStatus == 'runtime-authority' and visiblePlayerStates >= 2 then return 'host' end
  return 'unknown'
end

local function safetyConfigurationValid(config)
  return tostring(config.mode or '') == 'observe'
    and config.allowWriteProbes ~= true
    and config.allowRpcProbes ~= true
    and config.allowHudTickHook ~= true
    and config.allowRawIdentityEvidence ~= true
    and config.allowDeepArrayProbes ~= true
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

local function loadCatalog()
  local ok, value = pcall(require, 'crabsync_catalog')
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
  local hooks = passiveHookManagerFactory.new(config, safe, evidenceWriter, state, catalog)
  local inventory = inventoryStageManagerFactory.new(config, safe, evidenceWriter, state)
  local o = {
    sessionId = sessionId,
    config = config,
    safe = safe,
    evidenceWriter = evidenceWriter,
    catalog = catalog,
    catalogError = catalogErr,
    state = state,
    hooks = hooks,
    inventory = inventory,
    active = false,
    lastHeartbeatAt = nil,
    heartbeatSeconds = positiveNumber(config.fullObserveHeartbeatSeconds, 1),
    stableSamplesRequired = clampInteger(config.fullObserveStableSamplesRequired, 3, 3, 60),
    stableDwellSecondsRequired = clampInteger(config.fullObserveStableDwellSeconds, 2, 1, 60),
    stableCandidateKey = '',
    stableCandidateStartedAt = nil,
    stableConsecutiveSamples = 0,
    stableReady = false,
    stabilityResetReason = 'startup',
    awaitingLoadMapPost = false,
    lastContext = 'unknown',
    lastPlayerStatePresent = false,
    lastVisiblePlayerStates = 0,
    lifecycleHooksRegistered = {}
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
      category = 'full-observe',
      symbol = 'Runtime.CrabSyncFullObserve',
      result = result,
      runtimeStatus = result == 'ok' and 'PASSIVE_CAMPAIGN' or 'UNSUPPORTED',
      catalogSchemaVersion = tostring(self.catalog.schemaVersion or ''),
      catalogHash = tostring(self.catalog.catalogHash or ''),
      details = details or {},
      safetyClassification = 'read-only-passive-campaign',
      noWrites = true,
      noRpcs = true,
      noMutation = true,
      noHud = true,
      rawIdentityEvidence = false,
      passiveOnly = true
    })
    local writeOk = self.evidenceWriter:writeEvidence(row)
    self.state:noteWriteResult(writeOk)
  end

  function o:start()
    if not safetyConfigurationValid(self.config) or not campaignIdentityValid(self.config) then
      self.state:tripCircuit('full-observe', 'unsafe or unassigned campaign configuration rejected', 'rejected-unsafe')
      self:writeCoordinatorEvidence('FullObserve.StartRejected', 'unsupported', { reason = 'unsafe configuration or unassigned campaign identity' })
      self.state:flushStatus('start-rejected')
      return false
    end
    self.active = true
    self.state.lifecycleState = 'warming'
    self:resetStability('startup dwell required')
    if self.catalogError then
      self.state.dirtyEvidence = true
      self.state.evidenceHealth = 'catalog-unavailable'
      self:writeCoordinatorEvidence('FullObserve.CatalogUnavailable', 'unsupported', { error = self.catalogError })
    else
      self:writeCoordinatorEvidence('FullObserve.CatalogLoaded', 'ok', { hookCount = #(self.catalog.hooks or {}) })
    end
    self.hooks:registerAll()
    self:registerLifecycleHooks()
    self.state:flushStatus('started')
    crpLog.line('[CrabRuntimeProbe] crabsync-full-observe coordinator started')
    return true
  end

  function o:onLifecycleBreadcrumb(eventName, lifecycleState, beginGeneration)
    if eventName == 'load-map-pre' then self.awaitingLoadMapPost = true end
    if eventName == 'load-map-post' or eventName == 'init-game-state-post' then self.awaitingLoadMapPost = false end
    self:resetStability('passive lifecycle breadcrumb: ' .. tostring(eventName))
    if beginGeneration then
      self.state:beginLifecycleTransition(lifecycleState, eventName)
      self.lastPlayerStatePresent = false
      self.hooks:onLifecycleTransition()
    else self.state.lifecycleState = lifecycleState end
    self.inventory:resetTransient(self.state.lifecycleGeneration)
    self:writeCoordinatorEvidence('FullObserve.LifecycleBreadcrumb', 'ok', {
      lifecycleEvent = eventName,
      lifecycleState = lifecycleState,
      lifecycleGeneration = self.state.lifecycleGeneration,
      passiveGlobalHook = true
    })
    self.state:flushStatus('lifecycle-global-hook')
  end

  function o:registerLifecycleHook(name, callback)
    local registrationFunction = _G[name]
    if type(registrationFunction) ~= 'function' then
      self.lifecycleHooksRegistered[name] = 'unsupported'
      self:writeCoordinatorEvidence('FullObserve.LifecycleHookRegistration', 'unsupported', { hookName = name, reason = 'global hook unavailable' })
      return
    end
    local ok, err = pcall(function() registrationFunction(callback) end)
    self.lifecycleHooksRegistered[name] = ok and 'registered' or 'unsupported'
    self:writeCoordinatorEvidence('FullObserve.LifecycleHookRegistration', ok and 'ok' or 'unsupported', { hookName = name, error = ok and '' or tostring(err) })
  end

  function o:registerLifecycleHooks()
    self:registerLifecycleHook('RegisterLoadMapPreHook', function()
      self:onLifecycleBreadcrumb('load-map-pre', 'traveling', true)
    end)
    self:registerLifecycleHook('RegisterLoadMapPostHook', function()
      self:onLifecycleBreadcrumb('load-map-post', 'warming', false)
    end)
    self:registerLifecycleHook('RegisterInitGameStatePostHook', function()
      self:onLifecycleBreadcrumb('init-game-state-post', 'warming', false)
    end)
    self:writeCoordinatorEvidence('FullObserve.LifecycleHookRegistration', 'unsupported', {
      hookName = 'RegisterBeginPlayPostHook',
      reason = 'intentionally excluded because the global callback is not actor-scoped/capped'
    })
  end

  function o:refreshRuntime(runnerState)
    local facts = runtimeContext.snapshot(self.safe, runnerState or {})
    local playerStateFingerprint, playerState = localPlayerStateFingerprint(self.safe)
    local currentWorldFingerprint = worldFingerprint(self.safe)
    local playerStatePresent = playerStateFingerprint ~= ''
    local nextContext = tostring(facts.context or 'unknown')
    local runnerLifecycleState = tostring((runnerState and runnerState.lifecycleState) or 'warming')
    local candidateValid = runnerLifecycleState == 'stable'
      and self.awaitingLoadMapPost ~= true
      and playerStatePresent and currentWorldFingerprint ~= ''
      and facts.playerStateValid == true
      and nextContext ~= 'traveling' and nextContext ~= 'unstable'
      and nextContext ~= 'dead-or-respawning' and nextContext ~= 'menu'
      and nextContext ~= 'lobby' and nextContext ~= 'unknown'
    local candidateKey = candidateValid and (currentWorldFingerprint .. '|' .. playerStateFingerprint .. '|' .. nextContext) or ''
    local contextChangedAfterStable = self.stableReady and (not candidateValid or candidateKey ~= self.stableCandidateKey)
    local forceTransition = (self.lastPlayerStatePresent and not playerStatePresent)
      or contextChangedAfterStable
    local authorityStatus = self.safe.authorityStatus(playerState)
    local visiblePlayerStates = visibleCrabPlayerStateCount(self.safe)
    local observedRole = observedRoleFromFacts(authorityStatus, visiblePlayerStates)
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
      lifecycleState = candidateValid and 'warming' or (nextContext == 'solo' and 'warming' or nextContext),
      observedRole = observedRole,
      authorityStatus = authorityStatus,
      forceLifecycleTransition = forceTransition
    })
    self.lastPlayerStatePresent = playerStatePresent
    self.lastContext = nextContext
    if self.lastVisiblePlayerStates < 2 and visiblePlayerStates >= 2 then
      self:writeCoordinatorEvidence('FullObserve.MultiplayerJoinObserved', 'ok', { visiblePlayerStates = visiblePlayerStates })
    elseif self.lastVisiblePlayerStates >= 2 and visiblePlayerStates < 2 then
      self:writeCoordinatorEvidence('FullObserve.MultiplayerDisconnectObserved', 'ok', { visiblePlayerStates = visiblePlayerStates })
    end
    self.lastVisiblePlayerStates = visiblePlayerStates
    if changed then
      self:resetStability('fingerprint or context transition')
      self.hooks:onLifecycleTransition()
      self.inventory:resetTransient(self.state.lifecycleGeneration)
    end
    if candidateValid then
      self:observeStableCandidate(candidateKey)
    else
      self:resetStability(self.awaitingLoadMapPost and 'awaiting load-map post/init breadcrumb'
        or 'valid current world/PlayerState stable sample unavailable')
      if nextContext == 'traveling' or nextContext == 'dead-or-respawning' or nextContext == 'unstable' then
        self.state.lifecycleState = nextContext
      else
        self.state.lifecycleState = 'warming'
      end
    end
    if changed then
      self:writeCoordinatorEvidence('FullObserve.LifecycleTransition', 'ok', {
        lifecycleGeneration = self.state.lifecycleGeneration,
        lifecycleState = self.state.lifecycleState,
        context = nextContext,
        visiblePlayerStates = visiblePlayerStates,
        stableSamples = self.stableConsecutiveSamples,
        stableSamplesRequired = self.stableSamplesRequired,
        stableDwellSecondsRequired = self.stableDwellSecondsRequired
      })
      self.state:flushStatus('lifecycle-transition')
    end
  end

  function o:onTick(runnerState)
    if not self.active then return end
    self:refreshRuntime(runnerState)
    if self.state:checkStopMarker() then
      self.active = false
      self.hooks:setActive(false)
      self:writeCoordinatorEvidence('FullObserve.StopRequested', 'ok', { marker = 'results/dashboard_stop_requested.json' })
      self.state:flushStatus('stopped')
      return
    end
    if self.stableReady and self.state.lifecycleState == 'stable' then
      self.hooks:onStableLifecycle(self.state.lifecycleGeneration)
      self.hooks:onStableTick()
    end
    self.inventory:onTick()
    local now = os.time()
    if self.lastHeartbeatAt == nil or (now - self.lastHeartbeatAt) >= self.heartbeatSeconds then
      self.lastHeartbeatAt = now
      self.state.lastHeartbeatAt = utcNow()
      self.state:flushStatus('heartbeat')
    end
  end

  return o
end

return coordinator
