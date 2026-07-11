local json = require('json')
local snapshotSampler = require('snapshot_sampler')

local peerSampler = {}

local CATEGORY_FIELD_ORDER = {
  health = { 'currentHealth', 'currentMaxHealth', 'baseMaxHealth', 'maxHealthMultiplier' },
  crystals = { 'crystals' },
  slots = { 'weaponModSlots', 'abilityModSlots', 'meleeModSlots', 'perkSlots' },
  equipment = { 'weaponFingerprint', 'abilityFingerprint', 'meleeFingerprint' }
}

local CATEGORY_ORDER = { 'health', 'crystals', 'slots', 'equipment' }

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function clampInteger(value, fallback, minimum, maximum)
  local numberValue = math.floor(tonumber(value) or fallback)
  if numberValue < minimum then numberValue = minimum end
  if numberValue > maximum then numberValue = maximum end
  return numberValue
end

local function clampNumber(value, fallback, minimum, maximum)
  local numberValue = tonumber(value) or fallback
  if numberValue < minimum then numberValue = minimum end
  if numberValue > maximum then numberValue = maximum end
  return numberValue
end

local function observedRoleFromAuthority(authorityStatus)
  if authorityStatus == 'runtime-non-authority' then return 'joined-client' end
  if authorityStatus == 'runtime-authority' then return 'host' end
  return 'unknown'
end

local function objectFingerprint(safe, object)
  if not safe.isValidObject(object) then return '', 'invalid-object' end
  local fullName, fullNameErr = safe.getFullName(object)
  if fullNameErr then return '', 'full-name-unavailable' end
  local fingerprint = safe.fingerprintValue(fullName or '')
  if fingerprint == '' then return '', 'fingerprint-unavailable' end
  return fingerprint, nil
end

local function resolveLocalPlayerState(safe)
  local controller, controllerErr = safe.findFirst('CrabPC')
  if controllerErr or not safe.isValidObject(controller) then return nil end
  local playerState, playerStateErr = safe.getProperty(controller, 'PlayerState')
  if playerStateErr or not safe.isValidObject(playerState) then return nil end
  local fingerprint = objectFingerprint(safe, playerState)
  if fingerprint == '' then return nil end
  return { object = playerState, fingerprint = fingerprint }
end

-- v1.1.0 intentionally does not enumerate CrabPS instances.  Runtime class
-- enumeration is discovery, not a proven safe readiness read.  The pair is
-- correlated offline from two local bundles; remote visibility stays deferred
-- until a separately reviewed collection mechanism exists.
local function collectCandidates(safe, expectedLocalFingerprint, cap)
  local candidates = {}
  local seen = {}
  local discoveryStatus = 'unsupported'

  local function add(object)
    if #candidates >= cap or not safe.isValidObject(object) then return end
    local fingerprint = objectFingerprint(safe, object)
    if fingerprint == '' or seen[fingerprint] then return end
    seen[fingerprint] = true
    candidates[#candidates + 1] = {
      object = object,
      fingerprint = fingerprint,
      localSubject = fingerprint == expectedLocalFingerprint
    }
  end

  local localCandidate = resolveLocalPlayerState(safe)
  if localCandidate then add(localCandidate.object) end

  table.sort(candidates, function(left, right)
    if left.localSubject ~= right.localSubject then return left.localSubject end
    return left.fingerprint < right.fingerprint
  end)
  return candidates, discoveryStatus
end

local function fieldSignature(field)
  if type(field) ~= 'table' then return '<missing>' end
  return table.concat({
    tostring(field.status or ''),
    tostring(field.value == nil and '' or field.value),
    tostring(field.valueFingerprint or ''),
    tostring(field.reason or '')
  }, ':')
end

