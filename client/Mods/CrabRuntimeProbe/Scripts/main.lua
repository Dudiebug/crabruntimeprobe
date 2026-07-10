local SCRIPT_DIR = 'Mods/CrabRuntimeProbe/Scripts/'
package.path = package.path .. ';' .. SCRIPT_DIR .. '?.lua'

local crpLog = require('crp_log')
local writerFactory = require('result_writer')
local evidenceWriterFactory = require('evidence_writer')

local DEFAULT_CONFIG = {
  enabled = true,
  mode = 'observe',
  tickDriver = 'none',
  debugBreadcrumbs = true,
  debugTickHeartbeat = false,
  debugWriterSelfTest = false,
  allowHudTickHook = false,
  writeJsonlResults = true,
  writeMarkdownSnapshots = false,
  observeIntervalTicks = 10,
  probeIntervalTicks = 10,
  startupWarmupTicks = 60,
  contextStableTicksRequired = 10,
  maxProbesPerSession = 100,
  repeatProbeSet = false,
  allowUnknownRoleProbes = false,
  allowJoinedClientDeepProbes = false,
  allowDeepArrayProbes = false,
  allowInventoryInfoProbes = false,
  allowHealthProbes = false,
  allowIdentityProbes = false,
  allowRawIdentityEvidence = false,
  allowResourceVisibilityProbes = false,
  allowCrystalsReadProbes = false,
  allowSlotsReadProbes = false,
  allowSafeScalarWatchProbes = false,
  allowPerkDataAssetCatalogProbes = false,
  allowMaxSafePlayRecorderProbes = false,
  allowInventoryArrayShallowProbes = false,
  allowInventoryArrayShapeConfirmProbes = false,
  allowInventoryUserdataIntrospectionProbes = false,
  allowInventoryArrayCountProbes = false,
  allowInventoryElementDataAssetReadProbes = false,
  fullObserveEnabled = false,
  allowPassiveObservationHooks = false,
  allowFullObserveInventoryStages = false,
  allowFullObserveRuntimeDiscovery = false,
  statusWriterEnabled = false,
  allowWriteProbes = false,
  allowRpcProbes = false,
  campaignName = 'crabsync-full-observe',
  campaignId = 'unassigned',
  campaignSessionId = 'unassigned',
  machineId = 'unassigned',
  selectedRole = 'unselected',
  campaignGeneration = 0,
  resumeEvidenceSequence = 0,
  resumeStatusSequence = 0,
  statusRingSize = 4,
  fullObserveHeartbeatSeconds = 1,
  fullObserveInventoryIntervalSeconds = 2,
  fullObserveInventoryHeartbeatSeconds = 30,
  fullObserveCleanSamplesRequired = 3,
  fullObserveStableSamplesRequired = 3,
  fullObserveStableDwellSeconds = 2,
  fullObserveHookGlobalRowCap = 2048,
  fullObserveHookPerDescriptorRowCap = 128,
  fullObserveHookMinIntervalSeconds = 1,
  fullObserveHookTrackedDescriptorCap = 128,
  fullObserveSlotStabilityWindowSeconds = 30,
  fullObserveSlotStabilitySamplesRequired = 5,
  fullObserveMaxInventoryItems = 32,
  fullObserveMaxEnhancements = 16,
  fullObserveMaxStageRowsPerCategory = 256,
  resumeWeaponModsStage = 1,
  resumeAbilityModsStage = 1,
  resumeMeleeModsStage = 1,
  resumePerksStage = 1,
  resumeRelicsStage = 1,
  safeScalarWatchIntervalSeconds = 5,
  safeScalarWatchHeartbeatSeconds = 60,
  safeScalarWatchMaxSamples = 240,
  maxSafePlayIntervalSeconds = 5,
  maxSafePlayHeartbeatSeconds = 60,
  maxSafePlayMaxSamples = 720,
  maxSafePlayPerkCatalogIntervalSeconds = 60,
  maxSafePlayMaxPerkCatalogSnapshots = 60,
  maxSafePlayLogUnchangedHeartbeat = true,
  perkDataAssetCatalogMaxCandidates = 64,
  perkDataAssetCatalogMaxFields = 32,
  perkDataAssetCatalogMaxRejectionDiagnostics = 16,
  probeSet = 'shallow-core'
}

