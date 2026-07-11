local jsonReader = require('progressive_json_reader')

local guard = {}

local RESULTS_ROOT = 'Mods/CrabRuntimeProbe/Scripts/results/'
-- Large enough for a mature 111-candidate ledger with bounded evidence-session
-- history, while remaining far below the desktop reader's 8 MiB hard cap.
local MAX_ARTIFACT_BYTES = 2097152

local function exactObject(value, requiredKeys)
  if type(value) ~= 'table' or jsonReader.isArray(value) then return false end
  local expected = {}
  local expectedCount = 0
  for _, key in ipairs(requiredKeys) do expected[key] = true; expectedCount = expectedCount + 1 end
  local count = 0
  for key, _ in pairs(value) do
    if type(key) ~= 'string' or expected[key] ~= true then return false end
    count = count + 1
  end
  return count == expectedCount
end

local function validArray(value, maximumItems)
  return jsonReader.isArray(value) and #value <= maximumItems
end

local function validHash(value)
  return type(value) == 'string' and #value == 64 and value:match('^[a-f0-9]+$') ~= nil
end

local function validCandidateId(value)
  return type(value) == 'string' and #value <= 128
    and value:match('^hook%-[a-z0-9%-]+$') ~= nil
end

local function validOpaqueId(value, minimumLength, maximumLength)
  return type(value) == 'string' and #value >= minimumLength and #value <= maximumLength
    and value:match('^[A-Za-z0-9_-]+$') ~= nil
end

local function validDepth(value)
  return type(value) == 'number' and value >= 1 and value <= 7 and math.floor(value) == value
end

local function validInteger(value, minimum, maximum)
  return type(value) == 'number' and value >= minimum and value <= maximum and math.floor(value) == value
end

