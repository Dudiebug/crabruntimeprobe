local crpLog = require('crp_log')
local recordBuilder = require('record_builder')

local passiveHookManager = {}
local unpackArgs = table.unpack or unpack

local ALLOWED_SNAPSHOT_FIELDS = {
  WeaponDA = 'object',
  AbilityDA = 'object',
  MeleeDA = 'object',
  Crystals = 'scalar',
  NumWeaponModSlots = 'scalar',
  NumAbilityModSlots = 'scalar',
  NumMeleeModSlots = 'scalar',
  NumPerkSlots = 'scalar',
  BaseMaxHealth = 'scalar',
  MaxHealthMultiplier = 'scalar',
  CurrentHealth = 'health',
  CurrentMaxHealth = 'health',
  CurrentArmorPlates = 'health',
  CurrentArmorPlateHealth = 'health'
}

local EXACT_NATURAL_OBSERVATION_RULES = {
  ClientOnEliminated = { checklistIds = { 'health-elimination-death' } },
  ClientOnPickedUpPickup = { checklistIds = { 'transaction-client-picked-up' }, requireArguments = true },
  ClientOnTookDamage = { checklistIds = { 'health-damage' } },
  ClientOnUpdatedOutOfBoundsState = { checklistIds = { 'health-out-of-bounds' } },
  MulticastApplyEnhancement = { checklistIds = { 'transaction-multicast-enhancement' } },
  OnRep_AbilityDA = { checklistIds = { 'transaction-equipment-change' }, requireReadableScopedState = true, requireStateChange = true },
  OnRep_Crystals = { checklistIds = { 'resource-onrep-crystals' }, requireReadableScopedState = true },
  OnRep_Inventory = { checklistIds = { 'transaction-onrep-inventory' } },
  OnRep_IsEliminated = { checklistIds = { 'health-elimination-death' } },
  OnRep_MeleeDA = { checklistIds = { 'transaction-equipment-change' }, requireReadableScopedState = true, requireStateChange = true },
  OnRep_WeaponDA = { checklistIds = { 'transaction-equipment-change' }, requireReadableScopedState = true, requireStateChange = true },
  ServerApplyEnhancement = { checklistIds = { 'transaction-server-apply-enhancement' }, requireArguments = true },
  ServerAutoLoot = { checklistIds = { 'transaction-server-autoloot' } },
  ServerDealDamage = { checklistIds = { 'health-damage' }, requireArguments = true },
  ServerDealFallDamage = { checklistIds = { 'health-damage' }, requireArguments = true },
  ServerDropPickup = { checklistIds = { 'transaction-drop' }, requireArguments = true },
  ServerEquipInventory = { checklistIds = { 'transaction-official-equipment-rpc' }, requireArguments = true },
  ServerIncrementNumInventorySlots = { checklistIds = { 'slot-increment-arguments' }, requireArguments = true, requireReadableScopedState = true },
  ServerInteract = { checklistIds = { 'transaction-server-interact' }, requireArguments = true },
  ServerRemoveAbilityMod = { checklistIds = { 'transaction-typed-removal' }, requireArguments = true },
  ServerRemoveMeleeMod = { checklistIds = { 'transaction-typed-removal' }, requireArguments = true },
  ServerRemovePerk = { checklistIds = { 'transaction-typed-removal' }, requireArguments = true },
  ServerRemoveRelic = { checklistIds = { 'transaction-typed-removal' }, requireArguments = true },
  ServerRemoveWeaponMod = { checklistIds = { 'transaction-typed-removal' }, requireArguments = true },
  ServerSalvage = { checklistIds = { 'transaction-salvage' }, requireArguments = true },
  ServerSetAbilityDA = { checklistIds = { 'transaction-official-equipment-rpc' }, requireArguments = true },
  ServerSetMeleeDA = { checklistIds = { 'transaction-official-equipment-rpc' }, requireArguments = true },
  ServerSetWeaponDA = { checklistIds = { 'transaction-official-equipment-rpc' }, requireArguments = true },
  ServerUpgradeTotemPurchase = { checklistIds = { 'transaction-upgrade-totem' }, requireArguments = true }
}

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function clampInteger(value, fallback, minimum, maximum)
  local numberValue = math.floor(tonumber(value) or fallback)
  if numberValue < minimum then numberValue = minimum end
  if numberValue > maximum then numberValue = maximum end
  return numberValue
end

local function normalizeFieldName(value)
  local text = tostring(value or '')
  return text:match('([%w_]+)$') or text
end

local REVIEWED_ENGINE_HOOKS = {
  ['/Script/Engine.GameStateBase:OnRep_ReplicatedHasBegunPlay'] = true,
  ['/Script/Engine.Pawn:OnRep_PlayerState'] = true,
  ['/Script/Engine.PlayerState:OnRep_bIsInactive'] = true
}

local function validCatalogHookPath(path)
  if type(path) ~= 'string' or path == '' then return false end
  local allowedRoot = path:match('^/Script/CrabChampions%.') or path:match('^/Game/') or REVIEWED_ENGINE_HOOKS[path]
  return allowedRoot ~= nil and path:match(':[%w_]+$') ~= nil
end

local function validDiscoveredHookPath(path)
  if type(path) ~= 'string' or path == '' then return false end
  local allowedRoot = path:match('^/Script/CrabChampions%.') or path:match('^/Game/')
  return allowedRoot ~= nil and path:match(':[%w_]+$') ~= nil
end

local function globMatches(value, glob)
  local pattern = tostring(glob or '')
  pattern = pattern:gsub('([%^%$%(%)%%%.%[%]%+%-%?])', '%%%1')
  pattern = '^' .. pattern:gsub('%*', '.*') .. '$'
  return tostring(value or ''):match(pattern) ~= nil
