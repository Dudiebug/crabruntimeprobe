local json = {}

local ARRAY_MARKER = {}
local NULL_MARKER = {}

local function esc(s)
  s = tostring(s)
  s = s:gsub('[%z\1-\31\\"]', function(ch)
    if ch == '\\' then return '\\\\' end
    if ch == '"' then return '\\"' end
    if ch == '\b' then return '\\b' end
    if ch == '\f' then return '\\f' end
    if ch == '\n' then return '\\n' end
    if ch == '\r' then return '\\r' end
    if ch == '\t' then return '\\t' end
    return string.format('\\u%04x', string.byte(ch))
  end)
  return '"' .. s .. '"'
end

local function finiteNumber(value)
  return value == value and value ~= math.huge and value ~= -math.huge
end

local function sequentialLength(value)
  local count = 0
  for key, _ in pairs(value) do
    if type(key) ~= 'number' or key < 1 or math.floor(key) ~= key then
      return nil
    end
    if key > count then count = key end
  end
  for i = 1, count do
    if rawget(value, i) == nil then return nil end
  end
  return count
end

local function encode(value, seen, depth)
  if value == NULL_MARKER then return 'null' end
  local kind = type(value)
  if kind == 'nil' then return 'null' end
  if kind == 'boolean' then return tostring(value) end
  if kind == 'number' then
    if not finiteNumber(value) then return 'null' end
    return tostring(value)
  end
  if kind == 'string' then return esc(value) end
  if kind ~= 'table' then return esc('<' .. kind .. '>') end
  if depth > 16 then return esc('<max-depth>') end
  if seen[value] then return esc('<cycle>') end
  seen[value] = true

  local out = {}
  local arrayLength = sequentialLength(value)
  local explicitlyArray = getmetatable(value) == ARRAY_MARKER
  if explicitlyArray or (arrayLength ~= nil and arrayLength > 0) then
    arrayLength = arrayLength or 0
    for i = 1, arrayLength do
      out[#out + 1] = encode(value[i], seen, depth + 1)
    end
    seen[value] = nil
    return '[' .. table.concat(out, ',') .. ']'
  end

  local keys = {}
  for key, _ in pairs(value) do keys[#keys + 1] = tostring(key) end
  table.sort(keys)
  for _, key in ipairs(keys) do
    local rawValue = rawget(value, key)
    if rawValue == nil then
      for originalKey, candidate in pairs(value) do
        if tostring(originalKey) == key then
          rawValue = candidate
          break
        end
      end
    end
    out[#out + 1] = esc(key) .. ':' .. encode(rawValue, seen, depth + 1)
  end
  seen[value] = nil
  return '{' .. table.concat(out, ',') .. '}'
end

function json.array(values)
  return setmetatable(values or {}, ARRAY_MARKER)
end

json.null = NULL_MARKER

function json.encode(value)
  return encode(value, {}, 0)
end

return json
