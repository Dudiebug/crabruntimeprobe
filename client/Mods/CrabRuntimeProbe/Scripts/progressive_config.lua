local catalog = require('research_hook_catalog')
local artifactGuard = require('progressive_artifact_guard')

local progressiveConfig = {}

local MAX_CONFIG_BYTES = 65536
local MAX_CONFIG_LINES = 512
local MAX_TRUSTED_HOOKS = 111

local RUN_TYPES = {
  ['trusted-pool-only'] = true,
  ['canary-only'] = true,
  combined = true
}

local BLOCKED_CANARY_STATES = {
  quarantined = true,
  ['crash-suspect'] = true,
  unsupported = true,
  ['needs-revalidation'] = true
}

local REVIEWED_SCOPE_PROPERTIES = { OwningPS = true, PlayerState = true }
local REVIEWED_STATE_FIELDS = {
  ['CrabPS.Crystals'] = true,
  ['CrabPS.NumWeaponModSlots'] = true,
  ['CrabPS.NumAbilityModSlots'] = true,
  ['CrabPS.NumMeleeModSlots'] = true,
  ['CrabPS.NumPerkSlots'] = true,
  ['CrabPS.BaseMaxHealth'] = true,
  ['CrabPS.MaxHealthMultiplier'] = true,
  ['CrabPS.WeaponDA'] = true,
  ['CrabPS.AbilityDA'] = true,
  ['CrabPS.MeleeDA'] = true,
  -- Cataloged for provenance only. The executable runner deliberately omits
  -- this aggregate/inventory structure and records it as deferred.
  ['CrabPS.inventoryAndResources'] = true
}
local ARGUMENT_PROPERTY_TYPES = {
  ArrayProperty = true, BoolProperty = true, ByteProperty = true, EnumProperty = true,
  FloatProperty = true, IntProperty = true, ObjectProperty = true, StrProperty = true,
  StructProperty = true
}
local ARGUMENT_SUMMARIES = {
  ['class-and-redacted-full-name'] = true, ['length-and-fingerprint'] = true,
  scalar = true, ['shape-and-count-only-until-staged-proof'] = true
}
local ARGUMENT_REDACTIONS = {
  ['bounded-text-redaction'] = true, ['nested-values-not-read'] = true,
  none = true, ['object-identity-redacted'] = true
}

local RESEARCH_KEYS = {
  progressiveObservationEnabled = true,
  researchRunType = true,
  researchRunId = true,
  compatibilityFingerprint = true,
  compatibilityGameBuild = true,
  compatibilityUe4ssVersion = true,
  compatibilityComputedAtUtc = true,
  researchCoverageCatalogHash = true,
  researchHookCatalogIdentity = true,
  researchCallbackImplementationVersion = true,
  researchCallbackSchemaVersion = true,
  researchValidationBehaviorVersion = true,
  trustedCandidateSelections = true,
  canaryCandidateId = true,
  canaryHookPathFingerprint = true,
  canaryValidationDepth = true,
  canaryState = true,
  relicCountValidationEnabled = true
}

local function fail(reason)
  return nil, tostring(reason or 'invalid-progressive-configuration')
end

local function validOpaqueId(value, minimumLength, maximumLength)
  local text = tostring(value or '')
  return text ~= 'unassigned' and #text >= minimumLength and #text <= maximumLength
    and text:match('^[A-Za-z0-9_-]+$') ~= nil
end

local function validHash(value)
  local text = tostring(value or '')
  return #text == 64 and text:match('^[a-f0-9]+$') ~= nil
end

local function validComponent(value)
  local text = tostring(value or '')
  return text ~= 'unknown' and text ~= 'unavailable' and text ~= 'unassigned'
    and #text >= 1 and #text <= 128 and text:match('^[A-Za-z0-9_.+:-]+$') ~= nil
end

local function validTimestamp(value)
  local year, month, day, hour, minute, second, suffix = tostring(value or ''):match(
    '^(%d%d%d%d)%-(%d%d)%-(%d%d)T(%d%d):(%d%d):(%d%d)(.*)$')
  if not year then return false end
  month, day, hour, minute, second = tonumber(month), tonumber(day), tonumber(hour), tonumber(minute), tonumber(second)
  local timezoneValid = suffix == 'Z' or suffix:match('^%.%d+Z$') ~= nil
  if not timezoneValid then
    local zoneHour, zoneMinute = suffix:match('^[+-](%d%d):(%d%d)$')
    if not zoneHour then zoneHour, zoneMinute = suffix:match('^%.%d+[+-](%d%d):(%d%d)$') end
    timezoneValid = zoneHour ~= nil and tonumber(zoneHour) <= 23 and tonumber(zoneMinute) <= 59
  end
  return timezoneValid and month >= 1 and month <= 12 and day >= 1 and day <= 31
    and hour >= 0 and hour <= 23 and minute >= 0 and minute <= 59 and second >= 0 and second <= 60
