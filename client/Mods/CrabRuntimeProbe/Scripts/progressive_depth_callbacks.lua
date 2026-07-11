local callbacks = {}

local function makeDepth1(environment, selection, role)
  local function preCallback()
    environment:enter(selection, role, false, 'pre')
  end
  return preCallback, nil
end

local function makeDepth2(environment, selection, role)
  local function preCallback()
    environment:enter(selection, role, true, 'pre')
  end
  local function postCallback()
    local invocation = environment:resume(selection, role, 'post')
    environment:finish(selection, role, invocation, 'post')
  end
  return preCallback, postCallback
end

local function makeDepth3(environment, selection, role)
  local function preCallback(contextParam)
    local invocation = environment:enter(selection, role, true, 'pre')
    if environment:inspectionAllowed(invocation) then
      local context = environment:resolveContext(selection, role, invocation, contextParam, 'pre')
      invocation.contextSummary = context.summary
      context.object = nil
    end
  end
  local function postCallback()
    local invocation = environment:resume(selection, role, 'post')
    environment:finish(selection, role, invocation, 'post')
  end
  return preCallback, postCallback
end

local function makeDepth4(environment, selection, role)
  local function preCallback(contextParam)
    local invocation = environment:enter(selection, role, true, 'pre')
    if environment:inspectionAllowed(invocation) then
      local context = environment:resolveContext(selection, role, invocation, contextParam, 'pre')
      local scope = environment:resolveScope(selection, role, invocation, context, 'pre')
      invocation.contextSummary = context.summary
      invocation.scopeSummary = scope.summary
      context.object = nil
      scope.playerState = nil
    end
  end
  local function postCallback()
    local invocation = environment:resume(selection, role, 'post')
    environment:finish(selection, role, invocation, 'post')
  end
  return preCallback, postCallback
end

local function makeDepth5(environment, selection, role)
  local function preCallback(contextParam)
    local invocation = environment:enter(selection, role, true, 'pre')
    if environment:inspectionAllowed(invocation) then
      local context = environment:resolveContext(selection, role, invocation, contextParam, 'pre')
      local scope = environment:resolveScope(selection, role, invocation, context, 'pre')
      invocation.prestate = environment:readState(selection, role, invocation, scope, 'pre')
      invocation.contextSummary = context.summary
      invocation.scopeSummary = scope.summary
      context.object = nil
      scope.playerState = nil
    end
  end
  local function postCallback(contextParam)
    local invocation = environment:resume(selection, role, 'post')
    if environment:inspectionAllowed(invocation) then
      local postContext = environment:resolveContext(selection, role, invocation, contextParam, 'post')
      local postScope = environment:resolveScope(selection, role, invocation, postContext, 'post')
      invocation.poststate = environment:readState(selection, role, invocation, postScope, 'post')
    end
    environment:finish(selection, role, invocation, 'post')
  end
  return preCallback, postCallback
end

local function makeDepth6(environment, selection, role)
  local function preCallback(contextParam, ...)
    local invocation = environment:enter(selection, role, true, 'pre')
    if environment:inspectionAllowed(invocation) then
      local context = environment:resolveContext(selection, role, invocation, contextParam, 'pre')
      local scope = environment:resolveScope(selection, role, invocation, context, 'pre')
      invocation.prestate = environment:readState(selection, role, invocation, scope, 'pre')
      invocation.arguments = environment:readArguments(selection, role, invocation, 'pre', ...)
      invocation.contextSummary = context.summary
      invocation.scopeSummary = scope.summary
      context.object = nil
      scope.playerState = nil
    end
  end
  local function postCallback(contextParam)
    local invocation = environment:resume(selection, role, 'post')
    if environment:inspectionAllowed(invocation) then
      local postContext = environment:resolveContext(selection, role, invocation, contextParam, 'post')
      local postScope = environment:resolveScope(selection, role, invocation, postContext, 'post')
      invocation.poststate = environment:readState(selection, role, invocation, postScope, 'post')
    end
    environment:finish(selection, role, invocation, 'post')
  end
  return preCallback, postCallback
end

local function makeDepth7(environment, selection, role)
  local function preCallback(contextParam, ...)
    local invocation = environment:enter(selection, role, true, 'pre')
    if environment:inspectionAllowed(invocation) then
      local context = environment:resolveContext(selection, role, invocation, contextParam, 'pre')
      local scope = environment:resolveScope(selection, role, invocation, context, 'pre')
      invocation.prestate = environment:readState(selection, role, invocation, scope, 'pre')
      invocation.arguments = environment:readArguments(selection, role, invocation, 'pre', ...)
      invocation.contextSummary = context.summary
      invocation.scopeSummary = scope.summary
      context.object = nil
      scope.playerState = nil
    end
  end
  local function postCallback(contextParam)
    local invocation = environment:resume(selection, role, 'post')
    if environment:inspectionAllowed(invocation) then
      local postContext = environment:resolveContext(selection, role, invocation, contextParam, 'post')
      local postScope = environment:resolveScope(selection, role, invocation, postContext, 'post')
      invocation.poststate = environment:readState(selection, role, invocation, postScope, 'post')
      environment:writeEvidence(selection, role, invocation, postContext, postScope, 'post')
    end
    environment:finish(selection, role, invocation, 'post')
  end
  return preCallback, postCallback
end

local BUILDERS = {
  [1] = makeDepth1,
  [2] = makeDepth2,
  [3] = makeDepth3,
  [4] = makeDepth4,
  [5] = makeDepth5,
  [6] = makeDepth6,
  [7] = makeDepth7
}

function callbacks.build(validationDepth, environment, selection, role)
  local depth = tonumber(validationDepth)
  if depth == nil or math.floor(depth) ~= depth or BUILDERS[depth] == nil then
    return nil, nil, 'unsupported-validation-depth'
  end
  return BUILDERS[depth](environment, selection, role)
end

return callbacks
