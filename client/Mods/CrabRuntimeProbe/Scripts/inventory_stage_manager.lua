local recordBuilder = require('record_builder')

local inventoryStageManager = {}

local STAGES = {
  'wrapper-shape',
  'count-metadata',
  'first-element',
  'item-da-identity',
  'inventoryinfo-parent',
  'level-accumulated-buff',
  'enhancements-shape',
  'enhancements-count',
  'enhancements-values',
  'capped-local-iteration',
  'duplicate-semantics',
  'slot-index-stability',
  'joined-client-repeat',
  'remote-visibility'
}

local CATEGORY_DEFINITIONS = {
  WeaponMods = { daField = 'WeaponModDA', slotField = 'NumWeaponModSlots', checklistPrefix = 'inventory-weapon-mod' },
  AbilityMods = { daField = 'AbilityModDA', slotField = 'NumAbilityModSlots', checklistPrefix = 'inventory-ability-mod' },
  MeleeMods = { daField = 'MeleeModDA', slotField = 'NumMeleeModSlots', checklistPrefix = 'inventory-melee-mod' },
  Perks = { daField = 'PerkDA', slotField = 'NumPerkSlots', checklistPrefix = 'inventory-perk' },
  Relics = { daField = 'RelicDA', slotField = '', checklistPrefix = 'inventory-relic' }
}

local CATEGORY_ORDER = { 'WeaponMods', 'AbilityMods', 'MeleeMods', 'Perks', 'Relics' }

local RESUME_KEYS = {
  WeaponMods = 'resumeWeaponModsStage',
  AbilityMods = 'resumeAbilityModsStage',
  MeleeMods = 'resumeMeleeModsStage',
  Perks = 'resumePerksStage',
  Relics = 'resumeRelicsStage'
}

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function clamp(value, fallback, minimum, maximum)
  local numberValue = tonumber(value) or fallback
  numberValue = math.floor(numberValue)
  if numberValue < minimum then numberValue = minimum end
  if numberValue > maximum then numberValue = maximum end
  return numberValue
end

local PROGRESS_PATH = 'Mods/CrabRuntimeProbe/Scripts/results/full_observe_progress.txt'

local function flatValue(value)
  return tostring(value or ''):gsub('[\r\n=]+', '_')
end

local function loadProgress(config)
  local file = io.open(PROGRESS_PATH, 'r')
  if not file then return {} end
  local values = {}
  for line in file:lines() do
    local key, value = line:match('^([%w%._%-]+)=(.*)$')
    if key then values[key] = value end
  end
  file:close()
  if values.schemaVersion ~= '1'
    or values.campaignId ~= flatValue(config.campaignId)
    or values.campaignGeneration ~= flatValue(tonumber(config.campaignGeneration) or 0)
    or values.machineId ~= flatValue(config.machineId)
    or values.lastSessionId ~= flatValue(config.campaignSessionId) then
    return {}
  end
  return values
end

local function getLocalPlayerState(safe)
  local controller, controllerErr = safe.findFirst('CrabPC')
  if controllerErr then return nil, controllerErr end
  if not safe.isValidObject(controller) then return nil, 'local_controller_unavailable' end
  local playerState, playerStateErr = safe.getProperty(controller, 'PlayerState')
  if playerStateErr then return nil, playerStateErr end
  if not safe.isValidObject(playerState) then return nil, 'local_playerstate_unavailable' end
  return playerState, nil
end

local function readArray(safe, fieldName)
  local playerState, playerStateErr = getLocalPlayerState(safe)
  if playerStateErr then return nil, nil, playerStateErr end
  local value, err = safe.getProperty(playerState, fieldName)
  return value, playerState, err
end

local function firstRawElement(safe, value)
  local count, countErr = safe.getArrayLength(value)
  if countErr then return nil, nil, countErr end
  if count == 0 then return nil, 0, nil end
  local element, elementErr = safe.getArrayIndex(value, 0)
  return element, count, elementErr
end

local function firstElementValue(safe, value)
  local rawElement, count, rawErr = firstRawElement(safe, value)
  if rawErr or count == 0 then return nil, count, rawErr end
  local element, unwrapErr = safe.unwrapKnownValue(rawElement)
  if unwrapErr then return nil, count, unwrapErr end
  return element, count, nil
end

local function readInventoryInfo(safe, arrayValue)
  local element, count, elementErr = firstElementValue(safe, arrayValue)
  if elementErr or count == 0 then return nil, count, elementErr end
  local info, infoErr = safe.getKnownField(element, 'InventoryInfo')
  return info, count, infoErr
end

local function identitySummary(safe, value)
  if value == nil then return nil, 'nil_identity' end
  if safe.isValidObject(value) then
    local identity = safe.redactedObjectSummary(value, true)
    identity.fingerprint = identity.pathFingerprint
    return identity, nil
  end
  local text = tostring(value)
  local fingerprint, length = safe.fingerprintValue(text)
  return { status = 'observed-redacted', valueKind = type(value), fingerprint = fingerprint, length = length }, nil
end