end

local function parseBoolean(values, key)
  if values[key] == 'true' then return true end
  if values[key] == 'false' then return false end
  return nil
end

local function parseInteger(values, key, minimum, maximum)
  local value = tonumber(values[key])
  if value == nil or math.floor(value) ~= value or value < minimum or value > maximum then return nil end
  return value
end

local function readFlatConfig(path)
  local file = io.open(path, 'r')
  if not file then return fail('config-open-failed') end
  local text = file:read('*a') or ''
  file:close()
  if #text > MAX_CONFIG_BYTES then return fail('config-size-limit-exceeded') end

  local values = {}
  local lineCount = 0
  text = text .. '\n'
  for line in text:gmatch('(.-)\r?\n') do
    lineCount = lineCount + 1
    if lineCount > MAX_CONFIG_LINES then return fail('config-line-limit-exceeded') end
    local cleaned = line:gsub('%s*#.*$', '')
    if cleaned:match('%S') then
      local key, value = cleaned:match('^%s*([%w_]+)%s*=%s*(.-)%s*$')
      if not key then return fail('malformed-config-line-' .. tostring(lineCount)) end
      if values[key] ~= nil then return fail('duplicate-config-key-' .. key) end
      if #value > 4096 then return fail('config-value-limit-exceeded-' .. key) end
      values[key] = value
    end
  end
  return values, nil
end

local function buildCatalogIndex()
  if type(catalog) ~= 'table'
    or catalog.schemaVersion ~= 'hook-candidate-catalog-v1'
    or type(catalog.candidates) ~= 'table'
    or not validHash(catalog.coverageCatalogHash)
    or not validHash(catalog.hookCatalogIdentity) then
    return fail('generated-hook-catalog-invalid')
  end
  local byId = {}
  local candidateCount = 0
  for _, candidate in ipairs(catalog.candidates) do
    local id = tostring(candidate.id or '')
    local path = tostring(candidate.hookPath or '')
    local ownerKind = tostring(candidate.ownerKind or '')
    candidateCount = candidateCount + 1
    if candidateCount > 111 or #id > 128 or id:match('^hook%-[a-z0-9%-]+$') == nil
      or not validHash(candidate.hookPathFingerprint)
      or (ownerKind ~= 'native' and ownerKind ~= 'blueprint')
      or #path > 512
      or (not path:match('^/Script/[%w_%.]+:[%w_]+$') and not path:match('^/Game/[^%s]+:[%w_]+$'))
      or byId[id] ~= nil or not validHash(candidate.hookPathFingerprint)
      or tonumber(candidate.maximumValidationDepth) == nil
      or math.floor(tonumber(candidate.maximumValidationDepth)) ~= tonumber(candidate.maximumValidationDepth)
      or tonumber(candidate.maximumValidationDepth) < 1 or tonumber(candidate.maximumValidationDepth) > 7
      or (candidate.callbackPhase ~= 'pre' and candidate.callbackPhase ~= 'post')
      or type(candidate.knownCrashContext) ~= 'boolean' then
      return fail('generated-hook-catalog-candidate-invalid')
    end
    local scopeCount = 0
    local scopeSeen = {}
    for _, fieldName in ipairs(candidate.scopeProperties or {}) do
      scopeCount = scopeCount + 1
      if scopeCount > 4 or REVIEWED_SCOPE_PROPERTIES[fieldName] ~= true or scopeSeen[fieldName] then
        return fail('generated-hook-catalog-scope-invalid')
      end
      scopeSeen[fieldName] = true
    end
    local stateCount = 0
    local stateSeen = {}
    for _, fieldPath in ipairs(candidate.reviewedStateFields or {}) do
      stateCount = stateCount + 1
      if stateCount > 16 or REVIEWED_STATE_FIELDS[fieldPath] ~= true or stateSeen[fieldPath] then
        return fail('generated-hook-catalog-state-read-invalid')
      end
      stateSeen[fieldPath] = true
    end
    local argumentCount = 0
    local argumentSeen = {}
    for _, argument in ipairs(candidate.argumentSchema or {}) do
      argumentCount = argumentCount + 1
      local name = tostring(argument.name or '')
      local valueTypePath = tostring(argument.valueTypePath or '')
      if argumentCount > 16 or name:match('^[A-Za-z_][A-Za-z0-9_]*$') == nil or argumentSeen[name]
        or ARGUMENT_PROPERTY_TYPES[tostring(argument.propertyType or '')] ~= true
        or ARGUMENT_SUMMARIES[tostring(argument.safeSummary or '')] ~= true
        or ARGUMENT_REDACTIONS[tostring(argument.redaction or '')] ~= true
        or #valueTypePath > 256
        or (valueTypePath ~= '' and valueTypePath:match('^/Script/[^%s]+$') == nil
          and valueTypePath:match('^/Game/[^%s]+$') == nil) then
        return fail('generated-hook-catalog-argument-invalid')
      end
      argumentSeen[name] = true
    end
    byId[id] = candidate
  end
  if candidateCount < 1 then return fail('generated-hook-catalog-empty') end
  return byId, nil
