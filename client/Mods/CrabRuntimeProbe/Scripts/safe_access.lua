local safe = {}

local function try(fn)
  local ok, val = pcall(fn)
  if not ok then return nil, tostring(val) end
  return val, nil
end

safe.try = try

function safe.isValidObject(obj)
  if obj == nil then return false end
  local method, methodErr = try(function() return obj.IsValid end)
  if methodErr or type(method) ~= 'function' then return false end
  local isValid, err = try(function() return obj:IsValid() end)
  if err then return false end
  return isValid == true
end

function safe.findFirst(className)
  return try(function() return FindFirstOf(className) end)
end

function safe.findAll(className)
  return try(function() return FindAllOf(className) end)
end

function safe.getProperty(obj, propName)
  if not safe.isValidObject(obj) then return nil, 'invalid_object' end
  return try(function() return obj:GetPropertyValue(propName) end)
end

function safe.getDirectField(obj, fieldName)
  if not safe.isValidObject(obj) then return nil, 'invalid_object' end
  return try(function() return obj[fieldName] end)
end

function safe.getStructField(value, fieldName)
  if value == nil then return nil, 'nil_parent' end
  return try(function() return value[fieldName] end)
end

function safe.getFullName(obj)
  if not safe.isValidObject(obj) then return nil, 'invalid_object' end
  return try(function() return obj:GetFullName() end)
end

function safe.getName(obj)
  if not safe.isValidObject(obj) then return nil, 'invalid_object' end
  return try(function() return obj:GetName() end)
end

function safe.getClass(obj)
  if not safe.isValidObject(obj) then return nil, 'invalid_object' end
  return try(function() return obj:GetClass() end)
end

function safe.getObjectClassName(obj)
  local classObj, classErr = safe.getClass(obj)
  if classErr then return '', classErr end
  if not safe.isValidObject(classObj) then return '', nil end
  local className, nameErr = safe.getName(classObj)
  if nameErr then return '', nameErr end
  return tostring(className or ''), nil
end

function safe.parseIdentityFromFullName(fullName)
  if type(fullName) ~= 'string' or fullName == '' then
    return '', '', 'unavailable', 'fullName unavailable'
  end

  local objectClass = fullName:match('^([^%s]+)%s+')
  if objectClass == nil then objectClass = '' end

  local shortName = fullName:match('%.([^%.%s/]+)%s*$')
  local source = 'fullNameFallback'
  if shortName == nil or shortName == '' then
    shortName = fullName:match('/([^/%s]+)%s*$')
  end
  if shortName == nil or shortName == '' then
    shortName = ''
    source = 'unavailable'
  end

  return shortName, objectClass, source, nil
end