end

local function matchesAny(value, patterns)
  for _, pattern in ipairs(patterns or {}) do
    if globMatches(value, pattern) then return true end
  end
  return false
end

local function exactFunctionPath(fullName)
  local text = tostring(fullName or '')
  return text:match('(/Script/[^%s]+:[%w_]+)$') or text:match('(/Game/[^%s]+:[%w_]+)$')
end

local function discoveredFunctionType(name)
  local text = tostring(name or '')
  if text:match('^OnRep_') then return 'OnRep' end
  if text:match('^Server') then return 'RPC' end
  if text:match('^Client') then return 'RPC' end
  if text:match('^Multicast') then return 'multicast' end
  return 'event'
end

local function getLocalPlayerState(safe)
  local controller, controllerErr = safe.findFirst('CrabPC')
  if controllerErr then return nil, controllerErr end
  if not safe.isValidObject(controller) then return nil, 'local_controller_unavailable' end
  return safe.getProperty(controller, 'PlayerState')
end

local function summarizeObject(safe, value)
  if not safe.isValidObject(value) then return nil end
  local identity = safe.redactedObjectSummary(value, true)
  identity.valueKind = 'object'
  return identity
end

local function readHealthField(safe, playerState, fieldName)
  local healthInfo, healthErr = safe.getProperty(playerState, 'HealthInfo')
  if healthErr then return nil, healthErr end
  if healthInfo == nil then return nil, 'health_info_nil' end
  return safe.getStructField(healthInfo, fieldName)
end

local function captureSnapshot(safe, requestedFields, playerState, scopeConfirmed)
  local snapshot = {}
  local report = { requestedCount = 0, readableCount = 0, errorCount = 0, deferredCount = 0, scopeConfirmed = scopeConfirmed == true }
  if not safe.isValidObject(playerState) then
    snapshot.PlayerState = { status = 'unavailable', error = 'scoped_playerstate_unavailable' }
    report.errorCount = 1
    return snapshot, report
  end

  for _, requested in ipairs(requestedFields or {}) do
    report.requestedCount = report.requestedCount + 1
    local fieldName = normalizeFieldName(requested)
    local classification = ALLOWED_SNAPSHOT_FIELDS[fieldName]
    if classification == nil then
      snapshot[fieldName] = { status = 'deferred', reason = 'not in passive scalar snapshot allowlist' }
      report.deferredCount = report.deferredCount + 1
    else
      local value, err
      if classification == 'health' then
        value, err = readHealthField(safe, playerState, fieldName)
      else
        value, err = safe.getProperty(playerState, fieldName)
      end
      if err then
        snapshot[fieldName] = { status = 'error', error = recordBuilder.cleanString(err, 120) }
        report.errorCount = report.errorCount + 1
      elseif value == nil then
        snapshot[fieldName] = { status = 'nil' }
      elseif classification == 'object' then
        snapshot[fieldName] = summarizeObject(safe, value) or { status = 'unsupported', valueKind = type(value) }
        if snapshot[fieldName].status == 'observed-redacted' then report.readableCount = report.readableCount + 1 end
      elseif type(value) == 'number' or type(value) == 'boolean' then
        snapshot[fieldName] = { status = 'read', valueKind = type(value), value = value }
        report.readableCount = report.readableCount + 1
      else
        snapshot[fieldName] = { status = 'unsupported', valueKind = type(value) }
      end
    end
  end
  return snapshot, report
end