local function stageChecklistLinks(category, stage)
  local prefix = CATEGORY_DEFINITIONS[category].checklistPrefix
  local map = {
    -- Wrapper shape, raw first-element shape, and enhancement count are internal
    -- prerequisites. They remain in stage evidence but do not invent checklist IDs.
    [1] = {},
    [2] = { 'inventory-array-counts' },
    [3] = {},
    [4] = { prefix .. '-pickup', 'inventory-first-da-identity' },
    [5] = { 'inventory-info-parent' },
    [6] = { 'inventory-level', 'inventory-accumulated-buff' },
    [7] = { 'inventory-enhancements-shape' },
    [8] = {},
    [9] = { 'inventory-enhancements-values' },
    [10] = { 'inventory-capped-iteration' },
    [11] = { 'inventory-duplicate-semantics' },
    [12] = { 'inventory-order-index-stability' },
    [13] = { 'inventory-joined-client-reads' },
    [14] = { 'inventory-remote-visibility' }
  }
  return map[stage] or {}
end

local function copyList(values)
  local output = {}
  for index, value in ipairs(values or {}) do output[index] = value end
  return output
end

local function commonRelativeOrderStable(previous, current)
  local currentSet = {}
  for _, value in ipairs(current or {}) do currentSet[value] = true end
  local previousCommon = {}
  for _, value in ipairs(previous or {}) do
    if currentSet[value] then previousCommon[#previousCommon + 1] = value end
  end
  local previousSet = {}
  for _, value in ipairs(previous or {}) do previousSet[value] = true end
  local currentCommon = {}
  for _, value in ipairs(current or {}) do
    if previousSet[value] then currentCommon[#currentCommon + 1] = value end
  end
  return table.concat(previousCommon, '|') == table.concat(currentCommon, '|')
end

local function scalarMetadataToken(value)
  local kind = type(value)
  if kind == 'number' then
    if value ~= value or value == math.huge or value == -math.huge then return nil end
    return 'n:' .. tostring(value)
  end
  if kind == 'boolean' then return 'b:' .. tostring(value) end
  if kind == 'string' and #value <= 64 and value:match('^[%w_%-]+$') then return 's:' .. value end
  return nil
end

local function objectFingerprint(safe, value)
  if not safe.isValidObject(value) then return '' end
  local fullName = safe.getFullName(value)
  return safe.fingerprintValue(tostring(fullName or '') .. '|' .. tostring(value))
end

function inventoryStageManager.new(config, safe, evidenceWriter, state)
  local manager = {
    config = config or {},
    safe = safe,
    evidenceWriter = evidenceWriter,
    state = state,
    categories = {},
    lastSampleAt = nil,
    intervalSeconds = clamp((config or {}).fullObserveInventoryIntervalSeconds, 2, 1, 60),
    heartbeatSeconds = clamp((config or {}).fullObserveInventoryHeartbeatSeconds, 30, 5, 300),
    maxInventoryItems = clamp((config or {}).fullObserveMaxInventoryItems, 32, 1, 64),
    maxEnhancements = clamp((config or {}).fullObserveMaxEnhancements, 16, 1, 32),
    maxRowsPerCategory = clamp((config or {}).fullObserveMaxStageRowsPerCategory, 256, 16, 2048),
    cleanSamplesRequired = clamp((config or {}).fullObserveCleanSamplesRequired, 3, 3, 5),
    slotStabilityWindowSeconds = clamp((config or {}).fullObserveSlotStabilityWindowSeconds, 30, 10, 600),
    slotStabilitySamplesRequired = clamp((config or {}).fullObserveSlotStabilitySamplesRequired, 5, 3, 60),
    progress = loadProgress(config or {}),
    nextCategoryIndex = 1
  }

  for _, category in ipairs(CATEGORY_ORDER) do
    local resumeStage = clamp(manager.progress[category .. '.stage'] or (config or {})[RESUME_KEYS[category]], 1, 1, #STAGES)
    manager.categories[category] = {
      stage = resumeStage,
      completed = manager.progress[category .. '.completed'] == 'true',
      rowCount = 0,
      lastEvidenceFingerprint = '',
      lastEvidenceAt = nil,
      priorOrder = nil,
      stabilityStartedAt = nil,
      stabilitySampleCount = 0,
      pendingStabilityEvidence = nil,
      pendingStabilityOrderKey = nil,
      cleanStage = nil,
      cleanCount = 0,
      cleanLifecycleGeneration = nil,
      lifecycleGeneration = state.lifecycleGeneration
    }
    local restoredBreaker = manager.progress[category .. '.breaker']
    if restoredBreaker and restoredBreaker ~= '' and restoredBreaker ~= 'closed' then
      state.circuitBreakers['inventory:' .. category] = {
        state = restoredBreaker,
        reason = 'restored from matching campaign progress',
        openedAt = utcNow(),
        lifecycleGeneration = state.lifecycleGeneration
      }
    end
    local initialStatus = manager.categories[category].completed and 'confirmed' or 'blocked-by-prerequisite'
    state:setInventoryStage(category, resumeStage, STAGES[resumeStage], initialStatus,
      manager.categories[category].completed and 'restored complete' or 'waiting for clean prerequisite evidence')
  end

  function manager:resetTransient(lifecycleGeneration)
    for _, category in ipairs(CATEGORY_ORDER) do
      local categoryState = self.categories[category]
      categoryState.priorOrder = nil
      categoryState.stabilityStartedAt = nil
      categoryState.stabilitySampleCount = 0
      categoryState.pendingStabilityEvidence = nil
      categoryState.pendingStabilityOrderKey = nil
      categoryState.cleanStage = nil
      categoryState.cleanCount = 0
      categoryState.cleanLifecycleGeneration = nil
      categoryState.lifecycleGeneration = lifecycleGeneration
    end
  end


  function manager:persistProgress()
    local tempPath = PROGRESS_PATH .. '.' .. tostring(self.state.sessionId):gsub('[^%w_%-]', '_') .. '.tmp'
    local file = io.open(tempPath, 'w')
    if not file then
      self.state:noteWriteResult(false)
      return false
    end
    file:write('schemaVersion=1\n')
    file:write('campaignId=' .. flatValue(self.config.campaignId) .. '\n')
    file:write('campaignGeneration=' .. flatValue(tonumber(self.config.campaignGeneration) or 0) .. '\n')
    file:write('machineId=' .. flatValue(self.config.machineId) .. '\n')
    file:write('lastSessionId=' .. flatValue(self.state.sessionId) .. '\n')
    for _, category in ipairs(CATEGORY_ORDER) do
      local categoryState = self.categories[category]
      file:write(category .. '.stage=' .. tostring(categoryState.stage) .. '\n')
      file:write(category .. '.completed=' .. tostring(categoryState.completed == true) .. '\n')
      local breaker = self.state.circuitBreakers['inventory:' .. category]
      file:write(category .. '.breaker=' .. flatValue(breaker and breaker.state or 'closed') .. '\n')
    end
    file:close()
    os.remove(PROGRESS_PATH)
    local renamed = os.rename(tempPath, PROGRESS_PATH)
    if not renamed then
      os.remove(tempPath)
      self.state:noteWriteResult(false)
      return false
    end
    return true
  end

  function manager:resetClean(category)
    local categoryState = self.categories[category]
    categoryState.cleanStage = nil
    categoryState.cleanCount = 0
    categoryState.cleanLifecycleGeneration = nil
  end

  function manager:emit(category, stageNumber, status, reason, details, force)
    local categoryState = self.categories[category]
    details = details or {}
    if status ~= 'confirmed' and details.cleanPrerequisite ~= true then self:resetClean(category) end
    local now = os.time()
    local fingerprint = tostring(stageNumber) .. '|' .. tostring(status) .. '|' .. tostring(reason)
    if not force and categoryState.lastEvidenceFingerprint == fingerprint
      and categoryState.lastEvidenceAt ~= nil and (now - categoryState.lastEvidenceAt) < self.heartbeatSeconds then
      self.state:setInventoryStage(category, stageNumber, STAGES[stageNumber], status, reason)
      return
    end
    if categoryState.rowCount >= self.maxRowsPerCategory then
      self.state:tripCircuit('inventory:' .. category, 'inventory stage evidence row cap reached', 'overflow')
      return
    end
    categoryState.rowCount = categoryState.rowCount + 1
    categoryState.lastEvidenceFingerprint = fingerprint
    categoryState.lastEvidenceAt = now
    local links = stageChecklistLinks(category, stageNumber)
    local base = recordBuilder.fullObserveBase(self.config, self.state, 'Inventory.StageObservation')
    local row = recordBuilder.merge(base, {
      timestamp = utcNow(),
      sequence = self.state:nextSequence(),
      category = 'inventory:' .. category,
      symbol = 'CrabPS.' .. category,
      owner = 'CrabPS',
      member = category,
      accessMethod = 'AutomaticStagedRead',
      accessKind = 'inventory-stage',
      result = status == 'confirmed' and 'ok' or (status == 'unsupported' and 'unsupported' or (status == 'not-applicable' and 'not_applicable' or 'partial')),
      runtimeStatus = status == 'confirmed' and 'READ_OBSERVED' or (status == 'unsupported' and 'UNSUPPORTED' or (status == 'not-applicable' and 'NOT_APPLICABLE' or 'PARTIAL')),
      inventoryCategory = category,
      inventoryStage = stageNumber,
      inventoryStageName = STAGES[stageNumber],
      stageStatus = status,
      stageReason = reason,
      stageDetails = details,
      checklistLinks = links,
      qualifyingChecklistLinks = status == 'confirmed' and links or {},
      safetyClassification = 'read-only-staged',
      noWrites = true,
      noRpcs = true,
      noMutation = true,
      noHud = true,
      rawIdentityEvidence = false,
      maxInventoryItems = self.maxInventoryItems,
      maxEnhancements = self.maxEnhancements,
      lifecycleGeneration = self.state.lifecycleGeneration,
      writeApplyStatus = 'write-safety-not-proven'
    })
    local writeOk = self.evidenceWriter:writeEvidence(row)
    self.state:noteWriteResult(writeOk)
    self.state:setInventoryStage(category, stageNumber, STAGES[stageNumber], status, reason)
    if status == 'confirmed' then
      self.state:observeEvidence(row)
    else
      for _, checklistId in ipairs(links) do
        local checklistStatus = status == 'unsupported' and 'unsupported' or (status == 'not-applicable' and 'not-applicable' or 'partial')
        self.state:markChecklist(checklistId, checklistStatus, {
          reason = reason,
          nextInstruction = details and details.nextInstruction or ''
        })
      end
      self.state:flushStatus('inventory-stage')
    end
  end

  function manager:advance(category, details)
    local categoryState = self.categories[category]
    local current = categoryState.stage
    if categoryState.cleanStage ~= current or categoryState.cleanLifecycleGeneration ~= self.state.lifecycleGeneration then
      categoryState.cleanStage = current
      categoryState.cleanCount = 0
      categoryState.cleanLifecycleGeneration = self.state.lifecycleGeneration
    end
    categoryState.cleanCount = categoryState.cleanCount + 1
    if categoryState.cleanCount < self.cleanSamplesRequired then
      local pendingDetails = details or {}
      pendingDetails.cleanPrerequisite = true
      pendingDetails.cleanSampleCount = categoryState.cleanCount
      pendingDetails.cleanSamplesRequired = self.cleanSamplesRequired
      self:emit(category, current, 'partial', 'clean prerequisite sample ' .. tostring(categoryState.cleanCount) .. '/' .. tostring(self.cleanSamplesRequired), pendingDetails, true)
      return false
    end
    self:resetClean(category)
    self:emit(category, current, 'confirmed', 'clean prerequisite evidence captured', details, true)
    if current >= #STAGES then
      categoryState.completed = true
      self.state:setInventoryStage(category, current, STAGES[current], 'confirmed', 'all staged reads complete')
    else
      categoryState.stage = current + 1
      self.state:setInventoryStage(category, categoryState.stage, STAGES[categoryState.stage], 'blocked-by-prerequisite', 'waiting for next clean observation')
    end
    self:persistProgress()
    return true
  end

  function manager:unsupported(category, reason, details)
    local categoryState = self.categories[category]
    self:emit(category, categoryState.stage, 'unsupported', reason, details, true)
    self.state:tripCircuit('inventory:' .. category, reason, 'unsupported')
    self:persistProgress()
  end

  function manager:skipNotApplicable(category, reason, details)
    local categoryState = self.categories[category]
    local current = categoryState.stage
    self:emit(category, current, 'not-applicable', reason, details, true)
    if current < #STAGES then
      categoryState.stage = current + 1
      self.state:setInventoryStage(category, categoryState.stage, STAGES[categoryState.stage], 'blocked-by-prerequisite', 'waiting for next clean observation')
    else
      categoryState.completed = true
    end
    self:persistProgress()
  end

  function manager:readItemDAIdentity(category, arrayValue, index)
    local rawElement, rawErr = self.safe.getArrayIndex(arrayValue, index)
    if rawErr then return nil, rawErr end
    local element, unwrapErr = self.safe.unwrapKnownValue(rawElement)
    if unwrapErr then return nil, unwrapErr end
    local daValue, daErr = self.safe.getKnownField(element, CATEGORY_DEFINITIONS[category].daField)
    if daErr then return nil, daErr end
    return identitySummary(self.safe, daValue)
  end

  function manager:collectIdentities(category, arrayValue, cap)
    local count, countErr = self.safe.getArrayLength(arrayValue)
    if countErr then return nil, nil, countErr end
    local identities = {}
    local limit = math.min(count, cap)
    for offset = 0, limit - 1 do
      local identity, identityErr = self:readItemDAIdentity(category, arrayValue, offset)
      if identityErr then return nil, count, identityErr end
      identities[#identities + 1] = identity and (identity.fullName or identity.fingerprint or 'unknown') or 'nil'
    end
    return identities, count, nil
  end

  function manager:readItemRepresentation(category, arrayValue, index)
    local rawElement, rawErr = self.safe.getArrayIndex(arrayValue, index)
    if rawErr then return nil, rawErr end
    local element, unwrapErr = self.safe.unwrapKnownValue(rawElement)
    if unwrapErr then return nil, unwrapErr end
    local daValue, daErr = self.safe.getKnownField(element, CATEGORY_DEFINITIONS[category].daField)
    if daErr then return nil, daErr end
    local daIdentity, identityErr = identitySummary(self.safe, daValue)
    if identityErr or daIdentity == nil then return nil, identityErr or 'dataasset_identity_unavailable' end
    local inventoryInfo, infoErr = self.safe.getKnownField(element, 'InventoryInfo')
    if infoErr or inventoryInfo == nil then return nil, infoErr or 'inventory_info_unavailable' end
    local level, levelErr = self.safe.getKnownField(inventoryInfo, 'Level')
    local accumulatedBuff, buffErr = self.safe.getKnownField(inventoryInfo, 'AccumulatedBuff')
    local enhancements, enhancementsErr = self.safe.getKnownField(inventoryInfo, 'Enhancements')
    if levelErr or buffErr or enhancementsErr or enhancements == nil then
      return nil, levelErr or buffErr or enhancementsErr or 'metadata_unavailable'
    end
    local levelToken = scalarMetadataToken(level)
    local buffToken = scalarMetadataToken(accumulatedBuff)
    local enhancementCount, countErr = self.safe.getArrayLength(enhancements)
    if levelToken == nil or buffToken == nil or countErr then
      return nil, countErr or 'metadata_not_safely_representable'
    end
    local dataAssetFingerprint = tostring(daIdentity.fingerprint or daIdentity.pathFingerprint or '')
    if dataAssetFingerprint == '' then return nil, 'dataasset_fingerprint_unavailable' end
    local metadataFingerprint = self.safe.fingerprintValue(levelToken .. '|' .. buffToken .. '|e:' .. tostring(enhancementCount))
    return {
      arrayOffset = index,
      displaySlotIndex = index + 1,
      dataAssetFingerprint = dataAssetFingerprint,
      level = level,
      accumulatedBuff = accumulatedBuff,
      enhancementCount = enhancementCount,
      metadataFingerprint = metadataFingerprint,
      instanceIdentityProven = false,
      orderToken = dataAssetFingerprint .. ':' .. metadataFingerprint
    }, nil
  end

  function manager:collectRepresentations(category, arrayValue, cap)
    local count, countErr = self.safe.getArrayLength(arrayValue)
    if countErr then return nil, nil, countErr end
    local representations = {}
    local limit = math.min(count, cap)
    for offset = 0, limit - 1 do
      local representation, representationErr = self:readItemRepresentation(category, arrayValue, offset)
      if representationErr then return nil, count, representationErr end
      representations[#representations + 1] = representation
    end
    return representations, count, nil
  end

  function manager:runStage(category)
    local categoryState = self.categories[category]
    if categoryState.completed or not self.state:circuitAllows('inventory:' .. category) then return end
    local stage = categoryState.stage
    local arrayValue, playerState, arrayErr = readArray(self.safe, category)
    if arrayErr then
      self:emit(category, stage, 'partial', 'local inventory property unavailable', { error = arrayErr, nextInstruction = 'Enter a stable run with a valid local PlayerState.' })
      return
    end
    if arrayValue == nil then
      self:emit(category, stage, 'partial', 'inventory property returned nil', { nextInstruction = 'Enter a run and acquire an item in this category.' })
      return
    end

    if stage == 1 then
      local kind = type(arrayValue)
      if kind == 'userdata' or kind == 'table' then
        self:advance(category, { valueKind = kind, tostringKind = type(tostring(arrayValue)) })
      else
        self:unsupported(category, 'inventory wrapper has unsupported type', { valueKind = kind })
      end
      return
    end

    local count, countErr = self.safe.getArrayLength(arrayValue)
    if countErr then
      self:unsupported(category, 'official TArray length operation unsupported', { error = countErr })
      return
    end
    if stage == 2 then
      self:advance(category, { count = count, method = 'lua_len_operator_pcall', truncated = count > self.maxInventoryItems, cap = self.maxInventoryItems })
      return
    end
    if stage >= 3 and count == 0 then
      self:emit(category, stage, 'partial', 'no item available for this prerequisite', { count = 0, nextInstruction = 'Pick up an item in this inventory category.' })
      return
    end

    if stage == 3 then
      local rawElement, elementErr = self.safe.getArrayIndex(arrayValue, 0)
      if elementErr then
        self:unsupported(category, 'official numeric TArray index operation unsupported', { error = elementErr, index = 1 })
      else
        self:advance(category, { count = count, arrayOffset = 0, displaySlotIndex = 1, elementValueKind = type(rawElement), method = 'zero_based_numeric_index_pcall' })
      end
      return
    end

    if stage == 4 then
      local identity, identityErr = self:readItemDAIdentity(category, arrayValue, 0)
      if identityErr or identity == nil then
        self:unsupported(category, 'item DataAsset identity read unsupported', { error = identityErr or 'identity nil' })
      else
        self:advance(category, { dataAssetField = CATEGORY_DEFINITIONS[category].daField, identity = identity })
      end
      return
    end

    local inventoryInfo, _, infoErr = readInventoryInfo(self.safe, arrayValue)
    if infoErr or inventoryInfo == nil then
      self:unsupported(category, 'InventoryInfo parent read unsupported', { error = infoErr or 'InventoryInfo nil' })
      return
    end
    if stage == 5 then
      self:advance(category, { inventoryInfoValueKind = type(inventoryInfo) })
      return
    end

    local level, levelErr = self.safe.getKnownField(inventoryInfo, 'Level')
    local accumulatedBuff, buffErr = self.safe.getKnownField(inventoryInfo, 'AccumulatedBuff')
    if stage == 6 then
      if levelErr or buffErr or level == nil or accumulatedBuff == nil then
        self:emit(category, stage, 'partial', 'Level or AccumulatedBuff not yet readable', {
          levelStatus = levelErr or (level == nil and 'nil' or 'read'),
          accumulatedBuffStatus = buffErr or (accumulatedBuff == nil and 'nil' or 'read'),
          nextInstruction = 'Acquire or upgrade an item so its metadata is populated.'
        })
      else
        self:advance(category, { level = level, accumulatedBuff = accumulatedBuff })
      end
      return
    end

    local enhancements, enhancementsErr = self.safe.getKnownField(inventoryInfo, 'Enhancements')
    if enhancementsErr or enhancements == nil then
      self:emit(category, stage, 'partial', 'Enhancements value not yet readable', {
        error = enhancementsErr or 'nil',
        nextInstruction = 'Use an anvil or upgrade to produce enhancement metadata.'
      })
      return
    end
    if stage == 7 then
      self:advance(category, { enhancementsValueKind = type(enhancements), tostringKind = type(tostring(enhancements)) })
      return
    end

    local enhancementCount, enhancementCountErr = self.safe.getArrayLength(enhancements)
    if enhancementCountErr then
      self:unsupported(category, 'official enhancement TArray length operation unsupported', { error = enhancementCountErr })
      return
    end
    if stage == 8 then
      self:advance(category, { count = enhancementCount, method = 'lua_len_operator_pcall', truncated = enhancementCount > self.maxEnhancements, cap = self.maxEnhancements })
      return
    end
    if stage == 9 then
      if enhancementCount == 0 then
        self:emit(category, stage, 'partial', 'enhancement array is empty; no values observed', {
          count = 0,
          nextInstruction = 'Use an anvil so at least one enhancement value exists.'
        })
        return
      end
      local values = {}
      local unsupportedOffsets = {}
      local enhancementLimit = math.min(enhancementCount, self.maxEnhancements)
      for offset = 0, enhancementLimit - 1 do
        local value, valueErr = self.safe.getArrayIndex(enhancements, offset)
        if valueErr then
          self:unsupported(category, 'approved enhancement value index failed', { error = valueErr, arrayOffset = offset })
          return
        end
        local observedValue = value
        local kind = type(observedValue)
        if kind ~= 'number' and kind ~= 'boolean' and kind ~= 'string' then
          local unwrapped, unwrapErr = self.safe.unwrapKnownValue(value)
          if unwrapErr == nil then observedValue = unwrapped; kind = type(observedValue) end
        end
        local token = scalarMetadataToken(observedValue)
        if token ~= nil then
          values[#values + 1] = { arrayOffset = offset, displaySlotIndex = offset + 1, valueKind = kind, value = observedValue }
        else
          unsupportedOffsets[#unsupportedOffsets + 1] = { arrayOffset = offset, displaySlotIndex = offset + 1, valueKind = kind }
        end
      end
      if #unsupportedOffsets > 0 then
        self:emit(category, stage, 'partial', 'enhancement values include unsupported or redacted types', {
          count = enhancementCount,
          observedValues = values,
          unsupportedOffsets = unsupportedOffsets,
          nextInstruction = 'Collect a safely representable bounded enum/scalar value before confirming this stage.'
        })
      elseif enhancementCount > self.maxEnhancements then
        self:emit(category, stage, 'partial', 'capped enhancement subset read; values exceed reviewed cap', {
          count = enhancementCount,
          observedCount = enhancementLimit,
          values = values,
          cap = self.maxEnhancements,
          truncated = true,
          nextInstruction = 'Review and explicitly raise the enhancement cap before claiming complete value coverage.'
        })
      else
        self:advance(category, { count = enhancementCount, observedCount = enhancementLimit, values = values, cap = self.maxEnhancements, truncated = false })
      end
      return
    end

    local identities, identityCount, identitiesErr = self:collectIdentities(category, arrayValue, self.maxInventoryItems)
    if identitiesErr then
      self:unsupported(category, 'capped inventory identity iteration failed', { error = identitiesErr })
      return
    end
    if stage == 10 then
      if identityCount > self.maxInventoryItems then
        self:emit(category, stage, 'partial', 'capped subset read; full inventory exceeds reviewed cap', {
          count = identityCount,
          observedCount = #identities,
          identities = identities,
          cap = self.maxInventoryItems,
          truncated = true,
          nextInstruction = 'Review and explicitly raise the inventory cap before claiming full iteration.'
        })
      else
        self:advance(category, { count = identityCount, identities = identities, cap = self.maxInventoryItems, truncated = false })
      end
      return
    end
    if stage == 11 then
      local representations, representationCount, representationErr = self:collectRepresentations(category, arrayValue, self.maxInventoryItems)
      if representationErr then
        self:emit(category, stage, 'partial', 'duplicate DA candidates lack readable per-entry metadata representation', {
          error = representationErr,
          nextInstruction = 'Keep the duplicate items and populate readable Level, AccumulatedBuff, and enhancement count metadata.'
        })
        return
      end
      local groups = {}
      for _, representation in ipairs(representations) do
        local key = representation.dataAssetFingerprint
        groups[key] = groups[key] or {}
        groups[key][#groups[key] + 1] = representation
      end
      local duplicateGroups = {}
      for fingerprint, entries in pairs(groups) do
        if #entries >= 2 then
          duplicateGroups[#duplicateGroups + 1] = { dataAssetFingerprint = fingerprint, entries = entries }
        end
      end
      if #duplicateGroups == 0 then
        self:emit(category, stage, 'partial', 'no duplicate identity observed yet', {
          count = representationCount,
          nextInstruction = 'Pick up a second copy of the same item.'
        })
      else
        self:advance(category, {
          duplicateGroups = duplicateGroups,
          duplicateEvidenceBasis = 'same DA fingerprint at distinct array offsets with readable per-entry metadata representations',
          distinctInstanceIdentityProven = false
        })
      end
      return
    end
    if stage == 12 then
      local representations, representationCount, representationErr = self:collectRepresentations(category, arrayValue, self.maxInventoryItems)
      if representationErr then
        self:emit(category, stage, 'partial', 'slot/index evidence lacks readable per-entry metadata representation', {
          error = representationErr,
          nextInstruction = 'Keep item metadata readable before evaluating ordering.'
        })
        return
      end
      local representationCounts = {}
      local orderTokens = {}
      for _, representation in ipairs(representations) do
        representationCounts[representation.orderToken] = (representationCounts[representation.orderToken] or 0) + 1
        orderTokens[#orderTokens + 1] = representation.orderToken
      end
      local indistinguishableDuplicates = {}
      for token, duplicateCount in pairs(representationCounts) do
        if duplicateCount > 1 then
          indistinguishableDuplicates[#indistinguishableDuplicates + 1] = { representationFingerprint = self.safe.fingerprintValue(token), count = duplicateCount }
        end
      end
      if #indistinguishableDuplicates > 0 then
        self:emit(category, stage, 'partial', 'identical duplicate entries cannot be distinguished for stable per-instance ordering', {
          count = representationCount,
          indistinguishableDuplicates = indistinguishableDuplicates,
          perEntryRepresentations = representations,
          nextInstruction = 'Change metadata on one duplicate or discover a reviewed stable per-instance identifier.'
        })
        return
      end
      local orderKey = table.concat(orderTokens, '|')
      if categoryState.pendingStabilityEvidence ~= nil then
        if categoryState.pendingStabilityOrderKey ~= orderKey then
          categoryState.pendingStabilityEvidence = nil
          categoryState.pendingStabilityOrderKey = nil
          categoryState.priorOrder = copyList(orderTokens)
          categoryState.stabilityStartedAt = os.time()
          categoryState.stabilitySampleCount = 1
          self:resetClean(category)
          self:emit(category, stage, 'partial', 'slot/index order changed during consecutive confirmation', {
            count = representationCount,
            perEntryRepresentations = representations,
            nextInstruction = 'Continue until the new order is stable or make one clearly attributable inventory change.'
          }, true)
        else
          local advanced = self:advance(category, categoryState.pendingStabilityEvidence)
          if advanced then
            categoryState.pendingStabilityEvidence = nil
            categoryState.pendingStabilityOrderKey = nil
          end
        end
      elseif categoryState.priorOrder == nil then
        categoryState.priorOrder = copyList(orderTokens)
        categoryState.stabilityStartedAt = os.time()
        categoryState.stabilitySampleCount = 1
        self:emit(category, stage, 'partial', 'first slot/index order snapshot captured', {
          count = representationCount,
          perEntryRepresentations = representations,
          nextInstruction = 'Continue playing, then acquire, drop, or upgrade an item.'
        }, true)
      else
        local previousKey = table.concat(categoryState.priorOrder, '|')
        if previousKey ~= orderKey then
          local relativeStable = commonRelativeOrderStable(categoryState.priorOrder, orderTokens)
          categoryState.pendingStabilityEvidence = {
            stableAcrossMeaningfulChange = relativeStable,
            reorderObserved = not relativeStable,
            previousCount = #categoryState.priorOrder,
            currentCount = #orderTokens,
            previousFingerprint = self.safe.fingerprintValue(previousKey),
            currentFingerprint = self.safe.fingerprintValue(orderKey)
          }
          categoryState.pendingStabilityOrderKey = orderKey
          categoryState.priorOrder = copyList(orderTokens)
          local advanced = self:advance(category, categoryState.pendingStabilityEvidence)
          if advanced then categoryState.pendingStabilityEvidence = nil; categoryState.pendingStabilityOrderKey = nil end
        else
          categoryState.stabilitySampleCount = categoryState.stabilitySampleCount + 1
          local elapsed = os.time() - (categoryState.stabilityStartedAt or os.time())
          if elapsed >= self.slotStabilityWindowSeconds and categoryState.stabilitySampleCount >= self.slotStabilitySamplesRequired then
            categoryState.pendingStabilityEvidence = {
              stableAcrossWindow = true,
              windowSeconds = elapsed,
              sampleCount = categoryState.stabilitySampleCount,
              count = representationCount,
              perEntryRepresentations = representations
            }
            categoryState.pendingStabilityOrderKey = orderKey
            local advanced = self:advance(category, categoryState.pendingStabilityEvidence)
            if advanced then categoryState.pendingStabilityEvidence = nil; categoryState.pendingStabilityOrderKey = nil end
          else
            self:emit(category, stage, 'partial', 'slot/index stability window in progress', {
              elapsedSeconds = elapsed,
              requiredSeconds = self.slotStabilityWindowSeconds,
              sampleCount = categoryState.stabilitySampleCount,
              requiredSamples = self.slotStabilitySamplesRequired,
              nextInstruction = 'Continue playing or make one inventory change to test ordering across change.'
            })
          end
        end
      end
      return
    end
    if stage == 13 then
      local role = tostring(self.state.selectedRole):lower():gsub('%s+', '-')
      if role == 'host' then
        self:skipNotApplicable(category, 'joined-client repeat is not applicable on selected host role', { selectedRole = self.state.selectedRole })
      elseif role ~= 'joined-client' then
        self:emit(category, stage, 'partial', 'selected role is not declared', { nextInstruction = 'Prepare this computer as Host or Joined Client.' })
      elseif self.state.observedRole == 'joined-client' or self.state.authorityStatus == 'runtime-non-authority' then
        self:advance(category, { selectedRole = self.state.selectedRole, observedRole = self.state.observedRole, authorityStatus = self.state.authorityStatus, count = identityCount, provenReadRepeated = true })
      else
        if self.state.authorityStatus == 'runtime-authority' then
          self.state.dirtyEvidence = true
          self.state.evidenceHealth = 'role-mismatch'
        end
        self:emit(category, stage, 'partial', 'joined-client declaration lacks matching observed role or authority evidence', {
          selectedRole = self.state.selectedRole,
          observedRole = self.state.observedRole,
          authorityStatus = self.state.authorityStatus,
          nextInstruction = 'Join the host and remain in a stable multiplayer island.'
        })
      end
      return
    end
    if stage == 14 then
      local allPlayerStates, allErr = self.safe.findAll('CrabPS')
      if allErr then
        self:unsupported(category, 'catalog-approved CrabPS remote discovery unavailable', { error = allErr })
        return
      end
      if type(allPlayerStates) ~= 'table' then
        self:emit(category, stage, 'partial', 'remote CrabPS list not exposed as a capped table', { nextInstruction = 'Run with both host and joined client in the same island.' })
        return
      end
      local selectedRole = tostring(self.state.selectedRole or ''):lower():gsub('%s+', '-')
      local observedRole = tostring(self.state.observedRole or '')
      if selectedRole ~= observedRole then
        if observedRole ~= 'unknown' then self.state.dirtyEvidence = true; self.state.evidenceHealth = 'role-mismatch' end
        self:emit(category, stage, 'partial', 'remote visibility requires selected and observed role consistency', {
          selectedRole = selectedRole,
          observedRole = observedRole,
          nextInstruction = 'Wait until authority and distinct PlayerStates confirm this machine role.'
        })
        return
      end
      local visible = 0
      local uniqueFingerprints = {}
      local localReadable = false
      local remoteCandidates = {}
      self.safe.forEachArrayLimited(allPlayerStates, 16, function(_, wrapped)
        local candidate = wrapped
        if not self.safe.isValidObject(candidate) then candidate = self.safe.unwrapKnownValue(wrapped) end
        if self.safe.isValidObject(candidate) then
          local fingerprint = objectFingerprint(self.safe, candidate)
          if fingerprint ~= '' and not uniqueFingerprints[fingerprint] then
            uniqueFingerprints[fingerprint] = true
            visible = visible + 1
            local remoteArray, remoteErr = self.safe.getProperty(candidate, category)
            if remoteErr == nil and remoteArray ~= nil then
              local remoteCount, remoteCountErr = self.safe.getArrayLength(remoteArray)
              if remoteCountErr == nil then
                if fingerprint == tostring(self.state.localPlayerStateFingerprint or '') then
                  localReadable = true
                else
                  remoteCandidates[#remoteCandidates + 1] = { playerStateFingerprint = fingerprint, count = remoteCount, ownershipConfirmed = false }
                end
              end
            end
          end
        end
      end)
      if visible >= 2 and localReadable and #remoteCandidates >= 1 then
        self:emit(category, stage, 'partial', 'one-direction remote inventory candidate visibility observed; bidirectional proof remains Needs Coverage', {
          visibleDistinctPlayerStates = visible,
          localPlayerStateFingerprint = self.state.localPlayerStateFingerprint,
          localReadable = localReadable,
          remoteCandidates = remoteCandidates,
          selectedRole = selectedRole,
          observedRole = observedRole,
          visibilityDirection = selectedRole .. '-observed-remote-candidate',
          observedCandidateOnly = true,
          bidirectionalVisibilityProven = false,
          cap = 16,
          nextInstruction = 'Collect the reciprocal direction on the other machine and correlate the two evidence sessions.'
        })
      else
        self:emit(category, stage, 'partial', 'remote inventory visibility not yet confirmed', {
          visibleDistinctPlayerStates = visible,
          localReadable = localReadable,
          remoteCandidateCount = #remoteCandidates,
          nextInstruction = 'Keep host and joined client together in a stable run.'
        })
      end
    end
  end

  function manager:onTick()
    if self.config.allowFullObserveInventoryStages ~= true or self.state.stopRequested then return end
    if self.state.lifecycleState ~= 'stable' then return end
    local now = os.time()
    if self.lastSampleAt ~= nil and (now - self.lastSampleAt) < self.intervalSeconds then return end
    self.lastSampleAt = now
    local selectedCategory = nil
    for _ = 1, #CATEGORY_ORDER do
      local category = CATEGORY_ORDER[self.nextCategoryIndex]
      self.nextCategoryIndex = (self.nextCategoryIndex % #CATEGORY_ORDER) + 1
      local categoryState = self.categories[category]
      if not categoryState.completed and self.state:circuitAllows('inventory:' .. category) then
        selectedCategory = category
        break
      end
    end
    if selectedCategory then
      local ok, err = pcall(function() self:runStage(selectedCategory) end)
      if not ok then self:unsupported(selectedCategory, 'inventory stage Lua error', { error = tostring(err) }) end
    end
  end

  manager.STAGES = STAGES
  manager.CATEGORY_ORDER = CATEGORY_ORDER
  return manager
end

return inventoryStageManager
