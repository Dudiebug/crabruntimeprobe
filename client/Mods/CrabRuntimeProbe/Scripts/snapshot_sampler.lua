local recordBuilder = require('record_builder')

local sampler = {}

local SNAPSHOT_SCHEMA = 'snapshot-observation-v1'
local SOURCE_SCOPE = 'CrabPC.PlayerState'
local MAX_CONSECUTIVE_ERRORS = 3

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function clampNumber(value, fallback, minimum, maximum)
  local numberValue = tonumber(value) or fallback
  if numberValue < minimum then numberValue = minimum end
  if numberValue > maximum then numberValue = maximum end
  return numberValue
end

local function clean(value, cap)
  return recordBuilder.cleanString(value, cap or 192)
end

local function finiteNumber(value)
  return type(value) == 'number' and value == value and value ~= math.huge and value ~= -math.huge
end

local function objectToken(safe, value)
  if value == nil then return '', nil end
  -- GetFullName is the previously evidenced identity read path. The raw value is
  -- fingerprinted immediately and is never written or retained.
  local text, err = safe.getFullName(value)
  if err then return '', err end
  return safe.fingerprintValue(text or ''), nil
end

local function resolveLocalPlayerState(safe)
  local controller, controllerErr = safe.findFirst('CrabPC')
  if controllerErr then return nil, '', 'CrabPC: ' .. tostring(controllerErr) end
  if not safe.isValidObject(controller) then return nil, '', 'CrabPC unavailable' end

  local playerState, playerStateErr = safe.getProperty(controller, 'PlayerState')
  if playerStateErr then return nil, '', 'CrabPC.PlayerState: ' .. tostring(playerStateErr) end
  if not safe.isValidObject(playerState) then return nil, '', 'CrabPC.PlayerState unavailable' end

  local fingerprint, fingerprintErr = objectToken(safe, playerState)
  if fingerprintErr then return nil, '', 'CrabPC.PlayerState fingerprint: ' .. tostring(fingerprintErr) end
  if fingerprint == '' then return nil, '', 'CrabPC.PlayerState fingerprint unavailable' end
  return playerState, fingerprint, nil
end

local function nilField(sourcePath)
  return {
    status = 'unavailable',
    reason = clean(sourcePath .. ' was nil', 240)
  }
end

local function errorField(sourcePath, err)
  return {
    status = 'error',
    reason = clean(sourcePath .. ' read failed', 240)
  }
end

local function scalarField(value, sourcePath)
  if value == nil then return nilField(sourcePath) end
  local kind = type(value)
  if kind == 'boolean' then
    return {
      status = 'observed',
      value = value
    }
  end
  if finiteNumber(value) then
    return {
      status = 'observed',
      value = value
    }
  end
  return {
    status = 'unsupported',
    reason = kind == 'number'
      and clean(sourcePath .. ' contained a non-finite number', 240)
      or clean(sourcePath .. ' was not a reviewed scalar value', 240)
  }
end

local function readScalarProperty(safe, playerState, propertyName)
  local sourcePath = SOURCE_SCOPE .. '.' .. propertyName
  local value, err = safe.getProperty(playerState, propertyName)
  if err then return errorField(sourcePath, err), err end
  return scalarField(value, sourcePath), nil
end