local function categorySignature(categoryName, category)
  local parts = { tostring(category and category.result or '') }
  local fields = category and category.fields or {}
  for _, fieldName in ipairs(CATEGORY_FIELD_ORDER[categoryName] or {}) do
    parts[#parts + 1] = fieldName .. '=' .. fieldSignature(fields[fieldName])
  end
  return table.concat(parts, '|')
end

local function subjectSignature(subject)
  local parts = {
    tostring(subject.playerStateFingerprint or ''),
    tostring(subject.relation or ''),
    tostring(subject.authorityStatus or ''),
    tostring(subject.observedRole or ''),
    tostring(subject.stability or '')
  }
  for _, categoryName in ipairs(CATEGORY_ORDER) do
    local category = subject.categoryResults and subject.categoryResults[categoryName] or {}
    parts[#parts + 1] = categoryName .. '=' .. categorySignature(categoryName, category)
  end
  return table.concat(parts, '#')
end

local function readSubjects(safe, state, cap)
  local candidates, discoveryStatus = collectCandidates(safe, tostring(state.localPlayerStateFingerprint or ''), cap)
  local subjects = {}
  local stableCount = 0
  local aggregate = discoveryStatus == 'unsupported' and 'partial' or 'ok'

  for _, candidate in ipairs(candidates) do
    local relation = candidate.localSubject and 'local' or 'remote-visible'
    local playerState = candidate.object
    local authorityStatus = safe.authorityStatus(playerState)
    local categories, categoryResult = snapshotSampler.readReviewedScalarCategories(safe, playerState)
    -- Drop the only short-lived UObject reference before retaining any row
    -- material.  The candidate table itself never escapes this invocation.
    candidate.object = nil
    playerState = nil
    local stability = relation == 'local' and 'stable' or 'warming'
    if stability == 'stable' then stableCount = stableCount + 1 end
    local subject = {
      playerStateFingerprint = candidate.fingerprint,
      relation = relation,
      visibility = relation,
      authorityStatus = authorityStatus,
      observedRole = observedRoleFromAuthority(authorityStatus),
      stability = stability,
      categoryResults = categories
    }
    subjects[#subjects + 1] = subject
    if categoryResult == 'error' then
      aggregate = 'error'
    elseif categoryResult ~= 'ok' and aggregate == 'ok' then
      aggregate = 'partial'
    end
  end

  if #subjects == 0 then aggregate = 'unsupported' end
  return subjects, aggregate, discoveryStatus, stableCount
end

function peerSampler.new(config, safe, evidenceWriter, state)
  local o = {
    config = config or {},
    safe = safe,
    evidenceWriter = evidenceWriter,
    state = state,
    active = (config or {}).readinessCampaignEnabled == true
      and tostring((config or {}).campaignProfile or '') == 'crabsync-readiness-campaign'
      and (config or {}).readinessPeerSnapshotsEnabled == true,
    subjectCap = clampInteger((config or {}).readinessMaxPeers, 4, 1, 4),
    sampleIntervalSeconds = clampNumber((config or {}).readinessScalarIntervalSeconds, 1, 1, 60),
    unchangedHeartbeatSeconds = clampNumber((config or {}).readinessUnchangedHeartbeatSeconds, 30, 10, 600),
    lastSampleAt = nil,
    lastWrittenAt = nil,
    lastSignature = nil,
    lifecycleResetPending = false,
    peerSnapshotCount = 0
  }

  function o:setActive(active)
    self.active = active == true
  end

  function o:resetLifecycle(markLifecycleReset)
    self.lastSampleAt = nil
    self.lastWrittenAt = nil
    self.lastSignature = nil
    self.lifecycleResetPending = markLifecycleReset ~= false
  end

  function o:writeObservation(subjects, result, changeKind, now)
    local lifecycle = {
      state = tostring(self.state.lifecycleState or 'unknown'),
      generation = tonumber(self.state.lifecycleGeneration) or 0,
      context = tostring(self.state.context or 'unknown'),
      stable = self.state.stability.ready == true and self.state.lifecycleState == 'stable'
    }
    local row = {
      schemaVersion = 1,
      recordType = 'readiness-peer-snapshot',
      event = 'Readiness.PeerSnapshot',
      readinessSchema = 'peer-snapshot-v1',
      campaignId = tostring(self.config.campaignId or ''),
      campaignGeneration = tonumber(self.config.campaignGeneration) or 0,
      sessionId = tostring(self.state.sessionId or ''),
      machineId = tostring(self.config.machineId or ''),
      sequence = self.state:nextSequence(),
      timestampUtc = utcNow(),
      selectedRole = tostring(self.state.selectedRole or 'unknown'),
      observedRole = tostring(self.state.observedRole or 'unknown'),
      authorityStatus = tostring(self.state.authorityStatus or 'unknown'),
      profileId = 'crabsync-readiness-campaign',
      readinessPairId = tostring(self.config.readinessPairId or ''),
      lifecycle = lifecycle,
      source = {
        worldFingerprint = tostring(self.state.worldFingerprint or ''),
        localPlayerStateFingerprint = tostring(self.state.localPlayerStateFingerprint or '')
      },
      subjectCap = self.subjectCap,
      subjects = json.array(subjects),
      result = result,
      changeKind = changeKind,
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
      self.lastWrittenAt = now
      self.peerSnapshotCount = self.peerSnapshotCount + 1
    end
    return writeOk
  end

  function o:onTick()
    if not self.active then return { sampled = false, reason = 'disabled' } end
    if self.state.stability.ready ~= true or self.state.lifecycleState ~= 'stable' then
      return { sampled = false, reason = 'unstable' }
    end
    local now = os.time()
    if self.lastSampleAt ~= nil and (now - self.lastSampleAt) < self.sampleIntervalSeconds then
      return { sampled = false, reason = 'interval' }
    end
    self.lastSampleAt = now

    local subjects, result, discoveryStatus, stableCount = readSubjects(self.safe, self.state, self.subjectCap)
    local signatureParts = { result, discoveryStatus }
    for _, subject in ipairs(subjects) do signatureParts[#signatureParts + 1] = subjectSignature(subject) end
    local signature = table.concat(signatureParts, '\n')
    local changeKind = self.lifecycleResetPending and 'lifecycle-reset'
      or (self.lastSignature == nil and 'initial'
        or (self.lastSignature ~= signature and 'changed' or 'unchanged-heartbeat'))
    local shouldWrite = changeKind ~= 'unchanged-heartbeat'
      or self.lastWrittenAt == nil
      or (now - self.lastWrittenAt) >= self.unchangedHeartbeatSeconds
    local writeOk = true
    if shouldWrite then
      writeOk = self:writeObservation(subjects, result, changeKind, now)
      if writeOk then
        self.lastSignature = signature
        self.lifecycleResetPending = false
      end
    end
    self.state:setPeerSamplingSummary({
      enabled = true,
      peerSnapshotCount = self.peerSnapshotCount,
      visiblePlayerCount = #subjects,
      stablePlayerCount = stableCount,
      lastResult = result,
      lastChangeKind = changeKind,
      lastSampleAtUtc = utcNow(),
      reason = discoveryStatus == 'unsupported' and 'remote-visible PlayerState enumeration is deferred; local-only paired evidence remains available' or ''
    })
    return {
      sampled = true,
      result = result,
      changeKind = changeKind,
      written = shouldWrite and writeOk,
      visiblePlayerCount = #subjects,
      stablePlayerCount = stableCount
    }
  end

  function o:summary()
    return self.state:peerSamplingSummary()
  end

  return o
end

return peerSampler
