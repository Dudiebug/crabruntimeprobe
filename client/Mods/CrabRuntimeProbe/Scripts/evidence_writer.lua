local crpLog = require('crp_log')
local json = require('json')

local evidenceWriter = {}

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function appendLine(path, line)
  local f = io.open(path, 'a')
  if f then
    f:write(line .. '\n')
    f:close()
    return true
  end
  return false
end

local function writeFile(path, text)
  local f = io.open(path, 'w')
  if f then
    f:write(text)
    f:close()
    return true
  end
  return false
end

local function writeFileAtomic(path, text, token)
  local tempPath = path .. '.' .. tostring(token or 'session'):gsub('[^%w_%-]', '_') .. '.tmp'
  local backupPath = path .. '.previous'
  if not writeFile(tempPath, text) then return false end
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

local function readFile(path)
  local f = io.open(path, 'r')
  if not f then return nil end
  local text = f:read('*a')
  f:close()
  return text
end

local function touchFile(path)
  local f = io.open(path, 'a')
  if f then
    f:close()
    return true
  end
  return false
end

local function safetyGates(config)
  return {
    allowHudTickHook = config.allowHudTickHook == true,
    allowDeepArrayProbes = config.allowDeepArrayProbes == true,
    allowInventoryInfoProbes = config.allowInventoryInfoProbes == true,
    allowHealthProbes = config.allowHealthProbes == true,
    allowIdentityProbes = config.allowIdentityProbes == true,
    allowRawIdentityEvidence = config.allowRawIdentityEvidence == true,
    allowResourceVisibilityProbes = config.allowResourceVisibilityProbes == true,
    allowCrystalsReadProbes = config.allowCrystalsReadProbes == true,
    allowSlotsReadProbes = config.allowSlotsReadProbes == true,
    allowSafeScalarWatchProbes = config.allowSafeScalarWatchProbes == true,
    allowPerkDataAssetCatalogProbes = config.allowPerkDataAssetCatalogProbes == true,
    allowMaxSafePlayRecorderProbes = config.allowMaxSafePlayRecorderProbes == true,
    allowInventoryArrayShallowProbes = config.allowInventoryArrayShallowProbes == true,
    allowInventoryArrayShapeConfirmProbes = config.allowInventoryArrayShapeConfirmProbes == true,
    allowInventoryUserdataIntrospectionProbes = config.allowInventoryUserdataIntrospectionProbes == true,
    allowInventoryArrayCountProbes = config.allowInventoryArrayCountProbes == true,
    allowInventoryElementDataAssetReadProbes = config.allowInventoryElementDataAssetReadProbes == true,
    fullObserveEnabled = config.fullObserveEnabled == true,
    snapshotSamplerEnabled = config.snapshotSamplerEnabled == true,
    allowPassiveObservationHooks = config.allowPassiveObservationHooks == true,
    allowFullObserveInventoryStages = config.allowFullObserveInventoryStages == true,
    allowFullObserveRuntimeDiscovery = config.allowFullObserveRuntimeDiscovery == true,
    statusWriterEnabled = config.statusWriterEnabled == true,
    allowWriteProbes = config.allowWriteProbes == true,
    allowRpcProbes = config.allowRpcProbes == true,
    allowJoinedClientDeepProbes = config.allowJoinedClientDeepProbes == true,
    allowUnknownRoleProbes = config.allowUnknownRoleProbes == true
  }
end

