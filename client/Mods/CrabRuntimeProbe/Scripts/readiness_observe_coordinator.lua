local crpLog = require('crp_log')
local baselineFactory = require('full_observe_coordinator')
local peerSamplerFactory = require('peer_sampler')

local coordinator = {}

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function validOpaqueId(value, minimumLength, maximumLength)
  local text = tostring(value or '')
  return text ~= '' and text ~= 'unassigned' and #text >= minimumLength and #text <= maximumLength
    and text:match('^[%w_%-]+$') ~= nil
end

-- The short human correlation code is intentionally never a runtime value.
-- Require the dashboard's fixed SHA-256-derived form so a copied eight-byte
-- code cannot be mistaken for a safe pair ID and written to evidence.
local function validPairId(value)
  local text = tostring(value or '')
  local suffix = text:match('^readiness%-pair%-(.*)$')
  return suffix ~= nil and #suffix == 24 and suffix:match('^[0-9a-f]+$') ~= nil
end

local function boundedNumber(value, minimum, maximum)
  local numberValue = tonumber(value)
  return numberValue ~= nil and numberValue == numberValue
    and numberValue >= minimum and numberValue <= maximum
end

local function requiredChannelsConfigured(value)
  local expected = {
    health = true,
    crystals = true,
    slots = true,
    equipment = true,
    ['peer-snapshots'] = true
  }
  local seen = {}
  local count = 0
  for channel in tostring(value or ''):gmatch('[^,%s]+') do
    if expected[channel] ~= true or seen[channel] then return false end
    seen[channel] = true
    count = count + 1
  end
  return count == 5 and seen.health and seen.crystals and seen.slots and seen.equipment and seen['peer-snapshots']
end

local function readinessConfigurationValid(config)
  if config.readinessCampaignEnabled ~= true
    or tostring(config.campaignProfile or '') ~= 'crabsync-readiness-campaign'
    or tostring(config.campaignId or '') ~= 'crabsync-readiness-campaign'
    or tostring(config.probeSet or '') ~= 'crabsync-readiness-campaign'
    or config.writeJsonlResults ~= true
    or config.readinessPeerSnapshotsEnabled ~= true
    or config.readinessTerminalSnapshotEnabled ~= true
    or config.progressiveObservationEnabled == true
    or config.progressiveHooksArmed == true
    or tostring(config.readinessInventoryStage or 'disabled') ~= 'disabled'
    or not validPairId(config.readinessPairId)
    or not validOpaqueId(config.readinessManifestId, 8, 128)
    or not boundedNumber(config.readinessMaxPeers, 1, 4)
    or not boundedNumber(config.readinessHealthIntervalSeconds, 0.25, 5)
    or not boundedNumber(config.readinessScalarIntervalSeconds, 1, 60)
    or not boundedNumber(config.readinessUnchangedHeartbeatSeconds, 10, 600) then
    return false
  end
  if not requiredChannelsConfigured(config.readinessEnabledChannels)
    or tonumber(config.readinessHealthIntervalSeconds) ~= tonumber(config.readinessScalarIntervalSeconds)
    or tonumber(config.snapshotSampleIntervalSeconds) ~= tonumber(config.readinessScalarIntervalSeconds)
    or tonumber(config.snapshotUnchangedHeartbeatSeconds) ~= tonumber(config.readinessUnchangedHeartbeatSeconds) then
    return false
  end
  -- This profile never delegates to the legacy probe runner. Reject every
  -- legacy/deep gate anyway so a manually edited config cannot make its
  -- manifest or status look readiness-safe while carrying another capability.
  for _, key in ipairs({
    'allowHudTickHook', 'allowWriteProbes', 'allowRpcProbes', 'allowRawIdentityEvidence',
    'allowUnknownRoleProbes', 'allowJoinedClientDeepProbes', 'allowDeepArrayProbes',
    'allowInventoryInfoProbes', 'allowHealthProbes', 'allowIdentityProbes',
    'allowResourceVisibilityProbes', 'allowCrystalsReadProbes', 'allowSlotsReadProbes',
    'allowSafeScalarWatchProbes', 'allowPerkDataAssetCatalogProbes',
    'allowMaxSafePlayRecorderProbes', 'allowInventoryArrayShallowProbes',
    'allowInventoryArrayShapeConfirmProbes', 'allowInventoryUserdataIntrospectionProbes',
    'allowInventoryArrayCountProbes', 'allowInventoryElementDataAssetReadProbes',
    'allowPassiveObservationHooks', 'allowFullObserveInventoryStages', 'allowFullObserveRuntimeDiscovery'
  }) do
    if config[key] == true then return false end
  end
  return true
end

local function lifecycleShape(state, lifecycleState, lifecycleGeneration, context, stable)
  return {
    state = tostring(lifecycleState or state.lifecycleState or 'unknown'),
    generation = math.max(0, math.floor(tonumber(lifecycleGeneration) or state.lifecycleGeneration or 0)),
    context = tostring(context or state.context or 'unknown'),
    stable = stable == true
  }
end

