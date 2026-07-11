local recordBuilder = require('record_builder')
local depthCallbacks = require('progressive_depth_callbacks')

local runnerFactory = {}

local MAX_PENDING_PER_CANDIDATE = 16
local MAX_ARGUMENTS = 16
local MAX_STATE_FIELDS = 16

local REVIEWED_STATE_FIELDS = {
  ['CrabPS.Crystals'] = { property = 'Crystals', kind = 'scalar' },
  ['CrabPS.NumWeaponModSlots'] = { property = 'NumWeaponModSlots', kind = 'scalar' },
  ['CrabPS.NumAbilityModSlots'] = { property = 'NumAbilityModSlots', kind = 'scalar' },
  ['CrabPS.NumMeleeModSlots'] = { property = 'NumMeleeModSlots', kind = 'scalar' },
  ['CrabPS.NumPerkSlots'] = { property = 'NumPerkSlots', kind = 'scalar' },
  ['CrabPS.BaseMaxHealth'] = { property = 'BaseMaxHealth', kind = 'scalar' },
  ['CrabPS.MaxHealthMultiplier'] = { property = 'MaxHealthMultiplier', kind = 'scalar' },
  ['CrabPS.WeaponDA'] = { property = 'WeaponDA', kind = 'object' },
  ['CrabPS.AbilityDA'] = { property = 'AbilityDA', kind = 'object' },
  ['CrabPS.MeleeDA'] = { property = 'MeleeDA', kind = 'object' }
}
local REVIEWED_SCOPE_PROPERTIES = { OwningPS = true, PlayerState = true }

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function finiteNumber(value)
  return type(value) == 'number' and value == value and value ~= math.huge and value ~= -math.huge
end

local function newBreaker()
  return { state = 'closed', reason = '', openedAtUtc = '' }
end

local function breakerSummary(breakers)
  return {
    registration = breakers.registration.state,
    callback = breakers.callback.state,
    journal = breakers.journal.state,
    evidence = breakers.evidence.state
  }
end

