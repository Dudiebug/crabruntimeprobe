local crpLog = require('crp_log')
local baselineFactory = require('full_observe_coordinator')
local journalFactory = require('progressive_breadcrumb_journal')
local manifestWriter = require('progressive_run_manifest')
local hookRunnerFactory = require('progressive_hook_runner')
local relicValidatorFactory = require('relic_count_validator')

local coordinator = {}

function coordinator.new(sessionId, config, safe, evidenceWriter, selection)
  local baseline = baselineFactory.new(sessionId, config, safe, evidenceWriter)
  local journal = journalFactory.new(selection.runId)
  local hooks = hookRunnerFactory.new(config, selection, safe, evidenceWriter, baseline.state, journal)
  local relics = relicValidatorFactory.new(config, safe, evidenceWriter, baseline.state,
    selection.relicCountValidationEnabled)
  local o = {
    sessionId = sessionId,
    config = config,
    selection = selection,
    baseline = baseline,
    state = baseline.state,
    snapshots = baseline.snapshots,
    journal = journal,
    hooks = hooks,
    relics = relics,
    active = false,
    researchAllowed = not journal.faulted,
    runManifestWritten = false,
    registrationAttempted = false,
    registrationComplete = false,
    researchFaulted = false,
    shutdownComplete = false
  }

  function o:updateStatus(stage)
    local baselineReady = type(self.snapshots.isBaselineReady) == 'function'
      and self.snapshots:isBaselineReady() or false
    local summary = self.hooks:summary()
    summary.stage = tostring(stage or '')
    summary.safeSnapshotBaselineReady = baselineReady
    summary.relicCount = self.relics:summary()
    summary.compatibilityFingerprint = self.selection.compatibilityFingerprint
    summary.hookCatalogIdentity = self.selection.catalog.hookCatalogIdentity
    summary.researchAllowed = self.researchAllowed
    summary.registrationAttempted = self.registrationAttempted
    summary.registrationComplete = self.registrationComplete
    self.state:setResearchSummary(summary)
  end

  function o:start()
    self.config.progressiveHooksArmed = false
    self.state.activeProfile = 'progressive-broad-observation'
    self.state.workflow = 'progressive-broad-observation'
    self.state.collectionReadiness = 'warming'
    self.runManifestWritten = manifestWriter.write(self.sessionId, self.config, self.selection)
    if not self.runManifestWritten then self.researchAllowed = false end

    local baselineStarted = self.baseline:start()
    if baselineStarted ~= true then self.researchAllowed = false end
    self.active = baselineStarted == true
    if not self.runManifestWritten then
      self.state:tripCircuit('progressive:run-manifest', 'run manifest write failed; hooks remain disabled', 'open')
    end
    if self.journal.faulted then
      self.state:tripCircuit('progressive:journal', 'breadcrumb journal unavailable; hooks remain disabled', 'open')
    end
    self:updateStatus(self.researchAllowed and 'waiting-for-safe-baseline' or 'baseline-only-research-rejected')
    self.state:flushStatus('progressive-started')
    crpLog.line('[CrabRuntimeProbe] progressive observation run=' .. tostring(self.selection.runId)
      .. ' type=' .. tostring(self.selection.runType)
      .. ' trusted=' .. tostring(#self.selection.trusted)
      .. ' canary=' .. tostring(self.selection.canary and self.selection.canary.candidateId or 'none'))
    return self.active
  end

  function o:shutdown()
    if self.shutdownComplete then return end
    self.shutdownComplete = true
    self.hooks:shutdown()
    self:updateStatus('stopped')
  end

  function o:degradeResearch(reason)
    self.researchAllowed = false
    self.researchFaulted = true
    self.registrationComplete = false
    self.hooks:shutdown()
    self.state.collectionReadiness = self.config.progressiveHooksArmed == true and 'faulted' or 'degraded'
    self:updateStatus(tostring(reason or 'research-faulted-baseline-only'))
    self.state:flushStatus('progressive-research-faulted')
  end

  function o:onTick(runnerState)
    if not self.active then return end
    self.baseline:onTick(runnerState)
    if self.state.stopRequested then
      self:shutdown()
      self.active = false
      self.state:flushStatus('progressive-stopped')
      return
    end

    local baselineReady = type(self.snapshots.isBaselineReady) == 'function'
      and self.snapshots:isBaselineReady() or false
    if baselineReady then self.state.collectionReadiness = 'ready' end

    if self.registrationComplete and self.hooks:hasRuntimeFault() and not self.researchFaulted then
      self:degradeResearch('runtime-breaker-open-baseline-only')
    end
    if self.researchFaulted then
      self.state.collectionReadiness = self.config.progressiveHooksArmed == true and 'faulted' or 'degraded'
      self:updateStatus('research-faulted-baseline-only')
      return
    end

    -- The independent relic experiment starts only after the complete safe
    -- snapshot baseline, never merely because the lifecycle barrier is stable.
    if baselineReady and self.state.lifecycleState == 'stable' and self.state.stability.ready == true then
      self.relics:onTick()
    end

    if baselineReady and self.researchAllowed and not self.registrationAttempted then
      self.registrationAttempted = true
      self:updateStatus('registering-trusted-then-canary')
      self.state:flushStatus('progressive-registration-begin')
      self.registrationComplete = self.hooks:registerConfiguredHooks()
      if not self.registrationComplete then
        self:degradeResearch('registration-degraded-baseline-only')
      else
        self.state.collectionReadiness = 'collecting'
        self:updateStatus('collecting')
      end
      self.state:flushStatus('progressive-registration-complete')
    else
      local stage = 'waiting-for-safe-baseline'
      if baselineReady then
        if self.registrationComplete then stage = 'collecting'
        elseif not self.researchAllowed then stage = 'baseline-only-research-rejected'
        else stage = 'baseline-ready' end
      end
      self:updateStatus(stage)
    end
  end

  return o
end

return coordinator
