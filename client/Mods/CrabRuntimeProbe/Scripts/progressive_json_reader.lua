local reader = {}

local ARRAY_MARKER = {}
local NULL = {}

reader.null = NULL

local function fail(code)
  error(tostring(code or 'json-invalid'), 0)
end

local function utf8Codepoint(codepoint)
  if codepoint <= 0x7f then return string.char(codepoint) end
  if codepoint <= 0x7ff then
    return string.char(0xc0 + math.floor(codepoint / 0x40), 0x80 + (codepoint % 0x40))
  end
  if codepoint <= 0xffff then
    return string.char(0xe0 + math.floor(codepoint / 0x1000),
      0x80 + (math.floor(codepoint / 0x40) % 0x40), 0x80 + (codepoint % 0x40))
  end
  return string.char(0xf0 + math.floor(codepoint / 0x40000),
    0x80 + (math.floor(codepoint / 0x1000) % 0x40),
    0x80 + (math.floor(codepoint / 0x40) % 0x40), 0x80 + (codepoint % 0x40))
end

function reader.decode(text, options)
  options = options or {}
  if type(text) ~= 'string' then return nil, 'json-input-not-string' end
  local maximumBytes = tonumber(options.maximumBytes) or 262144
  if #text < 1 or #text > maximumBytes then return nil, 'json-size-outside-bound' end

  local state = {
    text = text,
    index = 1,
    length = #text,
    nodes = 0,
    maximumNodes = tonumber(options.maximumNodes) or 8192,
    maximumDepth = tonumber(options.maximumDepth) or 16,
    maximumStringBytes = tonumber(options.maximumStringBytes) or 4096,
    maximumContainerItems = tonumber(options.maximumContainerItems) or 1024
  }

  local parseValue

  local function skipWhitespace()
    while state.index <= state.length do
      local byte = string.byte(state.text, state.index)
      if byte ~= 0x20 and byte ~= 0x09 and byte ~= 0x0a and byte ~= 0x0d then break end
      state.index = state.index + 1
    end
  end

  local function parseUnicodeEscape()
    local hex = state.text:sub(state.index, state.index + 3)
    if #hex ~= 4 or hex:match('^[0-9A-Fa-f]+$') == nil then fail('json-unicode-escape-invalid') end
    state.index = state.index + 4
    local codepoint = tonumber(hex, 16)
    if codepoint >= 0xd800 and codepoint <= 0xdbff then
      if state.text:sub(state.index, state.index + 1) ~= '\\u' then fail('json-surrogate-pair-invalid') end
      state.index = state.index + 2
      local lowHex = state.text:sub(state.index, state.index + 3)
      if #lowHex ~= 4 or lowHex:match('^[0-9A-Fa-f]+$') == nil then fail('json-surrogate-pair-invalid') end
      state.index = state.index + 4
      local low = tonumber(lowHex, 16)
      if low < 0xdc00 or low > 0xdfff then fail('json-surrogate-pair-invalid') end
      codepoint = 0x10000 + ((codepoint - 0xd800) * 0x400) + (low - 0xdc00)
    elseif codepoint >= 0xdc00 and codepoint <= 0xdfff then
      fail('json-surrogate-pair-invalid')
    end
    if codepoint > 0x10ffff then fail('json-codepoint-invalid') end
    return utf8Codepoint(codepoint)
  end

  local function parseString()
    if state.text:sub(state.index, state.index) ~= '"' then fail('json-string-expected') end
    state.index = state.index + 1
    local output = {}
    local outputBytes = 0
    local segmentStart = state.index
    while state.index <= state.length do
      local byte = string.byte(state.text, state.index)
      if byte == 0x22 then
        local segment = state.text:sub(segmentStart, state.index - 1)
        output[#output + 1] = segment
        outputBytes = outputBytes + #segment
        state.index = state.index + 1
        if outputBytes > state.maximumStringBytes then fail('json-string-size-limit') end
        return table.concat(output)
      end
      if byte < 0x20 then fail('json-string-control-character') end
      if byte == 0x5c then
        local segment = state.text:sub(segmentStart, state.index - 1)
        output[#output + 1] = segment
        outputBytes = outputBytes + #segment
        state.index = state.index + 1
        local escape = state.text:sub(state.index, state.index)
        local escaped = ({
          ['"'] = '"', ['\\'] = '\\', ['/'] = '/', b = '\b', f = '\f',
          n = '\n', r = '\r', t = '\t'
        })[escape]
        if escape == 'u' then
          state.index = state.index + 1
          escaped = parseUnicodeEscape()
        elseif escaped ~= nil then
          state.index = state.index + 1
        else
          fail('json-string-escape-invalid')
        end
        output[#output + 1] = escaped
        outputBytes = outputBytes + #escaped
        if outputBytes > state.maximumStringBytes then fail('json-string-size-limit') end
        segmentStart = state.index
      else
        state.index = state.index + 1
      end
    end
    fail('json-string-unterminated')
  end

  local function parseNumber()
    local start = state.index
    if state.text:sub(state.index, state.index) == '-' then state.index = state.index + 1 end
    local first = state.text:sub(state.index, state.index)
    if first == '0' then
      state.index = state.index + 1
      if state.text:sub(state.index, state.index):match('%d') then fail('json-number-leading-zero') end
    elseif first:match('[1-9]') then
      repeat state.index = state.index + 1 until not state.text:sub(state.index, state.index):match('%d')
    else
      fail('json-number-invalid')
    end
    if state.text:sub(state.index, state.index) == '.' then
      state.index = state.index + 1
      if not state.text:sub(state.index, state.index):match('%d') then fail('json-number-fraction-invalid') end
      repeat state.index = state.index + 1 until not state.text:sub(state.index, state.index):match('%d')
    end
    local exponent = state.text:sub(state.index, state.index)
    if exponent == 'e' or exponent == 'E' then
      state.index = state.index + 1
      local sign = state.text:sub(state.index, state.index)
      if sign == '+' or sign == '-' then state.index = state.index + 1 end
      if not state.text:sub(state.index, state.index):match('%d') then fail('json-number-exponent-invalid') end
      repeat state.index = state.index + 1 until not state.text:sub(state.index, state.index):match('%d')
    end
    local value = tonumber(state.text:sub(start, state.index - 1))
    if value == nil or value ~= value or value == math.huge or value == -math.huge then fail('json-number-invalid') end
    return value
  end

  local function parseArray(depth)
    state.index = state.index + 1
    local output = setmetatable({}, ARRAY_MARKER)
    skipWhitespace()
    if state.text:sub(state.index, state.index) == ']' then state.index = state.index + 1; return output end
    while true do
      if #output >= state.maximumContainerItems then fail('json-container-item-limit') end
      output[#output + 1] = parseValue(depth + 1)
      skipWhitespace()
      local character = state.text:sub(state.index, state.index)
      if character == ']' then state.index = state.index + 1; return output end
      if character ~= ',' then fail('json-array-separator-invalid') end
      state.index = state.index + 1
      skipWhitespace()
    end
  end

  local function parseObject(depth)
    state.index = state.index + 1
    local output = {}
    local count = 0
    skipWhitespace()
    if state.text:sub(state.index, state.index) == '}' then state.index = state.index + 1; return output end
    while true do
      if count >= state.maximumContainerItems then fail('json-container-item-limit') end
      local key = parseString()
      if output[key] ~= nil then fail('json-duplicate-object-key') end
      skipWhitespace()
      if state.text:sub(state.index, state.index) ~= ':' then fail('json-object-colon-missing') end
      state.index = state.index + 1
      skipWhitespace()
      output[key] = parseValue(depth + 1)
      count = count + 1
      skipWhitespace()
      local character = state.text:sub(state.index, state.index)
      if character == '}' then state.index = state.index + 1; return output end
      if character ~= ',' then fail('json-object-separator-invalid') end
      state.index = state.index + 1
      skipWhitespace()
    end
  end

  parseValue = function(depth)
    if depth > state.maximumDepth then fail('json-depth-limit') end
    state.nodes = state.nodes + 1
    if state.nodes > state.maximumNodes then fail('json-node-limit') end
    skipWhitespace()
    local character = state.text:sub(state.index, state.index)
    if character == '"' then return parseString() end
    if character == '{' then return parseObject(depth) end
    if character == '[' then return parseArray(depth) end
    if character == '-' or character:match('%d') then return parseNumber() end
    if state.text:sub(state.index, state.index + 3) == 'true' then state.index = state.index + 4; return true end
    if state.text:sub(state.index, state.index + 4) == 'false' then state.index = state.index + 5; return false end
    if state.text:sub(state.index, state.index + 3) == 'null' then state.index = state.index + 4; return NULL end
    fail('json-value-invalid')
  end

  local ok, value = pcall(function()
    local decoded = parseValue(0)
    skipWhitespace()
    if state.index <= state.length then fail('json-trailing-content') end
    return decoded
  end)
  if not ok then return nil, tostring(value) end
  return value, nil
end

function reader.read(path, options)
  options = options or {}
  local maximumBytes = tonumber(options.maximumBytes) or 262144
  local file = io.open(path, 'r')
  if not file then return nil, 'json-file-open-failed' end
  local text = file:read(maximumBytes + 1) or ''
  file:close()
  if #text > maximumBytes then return nil, 'json-file-size-limit' end
  return reader.decode(text, options)
end

function reader.isArray(value)
  return type(value) == 'table' and getmetatable(value) == ARRAY_MARKER
end

return reader