local function readHealth(safe, playerState)
  local fields = {}
  local errors = {}
  local healthInfo, healthInfoErr = safe.getProperty(playerState, 'HealthInfo')
  if healthInfoErr then
    errors[#errors + 1] = 'HealthInfo: ' .. tostring(healthInfoErr)
    fields.currentHealth = errorField(SOURCE_SCOPE .. '.HealthInfo.CurrentHealth', healthInfoErr)
    fields.currentMaxHealth = errorField(SOURCE_SCOPE .. '.HealthInfo.CurrentMaxHealth', healthInfoErr)
  elseif healthInfo == nil then
    fields.currentHealth = nilField(SOURCE_SCOPE .. '.HealthInfo.CurrentHealth')
    fields.currentMaxHealth = nilField(SOURCE_SCOPE .. '.HealthInfo.CurrentMaxHealth')
  else
    for _, definition in ipairs({
      { key = 'currentHealth', field = 'CurrentHealth' },
      { key = 'currentMaxHealth', field = 'CurrentMaxHealth' }
    }) do
      local sourcePath = SOURCE_SCOPE .. '.HealthInfo.' .. definition.field
      local value, err = safe.getStructField(healthInfo, definition.field)
      if err then
        errors[#errors + 1] = definition.field .. ': ' .. tostring(err)
        fields[definition.key] = errorField(sourcePath, err)
      else
        fields[definition.key] = scalarField(value, sourcePath)
      end
    end
  end

  for _, definition in ipairs({
    { key = 'baseMaxHealth', field = 'BaseMaxHealth' },
    { key = 'maxHealthMultiplier', field = 'MaxHealthMultiplier' }
  }) do
    local field, err = readScalarProperty(safe, playerState, definition.field)
    fields[definition.key] = field
    if err then errors[#errors + 1] = definition.field .. ': ' .. tostring(err) end
  end
  return fields, errors
end

local function readCrystals(safe, playerState)
  local field, err = readScalarProperty(safe, playerState, 'Crystals')
  return { crystals = field }, err and { 'Crystals: ' .. tostring(err) } or {}
end

local function readSlots(safe, playerState)
  local fields = {}
  local errors = {}
  for _, definition in ipairs({
    { key = 'weaponModSlots', field = 'NumWeaponModSlots' },
    { key = 'abilityModSlots', field = 'NumAbilityModSlots' },
    { key = 'meleeModSlots', field = 'NumMeleeModSlots' },
    { key = 'perkSlots', field = 'NumPerkSlots' }
  }) do
    local field, err = readScalarProperty(safe, playerState, definition.field)
    fields[definition.key] = field
    if err then errors[#errors + 1] = definition.field .. ': ' .. tostring(err) end
  end
  return fields, errors
end

local function fingerprintField(safe, value, sourcePath)
  if value == nil then return nilField(sourcePath), nil end
  local fingerprint, err = objectToken(safe, value)
  if err then return errorField(sourcePath, err), err end
  if fingerprint == '' then
    return {
      status = 'unsupported',
      reason = clean(sourcePath .. ' fingerprint unavailable', 240)
    }, nil
  end
  return {
    status = 'observed',
    value = fingerprint,
    valueFingerprint = fingerprint
  }, nil
end

local function readEquipment(safe, playerState)
  local fields = {}
  local errors = {}
  for _, definition in ipairs({
    { key = 'weaponFingerprint', field = 'WeaponDA' },
    { key = 'abilityFingerprint', field = 'AbilityDA' },
    { key = 'meleeFingerprint', field = 'MeleeDA' }
  }) do
    local sourcePath = SOURCE_SCOPE .. '.' .. definition.field
    local value, propertyErr = safe.getProperty(playerState, definition.field)
    if propertyErr then
      errors[#errors + 1] = definition.field .. ': ' .. tostring(propertyErr)
      fields[definition.key] = errorField(sourcePath, propertyErr)
    else
      local field, fingerprintErr = fingerprintField(safe, value, sourcePath)
      fields[definition.key] = field
      if fingerprintErr then errors[#errors + 1] = definition.field .. ': ' .. tostring(fingerprintErr) end
    end
  end
  return fields, errors
end

local CATEGORY_DEFINITIONS = {
  {
    id = 'health',
    fieldOrder = { 'currentHealth', 'currentMaxHealth', 'baseMaxHealth', 'maxHealthMultiplier' },
    read = readHealth
  },
  {
    id = 'crystals',
    fieldOrder = { 'crystals' },
    read = readCrystals
  },
  {
    id = 'slots',
    fieldOrder = { 'weaponModSlots', 'abilityModSlots', 'meleeModSlots', 'perkSlots' },
    read = readSlots
  },
  {
    id = 'equipment',
    fieldOrder = { 'weaponFingerprint', 'abilityFingerprint', 'meleeFingerprint' },
    read = readEquipment
  }
}

local function fieldSignature(field)
  if type(field) ~= 'table' then return '<missing>' end
  return table.concat({
    tostring(field.status or ''),
    tostring(field.value == nil and '' or field.value),
    tostring(field.valueFingerprint or ''),
    tostring(field.reason or '')
  }, ':')
end

local function snapshotSignature(definition, fields)
  local parts = {}
  for _, name in ipairs(definition.fieldOrder or {}) do
    parts[#parts + 1] = name .. '=' .. fieldSignature(fields[name])
  end
  return table.concat(parts, '|')
end

local function resultFor(fields, errors)
  if #(errors or {}) > 0 then return 'error' end
  local observed = 0
  for _, field in pairs(fields or {}) do
    if type(field) == 'table' and field.status == 'observed' then
      observed = observed + 1
    end
  end
  return observed > 0 and 'ok' or 'partial'
end

-- This is intentionally the same reviewed, fixed-field path used by the
-- hook-free local sampler.  It does not inspect arrays, unwrap inventory
-- elements, read InventoryInfo/Enhancements, or retain the PlayerState after
-- the caller returns.  The readiness peer sampler consumes this helper only
-- for PlayerStates already visible to the current process.
function sampler.readReviewedScalarCategories(safe, playerState)
  local categories = {}
  local aggregate = 'ok'
  for _, definition in ipairs(CATEGORY_DEFINITIONS) do
    local readOk, fieldsOrErr, errors = pcall(definition.read, safe, playerState)
    local fields
    if readOk then
      fields = type(fieldsOrErr) == 'table' and fieldsOrErr or {}
      errors = type(errors) == 'table' and errors or {}
    else
      fields = { sample = errorField(SOURCE_SCOPE, fieldsOrErr) }
      errors = { clean(fieldsOrErr, 192) }
    end
    local result = resultFor(fields, errors)
    categories[definition.id] = {
      result = result,
      fields = fields
    }
    if result == 'error' then
      aggregate = 'error'
    elseif result ~= 'ok' and aggregate == 'ok' then
      aggregate = 'partial'
    end
  end
  return categories, aggregate
end

function sampler.new(config, safe, evidenceWriter, state)
  local o = {
    config = config or {},
    safe = safe,
    evidenceWriter = evidenceWriter,
    state = state,
    active = (config or {}).snapshotSamplerEnabled == true,
    sampleIntervalSeconds = clampNumber((config or {}).snapshotSampleIntervalSeconds, 3, 1, 60),
    unchangedHeartbeatSeconds = clampNumber((config or {}).snapshotUnchangedHeartbeatSeconds, 30, 10, 600),
    categoryIndex = 1,
    lastSampleAt = nil,
    snapshotSequence = 0,
    lifecycleGeneration = -1,
    lastSignatures = {},
    lastWrittenAt = {},
    consecutiveErrors = {},
    disabledCategories = {},
    baselineObserved = {},
    baselineReady = false
  }

  function o:setActive(active)
    self.active = active == true
  end

  function o:resetLifecycle(lifecycleGeneration)
    for category, _ in pairs(self.disabledCategories) do
      self.state.circuitBreakers['snapshot:' .. tostring(category)] = nil
    end
    self.lifecycleGeneration = tonumber(lifecycleGeneration) or 0
    self.categoryIndex = 1
    self.lastSampleAt = nil
    self.lastSignatures = {}
    self.lastWrittenAt = {}
    self.consecutiveErrors = {}
    self.disabledCategories = {}
    self.baselineObserved = {}
    self.baselineReady = false
    if type(self.state.setSamplingState) == 'function' then
      self.state:setSamplingState('', 'warming')
    end
  end

  function o:nextCategory()
    local checked = 0
    while checked < #CATEGORY_DEFINITIONS do
      local definition = CATEGORY_DEFINITIONS[self.categoryIndex]
      self.categoryIndex = (self.categoryIndex % #CATEGORY_DEFINITIONS) + 1
      checked = checked + 1
      if self.disabledCategories[definition.id] == nil then return definition end
    end
    return nil
  end

  function o:openCategoryCircuit(definition, reason)
    local category = definition.id
    local detail = clean(reason, 256)
    self.state.evidenceHealth = 'snapshot-category-partial'
    self.disabledCategories[category] = {
      reason = detail,
      openedAt = utcNow(),
      lifecycleGeneration = self.state.lifecycleGeneration
    }
    self.state.circuitBreakers['snapshot:' .. category] = {
      state = 'open',
      classification = 'read-error-disabled',
      reason = detail,
      openedAt = utcNow(),
      lifecycleGeneration = self.state.lifecycleGeneration
    }
  end

  function o:writeObservation(definition, fields, errors, result, changeKind, signature, now)
    self.snapshotSequence = self.snapshotSequence + 1
    local row = {
      schemaVersion = 1,
      recordType = 'snapshot-observation',
      sessionId = self.state.sessionId,
      campaignId = tostring(self.config.campaignId or ''),
      campaignGeneration = tonumber(self.config.campaignGeneration) or 0,
      machineId = tostring(self.config.machineId or ''),
      sequence = self.state:nextSequence(),
      timestampUtc = utcNow(),
      lifecycleGeneration = self.state.lifecycleGeneration,
      context = self.state.context,
      selectedRole = self.state.selectedRole,
      observedRole = self.state.observedRole,
      observationProfile = tostring(self.state.activeProfile or 'normal-play-guide'),
      worldFingerprint = self.state.worldFingerprint,
      playerStateFingerprint = self.state.localPlayerStateFingerprint,
      category = definition.id,
      dirtyEvidence = self.state.dirtyEvidence == true,
      crashSuspected = self.state.crashSuspected == true,
      stability = {
        stable = self.state.stability.ready == true and self.state.lifecycleState == 'stable',
        sampleCount = self.state.stability.consecutiveSamples,
        dwellSeconds = self.state.stability.dwellSeconds,
        worldStable = self.state.worldFingerprint ~= '',
        playerStateStable = self.state.localPlayerStateFingerprint ~= '',
        reason = result == 'error' and 'reviewed category read failed' or ''
      },
      fields = fields,
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
    -- Every row from a progressive process is conservatively excluded from
    -- hook-free Play Guide replay, including the pre-registration baseline.
    -- Live status separately reports whether hooks are active at this instant.
    if self.state.activeProfile == 'progressive-broad-observation' then
      row.safety.hooksDisabled = false
    end
    local writeOk = type(self.evidenceWriter.writeSnapshotObservation) == 'function'
      and self.evidenceWriter:writeSnapshotObservation(row)
      or false
    self.state:noteWriteResult(writeOk)
    if writeOk then
      self.lastSignatures[definition.id] = signature
      self.lastWrittenAt[definition.id] = now
    end
    return writeOk
  end

  function o:onTick()
    if not self.active then return { sampled = false, reason = 'disabled' } end
    if self.state.stability.ready ~= true or self.state.lifecycleState ~= 'stable' then
      return { sampled = false, reason = 'unstable' }
    end
    if self.lifecycleGeneration ~= self.state.lifecycleGeneration then
      self:resetLifecycle(self.state.lifecycleGeneration)
    end

    local now = os.time()
    if self.lastSampleAt ~= nil and (now - self.lastSampleAt) < self.sampleIntervalSeconds then
      return { sampled = false, reason = 'interval' }
    end
    self.lastSampleAt = now

    local definition = self:nextCategory()
    if definition == nil then return { sampled = false, reason = 'all-categories-disabled' } end
    if type(self.state.setSamplingState) == 'function' then
      self.state:setSamplingState(definition.id, self.baselineReady and 'collecting' or 'warming')
    end

    -- Objects are resolved for this single category read and never stored on the sampler.
    local playerState, fingerprint, scopeErr = resolveLocalPlayerState(self.safe)
    if scopeErr or fingerprint ~= tostring(self.state.localPlayerStateFingerprint or '') then
      return { sampled = false, scopeLost = true, reason = scopeErr or 'PlayerState fingerprint changed' }
    end

    local readOk, fieldsOrErr, errors = pcall(definition.read, self.safe, playerState)
    local fields
    if readOk then
      fields = type(fieldsOrErr) == 'table' and fieldsOrErr or {}
      errors = type(errors) == 'table' and errors or {}
    else
      fields = { sample = errorField(SOURCE_SCOPE, fieldsOrErr) }
      errors = { clean(fieldsOrErr, 192) }
    end

    local result = resultFor(fields, errors)
    if result == 'error' then
      self.consecutiveErrors[definition.id] = (self.consecutiveErrors[definition.id] or 0) + 1
      if self.consecutiveErrors[definition.id] >= MAX_CONSECUTIVE_ERRORS then
        local errorFingerprint = self.safe.fingerprintValue(table.concat(errors, ' | '))
        self:openCategoryCircuit(definition,
          'reviewed category read failed repeatedly; errorFingerprint=' .. tostring(errorFingerprint))
      end
    else
      self.consecutiveErrors[definition.id] = 0
    end

    local signature = snapshotSignature(definition, fields)
      .. '|result=' .. result
      .. '|lifecycle=' .. tostring(self.state.lifecycleGeneration)
    local previous = self.lastSignatures[definition.id]
    local lastWritten = self.lastWrittenAt[definition.id]
    local changeKind = previous == nil and 'initial' or (previous ~= signature and 'changed' or 'unchanged')
    local shouldWrite = changeKind ~= 'unchanged'
      or lastWritten == nil
      or (now - lastWritten) >= self.unchangedHeartbeatSeconds

    self.state.probeStage = 'snapshot:' .. definition.id .. ':' .. result
    local writeOk = true
    if shouldWrite then
      writeOk = self:writeObservation(definition, fields, errors, result,
        changeKind == 'unchanged' and 'unchanged-heartbeat' or changeKind, signature, now)
    end
    if result ~= 'error' and shouldWrite and writeOk then
      self.baselineObserved[definition.id] = true
      local baselineReady = true
      for _, requiredDefinition in ipairs(CATEGORY_DEFINITIONS) do
        if self.baselineObserved[requiredDefinition.id] ~= true then baselineReady = false break end
      end
      self.baselineReady = baselineReady
      if type(self.state.setSamplingState) == 'function' then
        self.state:setSamplingState(definition.id, baselineReady and 'ready' or 'warming')
      end
    end
    return {
      sampled = true,
      category = definition.id,
      result = result,
      changeKind = changeKind,
      written = shouldWrite and writeOk,
      writeSucceeded = writeOk,
      circuitOpen = self.disabledCategories[definition.id] ~= nil
    }
  end

  o.CATEGORY_DEFINITIONS = CATEGORY_DEFINITIONS
  o.SNAPSHOT_SCHEMA = SNAPSHOT_SCHEMA
  function o:isBaselineReady()
    return self.baselineReady == true
  end
  return o
end

return sampler