end

local function parseTrusted(values, byId)
  local raw = tostring(values.trustedCandidateSelections or '')
  local selections = {}
  local seen = {}
  if raw ~= '' then
    if raw:sub(1, 1) == ',' or raw:sub(-1) == ',' or raw:find(',,', 1, true) then
      return fail('trusted-hook-selection-list-malformed')
    end
    for token in raw:gmatch('[^,]+') do
      local id, depthText, fingerprint = token:match('^(hook%-[a-z0-9%-]+)@([0-7])@([a-f0-9]+)$')
      local depth = tonumber(depthText)
      local candidate = id and byId[id] or nil
      if not candidate or seen[id] or not validHash(fingerprint)
        or fingerprint ~= tostring(candidate.hookPathFingerprint)
        or depth == nil or depth < 1 or depth > math.min(7, tonumber(candidate.maximumValidationDepth) or 0) then
        return fail('trusted-hook-selection-invalid')
      end
      seen[id] = true
      selections[#selections + 1] = {
        candidate = candidate,
        candidateId = id,
        hookPathFingerprint = fingerprint,
        validationDepth = depth
      }
    end
  end
  if #selections > MAX_TRUSTED_HOOKS then return fail('trusted-hook-count-invalid') end
  return selections, nil
end

function progressiveConfig.load(path)
  local values, readErr = readFlatConfig(path)
  if not values then return { enabled = false, rejected = true, reason = readErr } end
  for key, _ in pairs(values) do
    local researchLike = key:match('^progressive') or key:match('^research')
      or key:match('^compatibility') or key:match('^trustedCandidate')
      or key:match('^canary') or key:match('^relicCount')
    if researchLike and RESEARCH_KEYS[key] ~= true then
      return { enabled = false, rejected = true, reason = 'unknown-research-config-key-' .. key }
    end
  end
  local enabled = parseBoolean(values, 'progressiveObservationEnabled')
  if enabled == nil then return { enabled = false, rejected = true, reason = 'progressive-enabled-boolean-invalid' } end
  if not enabled then return { enabled = false, rejected = false, raw = values } end

  local byId, catalogErr = buildCatalogIndex()
  if not byId then return { enabled = false, rejected = true, reason = catalogErr } end

  for _, key in ipairs({
    'allowWriteProbes', 'allowRpcProbes', 'allowHudTickHook', 'allowRawIdentityEvidence',
    'allowDeepArrayProbes', 'allowPassiveObservationHooks', 'allowFullObserveInventoryStages',
    'allowFullObserveRuntimeDiscovery'
  }) do
    if parseBoolean(values, key) ~= false then
      return { enabled = false, rejected = true, reason = 'unsafe-gate-' .. key }
    end
  end
  if parseBoolean(values, 'fullObserveEnabled') ~= true
    or parseBoolean(values, 'snapshotSamplerEnabled') ~= true
    or parseBoolean(values, 'statusWriterEnabled') ~= true
    or parseBoolean(values, 'writeJsonlResults') ~= true
    or tostring(values.mode or '') ~= 'observe'
    or tostring(values.tickDriver or '') ~= 'executeDelay'
    or tostring(values.probeSet or '') ~= 'crabsync-full-observe' then
    return { enabled = false, rejected = true, reason = 'progressive-safe-baseline-contract-invalid' }
  end

  local runId = tostring(values.researchRunId or '')
  local runType = tostring(values.researchRunType or '')
  local compatibilityFingerprint = tostring(values.compatibilityFingerprint or '')
  if not validOpaqueId(runId, 8, 128) then return { enabled = false, rejected = true, reason = 'run-id-invalid' } end
  if RUN_TYPES[runType] ~= true then return { enabled = false, rejected = true, reason = 'run-type-invalid' } end
  if not validHash(compatibilityFingerprint) then return { enabled = false, rejected = true, reason = 'compatibility-fingerprint-invalid' } end
  if tostring(values.researchCoverageCatalogHash or '') ~= tostring(catalog.coverageCatalogHash)
    or tostring(values.researchHookCatalogIdentity or '') ~= tostring(catalog.hookCatalogIdentity)
    or tostring(values.researchCallbackImplementationVersion or '') ~= tostring(catalog.callbackImplementationVersion)
    or tostring(values.researchCallbackSchemaVersion or '') ~= tostring(catalog.callbackSchemaVersion)
    or tostring(values.researchValidationBehaviorVersion or '') ~= tostring(catalog.validationBehaviorVersion) then
    return { enabled = false, rejected = true, reason = 'catalog-or-callback-compatibility-mismatch' }
  end
  if not validComponent(values.compatibilityGameBuild) or not validComponent(values.compatibilityUe4ssVersion)
    or not validTimestamp(values.compatibilityComputedAtUtc) then
    return { enabled = false, rejected = true, reason = 'compatibility-components-invalid' }
  end

  local trusted, trustedErr = parseTrusted(values, byId)
  if not trusted then return { enabled = false, rejected = true, reason = trustedErr } end

  local canary = nil
  if runType == 'trusted-pool-only' then
    if tostring(values.canaryCandidateId or '') ~= 'unassigned'
      or tostring(values.canaryHookPathFingerprint or '') ~= ''
      or tostring(values.canaryState or '') ~= 'untested'
      or parseInteger(values, 'canaryValidationDepth', 0, 0) ~= 0 then
      return { enabled = false, rejected = true, reason = 'trusted-only-run-must-not-arm-canary' }
    end
  else
    local candidateId = tostring(values.canaryCandidateId or '')
    local candidate = byId[candidateId]
    local depth = parseInteger(values, 'canaryValidationDepth', 1, 7)
    local state = tostring(values.canaryState or '')
    local fingerprint = tostring(values.canaryHookPathFingerprint or '')
    if not candidate or depth == nil or depth > math.min(7, tonumber(candidate.maximumValidationDepth) or 0)
      or fingerprint ~= tostring(candidate.hookPathFingerprint) or BLOCKED_CANARY_STATES[state] == true
      or state ~= 'armed' then
      return { enabled = false, rejected = true, reason = 'canary-selection-invalid-or-blocked' }
    end
    for _, selection in ipairs(trusted) do
      if selection.candidateId == candidateId then
        return { enabled = false, rejected = true, reason = 'canary-duplicates-trusted-candidate' }
      end
    end
    canary = {
      candidate = candidate,
      candidateId = candidateId,
      hookPathFingerprint = fingerprint,
      validationDepth = depth,
      priorState = state
    }
  end
  if runType == 'canary-only' and #trusted ~= 0 then
    return { enabled = false, rejected = true, reason = 'canary-only-run-has-trusted-hooks' }
  end

  local relicEnabled = parseBoolean(values, 'relicCountValidationEnabled')
  if relicEnabled == nil then return { enabled = false, rejected = true, reason = 'relic-count-gate-invalid' } end
  local selection = {
    enabled = true,
    rejected = false,
    runId = runId,
    runType = runType,
    compatibilityFingerprint = compatibilityFingerprint,
    compatibilityComputedAtUtc = tostring(values.compatibilityComputedAtUtc),
    gameBuild = tostring(values.compatibilityGameBuild),
    ue4ssVersion = tostring(values.compatibilityUe4ssVersion),
    catalog = catalog,
    catalogById = byId,
    trusted = trusted,
    canary = canary,
    relicCountValidationEnabled = relicEnabled,
    automaticInProcessAdvance = false,
    raw = values
  }
  local authorized, authorizationErr = artifactGuard.authorizeSelections(selection)
  if not authorized then
    return { enabled = false, rejected = true, reason = tostring(authorizationErr or 'research-artifact-authorization-failed') }
  end
  return selection
end

return progressiveConfig