function runnerFactory.new(config, selection, safe, evidenceWriter, state, journal)
  local o = {
    config = config,
    selection = selection,
    safe = safe,
    evidenceWriter = evidenceWriter,
    state = state,
    journal = journal,
    entries = {},
    registered = {},
    registrationOrder = {},
    pending = {},
    invocationSequence = 0,
    callbackCount = 0,
    registeredCount = 0,
    armed = false,
    completed = false,
    ambiguousRegistrationState = false,
    canaryRegistrationState = selection.canary and 'waiting-for-baseline' or 'not-configured'
  }

  local function ensureEntry(candidateSelection, role)
    local id = candidateSelection.candidateId
    local entry = o.entries[id]
    if entry == nil then
      entry = {
        selection = candidateSelection,
        role = role,
        registrationState = 'not-attempted',
        callbackCount = 0,
        lastInvocationId = '',
        breakers = {
          registration = newBreaker(), callback = newBreaker(),
          journal = newBreaker(), evidence = newBreaker()
        }
      }
      o.entries[id] = entry
    end
    return entry
  end

  for _, item in ipairs(selection.trusted or {}) do ensureEntry(item, 'trusted') end
  if selection.canary then ensureEntry(selection.canary, 'canary') end

  function o:openBreaker(candidateSelection, breakerName, reason)
    local entry = ensureEntry(candidateSelection, candidateSelection == self.selection.canary and 'canary' or 'trusted')
    local breaker = entry.breakers[breakerName]
    if breaker and breaker.state == 'closed' then
      breaker.state = 'open'
      breaker.reason = tostring(reason or (breakerName .. '-failure'))
      breaker.openedAtUtc = utcNow()
    end
    self.state:tripCircuit('progressive:' .. candidateSelection.candidateId .. ':' .. breakerName,
      tostring(reason or (breakerName .. '-failure')), 'open')
  end

  function o:boundary(candidateSelection, role, invocationId, phase, boundary)
    local ok = self.journal:write(candidateSelection.candidate, candidateSelection.validationDepth, role,
      invocationId, phase, boundary, self.state.lifecycleGeneration)
    if not ok then
      self:openBreaker(candidateSelection, 'journal', 'breadcrumb-journal-failure')
      error('breadcrumb-journal-failure')
    end
    return true
  end

  function o:enter(candidateSelection, role, expectsPost, phase)
    local entry = ensureEntry(candidateSelection, role)
    if entry.breakers.callback.state ~= 'closed' or entry.breakers.journal.state ~= 'closed' then
      error('callback-circuit-open')
    end
    self.invocationSequence = self.invocationSequence + 1
    local invocationId = 'inv-' .. tostring(self.invocationSequence)
    local invocation = {
      id = invocationId,
      lifecycleGeneration = self.state.lifecycleGeneration,
      skipInspection = false,
      contextSummary = nil,
      scopeSummary = nil,
      prestate = nil,
      poststate = nil,
      arguments = nil
    }
    -- A natural callback is definitive proof that a progressive hook is active,
    -- including the narrow window before RegisterHook returns its IDs.
    self.config.progressiveHooksArmed = true
    self:boundary(candidateSelection, role, invocationId, phase, 'callback-enter')
    self.callbackCount = self.callbackCount + 1
    entry.callbackCount = entry.callbackCount + 1
    entry.lastInvocationId = invocationId
    if expectsPost then
      local stack = self.pending[candidateSelection.candidateId] or {}
      if #stack >= MAX_PENDING_PER_CANDIDATE then error('pending-invocation-cap-reached') end
      stack[#stack + 1] = invocation
      self.pending[candidateSelection.candidateId] = stack
    end
    return invocation
  end

  function o:resume(candidateSelection, role, phase)
    local stack = self.pending[candidateSelection.candidateId] or {}
    local invocation = stack[#stack]
    if invocation then
      stack[#stack] = nil
      if #stack == 0 then self.pending[candidateSelection.candidateId] = nil end
      return invocation
    end
    local orphan = self:enter(candidateSelection, role, false, phase)
    orphan.skipInspection = true
    error('orphan-post-callback')
  end

  function o:inspectionAllowed(invocation)
    if invocation.skipInspection then return false end
    if self.state.lifecycleState ~= 'stable' or self.state.stability.ready ~= true
      or invocation.lifecycleGeneration ~= self.state.lifecycleGeneration then
      invocation.skipInspection = true
      return false
    end
    return true
  end

  function o:resolveContext(candidateSelection, role, invocation, contextParam, phase)
    self:boundary(candidateSelection, role, invocation.id, phase, 'context-resolve-begin')
    local contextObject, unwrapErr = self.safe.getHookParam(contextParam)
    if unwrapErr or not self.safe.isValidObject(contextObject) then error('hook-context-unavailable') end
    local className, classErr = self.safe.getObjectClassName(contextObject)
    if classErr or className == '' then error('hook-context-class-unavailable') end
    local fullName, fullNameErr = self.safe.getFullName(contextObject)
    if fullNameErr or type(fullName) ~= 'string' or fullName == '' then error('hook-context-fingerprint-unavailable') end
    local fingerprint = self.safe.fingerprintValue(fullName)
    self:boundary(candidateSelection, role, invocation.id, phase, 'context-resolve-complete')
    return {
      object = contextObject,
      summary = { className = className, fingerprint = fingerprint, status = 'observed-redacted' }
    }
  end

  function o:resolveScope(candidateSelection, role, invocation, contextResult, phase)
    self:boundary(candidateSelection, role, invocation.id, phase, 'scope-resolve-begin')
    local contextObject = contextResult and contextResult.object or nil
    local contextClass = contextResult and contextResult.summary and contextResult.summary.className or ''
    local playerState = nil
    local confirmed = false
    local source = 'unresolved'
    if contextClass == 'CrabPS' and self.safe.isValidObject(contextObject) then
      playerState = contextObject
      confirmed = true
      source = 'hook-context-playerstate'
    elseif self.safe.isValidObject(contextObject) then
      local inspected = 0
      for _, fieldName in ipairs(candidateSelection.candidate.scopeProperties or {}) do
        inspected = inspected + 1
        if inspected > 4 then break end
        if REVIEWED_SCOPE_PROPERTIES[fieldName] == true then
          local candidate, candidateErr = self.safe.getProperty(contextObject, fieldName)
          if candidateErr == nil and self.safe.isValidObject(candidate) then
            local candidateClass = self.safe.getObjectClassName(candidate)
            if candidateClass == 'CrabPS' then
              playerState = candidate
              confirmed = true
              source = 'curated-context-property:' .. fieldName
              break
            end
          end
        end
      end
    end
    if not self.safe.isValidObject(playerState) then
      local controller = self.safe.findFirst('CrabPC')
      if self.safe.isValidObject(controller) then
        local fallback = self.safe.getProperty(controller, 'PlayerState')
        if self.safe.isValidObject(fallback) then
          playerState = fallback
          confirmed = false
          source = 'local-playerstate-fallback-unconfirmed'
        end
      end
    end
    local fingerprint = ''
    if self.safe.isValidObject(playerState) then
      local fullName, fullNameErr = self.safe.getFullName(playerState)
      if not fullNameErr and type(fullName) == 'string' then fingerprint = self.safe.fingerprintValue(fullName) end
    end
    self:boundary(candidateSelection, role, invocation.id, phase, 'scope-resolve-complete')
    return {
      playerState = playerState,
      summary = { confirmed = confirmed, source = source, fingerprint = fingerprint }
    }
  end

  function o:readState(candidateSelection, role, invocation, scopeResult, phase)
    local beginBoundary = phase == 'post' and 'poststate-read-begin' or 'prestate-read-begin'
    local completeBoundary = phase == 'post' and 'poststate-read-complete' or 'prestate-read-complete'
    self:boundary(candidateSelection, role, invocation.id, phase, beginBoundary)
    local values = {}
    if not scopeResult or not scopeResult.summary or scopeResult.summary.confirmed ~= true
      or not self.safe.isValidObject(scopeResult.playerState) then
      values.status = 'scope-unconfirmed-no-state-read'
    else
      local count = 0
      for _, requestedPath in ipairs(candidateSelection.candidate.reviewedStateFields or {}) do
        count = count + 1
        if count > MAX_STATE_FIELDS then break end
        local definition = REVIEWED_STATE_FIELDS[requestedPath]
        if definition == nil then
          values[requestedPath] = { status = 'deferred-not-in-executable-allowlist' }
        else
          local value, valueErr = self.safe.getProperty(scopeResult.playerState, definition.property)
          if valueErr then
            values[requestedPath] = { status = 'error', errorFingerprint = self.safe.fingerprintValue(valueErr) }
          elseif definition.kind == 'scalar' and (type(value) == 'boolean' or finiteNumber(value)) then
            values[requestedPath] = { status = 'observed', value = value }
          elseif definition.kind == 'object' and self.safe.isValidObject(value) then
            local fullName, fullNameErr = self.safe.getFullName(value)
            values[requestedPath] = fullNameErr and { status = 'error' }
              or { status = 'observed-redacted', fingerprint = self.safe.fingerprintValue(fullName or '') }
          elseif value == nil then
            values[requestedPath] = { status = 'nil' }
          else
            values[requestedPath] = { status = 'unsupported-value-kind', valueKind = type(value) }
          end
        end
      end
    end
    self:boundary(candidateSelection, role, invocation.id, phase, completeBoundary)
    return values
  end

  function o:readArguments(candidateSelection, role, invocation, phase, ...)
    self:boundary(candidateSelection, role, invocation.id, phase, 'arguments-read-begin')
    local output = {}
    local count = 0
    for index, spec in ipairs(candidateSelection.candidate.argumentSchema or {}) do
      count = count + 1
      if count > MAX_ARGUMENTS then break end
      output[#output + 1] = self.safe.summarizeHookArgument(select(index, ...), spec, { allowShapeCount = false })
    end
    self:boundary(candidateSelection, role, invocation.id, phase, 'arguments-read-complete')
    return output
  end

  function o:writeEvidence(candidateSelection, role, invocation, contextResult, scopeResult, phase)
    local entry = ensureEntry(candidateSelection, role)
    if entry.breakers.evidence.state ~= 'closed' then return false end
    self:boundary(candidateSelection, role, invocation.id, phase, 'evidence-write-begin')
    local base = recordBuilder.fullObserveBase(self.config, self.state, 'ProgressiveHook.Observed')
    local row = recordBuilder.merge(base, {
      timestamp = utcNow(),
      sequence = self.state:nextSequence(),
      recordType = 'progressive-hook-observation',
      runId = self.selection.runId,
      candidateId = candidateSelection.candidateId,
      candidateRole = role,
      validationDepth = candidateSelection.validationDepth,
      hookPathFingerprint = candidateSelection.hookPathFingerprint,
      category = tostring(candidateSelection.candidate.category or ''),
      symbol = tostring(candidateSelection.candidate.hookPath or ''),
      invocationId = invocation.id,
      lifecycleGeneration = self.state.lifecycleGeneration,
      callingObject = contextResult and contextResult.summary or invocation.contextSummary,
      ownershipScope = scopeResult and scopeResult.summary or invocation.scopeSummary,
      preState = invocation.prestate,
      postState = invocation.poststate,
      arguments = invocation.arguments,
      result = 'ok',
      runtimeStatus = 'NATURALLY_OBSERVED_AT_VALIDATED_DEPTH',
      safetyClassification = 'progressive-passive-observation',
      runtimeInitiated = false,
      passiveOnly = true,
      noWrites = true,
      noRpcs = true,
      noMutation = true
    })
    local writeOk = self.evidenceWriter:writeEvidence(row)
    self.state:noteWriteResult(writeOk)
    if not writeOk then
      self:openBreaker(candidateSelection, 'evidence', 'progressive-evidence-write-failure')
      return false
    end
    self:boundary(candidateSelection, role, invocation.id, phase, 'evidence-write-complete')
    return true
  end

  function o:finish(candidateSelection, role, invocation, phase)
    self:boundary(candidateSelection, role, invocation.id, phase, 'callback-exit')
  end

  function o:guard(candidateSelection, role, phase, callback)
    if callback == nil then return nil end
    return function(...)
      local entry = ensureEntry(candidateSelection, role)
      if self.completed or entry.breakers.callback.state ~= 'closed'
        or entry.breakers.journal.state ~= 'closed' then return end
      -- Forward varargs directly so shallow depths never materialize or retain
      -- argument wrappers before their first callback breadcrumb.
      local ok = pcall(callback, ...)
      if not ok then self:openBreaker(candidateSelection, 'callback', 'validated-callback-boundary-failure') end
    end
  end

  function o:registerOne(candidateSelection, role)
    local entry = ensureEntry(candidateSelection, role)
    local function canaryState(value)
      if role == 'canary' then self.canaryRegistrationState = value end
    end
    if entry.registrationState ~= 'not-attempted' then canaryState('failed-duplicate-attempt'); return false end
    if entry.breakers.registration.state ~= 'closed' or self.journal.faulted then
      canaryState('blocked-by-circuit')
      return false
    end
    if candidateSelection.validationDepth < 1 or candidateSelection.validationDepth > 7 then
      canaryState('failed-invalid-depth')
      self:openBreaker(candidateSelection, 'registration', 'validation-depth-invalid')
      return false
    end
    if candidateSelection.candidate.ownerKind ~= 'native' then
      entry.registrationState = 'pending-compatible-owner-load'
      canaryState('pending-compatible-owner-load')
      self:openBreaker(candidateSelection, 'registration', 'blueprint-owner-not-safely-load-confirmed')
      return false
    end
    if type(RegisterHook) ~= 'function' then
      canaryState('failed-register-hook-unavailable')
      self:openBreaker(candidateSelection, 'registration', 'register-hook-unavailable')
      return false
    end

    local attemptId = 'registration-' .. tostring(#self.registrationOrder + 1)
    self:boundary(candidateSelection, role, attemptId, 'registration', 'registration-begin')
    entry.registrationState = 'registering'
    canaryState('registering')
    local preCallback, postCallback, callbackErr = depthCallbacks.build(
      candidateSelection.validationDepth, self, candidateSelection, role)
    if callbackErr or preCallback == nil then
      self:boundary(candidateSelection, role, attemptId, 'registration', 'registration-failed')
      canaryState('failed-callback-composition')
      self:openBreaker(candidateSelection, 'registration', 'callback-composition-failed')
      return false
    end
    preCallback = self:guard(candidateSelection, role, 'pre', preCallback)
    postCallback = self:guard(candidateSelection, role, 'post', postCallback)

    local ok, preId, postId = pcall(function()
      if postCallback then
        return RegisterHook(candidateSelection.candidate.hookPath, preCallback, postCallback)
      end
      return RegisterHook(candidateSelection.candidate.hookPath, preCallback)
    end)
    if not ok or type(preId) ~= 'number' or type(postId) ~= 'number' then
      entry.registrationState = 'failed'
      self.ambiguousRegistrationState = true
      self.config.progressiveHooksArmed = true
      self:boundary(candidateSelection, role, attemptId, 'registration', 'registration-failed')
      canaryState('failed-registration')
      self:openBreaker(candidateSelection, 'registration', 'register-hook-failed-or-invalid-ids')
      return false
    end

    local registration = {
      candidateSelection = candidateSelection,
      role = role,
      preId = preId,
      postId = postId
    }
    self.registered[#self.registered + 1] = registration
    if not pcall(function()
      self:boundary(candidateSelection, role, attemptId, 'registration', 'registration-complete')
    end) then
      local unregistered = false
      if type(UnregisterHook) == 'function' then
        unregistered = pcall(function() UnregisterHook(candidateSelection.candidate.hookPath, preId, postId) end)
      end
      if not unregistered then self.ambiguousRegistrationState = true end
      self.registered[#self.registered] = nil
      entry.registrationState = 'failed-journal'
      self.config.progressiveHooksArmed = self.ambiguousRegistrationState or self.registeredCount > 0
      canaryState('failed-journal')
      return false
    end
    entry.registrationState = 'registered'
    self.registeredCount = self.registeredCount + 1
    self.config.progressiveHooksArmed = true
    self.registrationOrder[#self.registrationOrder + 1] = candidateSelection.candidateId
    if role == 'canary' then self.canaryRegistrationState = 'registered' end
    return true
  end

  function o:registerConfiguredHooks()
    if self.armed then return false end
    self.armed = true
    for _, trustedSelection in ipairs(self.selection.trusted or {}) do
      local callOk, registered = pcall(function() return self:registerOne(trustedSelection, 'trusted') end)
      if not callOk or not registered then
        self.config.progressiveHooksArmed = self.ambiguousRegistrationState or self.registeredCount > 0
        return false
      end
    end
    if self.selection.canary then
      local callOk, registered = pcall(function() return self:registerOne(self.selection.canary, 'canary') end)
      if not callOk or not registered then
        if self.journal.faulted then
          self.canaryRegistrationState = 'failed-journal'
        elseif self.canaryRegistrationState == 'registering'
          or self.canaryRegistrationState == 'waiting-for-baseline' then
          self.canaryRegistrationState = 'failed-registration'
        end
        self.config.progressiveHooksArmed = self.ambiguousRegistrationState or self.registeredCount > 0
        return false
      end
    end
    self.config.progressiveHooksArmed = self.registeredCount > 0
    return true
  end

  function o:hasRuntimeFault()
    if self.journal.faulted then return true end
    for _, entry in pairs(self.entries) do
      for _, breaker in pairs(entry.breakers) do
        if breaker.state ~= 'closed' then return true end
      end
    end
    return false
  end

  function o:shutdown()
    if self.completed then return end
    self.completed = true
    local unregisterFailed = self.ambiguousRegistrationState
    if type(UnregisterHook) == 'function' then
      for index = #self.registered, 1, -1 do
        local registration = self.registered[index]
        local ok = pcall(function()
          UnregisterHook(registration.candidateSelection.candidate.hookPath,
            registration.preId, registration.postId)
        end)
        if not ok then unregisterFailed = true end
      end
    elseif #self.registered > 0 then
      unregisterFailed = true
    end
    local finalSelection = self.selection.canary
      or (#self.selection.trusted > 0 and self.selection.trusted[1] or nil)
    if finalSelection and not self.journal.faulted then
      pcall(function()
        self:boundary(finalSelection, finalSelection == self.selection.canary and 'canary' or 'trusted',
          self.selection.runId, 'runtime', 'run-complete')
      end)
    end
    self.config.progressiveHooksArmed = unregisterFailed
    if unregisterFailed then
      self.state:tripCircuit('progressive:unregistration',
        'one or more progressive hooks could not be unregistered; callbacks are inert but hooks remain registered', 'open')
    end
    return not unregisterFailed
  end

  function o:summary()
    local canaryEntry = self.selection.canary and self.entries[self.selection.canary.candidateId] or nil
    return {
      runId = self.selection.runId,
      runType = self.selection.runType,
      trustedHookCount = #self.selection.trusted,
      registeredHookCount = self.registeredCount,
      activeCanaryId = self.selection.canary and self.selection.canary.candidateId or '',
      canaryValidationDepth = self.selection.canary and self.selection.canary.validationDepth or 0,
      suggestedAction = self.selection.canary and tostring(self.selection.canary.candidate.suggestedAction or '') or '',
      canaryRegistrationState = self.canaryRegistrationState,
      callbackCount = self.callbackCount,
      canaryCallbackCount = canaryEntry and canaryEntry.callbackCount or 0,
      canaryCircuitBreakers = canaryEntry and breakerSummary(canaryEntry.breakers) or {},
      journal = self.journal:summary(),
      registrationOrder = self.registrationOrder,
      automaticInProcessAdvance = false
    }
  end

  return o
end

return runnerFactory
