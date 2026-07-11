[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Assert-CrabRuntimeProbeConfig.ps1')

function Assert-Contains {
  param([string]$Text, [string]$Pattern, [string]$Message)
  if ($Text -notmatch $Pattern) { throw $Message }
}

function Assert-NotContains {
  param([string]$Text, [string]$Pattern, [string]$Message)
  if ($Text -match $Pattern) { throw $Message }
}

$repoRoot = Resolve-CrabRuntimeProbeRepoRoot -StartPath $PSScriptRoot -RequireGit
$luaRoot = Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe\Scripts'
$configPath = Join-Path $luaRoot 'config.txt'

Assert-CrabRuntimeProbeConfig -ConfigPath $configPath -Label 'source full-observe-safe config'
Assert-CrabRuntimeProbeModLayout -ModRoot (Split-Path -Parent $luaRoot) -Label 'source full-observe mod'
Assert-CrabRuntimeProbeSnapshotObservationSchema `
  -SchemaPath (Join-Path $repoRoot 'schemas\snapshot-observation-v1.schema.json') `
  -Label 'source snapshot observation schema'
Assert-CrabRuntimeProbeNormalSamplerSafety `
  -ScriptsRoot $luaRoot `
  -Label 'source normal snapshot sampler'

$newRuntimeFiles = @(
  'record_builder.lua',
  'campaign_state.lua',
  'status_writer.lua',
  'snapshot_sampler.lua',
  'passive_hook_manager.lua',
  'inventory_stage_manager.lua',
  'full_observe_coordinator.lua'
)
$newRuntimeSource = ($newRuntimeFiles | ForEach-Object {
  $path = Join-Path $luaRoot $_
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing runtime module: $_" }
  Get-Content -Raw -LiteralPath $path
}) -join "`n"

foreach ($forbidden in @(
  'SetPropertyValue\s*\(',
  'ForEachUObject\s*\(',
  'NotifyOnNewObject\s*\(',
  'RegisterBeginPlayPostHook\s*\(',
  'RegisterHook\s*\([^\r\n]*HUD',
  '[:\.]\s*(?:Server|Client|Multicast)[A-Za-z0-9_]*\s*\(',
  '\b(?:socket|websocket|http|relay)\s*[\.:]'
)) {
  Assert-NotContains $newRuntimeSource $forbidden "Full-observe runtime contains forbidden active/broad/network path: $forbidden"
}

$main = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'main.lua')
Assert-Contains $main '(?s)local progressiveCampaign = progressiveSelection ~= nil\s+and cfg\.fullObserveEnabled == true\s+and cfg\.snapshotSamplerEnabled == true\s+and cfg\.probeSet == ''crabsync-full-observe''' 'Progressive campaign must require a validated selection and the exact snapshot profile.'
Assert-Contains $main '(?s)local snapshotCampaign = not progressiveCampaign\s+and cfg\.fullObserveEnabled == true\s+and cfg\.snapshotSamplerEnabled == true\s+and cfg\.probeSet == ''crabsync-full-observe''' 'Normal snapshot campaign must be mutually exclusive with the progressive campaign and retain its exact main-path gates.'
Assert-Contains $main 'cfg\.snapshotSamplerEnabled == true' 'Normal main path must require the snapshot sampler gate.'
Assert-Contains $main "cfg\.probeSet == 'crabsync-full-observe'" 'Normal main path must require the exact profile.'
Assert-Contains $main '(?s)if not snapshotCampaign and not progressiveCampaign then\s+local runner = require\(''probe_runner''\)' 'Legacy probe_runner must be unreachable in either snapshot campaign.'
Assert-Contains $main 'pcall\(require, "full_observe_coordinator"\)' 'Normal snapshot campaign must retain the protected literal hook-free coordinator import.'
Assert-Contains $main 'pcall\(require, "progressive_observe_coordinator"\)' 'Progressive campaign must use a separate protected literal coordinator import.'
Assert-Contains $main 'if state then state:onTick\(\) end' 'Normal snapshot ticks must bypass the legacy probe runner.'
Assert-Contains $main "validFullObserveIdentity\(cfg\)" 'Full observe must fail closed on the complete campaign identity contract.'
foreach ($identityField in @('campaignId', 'campaignSessionId', 'machineId', 'selectedRole', 'campaignGeneration')) {
  Assert-Contains $main ("config\." + [regex]::Escape($identityField)) "Full observe identity validation is missing $identityField."
}
Assert-NotContains $main "probeSet == 'all-readonly'" 'Full observe must not overload the legacy all-readonly registry path.'

foreach ($writerName in @('status_writer.lua', 'result_writer.lua', 'evidence_writer.lua')) {
  $writerSource = Get-Content -Raw -LiteralPath (Join-Path $luaRoot $writerName)
  Assert-NotContains $writerSource 'os\.execute' "$writerName must never spawn a shell while writing runtime evidence."
}

