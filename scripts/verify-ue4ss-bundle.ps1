[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [string]$BundlePath
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Assert-CrabRuntimeProbeConfig.ps1")

$RepoRoot = Resolve-CrabRuntimeProbeRepoRoot -StartPath $PSScriptRoot -RequireGit

if ([string]::IsNullOrWhiteSpace($BundlePath)) {
  $defaultBundle = Get-ChildItem -LiteralPath (Join-Path $RepoRoot "dist") -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "CrabRuntimeProbe-v*-UE4SS" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

  if ($null -eq $defaultBundle) {
    Write-Host "No UE4SS bundle path supplied and no dist\CrabRuntimeProbe-v*-UE4SS bundle exists. Skipping bundle verification."
    exit 0
  }

  $BundlePath = $defaultBundle.FullName
}

$BundleRoot = [System.IO.Path]::GetFullPath($BundlePath)
$errors = New-Object System.Collections.Generic.List[string]

function Add-Error {
  param([string]$Message)
  $errors.Add($Message) | Out-Null
}

function Require-File {
  param([string]$RelativePath)
  $full = Join-Path $BundleRoot $RelativePath
  if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Add-Error "Missing required file: $RelativePath"
  }
}

function Require-DirectoryAbsent {
  param([string]$RelativePath)
  $full = Join-Path $BundleRoot $RelativePath
  if (Test-Path -LiteralPath $full -PathType Container) {
    Add-Error "Forbidden directory present: $RelativePath"
  }
}

if (-not (Test-Path -LiteralPath $BundleRoot -PathType Container)) {
  Write-Error "Bundle path does not exist: $BundleRoot"
  exit 1
}

foreach ($file in @(
  "UE4SS-LICENSE.txt",
  "UE4SS-settings.ini",
  "INSTALL.txt",
  "CrabRuntimeProbe-README.md",
  "Mods\mods.txt",
  "Mods\CrabRuntimeProbe\enabled.txt",
  "Mods\CrabRuntimeProbe\Scripts\config.txt",
  "Mods\CrabRuntimeProbe\Scripts\crp_log.lua",
  "Mods\CrabRuntimeProbe\Scripts\json.lua",
  "Mods\CrabRuntimeProbe\Scripts\main.lua",
  "Mods\CrabRuntimeProbe\Scripts\probe_registry.lua",
  "Mods\CrabRuntimeProbe\Scripts\probe_runner.lua",
  "Mods\CrabRuntimeProbe\Scripts\result_writer.lua",
  "Mods\CrabRuntimeProbe\Scripts\runtime_context.lua",
  "Mods\CrabRuntimeProbe\Scripts\safe_access.lua",
  "Mods\CrabRuntimeProbe\Scripts\snapshot_sampler.lua",
  "schemas\snapshot-observation-v1.schema.json"
)) {
  Require-File $file
}

foreach ($supportDir in @(
  "Mods\BPML_GenericFunctions",
  "Mods\BPModLoaderMod",
  "Mods\Keybinds",
  "Mods\shared"
)) {
  if (-not (Test-Path -LiteralPath (Join-Path $BundleRoot $supportDir) -PathType Container)) {
    Add-Error "Missing UE4SS support directory: $supportDir"
  }
}

$modsTxt = Join-Path $BundleRoot "Mods\mods.txt"
if (Test-Path -LiteralPath $modsTxt) {
  $modsText = Get-Content -Raw -LiteralPath $modsTxt
  foreach ($requiredMod in @("BPModLoaderMod : 1", "BPML_GenericFunctions : 1", "CrabRuntimeProbe : 1", "Keybinds : 1")) {
    if ($modsText -notmatch [regex]::Escape($requiredMod)) {
      Add-Error "Mods/mods.txt missing entry: $requiredMod"
    }
  }
  if ($modsText -match "CrabInventorySync") {
    Add-Error "Mods/mods.txt must not enable CrabInventorySync"
  }
}

$configPath = Join-Path $BundleRoot "Mods\CrabRuntimeProbe\Scripts\config.txt"
if (Test-Path -LiteralPath $configPath) {
  try {
    Assert-CrabRuntimeProbeConfig -ConfigPath $configPath -Label "bundle config"
  } catch {
    Add-Error $_.Exception.Message
  }
}

$payloadScriptsRoot = Join-Path $BundleRoot "Mods\CrabRuntimeProbe\Scripts"
if (Test-Path -LiteralPath $payloadScriptsRoot -PathType Container) {
  try {
    Assert-CrabRuntimeProbeModLayout `
      -ModRoot (Split-Path -Parent $payloadScriptsRoot) `
      -Label 'UE4SS bundle mod'
  } catch {
    Add-Error $_.Exception.Message
  }
  try {
    Assert-CrabRuntimeProbeNormalSamplerSafety `
      -ScriptsRoot $payloadScriptsRoot `
      -Label 'UE4SS bundle normal snapshot sampler'
  } catch {
    Add-Error $_.Exception.Message
  }
}

$snapshotSchemaPath = Join-Path $BundleRoot "schemas\snapshot-observation-v1.schema.json"
if (Test-Path -LiteralPath $snapshotSchemaPath -PathType Leaf) {
  try {
    Assert-CrabRuntimeProbeSnapshotObservationSchema `
      -SchemaPath $snapshotSchemaPath `
      -Label 'UE4SS bundle snapshot observation schema'
  } catch {
    Add-Error $_.Exception.Message
  }
}

foreach ($dir in @(
  "Mods\CrabInventorySync",
  "server",
  "objectdump",
  ".git",
  "node_modules"
)) {
  Require-DirectoryAbsent $dir
}

$forbiddenFiles = Get-ChildItem -LiteralPath $BundleRoot -Recurse -Force -File | Where-Object {
  $_.Name -match '\.dmp$' -or
  $_.Name -match '\.jsonl$' -or
  $_.Name -match '\.log$' -or
  $_.Name -match '^push.*\.json$' -or
  $_.Name -match '^recv.*\.json$'
}
foreach ($file in $forbiddenFiles) {
  Add-Error "Forbidden runtime file present: $($file.FullName.Substring($BundleRoot.Length).TrimStart('\'))"
}

$forbiddenDirs = Get-ChildItem -LiteralPath $BundleRoot -Recurse -Force -Directory | Where-Object {
  $_.Name -eq ".git" -or
  $_.Name -eq "node_modules" -or
  $_.Name -eq "objectdump" -or
  $_.Name -eq "server" -or
  $_.Name -eq "results"
}
foreach ($dir in $forbiddenDirs) {
  Add-Error "Forbidden directory present: $($dir.FullName.Substring($BundleRoot.Length).TrimStart('\'))"
}

if ($errors.Count -gt 0) {
  Write-Host "Bundle verification failed:" -ForegroundColor Red
  foreach ($err in $errors) {
    Write-Host " - $err" -ForegroundColor Red
  }
  exit 1
}

Write-Host "Bundle verification passed: $BundleRoot"
exit 0
