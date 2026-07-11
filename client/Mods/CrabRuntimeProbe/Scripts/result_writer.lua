local crpLog = require('crp_log')
local json = require('json')
local writer = {}

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

local function fileExists(path)
  local f = io.open(path, 'r')
  if not f then return false end
  f:close()
  return true
end

function writer.new(sessionId, config)
  local o = {
    sessionId = sessionId,
    config = config,
    resultDir = 'Mods/CrabRuntimeProbe/Scripts/results',
    resultPath = 'Mods/CrabRuntimeProbe/Scripts/results/probe_results_' .. sessionId .. '.jsonl',
    fallbackPath = 'Mods/CrabRuntimeProbe/Scripts/probe_results_' .. sessionId .. '.jsonl',
    warnedFallback = false,
    warnedFailure = false,
    activeResultPath = nil
  }
  if fileExists(o.resultPath) then
    o.activeResultPath = o.resultPath
  elseif fileExists(o.fallbackPath) then
    o.activeResultPath = o.fallbackPath
  end

  function o:write(record)
    if self.config.writeJsonlResults == false then return true end
    record.timestamp = record.timestamp or utcNow()
    record.sessionId = self.sessionId
    local line = json.encode(record)
    if self.activeResultPath and appendLine(self.activeResultPath, line) then return true end
    local primaryCandidate = self.activeResultPath == self.resultPath and self.fallbackPath or self.resultPath
    local fallbackCandidate = primaryCandidate == self.resultPath and self.fallbackPath or self.resultPath
    if appendLine(primaryCandidate, line) then
      self.activeResultPath = primaryCandidate
      if primaryCandidate == self.fallbackPath and not self.warnedFallback then
        crpLog.line('[CrabRuntimeProbe] primary result path unavailable; using fallback')
        self.warnedFallback = true
      end
      return true
    end
    if appendLine(fallbackCandidate, line) then
      self.activeResultPath = fallbackCandidate
      if fallbackCandidate == self.fallbackPath then
        if not self.warnedFallback then
          crpLog.line('[CrabRuntimeProbe] primary result path unavailable; using fallback')
          self.warnedFallback = true
        end
      end
      return true
    end
      if not self.warnedFailure then
        crpLog.line('[CrabRuntimeProbe] ERROR: result write failed for primary and fallback')
        self.warnedFailure = true
      end
    return false
  end

  return o
end

return writer
