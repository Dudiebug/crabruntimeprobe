[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Assert-CrabRuntimeProbeConfig.ps1')

$repoRoot = Resolve-CrabRuntimeProbeRepoRoot -StartPath $PSScriptRoot -RequireGit
$buildPath = Join-Path $PSScriptRoot 'build-release.ps1'
$verifyPath = Join-Path $PSScriptRoot 'verify-release.ps1'
$schemaPath = Join-Path $repoRoot 'schemas\evidence-bundle-v1.schema.json'
$configPath = Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe\Scripts\config.txt'

foreach ($path in @($buildPath, $verifyPath, $schemaPath)) {
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release contract file: $path" }
}

Assert-CrabRuntimeProbeConfig -ConfigPath $configPath -Label 'release payload source config'
Assert-CrabRuntimeProbeModLayout `
  -ModRoot (Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe') `
  -Label 'release payload source mod'

$build = Get-Content -Raw -LiteralPath $buildPath
$verify = Get-Content -Raw -LiteralPath $verifyPath
foreach ($module in @(
  'record_builder.lua',
  'campaign_state.lua',
  'status_writer.lua',
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
foreach ($name in @('writesDisabled', 'rpcCallsDisabled', 'mutationDisabled', 'rawIdentityDisabled', 'hudHookDisabled')) {
  $property = $schema.properties.safety.properties.$name
  if ($null -eq $property -or $property.const -ne $true) {
    throw "Bundle safety contract must require $name=true."
  }
}

Write-Host 'CrabRuntimeProbe release contract checks passed.'