local function validTimestamp(value)
  if type(value) ~= 'string' or #value > 64 then return false end
  local year, month, day, hour, minute, second, suffix = value:match(
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

local function matchesCatalogIdentity(value, catalog)
  return value.coverageCatalogHash == catalog.coverageCatalogHash
    and value.hookCatalogIdentity == catalog.hookCatalogIdentity
    and value.callbackImplementationVersion == catalog.callbackImplementationVersion
    and value.callbackSchemaVersion == catalog.callbackSchemaVersion
    and value.validationBehaviorVersion == catalog.validationBehaviorVersion
end

local function readArtifact(path, maximumNodes, maximumItems)
  return jsonReader.read(path, {
    maximumBytes = MAX_ARTIFACT_BYTES,
    maximumNodes = maximumNodes or 8192,
    maximumDepth = 16,
    maximumStringBytes = 2048,
    maximumContainerItems = maximumItems or 1024
  })
end

local function validUniqueIds(value, maximumItems)
  if not validArray(value, maximumItems) then return false end
  local seen = {}
  for _, item in ipairs(value) do
    if not validOpaqueId(item, 1, 128) or seen[item] then return false end
    seen[item] = true
  end
  return true
end

function guard.authorizeSelections(selection)
  if type(selection) ~= 'table' or type(selection.catalog) ~= 'table' then
    return false, 'research-selection-invalid'
  end

  local trustedManifest, trustedReadErr = readArtifact(RESULTS_ROOT .. 'trusted_hook_manifest.json', 4096, 256)
  if not trustedManifest then return false, 'trusted-manifest-' .. tostring(trustedReadErr) end
  if not exactObject(trustedManifest, {
    'schemaVersion', 'generatedAtUtc', 'coverageCatalogHash', 'hookCatalogIdentity',
    'callbackImplementationVersion', 'callbackSchemaVersion', 'validationBehaviorVersion',
    'compatibilityFingerprint', 'generatedFromLedgerAtUtc', 'candidates'
  }) or trustedManifest.schemaVersion ~= 'trusted-hook-manifest-v1'
    or not validTimestamp(trustedManifest.generatedAtUtc)
    or not validTimestamp(trustedManifest.generatedFromLedgerAtUtc)
    or not matchesCatalogIdentity(trustedManifest, selection.catalog)
    or not validArray(trustedManifest.candidates, 111) then
    return false, 'trusted-manifest-contract-invalid'
  end

  local authorized = {}
  for _, entry in ipairs(trustedManifest.candidates) do
    if not exactObject(entry, {
      'candidateId', 'hookPathFingerprint', 'trustedDepth', 'compatibilityFingerprint'
    }) or not validCandidateId(entry.candidateId) or not validHash(entry.hookPathFingerprint)
      or not validDepth(entry.trustedDepth) or not validHash(entry.compatibilityFingerprint)
      or authorized[entry.candidateId] ~= nil then
      return false, 'trusted-manifest-entry-invalid'
    end
    local catalogCandidate = selection.catalogById[entry.candidateId]
    if not catalogCandidate or entry.hookPathFingerprint ~= catalogCandidate.hookPathFingerprint
      or entry.compatibilityFingerprint ~= selection.compatibilityFingerprint then
      return false, 'trusted-manifest-entry-incompatible'
    end
    authorized[entry.candidateId] = entry
  end
  if #trustedManifest.candidates > 0 then
    if trustedManifest.compatibilityFingerprint ~= selection.compatibilityFingerprint then
      return false, 'trusted-manifest-compatibility-mismatch'
    end
  elseif trustedManifest.compatibilityFingerprint ~= ''
    and trustedManifest.compatibilityFingerprint ~= selection.compatibilityFingerprint then
    return false, 'empty-trusted-manifest-compatibility-invalid'
  end

  for _, trustedSelection in ipairs(selection.trusted or {}) do
    local entry = authorized[trustedSelection.candidateId]
    if not entry or entry.hookPathFingerprint ~= trustedSelection.hookPathFingerprint
      or trustedSelection.validationDepth > entry.trustedDepth then
      return false, 'trusted-selection-not-authorized-at-depth'
    end
  end
  if selection.runType == 'trusted-pool-only' or selection.runType == 'combined' then
    local configured = {}
    for _, trustedSelection in ipairs(selection.trusted or {}) do
      configured[trustedSelection.candidateId] = true
    end
    for candidateId, entry in pairs(authorized) do
      if not configured[candidateId] then
        local promotedCanaryAtNextDepth = selection.runType == 'combined' and selection.canary
          and selection.canary.candidateId == candidateId
          and selection.canary.hookPathFingerprint == entry.hookPathFingerprint
          and selection.canary.validationDepth == entry.trustedDepth + 1
        if not promotedCanaryAtNextDepth then
          return false, 'trusted-manifest-entry-omitted-from-pool'
        end
      end
    end
  end


  local ledger, ledgerReadErr = readArtifact(RESULTS_ROOT .. 'hook_validation_ledger.json', 65536, 4096)
  if not ledger then return false, 'validation-ledger-' .. tostring(ledgerReadErr) end
  if not exactObject(ledger, {
    'schemaVersion', 'generatedAtUtc', 'updatedAtUtc', 'coverageCatalogHash', 'hookCatalogIdentity',
    'callbackImplementationVersion', 'callbackSchemaVersion', 'validationBehaviorVersion',
    'initialMigrationPolicy', 'candidates'
  }) or ledger.schemaVersion ~= 'hook-validation-ledger-v1'
    or not validTimestamp(ledger.generatedAtUtc) or not validTimestamp(ledger.updatedAtUtc)
    or type(ledger.initialMigrationPolicy) ~= 'string' or #ledger.initialMigrationPolicy < 1
    or #ledger.initialMigrationPolicy > 1024 or not matchesCatalogIdentity(ledger, selection.catalog)
    or not validArray(ledger.candidates, 512) then
    return false, 'validation-ledger-contract-invalid'
  end
  local allowedStates = {
    untested = true, armed = true, ['registration-clean'] = true,
    ['registered-not-observed'] = true, ['natural-callback-clean'] = true,
    provisional = true, trusted = true, ['needs-revalidation'] = true,
    unsupported = true, quarantined = true, ['crash-suspect'] = true
  }
  local ledgerById = {}
  for _, entry in ipairs(ledger.candidates) do
    if not exactObject(entry, {
      'candidateId', 'hookPathFingerprint', 'state', 'highestValidatedDepth', 'trustedDepth',
      'cleanRuns', 'naturalCallbacks', 'hostCleanRuns', 'joinedClientCleanRuns',
      'lifecycleTransitionRuns', 'evidenceSessions', 'legacyObservationMigrated',
      'legacyObservationTrusted', 'crashSuspectRuns', 'compatibilityFingerprint',
      'hasUnmatchedBreadcrumb', 'hasCorrelatedCrash', 'hasNewUe4ssCallbackError',
      'reducerFixtureCovered'
    }) or not validCandidateId(entry.candidateId) or not validHash(entry.hookPathFingerprint)
      or allowedStates[entry.state] ~= true or not validInteger(entry.highestValidatedDepth, 0, 7)
      or (entry.trustedDepth ~= jsonReader.null and not validDepth(entry.trustedDepth))
      or not validInteger(entry.cleanRuns, 0, 100000)
      or not validInteger(entry.naturalCallbacks, 0, 1000000)
      or not validInteger(entry.hostCleanRuns, 0, 100000)
      or not validInteger(entry.joinedClientCleanRuns, 0, 100000)
      or not validInteger(entry.lifecycleTransitionRuns, 0, 100000)
      or not validUniqueIds(entry.evidenceSessions, 4096)
      or type(entry.legacyObservationMigrated) ~= 'boolean' or entry.legacyObservationTrusted ~= false
      or not validUniqueIds(entry.crashSuspectRuns, 4096)
      or (entry.compatibilityFingerprint ~= '' and not validHash(entry.compatibilityFingerprint))
      or type(entry.hasUnmatchedBreadcrumb) ~= 'boolean' or type(entry.hasCorrelatedCrash) ~= 'boolean'
      or type(entry.hasNewUe4ssCallbackError) ~= 'boolean' or type(entry.reducerFixtureCovered) ~= 'boolean'
      or ledgerById[entry.candidateId] ~= nil then
      return false, 'validation-ledger-entry-invalid'
    end
    local catalogCandidate = selection.catalogById[entry.candidateId]
    if not catalogCandidate or entry.hookPathFingerprint ~= catalogCandidate.hookPathFingerprint then
      return false, 'validation-ledger-entry-catalog-mismatch'
    end
    ledgerById[entry.candidateId] = entry
  end
  for candidateId, trustedEntry in pairs(authorized) do
    local entry = ledgerById[candidateId]
    local countersStillDescribeTrustedDepth = entry
      and entry.highestValidatedDepth == trustedEntry.trustedDepth
    if not entry or entry.state ~= 'trusted' or entry.trustedDepth == jsonReader.null
      or entry.trustedDepth < trustedEntry.trustedDepth
      or entry.highestValidatedDepth < trustedEntry.trustedDepth
      or entry.compatibilityFingerprint ~= selection.compatibilityFingerprint
      or (countersStillDescribeTrustedDepth and (entry.cleanRuns < 3 or entry.naturalCallbacks < 3
        or entry.hostCleanRuns < 1 or entry.joinedClientCleanRuns < 1
        or entry.lifecycleTransitionRuns < 1))
      or #entry.crashSuspectRuns > 0
      or entry.hasUnmatchedBreadcrumb or entry.hasCorrelatedCrash or entry.hasNewUe4ssCallbackError
      or entry.reducerFixtureCovered ~= true then
      return false, 'trusted-selection-ledger-policy-not-met'
    end
  end
  if selection.canary then
    local entry = ledgerById[selection.canary.candidateId]
    if not entry then
      if selection.canary.validationDepth ~= 1 then return false, 'unrecorded-canary-must-start-depth-one' end
    else
      if entry.state == 'needs-revalidation' or entry.state == 'unsupported'
        or entry.state == 'quarantined' or entry.state == 'crash-suspect'
        or entry.hasUnmatchedBreadcrumb or entry.hasCorrelatedCrash
        or entry.hasNewUe4ssCallbackError or #entry.crashSuspectRuns > 0 then
        return false, 'canary-ledger-state-blocked'
      end
      local maximumNextDepth = entry.trustedDepth == jsonReader.null
        and math.max(1, entry.highestValidatedDepth)
        or math.min(7, entry.trustedDepth + 1)
      if selection.canary.validationDepth > maximumNextDepth then
        return false, 'canary-validation-depth-skipped'
      end
      if entry.trustedDepth ~= jsonReader.null and entry.trustedDepth >= selection.canary.validationDepth then
        return false, 'canary-depth-already-trusted'
      end
      if entry.highestValidatedDepth > 0 and entry.compatibilityFingerprint ~= selection.compatibilityFingerprint then
        return false, 'canary-ledger-compatibility-mismatch'
      end
    end
  end

  local quarantine, quarantineReadErr = readArtifact(RESULTS_ROOT .. 'hook_quarantine.json', 8192, 1024)
  if not quarantine then return false, 'quarantine-' .. tostring(quarantineReadErr) end
  if not exactObject(quarantine, {
    'schemaVersion', 'generatedAtUtc', 'updatedAtUtc', 'coverageCatalogHash', 'hookCatalogIdentity',
    'callbackImplementationVersion', 'callbackSchemaVersion', 'validationBehaviorVersion', 'entries'
  }) or quarantine.schemaVersion ~= 'hook-quarantine-v1'
    or not validTimestamp(quarantine.generatedAtUtc) or not validTimestamp(quarantine.updatedAtUtc)
    or not matchesCatalogIdentity(quarantine, selection.catalog)
    or not validArray(quarantine.entries, 512) then
    return false, 'quarantine-contract-invalid'
  end

  local blocked = {}
  for _, entry in ipairs(quarantine.entries) do
    if not exactObject(entry, {
      'candidateId', 'hookPathFingerprint', 'validationDepth', 'state', 'reason', 'runId',
      'quarantinedAtUtc', 'explicitRetryRequired', 'automaticRearmAllowed'
    }) or not validCandidateId(entry.candidateId) or not validHash(entry.hookPathFingerprint)
      or not validDepth(entry.validationDepth)
      or (entry.state ~= 'quarantined' and entry.state ~= 'crash-suspect')
      or type(entry.reason) ~= 'string' or #entry.reason < 1 or #entry.reason > 1024
      or not validOpaqueId(entry.runId, 1, 128) or not validTimestamp(entry.quarantinedAtUtc)
      or entry.explicitRetryRequired ~= true or entry.automaticRearmAllowed ~= false then
      return false, 'quarantine-entry-invalid'
    end
    local catalogCandidate = selection.catalogById[entry.candidateId]
    if not catalogCandidate or entry.hookPathFingerprint ~= catalogCandidate.hookPathFingerprint then
      return false, 'quarantine-entry-catalog-mismatch'
    end
    blocked[entry.candidateId] = true
  end
  for _, trustedSelection in ipairs(selection.trusted or {}) do
    if blocked[trustedSelection.candidateId] then return false, 'trusted-selection-is-quarantined' end
  end
  if selection.canary and blocked[selection.canary.candidateId] then
    return false, 'canary-selection-is-quarantined'
  end
  return true, nil
end

local function validateSelectionRecord(record, expected)
  return exactObject(record, { 'candidateId', 'hookPathFingerprint', 'validationDepth' })
    and record.candidateId == expected.candidateId
    and record.hookPathFingerprint == expected.hookPathFingerprint
    and record.validationDepth == expected.validationDepth
end

function guard.validateRunManifest(path, sessionId, config, selection)
  local manifest, readErr = readArtifact(path, 8192, 1024)
  if not manifest then return false, 'run-manifest-' .. tostring(readErr) end
  if not exactObject(manifest, {
    'schemaVersion', 'runId', 'sessionId', 'campaignGeneration', 'createdAtUtc', 'runType',
    'selectedRole', 'compatibility', 'safeSnapshotBaseline', 'trustedCandidates', 'canary',
    'registrationOrder', 'automaticInProcessAdvance', 'safety'
  }) or manifest.schemaVersion ~= 'hook-run-manifest-v1'
    or manifest.runId ~= selection.runId or manifest.sessionId ~= tostring(sessionId or '')
    or manifest.campaignGeneration ~= tonumber(config.campaignGeneration)
    or not validTimestamp(manifest.createdAtUtc) or manifest.runType ~= selection.runType
    or manifest.selectedRole ~= tostring(config.selectedRole or '')
    or manifest.safeSnapshotBaseline ~= true or manifest.automaticInProcessAdvance ~= false
    or not validArray(manifest.trustedCandidates, 111)
    or not validArray(manifest.registrationOrder, 114) then
    return false, 'run-manifest-contract-invalid'
  end

  local compatibility = manifest.compatibility
  if not exactObject(compatibility, {
    'schemaVersion', 'gameBuild', 'ue4ssVersion', 'coverageCatalogHash', 'hookCatalogIdentity',
    'callbackImplementationVersion', 'callbackSchemaVersion', 'validationBehaviorVersion',
    'fingerprint', 'computedAtUtc'
  }) or compatibility.schemaVersion ~= 'compatibility-fingerprint-v1'
    or compatibility.gameBuild ~= selection.gameBuild or compatibility.ue4ssVersion ~= selection.ue4ssVersion
    or compatibility.fingerprint ~= selection.compatibilityFingerprint
    or compatibility.computedAtUtc ~= selection.compatibilityComputedAtUtc
    or not matchesCatalogIdentity(compatibility, selection.catalog) then
    return false, 'run-manifest-compatibility-invalid'
  end

  if #manifest.trustedCandidates ~= #(selection.trusted or {}) then
    return false, 'run-manifest-trusted-count-mismatch'
  end
  for index, trustedSelection in ipairs(selection.trusted or {}) do
    if not validateSelectionRecord(manifest.trustedCandidates[index], trustedSelection) then
      return false, 'run-manifest-trusted-selection-mismatch'
    end
  end
  if selection.canary then
    if manifest.canary == jsonReader.null or not validateSelectionRecord(manifest.canary, selection.canary) then
      return false, 'run-manifest-canary-mismatch'
    end
  elseif manifest.canary ~= jsonReader.null then
    return false, 'run-manifest-unexpected-canary'
  end

  local expectedOrder = { 'safe-snapshot-baseline' }
  for _, trustedSelection in ipairs(selection.trusted or {}) do
    expectedOrder[#expectedOrder + 1] = trustedSelection.candidateId
  end
  if selection.canary then expectedOrder[#expectedOrder + 1] = selection.canary.candidateId end
  if #manifest.registrationOrder ~= #expectedOrder then return false, 'run-manifest-order-count-mismatch' end
  for index, candidateId in ipairs(expectedOrder) do
    if manifest.registrationOrder[index] ~= candidateId then return false, 'run-manifest-order-mismatch' end
  end

  local safety = manifest.safety
  if not exactObject(safety, {
    'readOnly', 'invokeFunctions', 'invokeRpcs', 'manualOnRep', 'mutation', 'runtimeDiscovery',
    'freeFormHookPath', 'maximumCanaries'
  }) or safety.readOnly ~= true or safety.invokeFunctions ~= false or safety.invokeRpcs ~= false
    or safety.manualOnRep ~= false or safety.mutation ~= false or safety.runtimeDiscovery ~= false
    or safety.freeFormHookPath ~= false or safety.maximumCanaries ~= 1 then
    return false, 'run-manifest-safety-invalid'
  end
  return true, nil
end

return guard
