local recordBuilder = require('record_builder')

local validatorFactory = {}

local CLEAN_SAMPLES_REQUIRED = 3
local INTERVAL_SECONDS = 3
local MAX_CONSECUTIVE_ERRORS = 3

local function utcNow()
  return os.date('!%Y-%m-%dT%H:%M:%SZ')
end

local function resolveLocalPlayerState(safe)
  local controller, controllerErr = safe.findFirst('CrabPC')
  if controllerErr or not safe.isValidObject(controller) then return nil, 'controller-unavailable' end
  local playerState, playerStateErr = safe.getProperty(controller, 'PlayerState')
  if playerStateErr or not safe.isValidObject(playerState) then return nil, 'playerstate-unavailable' end
  return playerState, nil
end

function validatorFactory.new(config, safe, evidenceWriter, state, enabled)
  local o = {
    config = config,
    safe = safe,
    evidenceWriter = evidenceWriter,
    state = state,
    enabled = enabled == true,
    stage = enabled == true and 'wrapper-shape' or 'disabled',
    wrapperCleanSamples = 0,
    wrapperValidatedGeneration = nil,
    baselineCount = nil,
    baselineCleanSamples = 0,
    lastCount = nil,
    increaseObserved = false,
    consecutiveErrors = 0,
    lastSampleAt = nil
  }

  function o:emit(eventName, result, details)
    local base = recordBuilder.fullObserveBase(self.config, self.state, eventName)
    local row = recordBuilder.merge(base, {
      timestamp = utcNow(),
      sequence = self.state:nextSequence(),
      recordType = 'relic-count-validation',
      category = 'relic-count',
      symbol = 'CrabPS.Relics',
      result = result,
      runtimeStatus = result == 'ok' and 'READ_OBSERVED' or (result == 'partial' and 'PARTIAL' or 'READ_ERROR'),
      validationStage = self.stage,
      lifecycleGeneration = self.state.lifecycleGeneration,
      selectedRole = self.state.selectedRole,
      localRelicCountIncreased = eventName == 'RelicCount.LocalCountIncreased',
      pickupCallbackObserved = false,
      persistenceProven = false,
      remoteVisibilityProven = false,
      writeApplySafetyProven = false,
      details = details or {},
      safetyClassification = 'relic-wrapper-count-only',
      noElementAccess = true,
      noInventoryInfo = true,
      noEnhancements = true,
      noWrites = true,
      noRpcs = true,
      noMutation = true
    })
    local ok = self.evidenceWriter:writeEvidence(row)
    self.state:noteWriteResult(ok)
    return ok
  end

  function o:fail(reason)
    self.consecutiveErrors = self.consecutiveErrors + 1
    self:emit('RelicCount.ReadFailed', 'error', {
      reasonCode = tostring(reason or 'relic-read-failed'),
      consecutiveErrors = self.consecutiveErrors
    })
    if self.consecutiveErrors >= MAX_CONSECUTIVE_ERRORS then
      self.enabled = false
      self.stage = 'faulted'
      self.state:tripCircuit('relic-count', 'relic wrapper/count read failed repeatedly', 'open')
    end
  end

  function o:faultEvidence(reason)
    self.enabled = false
    self.stage = 'faulted-evidence'
    self.state:tripCircuit('relic-count:evidence',
      tostring(reason or 'relic evidence boundary write failed'), 'open')
  end

  function o:readWrapper()
    local playerState, playerStateErr = resolveLocalPlayerState(self.safe)
    if playerStateErr then return nil, playerStateErr end
    if not self:emit('RelicCount.WrapperReadBegin', 'partial', {
      operation = 'CrabPS.Relics property wrapper read'
    }) then
      self:faultEvidence('wrapper-read-begin evidence write failed')
      return nil, 'relic-evidence-fault'
    end
    local wrapper, wrapperErr = self.safe.getProperty(playerState, 'Relics')
    if wrapperErr then return nil, 'relic-wrapper-read-error' end
    if wrapper == nil then return nil, 'relic-wrapper-nil' end
    local kind = type(wrapper)
    if kind ~= 'userdata' and kind ~= 'table' then return nil, 'relic-wrapper-kind-unsupported' end
    if not self:emit('RelicCount.WrapperReadComplete', 'ok', { wrapperKind = kind }) then
      self:faultEvidence('wrapper-read-complete evidence write failed')
      return nil, 'relic-evidence-fault'
    end
    return wrapper, nil
  end

  function o:onTick()
    if not self.enabled or self.state.stopRequested then return end
    if self.stage == 'local-count-increase-observed' then return end
    if self.state.lifecycleState ~= 'stable' or self.state.stability.ready ~= true then return end
    local now = os.time()
    if self.lastSampleAt ~= nil and (now - self.lastSampleAt) < INTERVAL_SECONDS then return end
    self.lastSampleAt = now

    if self.stage == 'wrapper-shape' then
      local wrapper, wrapperErr = self:readWrapper()
      wrapper = nil
      if wrapperErr then if self.enabled then self:fail(wrapperErr) end; return end
      self.consecutiveErrors = 0
      self.wrapperCleanSamples = self.wrapperCleanSamples + 1
      if self.wrapperCleanSamples >= CLEAN_SAMPLES_REQUIRED then
        self.wrapperValidatedGeneration = self.state.lifecycleGeneration
        self.stage = 'wait-next-lifecycle-generation'
        self:emit('RelicCount.WrapperValidated', 'ok', {
          cleanSamples = self.wrapperCleanSamples,
          countReadDeferred = true,
          nextRequiredLifecycleGeneration = self.wrapperValidatedGeneration + 1
        })
      end
      return
    end

    if self.stage == 'wait-next-lifecycle-generation' then
      if self.state.lifecycleGeneration <= (self.wrapperValidatedGeneration or self.state.lifecycleGeneration) then return end
      self.stage = 'count-baseline'
      self.baselineCleanSamples = 0
      self.baselineCount = nil
    end

    local wrapper, wrapperErr = self:readWrapper()
    if wrapperErr then if self.enabled then self:fail(wrapperErr) end; return end
    if not self:emit('RelicCount.CountReadBegin', 'partial', {
      operation = 'official wrapper length only'
    }) then
      wrapper = nil
      self:faultEvidence('count-read-begin evidence write failed')
      return
    end
    local count, countErr = self.safe.getArrayLength(wrapper)
    wrapper = nil
    if countErr or type(count) ~= 'number' then self:fail('relic-count-read-error'); return end
    self.consecutiveErrors = 0
    if not self:emit('RelicCount.CountReadComplete', 'ok', { count = count }) then
      self:faultEvidence('count-read-complete evidence write failed')
      return
    end

    if self.stage == 'count-baseline' then
      if self.baselineCount == nil or self.baselineCount == count then
        self.baselineCount = count
        self.baselineCleanSamples = self.baselineCleanSamples + 1
      else
        self.baselineCount = count
        self.baselineCleanSamples = 1
      end
      self.lastCount = count
      if self.baselineCleanSamples >= CLEAN_SAMPLES_REQUIRED then
        self.stage = 'watch-natural-count-increase'
        self:emit('RelicCount.StableBaseline', 'ok', {
          count = count,
          cleanSamples = self.baselineCleanSamples,
          label = 'local-relic-count-baseline'
        })
      end
      return
    end

    if self.stage == 'watch-natural-count-increase' then
      if self.lastCount ~= nil and count > self.lastCount then
        self.increaseObserved = true
        self.stage = 'local-count-increase-observed'
        self:emit('RelicCount.LocalCountIncreased', 'ok', {
          previousCount = self.lastCount,
          currentCount = count,
          delta = count - self.lastCount,
          label = 'local-relic-count-increased',
          conclusion = 'local relic count increased; callback, persistence, remote visibility, and write safety remain unproven'
        })
      end
      self.lastCount = count
    end
  end

  function o:summary()
    return {
      enabled = self.enabled,
      stage = self.stage,
      wrapperValidatedGeneration = self.wrapperValidatedGeneration or -1,
      baselineCount = self.baselineCount,
      lastCount = self.lastCount,
      localCountIncreaseObserved = self.increaseObserved,
      pickupCallbackObserved = false
    }
  end

  return o
end

return validatorFactory