local function snapshotChangedFields(preState, postState)
  local changed = {}
  for fieldName, before in pairs(preState or {}) do
    local after = (postState or {})[fieldName]
    if type(before) == 'table' and type(after) == 'table' then
      if before.status == 'read' and after.status == 'read' and before.value ~= after.value then
        changed[#changed + 1] = fieldName
      elseif before.status == 'observed-redacted' and after.status == 'observed-redacted'
        and tostring(before.pathFingerprint or before.fingerprint or '') ~= tostring(after.pathFingerprint or after.fingerprint or '') then
        changed[#changed + 1] = fieldName
      end
    end
  end
  table.sort(changed)
  return changed
end

local function listContains(values, wanted)
  for _, value in ipairs(values or {}) do
    if tostring(value) == tostring(wanted) then return true end
  end
  return false
end

function passiveHookManager.new(config, safe, evidenceWriter, state, catalog)
  local manager = {
    config = config or {},
    safe = safe,
    evidenceWriter = evidenceWriter,
    state = state,
    catalog = catalog or { hooks = {} },
    active = false,
    registeredCount = 0,
    registered = {},
    registrationAttempts = {},
    lastLifecycleRegistrationGeneration = nil,
    discoveryQueue = {},
    discoveryQueueIndex = 1,
    discoveredOwners = {},
    blueprintDescriptorIds = {},
    pending = {},
    pendingOverflow = {},
    invocationSequence = 0,
    callbackRowsAttempted = 0,
    callbackRowsByDescriptor = {},
    lastAcceptedInvocationAt = {},
    hookGlobalRowCap = clampInteger((config or {}).fullObserveHookGlobalRowCap, 2048, 64, 16384),
    hookPerDescriptorRowCap = clampInteger((config or {}).fullObserveHookPerDescriptorRowCap, 128, 8, 1024),
    hookMinIntervalSeconds = clampInteger((config or {}).fullObserveHookMinIntervalSeconds, 1, 1, 60)
  }
  manager.state.hookIo.limits = {
    globalRowCap = manager.hookGlobalRowCap,
    perDescriptorRowCap = manager.hookPerDescriptorRowCap,
    minimumInvocationIntervalSeconds = manager.hookMinIntervalSeconds,
    statusFlushPolicy = 'coordinator-heartbeat-only'
  }
  manager.knownHookPaths = {}
  for _, descriptor in ipairs(manager.catalog.hooks or {}) do
    manager.knownHookPaths[tostring(descriptor.hookPath or '')] = true
  end

  function manager:setActive(value)
    self.active = value == true
    if not self.active then self.pending = {}; self.pendingOverflow = {} end
  end

  function manager:descriptorId(descriptor)
    return tostring(descriptor.id or descriptor.symbolPath or descriptor.hookPath or 'unknown')
  end

  function manager:beginBoundedInvocation(descriptor)
    local descriptorId = self:descriptorId(descriptor)
    local now = os.time()
    local lastAccepted = self.lastAcceptedInvocationAt[descriptorId]
    if lastAccepted ~= nil and (now - lastAccepted) < self.hookMinIntervalSeconds then
      self.state:noteHookIo(descriptorId, 'coalescedInvocations', 1)
      return false, 'rate-coalesced'
    end
    if self.callbackRowsAttempted >= self.hookGlobalRowCap then
      self.state:noteHookIo(descriptorId, 'globalCapDrops', 1)
      return false, 'global-row-cap'
    end
    if (self.callbackRowsByDescriptor[descriptorId] or 0) >= self.hookPerDescriptorRowCap then
      self.state:noteHookIo(descriptorId, 'perDescriptorCapDrops', 1)
      return false, 'descriptor-row-cap'
    end
    self.lastAcceptedInvocationAt[descriptorId] = now
    return true, ''
  end

  function manager:claimCallbackRow(descriptor)
    local descriptorId = self:descriptorId(descriptor)
    if self.callbackRowsAttempted >= self.hookGlobalRowCap then
      self.state:noteHookIo(descriptorId, 'globalCapDrops', 1)
      return false
    end
    local descriptorRows = self.callbackRowsByDescriptor[descriptorId] or 0
    if descriptorRows >= self.hookPerDescriptorRowCap then
      self.state:noteHookIo(descriptorId, 'perDescriptorCapDrops', 1)
      return false
    end
    self.callbackRowsAttempted = self.callbackRowsAttempted + 1
    self.callbackRowsByDescriptor[descriptorId] = descriptorRows + 1
    return true
  end

  function manager:onLifecycleTransition()
    self.pending = {}
    self.pendingOverflow = {}
    for descriptorId, _ in pairs(self.blueprintDescriptorIds) do
      self.registered[descriptorId] = nil
    end
  end

  function manager:pendingKey(descriptor, contextSummary)
    return tostring(descriptor.id or descriptor.symbolPath or descriptor.hookPath)
      .. '|' .. tostring(contextSummary and contextSummary.fingerprint or 'unknown')
  end

  function manager:contextSummary(contextParam)
    local contextObj, contextErr = self.safe.resolveHookContext(contextParam)
    if contextErr then
      return nil, { status = 'error', error = recordBuilder.cleanString(contextErr, 120), fingerprint = 'unknown' }
    end
    local identity = self.safe.redactedObjectSummary(contextObj, false)
    return contextObj, {
      status = 'observed-redacted',
      className = tostring(identity.className or ''),
      pathSummary = tostring(identity.pathSummary or '<redacted-instance>'),
      fingerprint = tostring(identity.pathFingerprint or '')
    }
  end

  function manager:resolvePlayerStateScope(contextObj)
    if self.safe.isValidObject(contextObj) then
      local contextClass = self.safe.getObjectClassName(contextObj)
      if tostring(contextClass or '') == 'CrabPS' then
        local identity = self.safe.redactedObjectSummary(contextObj, false)
        return {
          playerState = contextObj,
          fingerprint = tostring(identity.pathFingerprint or ''),
          confirmed = true,
          source = 'hook-context-playerstate'
        }
      end
      for _, fieldName in ipairs({ 'OwningPS', 'PlayerState' }) do
        local candidate, candidateErr = self.safe.getProperty(contextObj, fieldName)
        if candidateErr == nil and self.safe.isValidObject(candidate) then
          local candidateClass = self.safe.getObjectClassName(candidate)
          if tostring(candidateClass or '') == 'CrabPS' then
            local identity = self.safe.redactedObjectSummary(candidate, false)
            return {
              playerState = candidate,
              fingerprint = tostring(identity.pathFingerprint or ''),
              confirmed = true,
              source = 'curated-context-property:' .. fieldName
            }
          end
        end
      end
    end
    local localPlayerState = getLocalPlayerState(self.safe)
    if self.safe.isValidObject(localPlayerState) then
      local identity = self.safe.redactedObjectSummary(localPlayerState, false)
      return {
        playerState = localPlayerState,
        fingerprint = tostring(identity.pathFingerprint or ''),
        confirmed = false,
        source = 'local-playerstate-fallback-candidate'
      }
    end
    return { playerState = nil, fingerprint = '', confirmed = false, source = 'unresolved' }
  end

  function manager:argumentsFullyObserved(arguments)
    if #arguments == 0 then return false end
    for _, argument in ipairs(arguments) do
      local status = tostring(argument.status or '')
      if status ~= 'observed' and status ~= 'observed-redacted' then return false end
    end
    return true
  end

  function manager:naturalQualification(descriptor, phase, scope, arguments, scopedReadable, stateChangeObserved)
    if phase ~= 'post' or scope.confirmed ~= true then return {}, 'post callback with confirmed scoped PlayerState required' end
    local functionName = tostring(descriptor.hookPath or ''):match(':([%w_]+)$') or ''
    local rule = EXACT_NATURAL_OBSERVATION_RULES[functionName]
    local linked = {}
    for _, checklistId in ipairs(descriptor.checklistLinks or {}) do
      linked[tostring(checklistId)] = true
    end
    local qualifying = {}
    local reasons = {}
    if rule ~= nil then
      local ruleSatisfied = true
      if rule.requireArguments and not self:argumentsFullyObserved(arguments) then
        ruleSatisfied = false
        reasons[#reasons + 1] = 'required arguments were not safely observed'
      end
      if rule.requireReadableScopedState and scopedReadable ~= true then
        ruleSatisfied = false
        reasons[#reasons + 1] = 'readable scoped state was not captured'
      end
      if rule.requireStateChange and stateChangeObserved ~= true then
        ruleSatisfied = false
        reasons[#reasons + 1] = 'scoped state change was not observed'
      end
      if ruleSatisfied then
        for _, checklistId in ipairs(rule.checklistIds or {}) do qualifying[#qualifying + 1] = checklistId end
      end
    end
    local accessKind = tostring(descriptor.type or '')
    if linked['official-apply-candidates-observed']
      and (accessKind == 'RPC' or accessKind == 'multicast') then
      qualifying[#qualifying + 1] = 'official-apply-candidates-observed'
    end
    if #qualifying == 0 then
      return {}, #reasons > 0 and table.concat(reasons, '; ') or 'descriptor links require separate evidence beyond callback presence'
    end
    return qualifying, 'exact reviewed natural observation qualified; broader state/safety claims remain partial'
  end

  function manager:argumentSummaries(descriptor, ...)
    local summaries = {}
    local schema = descriptor.argumentSchema or {}
    for index, spec in ipairs(schema) do
      local param = select(index, ...)
      local propertyType = tostring(spec.propertyType or '')
      local allowShapeCount = false
      for category, stageState in pairs(self.state.inventoryStages or {}) do
        local token = tostring(category):gsub('s$', '')
        if propertyType:find(token) and tonumber(stageState.stage or 0) >= 8 then allowShapeCount = true end
      end
      summaries[#summaries + 1] = self.safe.summarizeHookArgument(param, spec, { allowShapeCount = allowShapeCount })
    end
    return summaries
  end

  function manager:emitHookError(descriptor, phase, err)
    local descriptorId = self:descriptorId(descriptor)
    self.state:noteHookIo(descriptorId, 'callbackErrors', 1)
    if not self:claimCallbackRow(descriptor) then
      self.state:tripCircuit(descriptor.category or 'unknown', err, 'open')
      return
    end
    local base = recordBuilder.fullObserveBase(self.config, self.state, 'PassiveHook.Error')
    local row = recordBuilder.merge(base, {
      timestamp = utcNow(),
      sequence = self.state:nextSequence(),
      category = tostring(descriptor.category or 'unknown'),
      symbol = tostring(descriptor.symbolPath or ''),
      hookPath = tostring(descriptor.hookPath or ''),
      hookPhase = phase,
      result = 'lua_error',
      runtimeStatus = 'PASSIVE_CALLBACK_ERROR',
      error = recordBuilder.cleanString(err, 256),
      safetyClassification = 'passive-observation-only',
      naturalObservationStatus = 'callback-error',
      checklistLinks = descriptor.checklistLinks or {}
    })
    local writeOk = self.evidenceWriter:writeEvidence(row)
    self.state:noteWriteResult(writeOk)
    if writeOk then self.state:noteHookIo(descriptorId, 'evidenceRowsWritten', 1) end
    self.state:tripCircuit(descriptor.category or 'unknown', err, 'open')
  end

  function manager:emitDiscovery(descriptor, status, details)
    local base = recordBuilder.fullObserveBase(self.config, self.state, 'RuntimeDiscovery.Function')
    local row = recordBuilder.merge(base, {
      timestamp = utcNow(),
      sequence = self.state:nextSequence(),
      category = tostring(descriptor.category or 'runtime-discovery'),
      symbol = tostring(descriptor.symbolPath or ''),
      hookPath = tostring(descriptor.hookPath or ''),
      result = status == 'confirmed' and 'ok' or (status == 'needs-coverage' and 'partial' or 'unsupported'),
      runtimeStatus = status == 'confirmed' and 'RUNTIME_DISCOVERED' or (status == 'needs-coverage' and 'DISCOVERED_NEEDS_COVERAGE' or 'UNSUPPORTED'),
      discoveryStatus = status,
      discoveryDetails = details or {},
      safetyClassification = 'passive-reflection-only',
      noArbitraryUObjectCrawl = true,
      candidateClassCapped = true,
      passiveOnly = true,
      runtimeInitiated = false,
      writeApplyStatus = 'write-safety-not-proven'
    })
    local writeOk = self.evidenceWriter:writeEvidence(row)
    self.state:noteWriteResult(writeOk)
  end

  function manager:emitRegistration(descriptor, status, err, attemptKey)
    local base = recordBuilder.fullObserveBase(self.config, self.state, 'PassiveHook.Registration')
    local row = recordBuilder.merge(base, {
      timestamp = utcNow(),
      sequence = self.state:nextSequence(),
      category = tostring(descriptor.category or ''),
      symbol = tostring(descriptor.symbolPath or ''),
      hookPath = tostring(descriptor.hookPath or ''),
      result = status == 'registered' and 'ok' or (status:find('pending') and 'partial' or 'unsupported'),
      runtimeStatus = status == 'registered' and 'HOOK_REGISTERED' or (status:find('pending') and 'PENDING' or 'UNSUPPORTED'),
      hookRegistrationStatus = status,
      naturalObservationStatus = status == 'registered' and 'hook-registered' or 'not-registered',
      error = recordBuilder.cleanString(err or '', 256),
      registrationAttempt = tostring(attemptKey or ''),
      safetyClassification = 'passive-observation-only',
      checklistLinks = descriptor.checklistLinks or {},
      qualifyingChecklistLinks = {},
      runtimeInitiated = false,
      passiveOnly = true,
      writeApplyStatus = 'write-safety-not-proven'
    })
    local writeOk = self.evidenceWriter:writeEvidence(row)
    self.state:noteWriteResult(writeOk)
  end

  function manager:handleHook(descriptor, phase, contextParam, ...)
    if not self.active or self.state.stopRequested then return end
    if not self.state:circuitAllows(descriptor.category) then return end

    local descriptorId = self:descriptorId(descriptor)
    self.state:noteHookIo(descriptorId, 'callbackCount', 1)
    local contextObj, contextSummary = self:contextSummary(contextParam)
    local scope = { playerState = nil, fingerprint = '', confirmed = false, source = 'not-resolved-suppressed' }
    local key = self:pendingKey(descriptor, contextSummary)
    local preState = nil
    local preReport = nil
    local invocationId = nil
    local pendingEntry = nil
    if phase == 'pre' then
      local stack = self.pending[key] or {}
      if #stack >= 16 then
        self.pendingOverflow[key] = (self.pendingOverflow[key] or 0) + 1
        self.state:noteHookIo(descriptorId, 'coalescedInvocations', 1)
        return
      end
      self.invocationSequence = self.invocationSequence + 1
      invocationId = tostring(self.state.sessionId) .. ':' .. tostring(self.invocationSequence)
      local invocationAllowed, suppressionReason = self:beginBoundedInvocation(descriptor)
      if invocationAllowed then
        scope = self:resolvePlayerStateScope(contextObj)
        preState, preReport = captureSnapshot(self.safe, descriptor.preStateFields, scope.playerState, scope.confirmed)
      end
      stack[#stack + 1] = {
        invocationId = invocationId,
        preState = preState,
        preReport = preReport,
        scopeFingerprint = scope.fingerprint,
        scopeConfirmed = scope.confirmed,
        suppressed = not invocationAllowed,
        suppressionReason = suppressionReason
      }
      self.pending[key] = stack
      if not invocationAllowed then return end
    else
      if (self.pendingOverflow[key] or 0) > 0 then
        self.pendingOverflow[key] = self.pendingOverflow[key] - 1
        if self.pendingOverflow[key] <= 0 then self.pendingOverflow[key] = nil end
        return
      end
      local stack = self.pending[key] or {}
      pendingEntry = stack[#stack]
      if pendingEntry then
        stack[#stack] = nil
        preState = pendingEntry.preState
        preReport = pendingEntry.preReport
        invocationId = pendingEntry.invocationId
      else
        local invocationAllowed = self:beginBoundedInvocation(descriptor)
        if not invocationAllowed then return end
        self.invocationSequence = self.invocationSequence + 1
        invocationId = tostring(self.state.sessionId) .. ':post:' .. tostring(self.invocationSequence)
      end
      if #stack == 0 then self.pending[key] = nil else self.pending[key] = stack end
      if pendingEntry and pendingEntry.suppressed == true then return end
    end

    if phase == 'post' then scope = self:resolvePlayerStateScope(contextObj) end

    local authorityObject = scope.confirmed and scope.playerState or contextObj
    local authorityStatus = self.safe.authorityStatus(authorityObject)
    if authorityStatus ~= 'unknown' then self.state.authorityStatus = authorityStatus end
    local arguments = self:argumentSummaries(descriptor, ...)
    local postState, postReport = nil, nil
    if phase == 'post' then
      postState, postReport = captureSnapshot(self.safe, descriptor.postStateFields, scope.playerState, scope.confirmed)
    end
    local scopeMatches = pendingEntry ~= nil and pendingEntry.scopeConfirmed == true and scope.confirmed == true
      and tostring(pendingEntry.scopeFingerprint or '') ~= ''
      and tostring(pendingEntry.scopeFingerprint or '') == tostring(scope.fingerprint or '')
    local prePostCorrelated = phase == 'post' and scopeMatches
      and preReport ~= nil and postReport ~= nil
      and tonumber(preReport.readableCount or 0) > 0 and tonumber(postReport.readableCount or 0) > 0
    local changedFields = prePostCorrelated and snapshotChangedFields(preState, postState) or {}
    local scopedReadable = scope.confirmed == true and postReport ~= nil and tonumber(postReport.readableCount or 0) > 0
    local qualifyingChecklistLinks, qualificationReason = self:naturalQualification(
      descriptor, phase, scope, arguments, scopedReadable, #changedFields > 0)
    local visibilityDirection = 'unresolved'
    if scope.confirmed and scope.fingerprint ~= '' then
      if scope.fingerprint == tostring(self.state.localPlayerStateFingerprint or '') then
        visibilityDirection = 'local-scoped-observation'
      else
        visibilityDirection = 'remote-playerstate-candidate-observed'
      end
    elseif scope.source == 'local-playerstate-fallback-candidate' then
      visibilityDirection = 'local-fallback-candidate'
    end
    if not self:claimCallbackRow(descriptor) then return end
    local base = recordBuilder.fullObserveBase(self.config, self.state, 'PassiveHook.Observed')
    local row = recordBuilder.merge(base, {
      timestamp = utcNow(),
      sequence = self.state:nextSequence(),
      category = tostring(descriptor.category or ''),
      symbol = tostring(descriptor.symbolPath or ''),
      owner = tostring(descriptor.ownerPath or ''),
      member = tostring(descriptor.id or ''),
      accessMethod = 'RegisterHookPassiveCallback',
      accessKind = tostring(descriptor.type or 'event'),
      hookPath = tostring(descriptor.hookPath or ''),
      hookPhase = phase,
      invocationId = invocationId,
      callingObject = contextSummary,
      owningPlayerStateFingerprint = tostring(scope.fingerprint or ''),
      ownershipScope = tostring(scope.source or 'unresolved'),
      ownershipConfirmed = scope.confirmed == true,
      visibilityDirection = visibilityDirection,
      bidirectionalVisibilityProven = false,
      authorityStatus = authorityStatus,
      authorityStatusSource = scope.confirmed and 'scoped-playerstate' or 'hook-context-candidate',
      arguments = arguments,
      argumentMetadataStatus = #arguments > 0 and 'observed-redacted' or 'none',
      preState = preState,
      postState = postState,
      result = 'ok',
      runtimeStatus = 'NATURALLY_OBSERVED',
      naturalObservationStatus = prePostCorrelated and 'scoped-readable-pre-post-observed'
        or (scope.confirmed and 'scoped-callback-observed' or 'callback-observed-with-unconfirmed-local-fallback'),
      prePostCorrelated = prePostCorrelated,
      prePostCorrelationReason = prePostCorrelated and 'same confirmed PlayerState scope with readable pre/post state'
        or 'callback timing alone is not scoped readable state correlation',
      changedFields = changedFields,
      stateChangeObserved = #changedFields > 0,
      safetyClassification = tostring(descriptor.safetyClassification or 'passive-observation-only'),
      checklistLinks = descriptor.checklistLinks or {},
      qualifyingChecklistLinks = qualifyingChecklistLinks,
      naturalObservationQualification = qualificationReason,
      officialApplyObservationOnly = listContains(qualifyingChecklistLinks, 'official-apply-candidates-observed'),
      officialApplySafetyProven = false,
      source = 'runtime-evidence',
      writeApplyStatus = 'write-safety-not-proven',
      runtimeInitiated = false,
      passiveOnly = true
    })
    local writeOk = self.evidenceWriter:writeEvidence(row)
    self.state:noteWriteResult(writeOk)
    if writeOk then self.state:noteHookIo(descriptorId, 'evidenceRowsWritten', 1) end
    self.state:observeEvidence(row, { deferStatusFlush = true })
  end

  function manager:guardedCallback(descriptor, phase, contextParam, ...)
    local args = { n = select('#', ...), ... }
    local ok, err = pcall(function()
      self:handleHook(descriptor, phase, contextParam, unpackArgs(args, 1, args.n))
    end)
    if not ok then self:emitHookError(descriptor, phase, err) end
  end

  function manager:registerDescriptor(descriptor, attemptKey)
    local descriptorId = tostring(descriptor.id or descriptor.symbolPath or descriptor.hookPath)
    if self.registered[descriptorId] then return true end
    self.registrationAttempts[descriptorId] = self.registrationAttempts[descriptorId] or {}
    attemptKey = tostring(attemptKey or 'boot')
    if self.registrationAttempts[descriptorId][attemptKey] then return false end
    self.registrationAttempts[descriptorId][attemptKey] = true
    if descriptor.safetyClassification ~= 'passive-observation-only' then
      self.state:markHookRegistration(descriptor, 'rejected-unsafe', 'catalog safety classification is not passive-observation-only')
      self:emitRegistration(descriptor, 'rejected-unsafe', 'catalog safety classification is not passive-observation-only', attemptKey)
      return false
    end
    if not validCatalogHookPath(descriptor.hookPath) then
      self.state:markHookRegistration(descriptor, 'unsupported', 'missing or invalid exact hook path')
      self:emitRegistration(descriptor, 'unsupported', 'missing or invalid exact hook path', attemptKey)
      return false
    end
    if type(RegisterHook) ~= 'function' then
      self.state:markHookRegistration(descriptor, 'unsupported', 'RegisterHook unavailable')
      self:emitRegistration(descriptor, 'unsupported', 'RegisterHook unavailable', attemptKey)
      return false
    end

    local isBlueprint = tostring(descriptor.hookPath):match('^/Game/') ~= nil
    if isBlueprint then self.blueprintDescriptorIds[descriptorId] = true end
    local ok, firstId, secondId
    if isBlueprint then
      ok, firstId, secondId = pcall(function()
        return RegisterHook(descriptor.hookPath, function(contextParam, ...)
          self:guardedCallback(descriptor, 'post', contextParam, ...)
        end)
      end)
    else
      ok, firstId, secondId = pcall(function()
        return RegisterHook(descriptor.hookPath,
          function(contextParam, ...)
            self:guardedCallback(descriptor, 'pre', contextParam, ...)
          end,
          function(contextParam, ...)
            self:guardedCallback(descriptor, 'post', contextParam, ...)
          end)
      end)
    end
    if not ok then
      local status = tostring(descriptor.hookPath):match('^/Game/') and 'pending-next-lifecycle' or 'unsupported'
      self.state:markHookRegistration(descriptor, status, firstId)
      self:emitRegistration(descriptor, status, firstId, attemptKey)
      return false
    end
    self.registeredCount = self.registeredCount + 1
    self.registered[descriptorId] = true
    self.state:markHookRegistration(descriptor, 'registered', '')
    self:emitRegistration(descriptor, 'registered', '', attemptKey)
    return firstId ~= nil or secondId ~= nil or true
  end

  function manager:registerAll()
    if self.config.allowPassiveObservationHooks ~= true then return 0 end
    self.active = true
    for _, descriptor in ipairs(self.catalog.hooks or {}) do
      if tostring(descriptor.hookPath or ''):match('^/Game/') then
        self.state:markHookRegistration(descriptor, 'pending-stable-lifecycle', 'Blueprint UFunction registration waits for the exact path to load')
        self:emitRegistration(descriptor, 'pending-stable-lifecycle', 'Blueprint UFunction registration waits for the exact path to load', 'startup')
      else
        self:registerDescriptor(descriptor, 'boot')
      end
    end
    crpLog.line('[CrabRuntimeProbe] passive hooks registered=' .. tostring(self.registeredCount))
    self.state:flushStatus('hook-registration')
    return self.registeredCount
  end


  function manager:descriptorClassName(descriptor)
    local hookPath = tostring(descriptor.hookPath or '')
    local native = hookPath:match('^/Script/CrabChampions%.([%w_]+):')
    if native then return native end
    local blueprint = hookPath:match('%.([%w_]+):[%w_]+$')
    return blueprint or tostring(descriptor.ownerPath or ''):match('([%w_]+)$')
  end

  function manager:rootDiscoveryDescriptor(group)
    return {
      id = 'runtime-discovery-root-' .. tostring(group.className or 'unknown'):lower(),
      category = 'runtime-discovery',
      symbolPath = tostring(group.objectDumpPath or group.className or ''),
      hookPath = ''
    }
  end

  function manager:runtimeDiscoverOwner(group)
    local className = group.className
    local descriptors = group.descriptors or {}
    local functionCap = clampInteger(
      self.catalog.discoveryRules and self.catalog.discoveryRules.maximumFunctionsPerResolvedClass,
      128, 1, 128)
    local instance, instanceErr = self.safe.findFirst(className)
    if instanceErr or not self.safe.isValidObject(instance) then
      for _, descriptor in ipairs(descriptors) do
        self:emitDiscovery(descriptor, 'needs-coverage', { reason = 'catalog-approved class instance is not loaded in this lifecycle', className = className, error = instanceErr })
        if tostring(descriptor.hookPath or ''):match('^/Game/') then
          self.state:markHookRegistration(descriptor, 'pending-next-lifecycle', 'catalog-approved Blueprint class is not loaded')
        end
      end
      if group.catalogApprovedExactRoot == true then
        self:emitDiscovery(self:rootDiscoveryDescriptor(group), 'needs-coverage', {
          reason = 'exact catalog class root is not loaded in this lifecycle',
          className = className,
          objectDumpPath = group.objectDumpPath,
          nextRequiredObservation = 'Enter gameplay that loads this exact class root.'
        })
      end
      return
    end
    local classObject, classErr = self.safe.getClass(instance)
    if classErr or not self.safe.isValidObject(classObject) then
      for _, descriptor in ipairs(descriptors) do
        self:emitDiscovery(descriptor, 'needs-coverage', { reason = 'class reflection object unavailable in this lifecycle', className = className, error = classErr })
      end
      if group.catalogApprovedExactRoot == true then
        self:emitDiscovery(self:rootDiscoveryDescriptor(group), 'needs-coverage', {
          reason = 'exact catalog class root loaded but its reflection object is unavailable',
          className = className,
          objectDumpPath = group.objectDumpPath,
          error = classErr
        })
      end
      return
    end
    local forEachFunction, methodErr = self.safe.getDirectField(classObject, 'ForEachFunction')
    local foundNames = {}
    local visited = 0
    local hitFunctionCap = false
    local reflectionOk = type(forEachFunction) == 'function'
    local iterationErr = methodErr
    if reflectionOk then
      reflectionOk, iterationErr = pcall(function()
        classObject:ForEachFunction(function(functionObject)
          if visited >= functionCap then
            hitFunctionCap = true
            return true
          end
          visited = visited + 1
          if self.safe.isValidObject(functionObject) then
            local functionName = self.safe.getName(functionObject)
            local functionFullName = self.safe.getFullName(functionObject)
            if functionName and functionName ~= '' then
              foundNames[tostring(functionName)] = exactFunctionPath(functionFullName) or false
            end
          end
          return false
        end)
      end)
    end
    for _, descriptor in ipairs(descriptors) do
      local wantedName = tostring(descriptor.hookPath or ''):match(':([%w_]+)$') or ''
      local functionFound = false
      if not reflectionOk then
        self:emitDiscovery(descriptor, 'needs-coverage', { reason = 'ForEachFunction reflection unavailable in this lifecycle', className = className, error = iterationErr, cap = functionCap })
      elseif foundNames[wantedName] ~= nil then
        functionFound = true
        self:emitDiscovery(descriptor, 'confirmed', { className = className, functionName = wantedName, visited = visited, cap = functionCap })
      elseif hitFunctionCap then
        self:emitDiscovery(descriptor, 'needs-coverage', { reason = 'function was not found before the reviewed reflection cap', className = className, functionName = wantedName, visited = visited, cap = functionCap })
      else
        self:emitDiscovery(descriptor, 'unsupported', { reason = 'catalog-approved function not present in capped class reflection', className = className, functionName = wantedName, visited = visited, cap = functionCap })
      end
      if functionFound and tostring(descriptor.hookPath or ''):match('^/Game/') then
        self:registerDescriptor(descriptor, group.attemptKey)
      end
    end

    local rules = self.catalog.discoveryRules or {}
    if not reflectionOk and group.catalogApprovedExactRoot == true then
      self:emitDiscovery(self:rootDiscoveryDescriptor(group), 'needs-coverage', {
        className = className,
        objectDumpPath = group.objectDumpPath,
        error = iterationErr,
        reason = 'exact class root loaded but bounded function reflection was unavailable in this lifecycle'
      })
    end
    if reflectionOk then
      if group.catalogApprovedExactRoot == true then
        self:emitDiscovery(self:rootDiscoveryDescriptor(group), hitFunctionCap and 'needs-coverage' or 'confirmed', {
          className = className,
          objectDumpPath = group.objectDumpPath,
          visited = visited,
          cap = functionCap,
          reflectionCompleteWithinCap = not hitFunctionCap,
          reason = hitFunctionCap and 'exact class reflection reached the reviewed function cap' or 'exact class reflection completed within the reviewed cap'
        })
      end
      local discoveredNames = {}
      for name, _ in pairs(foundNames) do discoveredNames[#discoveredNames + 1] = name end
      table.sort(discoveredNames)
      for _, functionName in ipairs(discoveredNames) do
        local path = foundNames[functionName]
        local excluded = matchesAny(functionName, rules.excludedGeneratedFunctions or {})
        if group.catalogApprovedExactRoot == true and not excluded and validDiscoveredHookPath(path) and not self.knownHookPaths[path] then
          local descriptor = {
            id = 'runtime-discovered-' .. tostring(className):lower() .. '-' .. tostring(functionName):lower(),
            category = 'runtime-discovery',
            symbolPath = path,
            hookPath = path,
            ownerPath = tostring(group.objectDumpPath or className),
            type = discoveredFunctionType(functionName),
            argumentSchema = {},
            checklistLinks = {},
            safetyClassification = 'passive-observation-only',
            preStateFields = {},
            postStateFields = {}
          }
          self.knownHookPaths[path] = true
          self:emitDiscovery(descriptor, 'needs-coverage', {
            className = className,
            functionName = functionName,
            exactResolvedPath = path,
            disposition = tostring(rules.newlyDiscoveredCandidatesDisposition or 'needs-coverage'),
            argumentMetadataStatus = 'unknown-runtime-discovery',
            hookRegistrationStatus = 'not-reviewed-not-hooked',
            nextRequiredObservation = 'Add an explicit reviewed catalog descriptor before passive hook registration.'
          })
        end
      end
    end
    if group.isBlueprint ~= true then
      self.discoveredOwners[className] = true
    end
  end

  function manager:prepareDiscoveryQueue(lifecycleGeneration)
    local groups = {}
    local order = {}
    local attemptKey = 'lifecycle-' .. tostring(lifecycleGeneration)
    for _, descriptor in ipairs(self.catalog.hooks or {}) do
      local className = self:descriptorClassName(descriptor)
      if className == nil or className == '' then
        self:emitDiscovery(descriptor, 'unsupported', { reason = 'catalog descriptor has no exact owner class' })
      else
        local isBlueprint = tostring(descriptor.hookPath or ''):match('^/Game/') ~= nil
        if isBlueprint or not self.discoveredOwners[className] then
          if groups[className] == nil then
            groups[className] = { className = className, descriptors = {}, attemptKey = attemptKey, isBlueprint = isBlueprint }
            order[#order + 1] = className
          end
          groups[className].descriptors[#groups[className].descriptors + 1] = descriptor
          if isBlueprint then groups[className].isBlueprint = true end
        end
      end
    end

    local rules = self.catalog.discoveryRules or {}
    local function addExactRoots(roots, isBlueprint)
      for _, root in ipairs(roots or {}) do
        local className = tostring(root.shortName or '')
        local objectDumpPath = tostring(root.objectDumpPath or '')
        if className ~= '' and objectDumpPath ~= '' then
          if groups[className] == nil then
            groups[className] = {
              className = className,
              objectDumpPath = objectDumpPath,
              descriptors = {},
              attemptKey = attemptKey,
              isBlueprint = isBlueprint,
              discoveryOnly = true,
              catalogApprovedExactRoot = true
            }
            order[#order + 1] = className
          else
            groups[className].objectDumpPath = objectDumpPath
            groups[className].catalogApprovedExactRoot = true
            if isBlueprint then groups[className].isBlueprint = true end
          end
        end
      end
    end
    addExactRoots(rules.nativeClassRoots, false)
    addExactRoots(rules.blueprintClassRoots, true)

    table.sort(order)
    self.discoveryQueue = {}
    local maximumClasses = tonumber(rules.maximumResolvedClassesPerGeneration) or 128
    if maximumClasses < 1 then maximumClasses = 1 end
    if maximumClasses > 128 then maximumClasses = 128 end
    for _, className in ipairs(order) do
      if #self.discoveryQueue < maximumClasses then
        self.discoveryQueue[#self.discoveryQueue + 1] = groups[className]
      else
        local group = groups[className]
        self:emitDiscovery({
          id = 'runtime-discovery-root-cap-' .. tostring(className):lower(),
          category = 'runtime-discovery',
          symbolPath = tostring(group.objectDumpPath or className),
          hookPath = ''
        }, 'needs-coverage', {
          reason = 'catalog exact class root deferred by per-generation cap',
          className = className,
          cap = maximumClasses
        })
      end
    end
    self.discoveryQueueIndex = 1
  end

  function manager:onStableLifecycle(lifecycleGeneration)
    if not self.active or self.config.allowFullObserveRuntimeDiscovery ~= true then return end
    if self.lastLifecycleRegistrationGeneration == lifecycleGeneration then return end
    self.lastLifecycleRegistrationGeneration = lifecycleGeneration
    self:prepareDiscoveryQueue(lifecycleGeneration)
    self.state:flushStatus('stable-lifecycle-discovery-queued')
  end

  function manager:onStableTick()
    if not self.active or self.config.allowFullObserveRuntimeDiscovery ~= true then return end
    local group = self.discoveryQueue[self.discoveryQueueIndex]
    if not group then return end
    self.discoveryQueueIndex = self.discoveryQueueIndex + 1
    local ok, err = pcall(function() self:runtimeDiscoverOwner(group) end)
    if not ok then
      for _, descriptor in ipairs(group.descriptors or {}) do
        self:emitDiscovery(descriptor, 'unsupported', { reason = 'paced catalog-approved reflection error', className = group.className, error = err })
      end
      self.state:tripCircuit('runtime-discovery:' .. tostring(group.className), err, 'unsupported')
    else
      self.state:flushStatus('runtime-discovery-class')
    end
  end

  return manager
end

return passiveHookManager