local ALLOWED_TICK_DRIVERS = {
  none = true,
  registerTick = true,
  executeDelay = true,
  loopAsync = true,
  hud = true
}

local log = crpLog.line

local function parseConfig(path)
  local config = {}
  for k, v in pairs(DEFAULT_CONFIG) do
    config[k] = v
  end

  local f = io.open(path, 'r')
  if not f then
    return config
  end

  for line in f:lines() do
    local cleaned = line:gsub('%s*#.*$', '')
    local k, v = cleaned:match('^%s*([%w_]+)%s*=%s*(.-)%s*$')
    if k and v then
      if v == 'true' then v = true
      elseif v == 'false' then v = false
      elseif tonumber(v) ~= nil then v = tonumber(v)
      end
      config[k] = v
    end
  end

  f:close()
  return config
end

local function writeStartupRecord(writer, cfg, eventName, summary)
  return writer:write({
    event = eventName,
    tick = 0,
    mode = cfg.mode,
    tickDriver = tostring(cfg.tickDriver),
    probeId = eventName,
    probeName = eventName,
    category = 'debug',
    context = 'startup',
    role = 'unknown',
    lifecycleState = 'startup',
    result = 'ok',
    valueKind = 'startup',
    valueSummary = summary,
    error = ''
  })
end

