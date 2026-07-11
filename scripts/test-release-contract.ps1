[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Assert-CrabRuntimeProbeConfig.ps1')

$repoRoot = Resolve-CrabRuntimeProbeRepoRoot -StartPath $PSScriptRoot -RequireGit
$buildPath = Join-Path $PSScriptRoot 'build-release.ps1'
$verifyPath = Join-Path $PSScriptRoot 'verify-release.ps1'
$schemaPath = Join-Path $repoRoot 'schemas\evidence-bundle-v1.schema.json'
$liveStatusSchemaPath = Join-Path $repoRoot 'schemas\live-status-v1.schema.json'
$snapshotSchemaPath = Join-Path $repoRoot 'schemas\snapshot-observation-v1.schema.json'
$configPath = Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe\Scripts\config.txt'

foreach ($path in @($buildPath, $verifyPath, $schemaPath, $liveStatusSchemaPath, $snapshotSchemaPath)) {
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release contract file: $path" }
}

Assert-CrabRuntimeProbeConfig -ConfigPath $configPath -Label 'release payload source config'
Assert-CrabRuntimeProbeModLayout `
  -ModRoot (Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe') `
  -Label 'release payload source mod'
Assert-CrabRuntimeProbeNormalSamplerSafety `
  -ScriptsRoot (Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe\Scripts') `
  -Label 'source normal snapshot sampler'
Assert-CrabRuntimeProbeSnapshotObservationSchema `
  -SchemaPath $snapshotSchemaPath `
  -Label 'source snapshot observation schema'

$build = Get-Content -Raw -LiteralPath $buildPath
$verify = Get-Content -Raw -LiteralPath $verifyPath
foreach ($module in @(
  'record_builder.lua',
  'campaign_state.lua',
  'status_writer.lua',
  'snapshot_sampler.lua',
  'passive_hook_manager.lua',
  'inventory_stage_manager.lua',
  'full_observe_coordinator.lua',
  'crabsync_catalog.lua',
  'dashboard_autostart.lua'
)) {
  if ($verify -notmatch [regex]::Escape($module)) {
    throw "Release verification does not require $module."
  }
}
if ($verify -notmatch [regex]::Escape('coverage-catalog-v1.schema.json')) {
  throw 'Release verification does not require the coverage catalog schema.'
}
if ($verify -notmatch [regex]::Escape('snapshot-observation-v1.schema.json')) {
  throw 'Release verification does not require the snapshot observation schema.'
}
if ($verify -notmatch [regex]::Escape('INCIDENT_2026-07-10_HOOK_OBSERVER_CRASH.md')) {
  throw 'Release verification does not require the hook observer incident notice.'
}
if ($build -notmatch 'snapshotObservationSchemaVersion\s*=\s*1') {
  throw 'Release manifest does not declare snapshotObservationSchemaVersion=1.'
}
if ($build -notmatch 'generate_crabsync_coverage_catalog\.js.+--validate') {
  throw 'Release build does not validate generated CrabSync catalog/profile artifacts.'
}
foreach ($profileToken in @(
  'snapshot-observation', 'snapshotSamplerEnabled', 'gameplayHooksEnabled',
  'lifecycleHooksEnabled', 'runtimeDiscoveryEnabled', 'inventoryEscalationEnabled',
  'guiOwnsChecklistQualification', 'researchOnly'
)) {
  if ($verify -notmatch [regex]::Escape($profileToken)) {
    throw "Release verification does not enforce snapshot-first profile field $profileToken."
  }
}
if ($verify -notmatch 'Assert-CrabRuntimeProbeNormalSamplerSafety') {
  throw 'Release verification does not enforce the normal snapshot sampler safety closure.'
}
foreach ($text in @($build, $verify)) {
  if ($text -notmatch 'ObjectDump') { throw 'Release filtering must explicitly reject object-dump text files.' }
}

$schema = Get-Content -Raw -LiteralPath $schemaPath | ConvertFrom-Json -ErrorAction Stop
$required = @($schema.required)
foreach ($name in @(
  'schemaVersion', 'bundleFormat', 'campaignId', 'campaignName', 'profileId',
  'campaignGeneration', 'machineId', 'sessionId', 'selectedRole', 'preparedAtUtc',
  'collectedAtUtc', 'catalogHash', 'safety', 'files'
)) {
  if ($required -notcontains $name) { throw "Bundle schema does not require $name." }
}
foreach ($name in @(
  'writesDisabled', 'rpcCallsDisabled', 'mutationDisabled', 'rawIdentityDisabled',
  'hudHookDisabled', 'hooksDisabled', 'runtimeDiscoveryDisabled', 'inventoryStagesDisabled'
)) {
  if (@($schema.properties.safety.required) -notcontains $name) {
    throw "Bundle safety contract does not require $name."
  }
  $property = $schema.properties.safety.properties.$name
  if ($null -eq $property -or $property.type -ne 'boolean') {
    throw "Bundle safety contract must type $name as boolean."
  }
}

$liveStatusSchema = Get-Content -Raw -LiteralPath $liveStatusSchemaPath | ConvertFrom-Json -ErrorAction Stop
foreach ($name in @(
  'writesDisabled', 'rpcCallsDisabled', 'mutationDisabled', 'rawIdentityDisabled',
  'hudHookDisabled', 'hooksDisabled', 'runtimeDiscoveryDisabled', 'inventoryStagesDisabled'
)) {
  if (@($liveStatusSchema.properties.safety.required) -notcontains $name) {
    throw "Live-status safety contract does not require $name."
  }
  $property = $liveStatusSchema.properties.safety.properties.$name
  if ($null -eq $property -or $property.type -ne 'boolean') {
    throw "Live-status safety contract must type $name as boolean."
  }
}

Write-Host 'CrabRuntimeProbe release contract checks passed.'
