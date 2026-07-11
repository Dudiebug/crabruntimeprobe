local builder = {}
local json = require('json')

local function finiteNumber(value)
  return value == value and value ~= math.huge and value ~= -math.huge
end

local function cleanString(value, cap)
  local text = tostring(value or '')
  text = text:gsub('[\r\n\t]+', ' '):gsub('%s%s+', ' ')
  cap = cap or 512
  if #text > cap then text = text:sub(1, cap - 3) .. '...' end
  return text
end

local function safeValue(value, depth, seen)
  local kind = type(value)
  if kind == 'nil' or kind == 'boolean' then return value end
  if kind == 'number' then return finiteNumber(value) and value or nil end
  if kind == 'string' then return cleanString(value, 2048) end
  if kind ~= 'table' then return '<' .. kind .. '>' end
  if depth >= 6 then return '<max-depth>' end
  if seen[value] then return '<cycle>' end
  seen[value] = true
  local arrayLength = 0
  local sequential = true
  local hasAny = false
  for key, _ in pairs(value) do
    hasAny = true
    if type(key) ~= 'number' or key < 1 or math.floor(key) ~= key then
      sequential = false
      break
    end
    if key > arrayLength then arrayLength = key end
  end
  if sequential and hasAny then
    for i = 1, arrayLength do
      if rawget(value, i) == nil then sequential = false break end
    end
  end
  if sequential and hasAny then
    local output = json.array({})
    for i = 1, arrayLength do output[i] = safeValue(value[i], depth + 1, seen) end
    seen[value] = nil
    return output
  end
  local output = {}
  local count = 0
  for key, child in pairs(value) do
    count = count + 1
    if count > 128 then
      output._truncated = true
      break
    end
    output[tostring(key)] = safeValue(child, depth + 1, seen)
  end
  seen[value] = nil
  return output
end

function builder.safeValue(value)
  return safeValue(value, 0, {})
end

function builder.cleanString(value, cap)
  return cleanString(value, cap)
end

function builder.merge(base, extra)
  local record = {}
  for key, value in pairs(base or {}) do record[tostring(key)] = safeValue(value, 0, {}) end
  for key, value in pairs(extra or {}) do record[tostring(key)] = safeValue(value, 0, {}) end
  return record
end

function builder.fullObserveBase(config, campaignState, eventName)
  return {
    schemaVersion = 2,
    event = eventName,
    probeId = eventName,
    probeName = eventName,
    probeSet = tostring(config.probeSet or 'crabsync-full-observe'),
    mode = tostring(config.mode or 'observe'),
    category = 'full-observe',
    campaignId = tostring(config.campaignId or ''),
    campaignName = tostring(config.campaignName or 'crabsync-full-observe'),
    campaignGeneration = tonumber(config.campaignGeneration) or 0,
    machineId = tostring(config.machineId or ''),
    selectedRole = tostring(config.selectedRole or 'unselected'):lower():gsub('%s+', '-'),
    observedRole = campaignState and campaignState.observedRole or 'unknown',
    authorityStatus = campaignState and campaignState.authorityStatus or 'unknown',
    lifecycleState = campaignState and campaignState.lifecycleState or 'unknown',
    lifecycleGeneration = campaignState and campaignState.lifecycleGeneration or 0,
    noWrites = true,
    noRpcs = true,
    noMutation = true,
    noHud = true,
    rawIdentityEvidence = false,
    passiveOnly = true,
    runtimeInitiated = false
  }
end

return builder