local function readBuildInfo(path)
  local lines = {}
  local f = io.open(path, 'r')
  if not f then
    return lines
  end
  for line in f:lines() do
    lines[#lines + 1] = line
    if #lines >= 8 then break end
  end
  f:close()
  return lines
end

local cfg = parseConfig(SCRIPT_DIR .. 'config.txt')
log('[CrabRuntimeProbe] boot phase: config loaded')

if cfg.enabled == false then
  log('[CrabRuntimeProbe] disabled in config')
  return
end

if type(cfg.tickDriver) ~= 'string' or ALLOWED_TICK_DRIVERS[cfg.tickDriver] ~= true then
  log('[CrabRuntimeProbe] ERROR: invalid tickDriver=' .. tostring(cfg.tickDriver))
  return
end

local function validOpaqueId(value, minimumLength, maximumLength)
  local text = tostring(value or '')
  minimumLength = minimumLength or 1
  maximumLength = maximumLength or 96
  return text ~= '' and text ~= 'unassigned' and #text >= minimumLength and #text <= maximumLength
    and text:match('^[%w_%-]+$') ~= nil
end

local function validFullObserveIdentity(config)
  local generation = tonumber(config.campaignGeneration)
  local role = tostring(config.selectedRole or ''):lower():gsub('%s+', '-')
  return validOpaqueId(config.campaignId, 1, 128)
    and validOpaqueId(config.campaignSessionId, 8, 96)
    and validOpaqueId(config.machineId, 8, 96)
    and (role == 'host' or role == 'joined-client')
    and generation ~= nil and generation >= 1 and math.floor(generation) == generation
end

local sessionId = os.date('!%Y%m%dT%H%M%SZ')
if cfg.fullObserveEnabled == true and cfg.probeSet == 'crabsync-full-observe' then
  if validFullObserveIdentity(cfg) then
    sessionId = tostring(cfg.campaignSessionId)
  else
    log('[CrabRuntimeProbe] ERROR: full observe requires assigned campaign/session/machine/role/generation identity')
    return
  end
end
local writer = writerFactory.new(sessionId, cfg)
local evidenceWriter = evidenceWriterFactory.new(sessionId, cfg)
log('[CrabRuntimeProbe] boot phase: writer initialized')

log('[CrabRuntimeProbe] started session=' .. sessionId .. ' mode=' .. tostring(cfg.mode))
log('[CrabRuntimeProbe] config path=Mods/CrabRuntimeProbe/Scripts/config.txt')
log('[CrabRuntimeProbe] mode=' .. tostring(cfg.mode))
log('[CrabRuntimeProbe] tickDriver=' .. tostring(cfg.tickDriver))
local buildInfoLines = readBuildInfo(SCRIPT_DIR .. 'build_info.txt')
if #buildInfoLines == 0 then
  log('[CrabRuntimeProbe] build info unavailable')
else
  for _, line in ipairs(buildInfoLines) do
    log('[CrabRuntimeProbe] build ' .. tostring(line))
  end
end
evidenceWriter:writeSessionManifest(buildInfoLines)
log('[CrabRuntimeProbe] safety allowHudTickHook=' .. tostring(cfg.allowHudTickHook)
  .. ' allowDeepArrayProbes=' .. tostring(cfg.allowDeepArrayProbes)
  .. ' allowInventoryInfoProbes=' .. tostring(cfg.allowInventoryInfoProbes)
  .. ' allowHealthProbes=' .. tostring(cfg.allowHealthProbes)
  .. ' allowIdentityProbes=' .. tostring(cfg.allowIdentityProbes)
  .. ' allowRawIdentityEvidence=' .. tostring(cfg.allowRawIdentityEvidence)
  .. ' allowResourceVisibilityProbes=' .. tostring(cfg.allowResourceVisibilityProbes)
  .. ' allowCrystalsReadProbes=' .. tostring(cfg.allowCrystalsReadProbes)
  .. ' allowSlotsReadProbes=' .. tostring(cfg.allowSlotsReadProbes)
  .. ' allowSafeScalarWatchProbes=' .. tostring(cfg.allowSafeScalarWatchProbes)
  .. ' allowPerkDataAssetCatalogProbes=' .. tostring(cfg.allowPerkDataAssetCatalogProbes)
  .. ' allowMaxSafePlayRecorderProbes=' .. tostring(cfg.allowMaxSafePlayRecorderProbes)
  .. ' allowInventoryArrayShallowProbes=' .. tostring(cfg.allowInventoryArrayShallowProbes)
  .. ' allowInventoryArrayShapeConfirmProbes=' .. tostring(cfg.allowInventoryArrayShapeConfirmProbes)
  .. ' allowInventoryUserdataIntrospectionProbes=' .. tostring(cfg.allowInventoryUserdataIntrospectionProbes)
  .. ' allowInventoryArrayCountProbes=' .. tostring(cfg.allowInventoryArrayCountProbes)
  .. ' allowInventoryElementDataAssetReadProbes=' .. tostring(cfg.allowInventoryElementDataAssetReadProbes)
  .. ' fullObserveEnabled=' .. tostring(cfg.fullObserveEnabled)
  .. ' allowPassiveObservationHooks=' .. tostring(cfg.allowPassiveObservationHooks)
  .. ' allowFullObserveInventoryStages=' .. tostring(cfg.allowFullObserveInventoryStages)
  .. ' allowFullObserveRuntimeDiscovery=' .. tostring(cfg.allowFullObserveRuntimeDiscovery)
  .. ' statusWriterEnabled=' .. tostring(cfg.statusWriterEnabled)
  .. ' allowWriteProbes=' .. tostring(cfg.allowWriteProbes)
  .. ' allowRpcProbes=' .. tostring(cfg.allowRpcProbes))
log('[CrabRuntimeProbe] results primary=' .. tostring(writer.resultPath))
log('[CrabRuntimeProbe] results fallback=' .. tostring(writer.fallbackPath))
log('[CrabRuntimeProbe] evidence primary=' .. tostring(evidenceWriter.evidencePath))
log('[CrabRuntimeProbe] evidence fallback=' .. tostring(evidenceWriter.fallbackEvidencePath))

log('[CrabRuntimeProbe] boot phase: startup smoke write begin')
writeStartupRecord(writer, cfg, 'Debug.StartupSmoke', 'startup smoke')
log('[CrabRuntimeProbe] boot phase: startup smoke write complete')

if cfg.debugWriterSelfTest == true then
  writeStartupRecord(writer, cfg, 'Debug.WriterSelfTest', 'writer self-test')
end

log('[CrabRuntimeProbe] boot phase: tick driver decision')

if cfg.tickDriver == 'none' then
  log('[CrabRuntimeProbe] tick driver disabled: none')
  log('[CrabRuntimeProbe] startup smoke complete')
  log('[CrabRuntimeProbe] boot phase: startup complete')
  return
end

local safe = require('safe_access')
local runner = require('probe_runner')
local state = runner.new(cfg, safe, writer, evidenceWriter)
local fullObserveCoordinator = nil
if cfg.fullObserveEnabled == true and cfg.probeSet == 'crabsync-full-observe' then
  local coordinatorOk, coordinatorFactory = pcall(require, 'full_observe_coordinator')
  if coordinatorOk and type(coordinatorFactory) == 'table' and type(coordinatorFactory.new) == 'function' then
    local newOk, coordinatorOrErr = pcall(coordinatorFactory.new, sessionId, cfg, safe, evidenceWriter)
    if newOk then
      fullObserveCoordinator = coordinatorOrErr
    else
      log('[CrabRuntimeProbe] ERROR: full observe coordinator initialization failed: ' .. tostring(coordinatorOrErr))
    end
  else
    log('[CrabRuntimeProbe] ERROR: full observe coordinator unavailable: ' .. tostring(coordinatorFactory))
  end
end

local function tickOnce()
  local ok, err = pcall(function()
    state:onTick()
    if fullObserveCoordinator then fullObserveCoordinator:onTick(state) end
  end)
  if not ok then
    log('[CrabRuntimeProbe] tick error: ' .. tostring(err))
  end
end

local function registerSelectedTickDriver(driver)
  log('[CrabRuntimeProbe] boot phase: tick registration begin')
  log('[CrabRuntimeProbe] tick driver register begin: ' .. tostring(driver))

  if driver == 'registerTick' then
    if type(RegisterTick) ~= 'function' then
      log('[CrabRuntimeProbe] tick driver unavailable: registerTick')
      return false
    end
    RegisterTick(function()
      tickOnce()
    end)
  elseif driver == 'executeDelay' then
    if type(ExecuteWithDelay) ~= 'function' then
      log('[CrabRuntimeProbe] tick driver unavailable: executeDelay')
      return false
    end
    local function scheduleDelayedTick()
      ExecuteWithDelay(100, function()
        tickOnce()
        scheduleDelayedTick()
      end)
    end
    scheduleDelayedTick()
  elseif driver == 'loopAsync' then
    if type(LoopAsync) ~= 'function' then
      log('[CrabRuntimeProbe] tick driver unavailable: loopAsync')
      return false
    end
    LoopAsync(100, function()
      tickOnce()
      return true
    end)
  elseif driver == 'hud' then
    if cfg.allowHudTickHook ~= true then
      log('[CrabRuntimeProbe] tick driver blocked by allowHudTickHook=false: hud')
      return false
    end
    if type(RegisterHook) ~= 'function' then
      log('[CrabRuntimeProbe] tick driver unavailable: hud')
      return false
    end
    RegisterHook('/Script/Engine.HUD:ReceiveDrawHUD', function()
      tickOnce()
    end)
  end

  log('[CrabRuntimeProbe] tick source registered: ' .. tostring(driver))
  log('[CrabRuntimeProbe] boot phase: tick registration complete')
  return true
end

local ok, registeredOrError = pcall(function()
  return registerSelectedTickDriver(cfg.tickDriver)
end)

if not ok then
  log('[CrabRuntimeProbe] ERROR: tick driver registration failed: ' .. tostring(registeredOrError))
  return
end

if registeredOrError ~= true then
  log('[CrabRuntimeProbe] boot phase: startup complete')
  return
end

if fullObserveCoordinator then
  local startOk, startErr = pcall(function() return fullObserveCoordinator:start() end)
  if not startOk then
    log('[CrabRuntimeProbe] ERROR: full observe coordinator start failed: ' .. tostring(startErr))
  end
end

log('[CrabRuntimeProbe] boot phase: startup complete')
