local crpLog = require('crp_log')
local json = require('json')

local statusWriter = {}

local function clamp(value, fallback, minimum, maximum)
  local numberValue = tonumber(value) or fallback
  numberValue = math.floor(numberValue)
  if numberValue < minimum then numberValue = minimum end
  if numberValue > maximum then numberValue = maximum end
  return numberValue
end

local function safeName(value)
  local text = tostring(value or ''):gsub('[^%w_%-]', '_')
  if text == '' then text = 'unknown' end
  return text
end

local function ensureDirectory(path)
  if type(os.execute) ~= 'function' then return end
  pcall(function() os.execute('if not exist "' .. path .. '" mkdir "' .. path .. '"') end)
end

local function writeClosedFile(path, text)
  local file = io.open(path, 'w')
  if not file then return false, 'open_failed' end
  local ok, err = pcall(function() file:write(text) end)
  file:close()
  if not ok then return false, tostring(err) end
  return true, nil
end

local function resumeSequence(resultDir, sessionId, ringSize, configured)
  local maximum = tonumber(configured) or 0
  for slot = 0, ringSize - 1 do
    local path = resultDir .. '/live_status.slot' .. tostring(slot) .. '.json'
    local file = io.open(path, 'r')
    if file then
      local text = file:read('*a') or ''
      file:close()
      local fileSession = text:match('"sessionId"%s*:%s*"([^"]+)"')
      local sequence = tonumber(text:match('"sequence"%s*:%s*(%d+)'))
      if fileSession == sessionId and sequence and sequence > maximum then maximum = sequence end
    end
  end
  return maximum
end

function statusWriter.new(sessionId, config)
  local safeSessionId = safeName(sessionId)
  local ringSize = clamp((config or {}).statusRingSize, 4, 2, 16)
  local resultDir = 'Mods/CrabRuntimeProbe/Scripts/results'
  local o = {
    sessionId = safeSessionId,
    config = config or {},
    resultDir = resultDir,
    ringSize = ringSize,
    sequence = resumeSequence(resultDir, tostring(sessionId or ''), ringSize, (config or {}).resumeStatusSequence),
    paths = {},
    warned = false
  }

  function o:writeSnapshot(snapshot)
    if self.config.statusWriterEnabled ~= true then return false end
    ensureDirectory(self.resultDir)
    self.sequence = self.sequence + 1
    snapshot.sequence = self.sequence
    local slot = (self.sequence - 1) % self.ringSize
    local finalPath = self.resultDir .. '/live_status.slot' .. tostring(slot) .. '.json'
    local tempPath = finalPath .. '.' .. self.sessionId .. '.' .. tostring(self.sequence) .. '.tmp'
    local ok, err = writeClosedFile(tempPath, json.encode(snapshot))
    if not ok then
      if not self.warned then
        crpLog.line('[CrabRuntimeProbe] ERROR: live status temp write failed: ' .. tostring(err))
        self.warned = true
      end
      return false
    end
    os.remove(finalPath)
    local renamed, renameErr = os.rename(tempPath, finalPath)
    if not renamed then
      os.remove(tempPath)
      if not self.warned then
        crpLog.line('[CrabRuntimeProbe] ERROR: live status atomic rename failed: ' .. tostring(renameErr))
        self.warned = true
      end
      return false
    end
    self.paths[slot + 1] = finalPath
    return true
  end

  return o
end

return statusWriter
