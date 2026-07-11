local recordBuilder = require('record_builder')

local campaignState = {}

local VALID_CHECKLIST_STATUSES = {
  ['not-observed'] = true,
  ['in-progress'] = true,
  partial = true,
  confirmed = true,
  unsupported = true,
  ['blocked-by-prerequisite'] = true,
  ['crash-suspect'] = true,
  ['dirty-evidence'] = true,
  ['not-applicable'] = true
}

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function fileExists(path)
  local file = io.open(path, 'r')
  if not file then return false end
  file:close()
  return true
end

local SEQUENCE_PATH = 'Mods/CrabRuntimeProbe/Scripts/results/full_observe_sequence.txt'
local SEQUENCE_FALLBACK_PATH = 'Mods/CrabRuntimeProbe/Scripts/full_observe_sequence.txt'

local function flatValue(value)
  return tostring(value or ''):gsub('[\r\n=]+', '_')
end

local function loadSequencePath(path, sessionId, config)
  local file = io.open(path, 'r')
  if not file then return nil end
  local values = {}
  for line in file:lines() do
    local key, value = line:match('^([%w_]+)=(.*)$')
    if key then values[key] = value end
  end
  file:close()
  if values.sessionId ~= flatValue(sessionId)
    or values.campaignId ~= flatValue(config.campaignId)
    or values.campaignGeneration ~= flatValue(tonumber(config.campaignGeneration) or 0) then
    return nil
  end
  return tonumber(values.sequence) or 0
end

local function loadSequence(sessionId, config)
  local configured = tonumber(config.resumeEvidenceSequence) or 0
  return math.max(
    configured,
    loadSequencePath(SEQUENCE_PATH, sessionId, config) or 0,
    loadSequencePath(SEQUENCE_PATH .. '.previous', sessionId, config) or 0,
    loadSequencePath(SEQUENCE_FALLBACK_PATH, sessionId, config) or 0,
    loadSequencePath(SEQUENCE_FALLBACK_PATH .. '.previous', sessionId, config) or 0)
end

local function writeSequencePath(path, sessionId, config, sequence)
  local tempPath = path .. '.' .. flatValue(sessionId) .. '.tmp'
  local file = io.open(tempPath, 'w')
  if not file then return false end
  file:write('sessionId=' .. flatValue(sessionId) .. '\n')
  file:write('campaignId=' .. flatValue(config.campaignId) .. '\n')
  file:write('campaignGeneration=' .. flatValue(tonumber(config.campaignGeneration) or 0) .. '\n')
  file:write('sequence=' .. tostring(sequence) .. '\n')
  file:close()
  local backupPath = path .. '.previous'
  os.remove(backupPath)
  local existing = io.open(path, 'r')
  if existing then
    existing:close()
    if not os.rename(path, backupPath) then os.remove(tempPath); return false end
  end
  if not os.rename(tempPath, path) then
    os.remove(tempPath)
    os.rename(backupPath, path)
    return false
  end
  os.remove(backupPath)
  return true
end

local function checklistEntry(id)
  return {
    id = id,
    status = 'not-observed',
    observationCount = 0,
    firstTimestamp = '',
    latestTimestamp = '',
    sourceRoles = {},
    evidenceSessions = {},
    nextInstruction = '',
    reason = ''
  }
end

