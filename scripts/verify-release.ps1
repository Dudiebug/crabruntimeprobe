[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$BundlePath
)

$ErrorActionPreference = "Stop"
$BundleRoot = [System.IO.Path]::GetFullPath($BundlePath)
$errors = New-Object System.Collections.Generic.List[string]

function Add-Problem([string]$Message) { $errors.Add($Message) | Out-Null }
function Require-File([string]$Relative) {
  if (-not (Test-Path -LiteralPath (Join-Path $BundleRoot $Relative) -PathType Leaf)) {
    Add-Problem "Missing required file: $Relative"
  }
}

if (-not (Test-Path -LiteralPath $BundleRoot -PathType Container)) {
  throw "Bundle does not exist: $BundleRoot"
}

foreach ($file in @(
  "CrabRuntimeProbe.Dashboard.exe",
  "Payload\UE4SS.dll",
  "Payload\dwmapi.dll",
  "Payload\Mods\mods.txt",
  "Payload\Mods\CrabRuntimeProbe\enabled.txt",
  "Payload\Mods\CrabRuntimeProbe\Scripts\main.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\dashboard_autostart.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\config.txt",
  "Payload\Mods\CrabRuntimeProbe\Scripts\record_builder.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\campaign_state.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\status_writer.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\passive_hook_manager.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\inventory_stage_manager.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\full_observe_coordinator.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\crabsync_catalog.lua",
  "campaign\crabsync-full-observe.profile.json",
  "campaign\crabsync-full-observe.checklist.json",
  "campaign\crabsync_coverage_catalog.json",
  "schemas\live-status-v1.schema.json",
  "schemas\campaign-control-v1.schema.json",
  "schemas\evidence-bundle-v1.schema.json",
  "schemas\coverage-catalog-v1.schema.json",
  "docs\CRABSYNC_FULL_CAMPAIGN_GUIDE.md",
  "docs\CRABSYNC_COVERAGE_CATALOG.md",
  "LICENSE",
  "UE4SS-LICENSE.txt",
  "THIRD_PARTY_NOTICES.md",
  "version-manifest.json"
)) { Require-File $file }

$forbidden = Get-ChildItem -LiteralPath $BundleRoot -Recurse -Force | Where-Object {
  $_.Name -in @(".git", "node_modules", "objectdump", "server", "results", "CrabInventorySync") -or
  (-not $_.PSIsContainer -and ($_.Name -match '\.(jsonl|log|dmp|dump|tmp)$' -or
    $_.Name -match '(?i)(UE4SS[_-]?ObjectDump|ObjectDump).*\.txt$'))
}
foreach ($item in $forbidden) {
  Add-Problem "Forbidden runtime/source artifact: $($item.FullName.Substring($BundleRoot.Length).TrimStart('\'))"
}

$configPath = Join-Path $BundleRoot "Payload\Mods\CrabRuntimeProbe\Scripts\config.txt"
if (Test-Path -LiteralPath $configPath) {
  $config = Get-Content -LiteralPath $configPath -Raw
  foreach ($safeDefault in @(
    "allowWriteProbes = false",
    "allowRpcProbes = false",
    "allowHudTickHook = false",
    "allowRawIdentityEvidence = false",
    "allowDeepArrayProbes = false",
    "fullObserveEnabled = false",
    "allowPassiveObservationHooks = false",
    "allowFullObserveInventoryStages = false",
    "allowFullObserveRuntimeDiscovery = false"
  )) {
    if ($config -notmatch [regex]::Escape($safeDefault)) { Add-Problem "Unsafe or missing config default: $safeDefault" }
  }
}

$mainLuaPath = Join-Path $BundleRoot "Payload\Mods\CrabRuntimeProbe\Scripts\main.lua"
$autoStartLuaPath = Join-Path $BundleRoot "Payload\Mods\CrabRuntimeProbe\Scripts\dashboard_autostart.lua"
if ((Test-Path -LiteralPath $mainLuaPath -PathType Leaf) -and
    (Test-Path -LiteralPath $autoStartLuaPath -PathType Leaf)) {
  $mainLua = Get-Content -LiteralPath $mainLuaPath -Raw
  $autoStartLua = Get-Content -LiteralPath $autoStartLuaPath -Raw
  if ($mainLua -notmatch [regex]::Escape("require('dashboard_autostart')") -or
      $mainLua -notmatch [regex]::Escape("dashboard_autostart.txt")) {
    Add-Problem "Runtime payload does not invoke dashboard autostart."
  }
  foreach ($token in @('--game-autostart', 'os.execute', 'start "" /b')) {
    if ($autoStartLua -notmatch [regex]::Escape($token)) {
      Add-Problem "Dashboard autostart contract is missing: $token"
    }
  }
}

$manifestPath = Join-Path $BundleRoot "version-manifest.json"
if (Test-Path -LiteralPath $manifestPath) {
  try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json } catch { Add-Problem "Invalid version-manifest.json: $($_.Exception.Message)" }
  if ($null -ne $manifest) {
    $manifestPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
      if ([System.IO.Path]::IsPathRooted([string]$entry.path) -or [string]$entry.path -match '\.\.') {
        Add-Problem "Manifest path is not relative/contained: $($entry.path)"
        continue
      }
      $normalizedPath = ([string]$entry.path).Replace('\', '/')
      if (-not $manifestPaths.Add($normalizedPath)) {
        Add-Problem "Duplicate manifest path: $normalizedPath"
        continue
      }
      $full = Join-Path $BundleRoot ([string]$entry.path).Replace('/', '\')
      if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        Add-Problem "Manifest file missing: $($entry.path)"
      } else {
        $actual = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        if ($actual -ne [string]$entry.sha256) { Add-Problem "Manifest hash mismatch: $($entry.path)" }
        $actualSize = (Get-Item -LiteralPath $full).Length
        if ($actualSize -ne [long]$entry.size) { Add-Problem "Manifest size mismatch: $($entry.path)" }
      }
    }
    Get-ChildItem -LiteralPath $BundleRoot -Recurse -File | ForEach-Object {
      $relative = $_.FullName.Substring($BundleRoot.Length).TrimStart('\').Replace('\', '/')
      if ($relative -ne 'version-manifest.json' -and -not $manifestPaths.Contains($relative)) {
        Add-Problem "File missing from version manifest: $relative"
      }
    }
  }
}

$textFiles = Get-ChildItem -LiteralPath $BundleRoot -Recurse -File | Where-Object {
  $_.Extension -in @(".json", ".md", ".txt", ".lua", ".ini")
}
foreach ($file in $textFiles) {
  $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
  if ($text -match '(?i)[A-Z]:\\Users\\|source_repo_path\s*=|CrabInvSync-master') {
    Add-Problem "Local/source path leaked in $($file.FullName.Substring($BundleRoot.Length).TrimStart('\'))"
  }
}

if ($errors.Count -gt 0) {
  Write-Host "Release verification failed:" -ForegroundColor Red
  $errors | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
  exit 1
}

Write-Host "Release verification passed: $BundleRoot" -ForegroundColor Green
exit 0