$coordinator = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'full_observe_coordinator.lua')
Assert-Contains $coordinator "tostring\(config\.mode or ''\) == 'observe'" 'Full observe must require mode=observe.'
Assert-Contains $coordinator "config\.allowWriteProbes ~= true" 'Full observe must reject enabled write probes.'
Assert-Contains $coordinator "config\.allowRpcProbes ~= true" 'Full observe must reject enabled RPC probes.'
Assert-Contains $coordinator "config\.allowHudTickHook ~= true" 'Full observe must reject HUD hooks.'
Assert-Contains $coordinator "config\.allowRawIdentityEvidence ~= true" 'Full observe must reject raw identity evidence.'
Assert-Contains $coordinator "config\.allowDeepArrayProbes ~= true" 'Full observe must reject legacy deep-array probes.'
Assert-Contains $coordinator 'campaignIdentityValid\(self\.config\)' 'Coordinator defense-in-depth must reject unassigned campaign identity.'
Assert-NotContains $coordinator "registerLifecycleHook\('RegisterBeginPlayPostHook'" 'Global BeginPlay must remain intentionally unregistered.'

$hookManager = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'passive_hook_manager.lua')
Assert-Contains $hookManager "descriptor\.safetyClassification ~= 'passive-observation-only'" 'The unreachable legacy hook manager must reject disabled research descriptors.'
Assert-NotContains (Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'crabsync_catalog.lua')) 'safetyClassification = "passive-observation-only"' 'Generated exact-call descriptors must not be classified as normal passive observers.'
Assert-Contains (Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'crabsync_catalog.lua')) 'callPolicy = "disabled-do-not-register-or-invoke"' 'Generated exact-call descriptors must remain explicitly disabled.'
Assert-Contains $hookManager 'REVIEWED_ENGINE_HOOKS' 'Engine hooks must be constrained by an explicit reviewed allowlist.'
foreach ($enginePath in @(
  '/Script/Engine.GameStateBase:OnRep_ReplicatedHasBegunPlay',
  '/Script/Engine.Pawn:OnRep_PlayerState',
  '/Script/Engine.PlayerState:OnRep_bIsInactive'
)) {
  Assert-Contains $hookManager ([regex]::Escape($enginePath)) "Missing reviewed Engine hook allowlist entry: $enginePath"
}
Assert-Contains $hookManager "runtimeStatus = 'NATURALLY_OBSERVED'" 'Hook registration must not be mislabeled as natural evidence.'
Assert-Contains $hookManager 'qualifyingChecklistLinks = \{\}' 'Registration rows must never qualify checklist items.'
Assert-NotContains $hookManager "registerDescriptor\(descriptor, group\.attemptKey \.\. '-runtime-discovery'\)" 'Unknown reflected UFunctions must remain Needs Coverage and must not be auto-hooked.'

$findAllCalls = [regex]::Matches($coordinator + "`n" + (Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'inventory_stage_manager.lua')), "\.findAll\(([^\)]*)\)")
foreach ($call in $findAllCalls) {
  if ($call.Groups[1].Value.Trim() -ne "'CrabPS'") {
    throw "Full observe may only use exact capped CrabPS FindAll discovery; found $($call.Value)."
  }
}

foreach ($key in @(
  'snapshotSamplerEnabled',
  'fullObserveEnabled',
  'allowPassiveObservationHooks',
  'allowFullObserveInventoryStages',
  'allowFullObserveRuntimeDiscovery',
  'statusWriterEnabled',
  'allowWriteProbes',
  'allowRpcProbes',
  'allowHudTickHook',
  'allowRawIdentityEvidence',
  'allowDeepArrayProbes'
)) {
  $value = Get-CrabRuntimeProbeConfigValue -ConfigPath $configPath -Key $key
  if ($value -ne 'false') { throw "Safe source config expected $key=false, got '$value'." }
}

foreach ($expected in @{
  fullObserveCleanSamplesRequired = '3'
  fullObserveStableSamplesRequired = '3'
  fullObserveStableDwellSeconds = '2'
  fullObserveHookGlobalRowCap = '2048'
  fullObserveHookPerDescriptorRowCap = '128'
  fullObserveHookMinIntervalSeconds = '1'
  fullObserveHookTrackedDescriptorCap = '128'
}.GetEnumerator()) {
  $value = Get-CrabRuntimeProbeConfigValue -ConfigPath $configPath -Key $expected.Key
  if ($value -ne $expected.Value) { throw "Source config expected $($expected.Key)=$($expected.Value), got '$value'." }
}

Write-Host 'Full-observe source safety guards passed.'
