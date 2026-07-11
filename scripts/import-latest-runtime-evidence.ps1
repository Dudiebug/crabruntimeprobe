[CmdletBinding()]
param(
  [string]$From = "C:\Program Files (x86)\Steam\steamapps\common\Crab Champions\CrabChampions\Binaries\Win64\Mods\CrabRuntimeProbe\Scripts\results"
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Assert-CrabRuntimeProbeConfig.ps1")

$RepoRoot = Resolve-CrabRuntimeProbeRepoRoot -StartPath $PSScriptRoot -RequireGit

Push-Location $RepoRoot
try {
  Write-Host "Importing runtime evidence from: $From"
  node tools/import_runtime_evidence.js --from $From
  Write-Host "Generating docs..."
  node tools/generate_access_docs.js
  Write-Host "Refreshing CrabSync coverage catalog from imported runtime evidence..."
  node tools/generate_crabsync_coverage_catalog.js --refresh-runtime
  Write-Host "Building wiki staging..."
  node tools/build_wiki_docs.js
  Write-Host "docs generated = docs\"
  Write-Host "wiki staged at = dist\wiki\"
} finally {
  Pop-Location
}
