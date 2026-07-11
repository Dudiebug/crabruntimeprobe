[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Assert-CrabRuntimeProbeConfig.ps1')

function Assert-Contains {
  param([string]$Text, [string]$Pattern, [string]$Message)
  if ($Text -notmatch $Pattern) { throw $Message }
}

function Expect-SafetyFailure {
  param(
    [Parameter(Mandatory = $true)][scriptblock]$Action,
    [Parameter(Mandatory = $true)][string]$ExpectedFragment
  )

  try {
    & $Action
  } catch {
    if ($_.Exception.Message -notmatch [regex]::Escape($ExpectedFragment)) {
      throw "Safety guard failed for the wrong reason. Expected '$ExpectedFragment', got '$($_.Exception.Message)'."
    }
    return
  }
  throw "Safety guard unexpectedly accepted fixture containing: $ExpectedFragment"
}

$repoRoot = Resolve-CrabRuntimeProbeRepoRoot -StartPath $PSScriptRoot -RequireGit
$luaRoot = Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe\Scripts'
$configPath = Join-Path $luaRoot 'config.txt'
$schemaPath = Join-Path $repoRoot 'schemas\snapshot-observation-v1.schema.json'

Assert-CrabRuntimeProbeConfig -ConfigPath $configPath -Label 'snapshot-first source config'
Assert-CrabRuntimeProbeModLayout `
  -ModRoot (Split-Path -Parent $luaRoot) `
  -Label 'snapshot-first source mod'
Assert-CrabRuntimeProbeSnapshotObservationSchema `
  -SchemaPath $schemaPath `
  -Label 'snapshot-first source schema'
Assert-CrabRuntimeProbeNormalSamplerSafety `
  -ScriptsRoot $luaRoot `
  -Label 'source normal snapshot sampler'

$main = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'main.lua')
$coordinator = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'full_observe_coordinator.lua')
Assert-Contains $main 'snapshotSamplerEnabled\s*=\s*false' 'main.lua must default snapshotSamplerEnabled to false.'
Assert-Contains $coordinator 'config\.snapshotSamplerEnabled' 'The normal coordinator must explicitly gate snapshot sampling.'
Assert-Contains $coordinator 'require\s*\(\s*[''"]snapshot_sampler[''"]\s*\)' 'The normal coordinator must load snapshot_sampler.'

foreach ($key in @(
  'allowPassiveObservationHooks',
  'allowFullObserveInventoryStages',
  'allowFullObserveRuntimeDiscovery'
)) {
  $value = Get-CrabRuntimeProbeConfigValue -ConfigPath $configPath -Key $key
  if ($value -ne 'false') { throw "Normal source config requires $key=false, got '$value'." }
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('CrabRuntimeProbeSnapshotSafety_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
  $safeSampler = @'
local sampler = {}
sampler.safety = {
  writesDisabled = true,
  rpcCallsDisabled = true,
  mutationDisabled = true,
  hooksDisabled = true,
  runtimeDiscoveryDisabled = true,
  inventoryStagesDisabled = true,
  rawIdentityDisabled = true
}
return sampler
'@
  Set-Content -LiteralPath (Join-Path $fixtureRoot 'snapshot_sampler.lua') -Value $safeSampler -Encoding ASCII
  Set-Content -LiteralPath (Join-Path $fixtureRoot 'full_observe_coordinator.lua') -Value "local sampler = require('snapshot_sampler')`nreturn sampler" -Encoding ASCII

  Assert-CrabRuntimeProbeNormalSamplerSafety -ScriptsRoot $fixtureRoot -Label 'safe test fixture'

  Set-Content -LiteralPath (Join-Path $fixtureRoot 'snapshot_sampler.lua') -Value ($safeSampler + "`nRegisterHook('/Script/Game.Unsafe:Call', function() end)") -Encoding ASCII
  Expect-SafetyFailure `
    -Action { Assert-CrabRuntimeProbeNormalSamplerSafety -ScriptsRoot $fixtureRoot -Label 'hook fixture' } `
    -ExpectedFragment 'RegisterHook call'

  Set-Content -LiteralPath (Join-Path $fixtureRoot 'snapshot_sampler.lua') -Value ($safeSampler + "`nForEachFunction(function() end)") -Encoding ASCII
  Expect-SafetyFailure `
    -Action { Assert-CrabRuntimeProbeNormalSamplerSafety -ScriptsRoot $fixtureRoot -Label 'discovery fixture' } `
    -ExpectedFragment 'runtime UFunction reflection'

  Set-Content -LiteralPath (Join-Path $fixtureRoot 'snapshot_sampler.lua') -Value ($safeSampler + "`nlocal WeaponMods = {}") -Encoding ASCII
  Expect-SafetyFailure `
    -Action { Assert-CrabRuntimeProbeNormalSamplerSafety -ScriptsRoot $fixtureRoot -Label 'inventory property fixture' } `
    -ExpectedFragment "crash-suspect inventory property 'WeaponMods'"

  Set-Content -LiteralPath (Join-Path $fixtureRoot 'snapshot_sampler.lua') -Value ($safeSampler + "`nlocal wrapper = {}`nlocal count = #wrapper") -Encoding ASCII
  Expect-SafetyFailure `
    -Action { Assert-CrabRuntimeProbeNormalSamplerSafety -ScriptsRoot $fixtureRoot -Label 'wrapper count fixture' } `
    -ExpectedFragment "unreviewed Lua length operation '#wrapper'"

  Set-Content -LiteralPath (Join-Path $fixtureRoot 'snapshot_sampler.lua') -Value ($safeSampler + "`nsafe.findAll('CrabPS')") -Encoding ASCII
  Expect-SafetyFailure `
    -Action { Assert-CrabRuntimeProbeNormalSamplerSafety -ScriptsRoot $fixtureRoot -Label 'find-all fixture' } `
    -ExpectedFragment 'runtime class instance enumeration helper'

  Set-Content -LiteralPath (Join-Path $fixtureRoot 'snapshot_sampler.lua') -Value $safeSampler -Encoding ASCII
  Set-Content -LiteralPath (Join-Path $fixtureRoot 'passive_hook_manager.lua') -Value 'return {}' -Encoding ASCII
  Set-Content -LiteralPath (Join-Path $fixtureRoot 'full_observe_coordinator.lua') -Value @"
local sampler = require('snapshot_sampler')
local hooks = require('passive_hook_manager')
return sampler
"@ -Encoding ASCII
  Expect-SafetyFailure `
    -Action { Assert-CrabRuntimeProbeNormalSamplerSafety -ScriptsRoot $fixtureRoot -Label 'expert dependency fixture' } `
    -ExpectedFragment "expert module 'passive_hook_manager'"

  Set-Content -LiteralPath (Join-Path $fixtureRoot 'inventory_stage_manager.lua') -Value 'return {}' -Encoding ASCII
  Set-Content -LiteralPath (Join-Path $fixtureRoot 'full_observe_coordinator.lua') -Value @"
local sampler = require('snapshot_sampler')
local inventory = require('inventory_stage_manager')
return sampler
"@ -Encoding ASCII
  Expect-SafetyFailure `
    -Action { Assert-CrabRuntimeProbeNormalSamplerSafety -ScriptsRoot $fixtureRoot -Label 'inventory dependency fixture' } `
    -ExpectedFragment "expert module 'inventory_stage_manager'"
} finally {
  if (Test-Path -LiteralPath $fixtureRoot) {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
  }
}

Write-Host 'Snapshot-first source and release safety contract checks passed.'