local function addUnique(list, value)
  if value == nil or value == '' then return end
  for _, current in ipairs(list) do
    if current == value then return end
  end
  list[#list + 1] = value
end

local function configuredProfile(config)
  config = config or {}
  if config.progressiveObservationEnabled == true then
    return 'progressive-broad-observation'
  end
  if config.readinessCampaignEnabled == true
    and tostring(config.campaignProfile or '') == 'crabsync-readiness-campaign' then
    return 'crabsync-readiness-campaign'
  end
  return 'normal-play-guide'
end

local function derivedReadinessPairId(value)
  local text = tostring(value or '')
  local suffix = text:match('^readiness%-pair%-(.*)$')
  return suffix ~= nil and #suffix == 24 and suffix:match('^[0-9a-f]+$') ~= nil and text or ''
end

local function readinessManifestId(value)
  local text = tostring(value or '')
  return #text >= 8 and #text <= 128 and text:match('^readiness%-manifest%-%w[%w_%-]*$') ~= nil and text or ''
end

function campaignState.new(sessionId, config, catalog)
  local profile = configuredProfile(config)
  local state = {
    sessionId = tostring(sessionId or ''),
    config = config or {},
    catalog = catalog or {},
    sequence = loadSequence(sessionId, config or {}),
    selectedRole = tostring((config or {}).selectedRole or 'unselected'):lower():gsub('%s+', '-'),
    observedRole = 'unknown',
    authorityStatus = 'unknown',
    context = 'unknown',
    lifecycleState = 'startup',
    lifecycleGeneration = 0,
    worldFingerprint = '',
    localPlayerStateFingerprint = '',
    lastHeartbeatAt = '',
    startedAt = utcNow(),
    stoppedAt = '',
    stopRequested = false,
    probeStage = 'startup',
    workflow = profile,
    activeProfile = profile,
    currentSamplingCategory = '',
    collectionReadiness = 'warming',
    inventoryDepth = 0,
    evidenceHealth = 'healthy',
    crashSuspected = false,
    dirtyEvidence = false,
    writeFailureCount = 0,
    checklist = {},
    hookRegistration = {},
    circuitBreakers = {},
    inventoryStages = {},
    readiness = {
      enabled = profile == 'crabsync-readiness-campaign',
      pairId = profile == 'crabsync-readiness-campaign' and derivedReadinessPairId((config or {}).readinessPairId) or '',
      manifestId = profile == 'crabsync-readiness-campaign' and readinessManifestId((config or {}).readinessManifestId) or '',
      inventoryStage = tostring((config or {}).readinessInventoryStage or 'disabled'),
      stageState = profile == 'crabsync-readiness-campaign' and 'warming' or 'not-enabled',
      enabledChannels = tostring((config or {}).readinessEnabledChannels or ''),
      safeReadChannelsReady = false,
      visiblePlayerCount = 0,
      stablePlayerCount = 0,
      peerSnapshotCount = 0,
      inventoryCategoryCount = 0,
      maxPeers = math.max(1, math.min(4, math.floor(tonumber((config or {}).readinessMaxPeers) or 4))),
      maxInventoryItems = 0,
      maxEnhancements = 0,
      detail = profile == 'crabsync-readiness-campaign'
        and 'local scalar readiness foundation; remote visibility and inventory are deferred' or '',
      lastResult = 'not-enabled',
      lastChangeKind = '',
      lastSampleAtUtc = '',
      terminalLifecycle = {
        emitted = false,
        timestampUtc = '',
        reason = '',
        priorGeneration = 0,
        nextGeneration = 0
      }
    },
    research = {
      runId = '',
      runType = '',
      stage = '',
      trustedHookCount = 0,
      registeredHookCount = 0,
      activeCanaryId = '',
      canaryValidationDepth = 0,
      suggestedAction = '',
      canaryRegistrationState = 'not-configured',
      callbackCount = 0,
      canaryCallbackCount = 0,
      canaryCircuitBreakers = {},
      journal = {},
      registrationOrder = {},
      safeSnapshotBaselineReady = false,
      automaticInProcessAdvance = false,
      researchAllowed = false,
      registrationAttempted = false,
      registrationComplete = false,
      relicCount = {}
    },
    stability = {
      ready = false,
      consecutiveSamples = 0,
      requiredSamples = tonumber((config or {}).fullObserveStableSamplesRequired) or 3,
      dwellSeconds = 0,
      requiredDwellSeconds = tonumber((config or {}).fullObserveStableDwellSeconds) or 2,
      candidateFingerprint = '',
      resetReason = 'startup'
    },
    hookIo = {
      global = {
        callbackCount = 0,
        evidenceRowsWritten = 0,
        coalescedInvocations = 0,
        globalCapDrops = 0,
        perDescriptorCapDrops = 0,
        callbackErrors = 0,
        untrackedDescriptorEvents = 0
      },
      descriptors = {},
      trackedDescriptorCount = 0,
      trackedDescriptorCap = math.max(1, math.min(128, math.floor(tonumber((config or {}).fullObserveHookTrackedDescriptorCap) or 128)))
    },
    statusWriter = nil
  }

  for _, descriptor in ipairs((catalog and catalog.hooks) or {}) do
    for _, checklistId in ipairs(descriptor.checklistLinks or {}) do
      if state.checklist[checklistId] == nil then
        state.checklist[checklistId] = checklistEntry(checklistId)
      end
    end
  end

  function state:nextSequence()
    self.sequence = self.sequence + 1
    if not writeSequencePath(SEQUENCE_PATH, self.sessionId, self.config, self.sequence)
      and not writeSequencePath(SEQUENCE_FALLBACK_PATH, self.sessionId, self.config, self.sequence) then
      self.writeFailureCount = self.writeFailureCount + 1
      self.evidenceHealth = 'sequence-persistence-error'
      self.dirtyEvidence = true
    end
    return self.sequence
  end

  function state:setStatusWriter(writer)
    self.statusWriter = writer
  end

  function state:ensureChecklist(id)
    local key = tostring(id or '')
    if key == '' then return nil end
    if self.checklist[key] == nil then self.checklist[key] = checklistEntry(key) end
    return self.checklist[key]
  end

  function state:markChecklist(id, status, details)
    if not VALID_CHECKLIST_STATUSES[status] then status = 'partial' end
    local entry = self:ensureChecklist(id)
    if not entry then return end
    details = details or {}
    if entry.status == 'confirmed' and status ~= 'dirty-evidence' and status ~= 'crash-suspect' then
      status = 'confirmed'
    end
    entry.status = status
    entry.reason = recordBuilder.cleanString(details.reason or entry.reason, 256)
    entry.nextInstruction = recordBuilder.cleanString(details.nextInstruction or entry.nextInstruction, 256)
    if details.observed == true then
      local timestamp = tostring(details.timestamp or utcNow())
      entry.observationCount = entry.observationCount + 1
      if entry.firstTimestamp == '' then entry.firstTimestamp = timestamp end
      entry.latestTimestamp = timestamp
      addUnique(entry.sourceRoles, tostring(details.role or self.selectedRole))
      addUnique(entry.evidenceSessions, tostring(details.sessionId or self.sessionId))
    end
  end

  function state:observeEvidence(row, options)
    options = options or {}
    local links = row and (row.qualifyingChecklistLinks or {}) or {}
    local qualifying = {}
    local phase = tostring(row and row.hookPhase or 'observed')
    for _, checklistId in ipairs(links) do
      qualifying[tostring(checklistId)] = true
      self:markChecklist(checklistId, phase == 'pre' and 'in-progress' or 'confirmed', {
        observed = true,
        timestamp = row.timestamp,
        role = row.selectedRole or self.selectedRole,
        sessionId = row.sessionId or self.sessionId,
        reason = phase == 'pre' and 'qualifying natural call entered' or 'qualifying passive observation captured'
      })
    end
    for _, checklistId in ipairs((row and row.checklistLinks) or {}) do
      if not qualifying[tostring(checklistId)] then
        self:markChecklist(checklistId, 'partial', {
          observed = true,
          timestamp = row.timestamp,
          role = row.selectedRole or self.selectedRole,
          sessionId = row.sessionId or self.sessionId,
          reason = 'natural callback observed; checklist requires separately scoped qualifying evidence'
        })
      end
    end
    self.probeStage = tostring(row and (row.inventoryStageName or row.symbol or row.probeName) or self.probeStage)
    if options.deferStatusFlush ~= true then self:flushStatus('evidence') end
  end

  function state:setStability(details)
    details = details or {}
    self.stability.ready = details.ready == true
    self.stability.consecutiveSamples = tonumber(details.consecutiveSamples) or 0
    self.stability.requiredSamples = tonumber(details.requiredSamples) or self.stability.requiredSamples
    self.stability.dwellSeconds = tonumber(details.dwellSeconds) or 0
    self.stability.requiredDwellSeconds = tonumber(details.requiredDwellSeconds) or self.stability.requiredDwellSeconds
    self.stability.candidateFingerprint = tostring(details.candidateFingerprint or '')
    self.stability.resetReason = recordBuilder.cleanString(details.resetReason or self.stability.resetReason, 160)
  end

  function state:setSamplingState(category, readiness)
    self.currentSamplingCategory = recordBuilder.cleanString(category or '', 64)
    if readiness ~= nil and readiness ~= '' then
      self.collectionReadiness = recordBuilder.cleanString(readiness, 64)
    end
  end

  function state:setPeerSamplingSummary(summary)
    summary = type(summary) == 'table' and summary or {}
    local current = self.readiness
    current.peerSnapshotCount = math.max(0, math.floor(tonumber(summary.peerSnapshotCount) or current.peerSnapshotCount or 0))
    current.visiblePlayerCount = math.max(0, math.min(4, math.floor(tonumber(summary.visiblePlayerCount) or 0)))
    current.stablePlayerCount = math.max(0, math.min(4, math.floor(tonumber(summary.stablePlayerCount) or 0)))
    current.lastResult = recordBuilder.cleanString(summary.lastResult or current.lastResult or 'unknown', 32)
    current.lastChangeKind = recordBuilder.cleanString(summary.lastChangeKind or current.lastChangeKind or '', 32)
    current.lastSampleAtUtc = recordBuilder.cleanString(summary.lastSampleAtUtc or current.lastSampleAtUtc or '', 64)
    if summary.reason and summary.reason ~= '' then
      current.detail = recordBuilder.cleanString(summary.reason, 240)
    end
  end

  function state:peerSamplingSummary()
    local current = self.readiness or {}
    return {
      peerSnapshotCount = math.max(0, math.floor(tonumber(current.peerSnapshotCount) or 0)),
      visiblePlayerCount = math.max(0, math.min(4, math.floor(tonumber(current.visiblePlayerCount) or 0))),
      stablePlayerCount = math.max(0, math.min(4, math.floor(tonumber(current.stablePlayerCount) or 0)))
    }
  end

  function state:setReadinessStage(stageState, safeReadChannelsReady, detail)
    local current = self.readiness
    current.stageState = recordBuilder.cleanString(stageState or current.stageState or 'unavailable', 64)
    if safeReadChannelsReady ~= nil then current.safeReadChannelsReady = safeReadChannelsReady == true end
    if detail and detail ~= '' then current.detail = recordBuilder.cleanString(detail, 240) end
  end

  function state:markTerminalLifecycle(details)
    details = type(details) == 'table' and details or {}
    self.readiness.terminalLifecycle = {
      emitted = true,
      timestampUtc = recordBuilder.cleanString(details.timestampUtc or utcNow(), 64),
      reason = recordBuilder.cleanString(details.reason or 'lifecycle-transition', 240),
      priorGeneration = math.max(0, math.floor(tonumber(details.priorGeneration) or self.lifecycleGeneration or 0)),
      nextGeneration = math.max(0, math.floor(tonumber(details.nextGeneration) or self.lifecycleGeneration or 0))
    }
  end

  function state:setResearchSummary(summary)
    summary = type(summary) == 'table' and summary or {}
    local journal = type(summary.journal) == 'table' and summary.journal or {}
    local relicCount = type(summary.relicCount) == 'table' and summary.relicCount or {}
    local registrationOrder = {}
    for index, candidateId in ipairs(summary.registrationOrder or {}) do
      if index > 112 then break end
      registrationOrder[index] = recordBuilder.cleanString(candidateId, 128)
    end
    self.research = {
      runId = recordBuilder.cleanString(summary.runId or '', 128),
      runType = recordBuilder.cleanString(summary.runType or '', 32),
      stage = recordBuilder.cleanString(summary.stage or '', 96),
      trustedHookCount = tonumber(summary.trustedHookCount) or 0,
      registeredHookCount = tonumber(summary.registeredHookCount) or 0,
      activeCanaryId = recordBuilder.cleanString(summary.activeCanaryId or '', 128),
      canaryValidationDepth = tonumber(summary.canaryValidationDepth) or 0,
      suggestedAction = recordBuilder.cleanString(summary.suggestedAction or '', 256),
      canaryRegistrationState = recordBuilder.cleanString(summary.canaryRegistrationState or '', 64),
      callbackCount = tonumber(summary.callbackCount) or 0,
      canaryCallbackCount = tonumber(summary.canaryCallbackCount) or 0,
      canaryCircuitBreakers = recordBuilder.safeValue(summary.canaryCircuitBreakers or {}),
      journal = {
        state = recordBuilder.cleanString(journal.state or '', 32),
        sequence = tonumber(journal.sequence) or 0,
        recordCount = tonumber(journal.recordCount) or 0,
        recordCap = tonumber(journal.recordCap) or 0,
        lastBoundary = recordBuilder.cleanString(journal.lastBoundary or '', 64),
        lastCompletedBoundary = recordBuilder.cleanString(journal.lastCompletedBoundary or '', 64),
        lastCandidateId = recordBuilder.cleanString(journal.lastCandidateId or '', 128),
        faultReason = recordBuilder.cleanString(journal.faultReason or '', 96)
      },
      registrationOrder = registrationOrder,
      safeSnapshotBaselineReady = summary.safeSnapshotBaselineReady == true,
      automaticInProcessAdvance = false,
      researchAllowed = summary.researchAllowed == true,
      registrationAttempted = summary.registrationAttempted == true,
      registrationComplete = summary.registrationComplete == true,
      compatibilityFingerprint = recordBuilder.cleanString(summary.compatibilityFingerprint or '', 64),
      hookCatalogIdentity = recordBuilder.cleanString(summary.hookCatalogIdentity or '', 64),
      relicCount = {
        enabled = relicCount.enabled == true,
        stage = recordBuilder.cleanString(relicCount.stage or '', 64),
        wrapperValidatedGeneration = tonumber(relicCount.wrapperValidatedGeneration) or -1,
        baselineCount = tonumber(relicCount.baselineCount),
        lastCount = tonumber(relicCount.lastCount),
        localCountIncreaseObserved = relicCount.localCountIncreaseObserved == true,
        pickupCallbackObserved = false
      }
    }
  end

  function state:noteHookIo(descriptorId, field, amount)
    local delta = tonumber(amount) or 1
    local key = tostring(field or '')
    if self.hookIo.global[key] == nil then self.hookIo.global[key] = 0 end
    self.hookIo.global[key] = self.hookIo.global[key] + delta
    local id = tostring(descriptorId or '')
    if id == '' then return end
    local entry = self.hookIo.descriptors[id]
    if entry == nil then
      if self.hookIo.trackedDescriptorCount >= self.hookIo.trackedDescriptorCap then
        self.hookIo.global.untrackedDescriptorEvents = self.hookIo.global.untrackedDescriptorEvents + delta
        return
      end
      entry = { callbackCount = 0, evidenceRowsWritten = 0, coalescedInvocations = 0, perDescriptorCapDrops = 0, callbackErrors = 0 }
      self.hookIo.descriptors[id] = entry
      self.hookIo.trackedDescriptorCount = self.hookIo.trackedDescriptorCount + 1
    end
    if entry[key] == nil then entry[key] = 0 end
    entry[key] = entry[key] + delta
  end

  function state:markHookRegistration(descriptor, status, err)
    local id = tostring((descriptor and descriptor.id) or '')
    if id == '' then return end
    self.hookRegistration[id] = {
      status = tostring(status or 'unknown'),
      category = tostring(descriptor.category or ''),
      symbolPath = tostring(descriptor.symbolPath or ''),
      hookPath = tostring(descriptor.hookPath or ''),
      error = recordBuilder.cleanString(err or '', 256),
      updatedAt = utcNow()
    }
  end

  function state:circuitAllows(category)
    local breaker = self.circuitBreakers[tostring(category or '')]
    return breaker == nil or breaker.state == 'closed'
  end

  function state:tripCircuit(category, reason, classification)
    local key = tostring(category or 'unknown')
    self.circuitBreakers[key] = {
      state = tostring(classification or 'open'),
      reason = recordBuilder.cleanString(reason or '', 256),
      openedAt = utcNow(),
      lifecycleGeneration = self.lifecycleGeneration
    }
    self.dirtyEvidence = true
    if classification == 'crash-suspect' then self.crashSuspected = true end
    self:flushStatus('circuit-breaker')
  end

  function state:setInventoryStage(category, stageNumber, stageName, status, reason)
    local key = tostring(category or '')
    self.inventoryStages[key] = {
      stage = tonumber(stageNumber) or 1,
      stageName = tostring(stageName or ''),
      status = tostring(status or 'in-progress'),
      reason = recordBuilder.cleanString(reason or '', 256),
      updatedAt = utcNow(),
      lifecycleGeneration = self.lifecycleGeneration
    }
    self.inventoryDepth = math.max(self.inventoryDepth, tonumber(stageNumber) or 0)
    self.probeStage = key .. ':' .. tostring(stageName or stageNumber)
  end

  function state:updateRuntime(facts)
    facts = facts or {}
    local nextWorld = tostring(facts.worldFingerprint or '')
    local nextPlayerState = tostring(facts.localPlayerStateFingerprint or '')
    local lifecycleChanged = false
    if self.worldFingerprint ~= '' and nextWorld ~= '' and self.worldFingerprint ~= nextWorld then
      lifecycleChanged = true
    end
    if self.localPlayerStateFingerprint ~= '' and nextPlayerState ~= '' and self.localPlayerStateFingerprint ~= nextPlayerState then
      lifecycleChanged = true
    end
    if facts.forceLifecycleTransition == true then lifecycleChanged = true end
    if lifecycleChanged then self.lifecycleGeneration = self.lifecycleGeneration + 1 end
    self.worldFingerprint = nextWorld
    self.localPlayerStateFingerprint = nextPlayerState
    self.context = tostring(facts.context or self.context)
    self.lifecycleState = tostring(facts.lifecycleState or self.lifecycleState)
    self.observedRole = tostring(facts.observedRole or self.observedRole)
    if facts.authorityStatus and facts.authorityStatus ~= '' then
      self.authorityStatus = tostring(facts.authorityStatus)
    end
    self.lastHeartbeatAt = utcNow()
    return lifecycleChanged
  end

  function state:beginLifecycleTransition(lifecycleState, reason)
    self.lifecycleGeneration = self.lifecycleGeneration + 1
    self.lifecycleState = tostring(lifecycleState or 'traveling')
    self.worldFingerprint = ''
    self.localPlayerStateFingerprint = ''
    self.probeStage = 'lifecycle:' .. tostring(reason or lifecycleState or 'transition')
    self.lastHeartbeatAt = utcNow()
    self:flushStatus('lifecycle-breadcrumb')
  end

  function state:noteWriteResult(ok)
    if ok == false then
      self.writeFailureCount = self.writeFailureCount + 1
      self.evidenceHealth = 'write-error'
      self.dirtyEvidence = true
    end
  end

  function state:checkStopMarker()
    if self.stopRequested then return true end
    if fileExists('Mods/CrabRuntimeProbe/Scripts/results/dashboard_stop_requested.json') then
      self.stopRequested = true
      self.stoppedAt = utcNow()
      self.probeStage = 'stopped'
      self:flushStatus('stop-marker')
    end
    return self.stopRequested
  end

  function state:snapshot(reason)
    return {
      schemaVersion = 1,
      sequence = self.sequence,
      writtenAtUtc = utcNow(),
      heartbeatAtUtc = self.lastHeartbeatAt ~= '' and self.lastHeartbeatAt or utcNow(),
      campaignId = tostring(self.config.campaignId or ''),
      campaignName = tostring(self.config.campaignName or 'crabsync-full-observe'),
      campaignGeneration = tonumber(self.config.campaignGeneration) or 0,
      machineId = tostring(self.config.machineId or ''),
      sessionId = self.sessionId,
      selectedRole = self.selectedRole,
      observedRole = self.observedRole,
      authorityStatus = self.authorityStatus,
      lifecycle = {
        state = self.lifecycleState,
        generation = self.lifecycleGeneration,
        context = self.context,
        stable = self.stability.ready == true and self.lifecycleState == 'stable',
        worldFingerprint = self.worldFingerprint,
        localPlayerStateFingerprint = self.localPlayerStateFingerprint,
        stabilityReady = self.stability.ready,
        stableSamples = self.stability.consecutiveSamples,
        stableSamplesRequired = self.stability.requiredSamples,
        stableDwellSeconds = self.stability.dwellSeconds,
        stableDwellSecondsRequired = self.stability.requiredDwellSeconds
      },
      runtime = {
        probeStage = self.probeStage,
        currentProbeStage = self.probeStage,
        heartbeat = self.lastHeartbeatAt,
        stopRequested = self.stopRequested,
        runtimeProbeLoaded = true,
        runtimeProbeState = self.stopRequested and 'stopped' or 'active',
        ue4ssState = 'loaded',
        gameProcessState = 'running',
        reason = tostring(reason or ''),
        evidenceSequence = self.sequence,
        activeProfile = self.activeProfile,
        profileId = self.activeProfile,
        workflow = self.workflow,
        currentSamplingCategory = self.currentSamplingCategory,
        collectionReady = self.collectionReadiness == 'ready' or self.collectionReadiness == 'collecting',
        collectionReadiness = self.collectionReadiness,
        stability = self.stability,
        hookIo = self.hookIo,
        readiness = self.readiness,
        research = self.research
      },
      catalog = {
        schemaVersion = tostring(self.catalog.schemaVersion or ''),
        catalogHash = tostring(self.catalog.catalogHash or ''),
        hookCount = #((self.catalog and self.catalog.hooks) or {})
      },
      safety = {
        writesDisabled = self.config.allowWriteProbes ~= true,
        rpcCallsDisabled = self.config.allowRpcProbes ~= true,
        rpcsDisabled = self.config.allowRpcProbes ~= true,
        mutationDisabled = true,
        hooksDisabled = self.config.allowPassiveObservationHooks ~= true and self.config.progressiveHooksArmed ~= true,
        runtimeDiscoveryDisabled = self.config.allowFullObserveRuntimeDiscovery ~= true,
        inventoryStagesDisabled = self.config.allowFullObserveInventoryStages ~= true,
        hudHookDisabled = self.config.allowHudTickHook ~= true,
        rawIdentityDisabled = self.config.allowRawIdentityEvidence ~= true,
        inventoryDepth = self.inventoryDepth,
        circuitBreakers = self.circuitBreakers
      },
      checklist = self.checklist,
      hookRegistration = self.hookRegistration,
      inventoryStages = self.inventoryStages,
      evidenceHealth = self.evidenceHealth,
      crashSuspected = self.crashSuspected,
      dirtyEvidence = self.dirtyEvidence
    }
  end

  function state:flushStatus(reason)
    if self.statusWriter == nil then return false end
    local ok = self.statusWriter:writeSnapshot(self:snapshot(reason))
    if not ok then
      self.writeFailureCount = self.writeFailureCount + 1
      self.evidenceHealth = 'status-write-error'
      self.dirtyEvidence = true
    end
    return ok
  end

  return state
end

return campaignState
