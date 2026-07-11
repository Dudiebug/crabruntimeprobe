local json = require('json')

local journalFactory = {}

local MAX_RECORDS = 8192
local MAX_LINE_BYTES = 1024

local PHASES = {
  registration = true,
  pre = true,
  post = true,
  ['blueprint-post-only'] = true,
  runtime = true
}

local BOUNDARIES = {
  ['registration-begin'] = true,
  ['registration-complete'] = true,
  ['registration-failed'] = true,
  ['callback-enter'] = true,
  ['context-resolve-begin'] = true,
  ['context-resolve-complete'] = true,
  ['scope-resolve-begin'] = true,
  ['scope-resolve-complete'] = true,
  ['prestate-read-begin'] = true,
  ['prestate-read-complete'] = true,
  ['arguments-read-begin'] = true,
  ['arguments-read-complete'] = true,
  ['poststate-read-begin'] = true,
  ['poststate-read-complete'] = true,
  ['evidence-write-begin'] = true,
  ['evidence-write-complete'] = true,
  ['callback-exit'] = true,
  ['run-complete'] = true
}

local COMPLETED_BOUNDARIES = {
  ['registration-complete'] = true,
  ['context-resolve-complete'] = true,
  ['scope-resolve-complete'] = true,
  ['prestate-read-complete'] = true,
  ['arguments-read-complete'] = true,
  ['poststate-read-complete'] = true,
  ['evidence-write-complete'] = true,
  ['callback-exit'] = true,
  ['run-complete'] = true
}

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function monotonicMicros()
  local value = os.clock()
  if type(value) ~= 'number' or value < 0 or value ~= value then return 0 end
  return math.floor(value * 1000000)
end

local function safeName(value)
  local text = tostring(value or '')
  if #text < 1 or #text > 128 or text:match('^[A-Za-z0-9_-]+$') == nil then return nil end
  return text
end

local function fileExists(path)
  local file = io.open(path, 'r')
  if not file then return false end
  file:close()
  return true
end

local function appendDurable(path, line)
  local file = io.open(path, 'a')
  if not file then return false, 'open-failed' end
  local ok, err = pcall(function()
    file:write(line)
    file:write('\n')
    file:flush()
  end)
  file:close()
  if not ok then return false, tostring(err) end
  return true, nil
end

local function writeNewAtomic(path, text, token)
  if fileExists(path) then return false end
  local tempPath = path .. '.' .. tostring(token):gsub('[^A-Za-z0-9_-]', '_') .. '.tmp'
  os.remove(tempPath)
  local file = io.open(tempPath, 'w')
  if not file then return false end
  local ok = pcall(function()
    file:write(text)
    file:write('\n')
    file:flush()
  end)
  file:close()
  if not ok or fileExists(path) then os.remove(tempPath); return false end
  if not os.rename(tempPath, path) then os.remove(tempPath); return false end
  return true
end

function journalFactory.new(runId)
  local normalizedRunId = safeName(runId)
  local o = {
    runId = normalizedRunId or 'invalid',
    path = 'Mods/CrabRuntimeProbe/Scripts/results/hook_breadcrumbs_' .. tostring(normalizedRunId or 'invalid') .. '.jsonl',
    consumedPath = 'Mods/CrabRuntimeProbe/Scripts/results/hook_run_consumed_' .. tostring(normalizedRunId or 'invalid') .. '.json',
    sequence = 0,
    recordCount = 0,
    faulted = normalizedRunId == nil,
    faultReason = normalizedRunId == nil and 'invalid-run-id' or '',
    lastBoundary = '',
    lastCompletedBoundary = '',
    lastCandidateId = '',
    lastInvocationId = ''
  }

  if not o.faulted and (fileExists(o.path) or fileExists(o.consumedPath)) then
    o.faulted = true
    o.faultReason = 'research-run-already-consumed'
  elseif not o.faulted then
    local marker = json.encode({
      schemaVersion = 'hook-run-consumed-v1',
      runId = o.runId,
      consumedAtUtc = utcNow(),
      automaticRearmAllowed = false
    })
    if #marker > MAX_LINE_BYTES or not writeNewAtomic(o.consumedPath, marker, o.runId) then
      o.faulted = true
      o.faultReason = 'run-consumption-marker-write-failed'
    end
  end

  function o:trip(reason)
    self.faulted = true
    self.faultReason = tostring(reason or 'journal-fault')
    return false
  end

  function o:write(candidate, validationDepth, candidateRole, invocationId, phase, boundary, lifecycleGeneration)
    if self.faulted then return false end
    if self.recordCount >= MAX_RECORDS then return self:trip('journal-record-cap-reached') end
    if type(candidate) ~= 'table' or safeName(candidate.id) == nil
      or type(candidate.hookPathFingerprint) ~= 'string'
      or #candidate.hookPathFingerprint ~= 64
      or candidate.hookPathFingerprint:match('^[a-f0-9]+$') == nil then
      return self:trip('journal-candidate-invalid')
    end
    local depth = tonumber(validationDepth)
    local generation = tonumber(lifecycleGeneration)
    local normalizedInvocationId = safeName(invocationId)
    if depth == nil or math.floor(depth) ~= depth or depth < 1 or depth > 7
      or (candidateRole ~= 'trusted' and candidateRole ~= 'canary')
      or normalizedInvocationId == nil or PHASES[phase] ~= true or BOUNDARIES[boundary] ~= true
      or generation == nil or generation < 0 or math.floor(generation) ~= generation then
      return self:trip('journal-record-invalid')
    end

    local nextSequence = self.sequence + 1
    local record = {
      schemaVersion = 'hook-breadcrumb-v1',
      sequence = nextSequence,
      runId = self.runId,
      candidateId = candidate.id,
      hookPathFingerprint = candidate.hookPathFingerprint,
      validationDepth = depth,
      candidateRole = candidateRole,
      invocationId = normalizedInvocationId,
      phase = phase,
      boundary = boundary,
      lifecycleGeneration = generation,
      timestampUtc = utcNow(),
      monotonicMicros = monotonicMicros()
    }
    local line = json.encode(record)
    if #line > MAX_LINE_BYTES then return self:trip('journal-line-cap-reached') end
    local ok = appendDurable(self.path, line)
    if not ok then return self:trip('journal-write-failed') end
    self.sequence = nextSequence
    self.recordCount = self.recordCount + 1
    self.lastBoundary = boundary
    self.lastCandidateId = candidate.id
    self.lastInvocationId = normalizedInvocationId
    if COMPLETED_BOUNDARIES[boundary] then self.lastCompletedBoundary = boundary end
    return true
  end

  function o:summary()
    return {
      state = self.faulted and 'faulted' or 'healthy',
      sequence = self.sequence,
      recordCount = self.recordCount,
      recordCap = MAX_RECORDS,
      lastBoundary = self.lastBoundary,
      lastCompletedBoundary = self.lastCompletedBoundary,
      lastCandidateId = self.lastCandidateId,
      faultReason = self.faultReason
    }
  end

  return o
end

return journalFactory