local function unsafeActiveGates(config)
  local gates = safetyGates(config)
  local active = {}
  for _, key in ipairs({
    'allowHudTickHook',
    'allowDeepArrayProbes',
    'allowInventoryInfoProbes',
    'allowRawIdentityEvidence',
    'allowWriteProbes',
    'allowRpcProbes',
    'allowJoinedClientDeepProbes',
    'allowUnknownRoleProbes',
    'allowPassiveObservationHooks',
    'allowFullObserveInventoryStages',
    'allowFullObserveRuntimeDiscovery'
  }) do
    if gates[key] == true then active[#active + 1] = key end
  end
  return active
end

local function fileExists(path)
  local f = io.open(path, 'r')
  if not f then return false end
  f:close()
  return true
end

local function activeResearchGates(config)
  local gates = safetyGates(config)
  local active = {}
  for _, key in ipairs({
    'allowHudTickHook',
    'allowDeepArrayProbes',
    'allowInventoryInfoProbes',
    'allowHealthProbes',
    'allowIdentityProbes',
    'allowRawIdentityEvidence',
    'allowResourceVisibilityProbes',
    'allowCrystalsReadProbes',
    'allowSlotsReadProbes',
    'allowSafeScalarWatchProbes',
    'allowPerkDataAssetCatalogProbes',
    'allowMaxSafePlayRecorderProbes',
    'allowInventoryArrayShallowProbes',
    'allowInventoryArrayShapeConfirmProbes',
    'allowInventoryUserdataIntrospectionProbes',
    'allowInventoryArrayCountProbes',
    'allowInventoryElementDataAssetReadProbes',
    'fullObserveEnabled',
    'snapshotSamplerEnabled',
    'allowPassiveObservationHooks',
    'allowFullObserveInventoryStages',
    'allowFullObserveRuntimeDiscovery',
    'statusWriterEnabled',
    'allowWriteProbes',
    'allowRpcProbes',
    'allowJoinedClientDeepProbes',
    'allowUnknownRoleProbes'
  }) do
    if gates[key] == true then
      active[#active + 1] = key
    end
  end
  return active
end

local function activeObservationGates(config)
  local unsafe = {}
  for _, key in ipairs(unsafeActiveGates(config)) do unsafe[key] = true end
  local active = {}
  for _, key in ipairs(activeResearchGates(config)) do
    if not unsafe[key] then active[#active + 1] = key end
  end
  return active
end

local function readSequenceForSession(sessionId, config)
  local maximum = tonumber(config.resumeEvidenceSequence) or 0
  for _, path in ipairs({
    'Mods/CrabRuntimeProbe/Scripts/results/full_observe_sequence.txt',
    'Mods/CrabRuntimeProbe/Scripts/results/full_observe_sequence.txt.previous',
    'Mods/CrabRuntimeProbe/Scripts/full_observe_sequence.txt',
    'Mods/CrabRuntimeProbe/Scripts/full_observe_sequence.txt.previous'
  }) do
    local text = readFile(path)
    if text then
      local storedSession = text:match('sessionId=([^\r\n]+)')
      local storedCampaign = text:match('campaignId=([^\r\n]+)')
      local storedGeneration = tonumber(text:match('campaignGeneration=(%d+)'))
      if storedSession == tostring(sessionId) and storedCampaign == tostring(config.campaignId)
        and storedGeneration == tonumber(config.campaignGeneration) then
        maximum = math.max(maximum, tonumber(text:match('sequence=(%d+)')) or 0)
      end
    end
  end
  return maximum
end

local function parseBuildInfo(lines)
  local info = {}
  for _, line in ipairs(lines or {}) do
    local k, v = tostring(line):match('^%s*([%w_]+)%s*=%s*(.-)%s*$')
    if k and k ~= 'source_repo_path' then
      info[k] = v
    end
  end
  return info
end

local function configSnapshot(config)
  local snapshot = {}
  for k, v in pairs(config) do
    if type(v) == 'string' or type(v) == 'number' or type(v) == 'boolean' then
      snapshot[k] = v
    end
  end
  return snapshot
end

local function withDefaults(record, sessionId, config)
  record.timestamp = record.timestamp or utcNow()
  record.sessionId = sessionId
  record.game = 'Crab Champions'
  record.mod = 'CrabRuntimeProbe'
  record.schemaVersion = record.schemaVersion or 1
  record.mode = record.mode or tostring(config.mode)
  record.tickDriver = record.tickDriver or tostring(config.tickDriver)
  record.safetyGates = record.safetyGates or safetyGates(config)
  return record
end

function evidenceWriter.new(sessionId, config)
  local o = {
    sessionId = sessionId,
    config = config,
    resultDir = 'Mods/CrabRuntimeProbe/Scripts/results',
    evidencePath = 'Mods/CrabRuntimeProbe/Scripts/results/access_evidence_' .. sessionId .. '.jsonl',
    fallbackEvidencePath = 'Mods/CrabRuntimeProbe/Scripts/access_evidence_' .. sessionId .. '.jsonl',
    manifestPath = 'Mods/CrabRuntimeProbe/Scripts/results/session_manifest_' .. sessionId .. '.json',
    fallbackManifestPath = 'Mods/CrabRuntimeProbe/Scripts/session_manifest_' .. sessionId .. '.json',
    warnedFallback = false,
    warnedFailure = false,
    activeEvidencePath = nil
  }

  function o:writeEncodedLine(line)
    if self.activeEvidencePath and appendLine(self.activeEvidencePath, line) then return true end
    local primaryCandidate = self.activeEvidencePath == self.evidencePath and self.fallbackEvidencePath or self.evidencePath
    local fallbackCandidate = primaryCandidate == self.evidencePath and self.fallbackEvidencePath or self.evidencePath
    if appendLine(primaryCandidate, line) then
      self.activeEvidencePath = primaryCandidate
      if primaryCandidate == self.fallbackEvidencePath and not self.warnedFallback then
        crpLog.line('[CrabRuntimeProbe] primary evidence path unavailable; using fallback')
        self.warnedFallback = true
      end
      return true
    end
    if appendLine(fallbackCandidate, line) then
      self.activeEvidencePath = fallbackCandidate
      if fallbackCandidate == self.fallbackEvidencePath then
        if not self.warnedFallback then
          crpLog.line('[CrabRuntimeProbe] primary evidence path unavailable; using fallback')
          self.warnedFallback = true
        end
      end
      return true
    end
      if not self.warnedFailure then
        crpLog.line('[CrabRuntimeProbe] ERROR: evidence write failed for primary and fallback')
        self.warnedFailure = true
      end
    return false
  end

  function o:writeEvidence(record)
    if self.config.writeJsonlResults == false then return true end
    return self:writeEncodedLine(json.encode(withDefaults(record, self.sessionId, self.config)))
  end

  function o:writeSnapshotObservation(record)
    if self.config.writeJsonlResults == false then return true end
    if type(record) ~= 'table'
      or record.recordType ~= 'snapshot-observation'
      or record.schemaVersion ~= 1
      or tostring(record.sessionId or '') ~= tostring(self.sessionId) then
      return false
    end
    -- Snapshot records deliberately bypass generic evidence defaults so every
    -- emitted key conforms to the strict snapshot-observation-v1 schema.
    return self:writeEncodedLine(json.encode(record))
  end

  function o:writeSessionManifest(buildInfoLines)
    local existingText = readFile(self.manifestPath) or readFile(self.manifestPath .. '.previous')
      or readFile(self.fallbackManifestPath) or readFile(self.fallbackManifestPath .. '.previous')
    local existingOutputPath = existingText and existingText:match('"evidenceOutputPath"%s*:%s*"([^"]+)"') or nil
    if self.config.writeJsonlResults ~= false then
      if (existingOutputPath == self.evidencePath or existingOutputPath == self.fallbackEvidencePath)
        and touchFile(existingOutputPath) then
        self.activeEvidencePath = existingOutputPath
      elseif fileExists(self.evidencePath) and touchFile(self.evidencePath) then
        self.activeEvidencePath = self.evidencePath
      elseif fileExists(self.fallbackEvidencePath) and touchFile(self.fallbackEvidencePath) then
        self.activeEvidencePath = self.fallbackEvidencePath
      elseif touchFile(self.evidencePath) then
        self.activeEvidencePath = self.evidencePath
      elseif touchFile(self.fallbackEvidencePath) then
        self.activeEvidencePath = self.fallbackEvidencePath
      end
    end
    local existingSession = existingText and existingText:match('"sessionId"%s*:%s*"([^"]+)"') or nil
    local existingStartedAt = existingSession == self.sessionId and existingText:match('"startedAt"%s*:%s*"([^"]+)"') or nil
    local existingInitialSequence = existingSession == self.sessionId and tonumber(existingText:match('"initialEvidenceSequence"%s*:%s*(%d+)')) or nil
    local existingRevision = existingSession == self.sessionId and tonumber(existingText:match('"manifestRevision"%s*:%s*(%d+)')) or 0
    local now = utcNow()
    local unsafeGates = unsafeActiveGates(self.config)
    local resumeSequence = readSequenceForSession(self.sessionId, self.config)
    local manifest = {
      sessionId = self.sessionId,
      startedAt = existingStartedAt or now,
      resumedAt = existingStartedAt and now or '',
      manifestRevision = existingRevision + 1,
      initialEvidenceSequence = existingInitialSequence or resumeSequence,
      resumeEvidenceSequence = resumeSequence,
      game = 'Crab Champions',
      mod = 'CrabRuntimeProbe',
      schemaVersion = self.config.fullObserveEnabled == true and 2 or 1,
      runtimeProbeVersion = 'unknown',
      buildInfo = parseBuildInfo(buildInfoLines),
      config = configSnapshot(self.config),
      probeSet = tostring(self.config.probeSet),
      tickDriver = tostring(self.config.tickDriver),
      safetyGates = safetyGates(self.config),
      activeResearchGates = activeResearchGates(self.config),
      activeObservationGates = activeObservationGates(self.config),
      unsafeActiveGates = unsafeGates,
      evidenceOutputPath = self.activeEvidencePath or '',
      warning = #unsafeGates > 0 and ('unsafe research gates enabled: ' .. table.concat(unsafeGates, ', ')) or ''
    }
    local text = json.encode(manifest)
    if writeFileAtomic(self.manifestPath, text, self.sessionId) then return true end
    return writeFileAtomic(self.fallbackManifestPath, text, self.sessionId)
  end

  return o
end

return evidenceWriter