function coordinator.new(sessionId, config, safe, evidenceWriter)
  local baseline = baselineFactory.new(sessionId, config, safe, evidenceWriter)
  local peers = peerSamplerFactory.new(config, safe, evidenceWriter, baseline.state)
  local o = {
    sessionId = sessionId,
    config = config,
    safe = safe,
    evidenceWriter = evidenceWriter,
    baseline = baseline,
    state = baseline.state,
    snapshots = baseline.snapshots,
    peers = peers,
    active = false,
    lastLifecycleGeneration = tonumber(baseline.state.lifecycleGeneration) or 0,
    terminalSignals = {}
  }

  -- Emit only fields accepted by terminal-lifecycle-v1.  The generic evidence
  -- writer adds legacy fields, so readiness rows use its raw closed-schema
  -- method instead.
  function o:writeReadinessTerminal(priorLifecycle, nextLifecycle, reason)
    local signalKey = tostring(priorLifecycle.generation) .. '|' .. tostring(reason or '')
    if self.terminalSignals[signalKey] == true then return true end
    local row = {
      schemaVersion = 1,
      recordType = 'readiness-lifecycle-terminal',
      event = 'Readiness.LifecycleTerminal',
      readinessSchema = 'terminal-lifecycle-v1',
      campaignId = tostring(self.config.campaignId or ''),
      campaignGeneration = tonumber(self.config.campaignGeneration) or 0,
      sessionId = tostring(self.state.sessionId or ''),
      machineId = tostring(self.config.machineId or ''),
      sequence = self.state:nextSequence(),
      timestampUtc = utcNow(),
      selectedRole = tostring(self.state.selectedRole or 'unknown'),
      profileId = 'crabsync-readiness-campaign',
      readinessPairId = tostring(self.config.readinessPairId or ''),
      priorLifecycle = priorLifecycle,
      nextLifecycle = nextLifecycle,
      reason = tostring(reason or 'lifecycle-transition'),
      baselineReady = type(self.snapshots.isBaselineReady) == 'function' and self.snapshots:isBaselineReady() or false,
      peerSamplingSummary = self.peers:summary(),
      dirtyEvidence = self.state.dirtyEvidence == true,
      crashSuspected = self.state.crashSuspected == true,
      safety = {
        writesDisabled = true,
        rpcCallsDisabled = true,
        mutationDisabled = true,
        hooksDisabled = true,
        runtimeDiscoveryDisabled = true,
        inventoryStagesDisabled = true,
        rawIdentityDisabled = true
      }
    }
    local writeOk = type(self.evidenceWriter.writeReadinessRecord) == 'function'
      and self.evidenceWriter:writeReadinessRecord(row)
      or false
    self.state:noteWriteResult(writeOk)
    if writeOk then
      self.terminalSignals[signalKey] = true
      self.state:markTerminalLifecycle({
        timestampUtc = row.timestampUtc,
        reason = row.reason,
        priorGeneration = priorLifecycle.generation,
        nextGeneration = nextLifecycle.generation
      })
      self.state:flushStatus('readiness-terminal-lifecycle')
    end
    return writeOk
  end

  function o:start()
    if not readinessConfigurationValid(self.config) then
      self.state:tripCircuit('readiness-runtime',
        'readiness profile rejected: hooks, inventory stages, or pair identity were unsafe or incomplete', 'rejected-unsafe')
      self.state:flushStatus('readiness-start-rejected')
      return false
    end

    local started = self.baseline:start()
    if started ~= true then return false end
    self.state.activeProfile = 'crabsync-readiness-campaign'
    self.state.workflow = 'crabsync-readiness-campaign'
    self.state.collectionReadiness = 'warming'
    self.state.probeStage = 'readiness:waiting-for-stable-game'
    self.state:setReadinessStage('warming', false,
      'local scalar readiness foundation; remote visibility and inventory are deferred')
    self.peers:setActive(true)
    self.peers:resetLifecycle(false)
    self.lastLifecycleGeneration = tonumber(self.state.lifecycleGeneration) or 0
    self.baseline:setLifecycleTransitionListener(function(priorLifecycle, nextLifecycle, reason)
      self:writeReadinessTerminal(priorLifecycle, nextLifecycle, reason)
    end)
    self.active = true
    self.state:flushStatus('readiness-started')
    crpLog.line('[CrabRuntimeProbe] readiness observer started; local scalar and lifecycle evidence only')
    return true
  end

  function o:onTick(runnerState)
    if not self.active then return end

    -- Claim the dashboard stop marker before the baseline clears its state so
    -- the final readiness row retains the last stable scope.
    if not self.state.stopRequested and self.state:checkStopMarker() then
      local priorLifecycle = lifecycleShape(self.state, self.state.lifecycleState,
        self.state.lifecycleGeneration, self.state.context,
        self.state.stability.ready == true and self.state.lifecycleState == 'stable')
      local nextLifecycle = lifecycleShape(self.state, 'stopped', self.state.lifecycleGeneration,
        self.state.context, false)
      self:writeReadinessTerminal(priorLifecycle, nextLifecycle, 'stop-requested')
    end

    self.baseline:onTick(runnerState)
    if self.state.stopRequested then
      self.peers:setActive(false)
      self.state.lifecycleState = 'stopped'
      self.state.probeStage = 'readiness:stopped'
      self.state:setReadinessStage('stopped', false, 'readiness collection stopped by dashboard marker')
      self.state:flushStatus('readiness-stopped')
      self.active = false
      return
    end

    local lifecycleGeneration = tonumber(self.state.lifecycleGeneration) or 0
    if lifecycleGeneration ~= self.lastLifecycleGeneration then
      self.lastLifecycleGeneration = lifecycleGeneration
      self.peers:resetLifecycle()
    end

    if self.state.stability.ready == true and self.state.lifecycleState == 'stable' then
      self.peers:onTick()
    end
    local baselineReady = type(self.snapshots.isBaselineReady) == 'function' and self.snapshots:isBaselineReady() or false
    self.state:setReadinessStage(
      baselineReady and 'collecting-local-scalars' or 'warming',
      baselineReady,
      baselineReady and 'local scalar baseline ready; remote visibility and inventory remain deferred'
        or 'waiting for stable local scalar baseline')
  end

  return o
end

return coordinator