function safe.summarizeObjectIdentity(obj)
  if obj == nil then
    return 'exists=false', nil, {
      fullName = '',
      shortName = '',
      nameSource = 'unavailable',
      objectClass = ''
    }
  end

  local parts = { 'exists=true' }
  local errors = {}
  local identity = {
    fullName = '',
    shortName = '',
    nameSource = 'unavailable',
    objectClass = ''
  }

  local isValid, isValidErr = try(function()
    if type(obj.IsValid) ~= 'function' then return false end
    return obj:IsValid()
  end)
  if isValidErr then
    parts[#parts + 1] = 'isValid=error'
    errors[#errors + 1] = 'IsValid: ' .. tostring(isValidErr)
    return table.concat(parts, ' '), table.concat(errors, '; '), identity
  end

  parts[#parts + 1] = 'isValid=' .. tostring(isValid == true)
  if isValid ~= true then
    return table.concat(parts, ' '), nil, identity
  end

  local fullName, fullNameErr = safe.getFullName(obj)
  if fullNameErr then
    parts[#parts + 1] = 'fullName=error'
    errors[#errors + 1] = 'GetFullName: ' .. tostring(fullNameErr)
  elseif fullName ~= nil then
    identity.fullName = tostring(fullName)
    parts[#parts + 1] = 'fullName=' .. identity.fullName
  end

  local name, nameErr = safe.getName(obj)
  if nameErr then
    errors[#errors + 1] = 'GetName: ' .. tostring(nameErr)
  elseif name ~= nil then
    identity.shortName = tostring(name)
    identity.nameSource = 'GetName'
  end

  if identity.shortName == '' and identity.fullName ~= '' then
    local fallbackName, objectClass, fallbackSource, fallbackErr = safe.parseIdentityFromFullName(identity.fullName)
    identity.shortName = fallbackName or ''
    identity.objectClass = objectClass or ''
    identity.nameSource = fallbackSource or 'fullNameFallback'
    if fallbackErr then
      errors[#errors + 1] = tostring(fallbackErr)
    end
  elseif identity.fullName ~= '' then
    local _, objectClass = safe.parseIdentityFromFullName(identity.fullName)
    identity.objectClass = objectClass or ''
  end

  parts[#parts + 1] = 'name=' .. identity.shortName
  parts[#parts + 1] = 'nameSource=' .. identity.nameSource

  local err = nil
  if #errors > 0 then err = table.concat(errors, '; ') end
  return table.concat(parts, ' '), err, identity
end

function safe.getArray(obj, propName)
  return safe.getProperty(obj, propName)
end

function safe.getArrayElement(elem)
  -- Risky: only use from gated active probes with strict limits.
  return try(function() return elem:get() end)
end

function safe.forEachArrayLimited(arr, maxElements, callback)
  if type(arr) ~= 'table' then return 0, 'not_array' end
  local count = 0
  for i, elem in ipairs(arr) do
    if i > maxElements then break end
    count = count + 1
    callback(i, elem)
  end
  return count, nil
end

function safe.countArrayLimited(arr, maxElements)
  if arr == nil then return nil, 'nil_array' end
  if type(arr) ~= 'table' then return nil, 'not_array' end
  local count = 0
  for _, _ in ipairs(arr) do
    count = count + 1
    if count >= maxElements then break end
  end
  return count, nil
end


function safe.fingerprintValue(value)
  local text = tostring(value or '')
  if text == '' then return '', 0 end
  local hash = 2166136261
  for i = 1, #text do
    hash = (hash * 16777619 + string.byte(text, i)) % 4294967296
  end
  return string.format('%08x', hash), #text
end

function safe.getHookParam(param)
  if param == nil then return nil, nil end
  return try(function() return param:get() end)
end

function safe.resolveHookContext(contextParam)
  if safe.isValidObject(contextParam) then return contextParam, nil end
  local value, err = safe.getHookParam(contextParam)
  if err then return nil, err end
  if safe.isValidObject(value) then return value, nil end
  return nil, 'invalid_hook_context'
end

local function cleanSummary(value, cap)
  local text = tostring(value or ''):gsub('[\r\n\t]+', ' '):gsub('%s%s+', ' ')
  cap = cap or 160
  if #text > cap then text = text:sub(1, cap - 3) .. '...' end
  return text
end

function safe.getUnrealType(value)
  if value == nil then return '' end
  local unrealType, err = try(function()
    if type(value.type) ~= 'function' then return '' end
    return value:type()
  end)
  if err then return '' end
  return tostring(unrealType or '')
end

function safe.redactedObjectSummary(obj, allowAssetPath)
  if not safe.isValidObject(obj) then
    return { status = 'invalid', className = '', pathFingerprint = '', pathSummary = '<invalid>' }
  end
  local fullName = safe.getFullName(obj)
  local className = safe.getObjectClassName(obj)
  -- Never stringify a UObject as a fallback. UE4SS object formatting can expose
  -- an address and may touch a stale native representation. An unavailable
  -- GetFullName result remains an unavailable fingerprint.
  local fingerprint = safe.fingerprintValue(type(fullName) == 'string' and fullName or '')
  local pathSummary = '<redacted-instance>'
  if allowAssetPath == true then
    local text = tostring(fullName or '')
    local assetPath = text:match('(/Game/[^%s:]+)')
    if assetPath then pathSummary = cleanSummary(assetPath, 192) end
  end
  return {
    status = 'observed-redacted',
    className = tostring(className or ''),
    pathFingerprint = fingerprint,
    pathSummary = pathSummary
  }
end

function safe.summarizeHookArgument(param, spec, options)
  spec = spec or {}
  options = options or {}
  local summary = {
    name = tostring(spec.name or ''),
    direction = tostring(spec.direction or 'in'),
    propertyType = tostring(spec.propertyType or ''),
    redaction = tostring(spec.redaction or 'redacted'),
    status = 'unsupported',
    valueKind = 'unknown',
    valueSummary = 'not read'
  }
  if summary.redaction == 'omit' or spec.safeSummary == false or spec.safeSummary == 'none' then
    summary.status = 'redacted'
    summary.valueSummary = '<redacted>'
    return summary
  end

  local requestedSummary = tostring(spec.safeSummary or '')
  if requestedSummary == 'shape-and-count-only-until-staged-proof' and options.allowShapeCount ~= true then
    summary.status = 'deferred'
    summary.valueKind = 'unknown'
    summary.valueSummary = 'shape/count deferred until matching staged proof'
    return summary
  end

  local value, err = safe.getHookParam(param)
  if err then
    summary.status = 'error'
    local errorFingerprint = safe.fingerprintValue(err)
    summary.valueSummary = 'errorFingerprint=' .. tostring(errorFingerprint)
    return summary
  end
  if value == nil then
    summary.status = 'nil'
    summary.valueKind = 'nil'
    summary.valueSummary = 'nil'
    return summary
  end

  local kind = type(value)
  summary.valueKind = kind
  local safeSummary = requestedSummary
  if kind == 'boolean' and (safeSummary == 'scalar' or safeSummary == 'boolean') then
    summary.status = 'observed'
    summary.valueSummary = tostring(value)
  elseif kind == 'number' and (safeSummary == 'scalar' or safeSummary == 'number' or safeSummary == 'enum') then
    if value == value and value ~= math.huge and value ~= -math.huge then
      summary.status = 'observed'
      summary.valueSummary = tostring(value)
    else
      summary.status = 'unsupported'
      summary.valueSummary = 'non-finite number'
    end
  elseif kind == 'string' then
    local fingerprint, length = safe.fingerprintValue(value)
    summary.status = 'observed-redacted'
    summary.valueSummary = 'fingerprint=' .. fingerprint .. ' length=' .. tostring(length)
  elseif safe.isValidObject(value) and (safeSummary == 'object' or safeSummary == 'objectIdentity' or safeSummary == 'uobject'
    or safeSummary == 'class-and-redacted-full-name' or safeSummary == 'object-identity-redacted') then
    local identity = safe.redactedObjectSummary(value, false)
    summary.status = 'observed-redacted'
    summary.valueKind = 'object'
    summary.valueSummary = 'class=' .. cleanSummary(identity.className or '', 80) .. ' pathFingerprint=' .. tostring(identity.pathFingerprint or '')
  elseif safeSummary == 'shape-and-count-only-until-staged-proof' then
    local count, countErr = safe.getArrayLength(value)
    summary.valueKind = type(value)
    if countErr then
      summary.status = 'observed-shape-only'
      summary.valueSummary = 'kind=' .. type(value) .. ' count=unsupported'
    else
      summary.status = 'observed-redacted'
      summary.valueSummary = 'kind=' .. type(value) .. ' count=' .. tostring(count)
    end
  else
    summary.status = 'unsupported'
    summary.valueSummary = '<' .. kind .. ' redacted>'
  end
  return summary
end

function safe.getArrayLength(value)
  if value == nil then return nil, 'nil_array' end
  local count, err = try(function() return #value end)
  if err then return nil, err end
  if type(count) ~= 'number' or count < 0 or math.floor(count) ~= count then
    return nil, 'invalid_array_length'
  end
  return count, nil
end

function safe.getArrayIndex(value, index)
  if value == nil then return nil, 'nil_array' end
  if type(index) ~= 'number' or index < 0 or math.floor(index) ~= index then
    return nil, 'invalid_index'
  end
  return try(function() return value[index] end)
end

function safe.getKnownField(value, fieldName)
  if value == nil then return nil, 'nil_parent' end
  local unrealType = safe.getUnrealType(value)
  if unrealType:find('ScriptStruct') or unrealType:find('Struct') then
    return safe.getStructField(value, fieldName)
  end
  if unrealType:find('LocalUnrealParam') or unrealType:find('RemoteUnrealParam') then
    local unwrapped, unwrapErr = safe.unwrapKnownValue(value)
    if unwrapErr then return nil, unwrapErr end
    local unwrappedType = safe.getUnrealType(unwrapped)
    if unwrappedType:find('ScriptStruct') or unwrappedType:find('Struct') or type(unwrapped) == 'table' then
      return safe.getStructField(unwrapped, fieldName)
    end
    if safe.isValidObject(unwrapped) then return safe.getProperty(unwrapped, fieldName) end
    return nil, 'unsupported_unwrapped_field_parent'
  end
  if type(value) == 'table' then return safe.getStructField(value, fieldName) end
  if safe.isValidObject(value) then return safe.getProperty(value, fieldName) end
  return safe.getStructField(value, fieldName)
end

function safe.unwrapKnownValue(value)
  if value == nil then return nil, nil end
  local unrealType = safe.getUnrealType(value)
  if unrealType:find('ScriptStruct') or unrealType:find('Struct') then return value, nil end
  if safe.isValidObject(value) and not unrealType:find('UnrealParam') then return value, nil end
  local unwrapped, err = try(function() return value:get() end)
  if err then return nil, err end
  return unwrapped, nil
end

function safe.authorityStatus(obj)
  if not safe.isValidObject(obj) then return 'unknown' end
  for _, fieldName in ipairs({ 'LocalRole', 'Role' }) do
    local value, err = safe.getProperty(obj, fieldName)
    if err == nil and value ~= nil then
      local text = tostring(value)
      if text:find('Authority') then return 'runtime-authority' end
      if text:find('AutonomousProxy') or text:find('SimulatedProxy') then return 'runtime-non-authority' end
    end
  end
  return 'unknown'
end

return safe
