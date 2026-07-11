local json = require('json')
local artifactGuard = require('progressive_artifact_guard')

local manifestWriter = {}

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function writeClosed(path, text)
  local file = io.open(path, 'w')
  if not file then return false end
  local ok = pcall(function()
    file:write(text)
    file:flush()
  end)
  file:close()
  return ok
end

local function writeAtomic(path, text, token)
  local tempPath = path .. '.' .. tostring(token):gsub('[^A-Za-z0-9_-]', '_') .. '.tmp'
  if not writeClosed(tempPath, text) then return false end
  local renamed = os.rename(tempPath, path)
  if not renamed then os.remove(tempPath); return false end
  return true
end

local function selectionRecord(selection)
  return {
    candidateId = selection.candidateId,
    hookPathFingerprint = selection.hookPathFingerprint,
    validationDepth = selection.validationDepth
  }
end

function manifestWriter.write(sessionId, config, selection)
  if type(selection) ~= 'table' or selection.enabled ~= true then return false end
  local trusted = json.array({})
  local registrationOrder = json.array({ 'safe-snapshot-baseline' })
  for _, item in ipairs(selection.trusted or {}) do
    trusted[#trusted + 1] = selectionRecord(item)
    registrationOrder[#registrationOrder + 1] = item.candidateId
  end
  local canary = json.null
  if selection.canary then
    canary = selectionRecord(selection.canary)
    registrationOrder[#registrationOrder + 1] = selection.canary.candidateId
  end
  local manifest = {
    schemaVersion = 'hook-run-manifest-v1',
    runId = selection.runId,
    sessionId = tostring(sessionId or ''),
    campaignGeneration = tonumber(config.campaignGeneration) or 0,
    createdAtUtc = utcNow(),
    runType = selection.runType,
    selectedRole = tostring(config.selectedRole or ''),
    compatibility = {
      schemaVersion = 'compatibility-fingerprint-v1',
      gameBuild = selection.gameBuild,
      ue4ssVersion = selection.ue4ssVersion,
      coverageCatalogHash = selection.catalog.coverageCatalogHash,
      hookCatalogIdentity = selection.catalog.hookCatalogIdentity,
      callbackImplementationVersion = selection.catalog.callbackImplementationVersion,
      callbackSchemaVersion = selection.catalog.callbackSchemaVersion,
      validationBehaviorVersion = selection.catalog.validationBehaviorVersion,
      fingerprint = selection.compatibilityFingerprint,
      computedAtUtc = selection.compatibilityComputedAtUtc
    },
    safeSnapshotBaseline = true,
    trustedCandidates = trusted,
    canary = canary,
    registrationOrder = registrationOrder,
    automaticInProcessAdvance = false,
    safety = {
      readOnly = true,
      invokeFunctions = false,
      invokeRpcs = false,
      manualOnRep = false,
      mutation = false,
      runtimeDiscovery = false,
      freeFormHookPath = false,
      maximumCanaries = 1
    }
  }
  local path = 'Mods/CrabRuntimeProbe/Scripts/results/hook_run_manifest_' .. selection.runId .. '.json'
  local existing = io.open(path, 'r')
  if existing ~= nil then
    existing:close()
    -- The dashboard publishes this immutable identity before arming config.
    -- Existing content must match the exact runtime selection; mere existence
    -- is never authorization to register.
    return artifactGuard.validateRunManifest(path, sessionId, config, selection)
  end
  if not writeAtomic(path, json.encode(manifest), selection.runId) then return false end
  return artifactGuard.validateRunManifest(path, sessionId, config, selection)
end

return manifestWriter
