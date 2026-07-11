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

function campaignState.new(sessionId, config, catalog)
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
    inventoryDepth = 0,
    evidenceHealth = 'healthy',
    crashSuspected = false,
    dirtyEvidence = false,
    writeFailureCount = 0,
    checklist = {},
    hookRegistration = {},
    circuitBreakers = {},
    inventoryStages = {},
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
        stability = self.stability,
        hookIo = self.hookIo
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
        hooksDisabled = self.config.allowPassiveObservationHooks ~= true,
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
    return self.statusWriter:writeSnapshot(self:snapshot(reason))
  end

  return state
end

return campaignState
