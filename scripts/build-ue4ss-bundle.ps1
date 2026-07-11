param(
  [Parameter(Mandatory = $true)]
  [string]$CrabInvSyncRoot,

  [string]$OutputDir = "dist",
  [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
  [string]$Version = "1.0.4",
  [switch]$NoZip
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$DistRoot = Join-Path $RepoRoot "dist"
$ResolvedOutputDir = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
  [System.IO.Path]::GetFullPath($OutputDir)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputDir))
}

function Assert-InsidePath {
  param(
    [Parameter(Mandatory = $true)][string]$Parent,
    [Parameter(Mandatory = $true)][string]$Child
  )
  $parentExact = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\')
  $parentFull = $parentExact + '\'
  $childFull = [System.IO.Path]::GetFullPath($Child)
  $childExact = $childFull.TrimEnd('\')
  if (($childExact -ne $parentExact) -and (-not $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to operate outside $Parent`: $Child"
  }
}

Assert-InsidePath -Parent $DistRoot -Child $ResolvedOutputDir

$BundleName = "CrabRuntimeProbe-v$Version-UE4SS"
$BundleRoot = Join-Path $ResolvedOutputDir $BundleName
$ZipPath = Join-Path $ResolvedOutputDir "$BundleName.zip"
$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CrabRuntimeProbeBundle_" + [System.Guid]::NewGuid().ToString("N"))

function Find-CrabInvSyncRoot {
  param([Parameter(Mandatory = $true)][string]$Path)

  if (Test-Path (Join-Path $Path "client")) {
    return (Resolve-Path $Path).Path
  }

  $child = Get-ChildItem -LiteralPath $Path -Directory | Where-Object {
    Test-Path (Join-Path $_.FullName "client")
  } | Select-Object -First 1

  if ($null -ne $child) {
    return $child.FullName
  }

  throw "Could not find CrabInvSync root with a client directory under $Path"
}

function Copy-RequiredFile {
  param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
  )
  if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    throw "Missing required file: $Source"
  }
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
  Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-OptionalFile {
  param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
  )
  if (Test-Path -LiteralPath $Source -PathType Leaf) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
  }
}

function Copy-CleanDirectory {
  param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
  )
  if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
    throw "Missing required directory: $Source"
  }

  $sourceFull = [System.IO.Path]::GetFullPath($Source).TrimEnd('\') + '\'
  Get-ChildItem -LiteralPath $Source -Recurse -Force | ForEach-Object {
    $itemFull = [System.IO.Path]::GetFullPath($_.FullName)
    if (-not $itemFull.StartsWith($sourceFull, [System.StringComparison]::OrdinalIgnoreCase)) {
      throw "Refusing to copy path outside $Source`: $itemFull"
    }
    $relative = $itemFull.Substring($sourceFull.Length)
    if ([string]::IsNullOrWhiteSpace($relative)) { return }
    $segments = $relative -split '[\\/]'
    $name = $_.Name

    if ($segments -contains ".git") { return }
    if ($segments -contains "node_modules") { return }
    if ($segments -contains "results") { return }
    if ($name -match '\.(jsonl|log|dmp|dump|tmp)$') { return }
    if ($name -match '(?i)(UE4SS[_-]?ObjectDump|ObjectDump).*\.txt$') { return }
    if ($name -match '^(push|recv).*\.json$') { return }
    if ($name -match '^hook_run_(?:consumed|manifest|classification)_.*\.json$') { return }

    $target = Join-Path $Destination $relative
    if ($_.PSIsContainer) {
      New-Item -ItemType Directory -Force -Path $target | Out-Null
    } else {
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
      Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
  }
}

function Write-ModsTxt {
  param([Parameter(Mandatory = $true)][string]$ModsDir)
  $modsTxt = @"
BPModLoaderMod : 1
BPML_GenericFunctions : 1
CrabRuntimeProbe : 1

; Built-in keybinds, do not move up!
Keybinds : 1
"@
  Set-Content -LiteralPath (Join-Path $ModsDir "mods.txt") -Value $modsTxt -Encoding UTF8
}

function Write-InstallTxt {
  param([Parameter(Mandatory = $true)][string]$BundleDir)
  $install = @"
CrabRuntimeProbe UE4SS Bundle

Extract ZIP contents into:
Crab Champions\CrabChampions\Binaries\Win64

For a clean install, remove an older Mods\CrabRuntimeProbe folder before
extracting this bundle. Do not reuse v1.0.2 runtime configuration or trust data.

Normal Play Guide is hook-free. Progressive Broad Observation begins with an
empty trusted pool and only recommends OnRep_IslandRewardRarity at validation
Depth 1; no canary is prearmed by this package.

Deep inventory, InventoryInfo, health, write, and RPC probes are disabled by default.

UE4SS is redistributed under UE4SS-LICENSE.txt.

This package does not include Crab Champions game binaries.

Included UE4SS support mods from the CrabInvSync template:
- BPML_GenericFunctions
- BPModLoaderMod
- Keybinds
- shared

These support mods are UE4SS support files, not CrabRuntimeProbe gameplay code.
"@
  Set-Content -LiteralPath (Join-Path $BundleDir "INSTALL.txt") -Value $install -Encoding UTF8
}

try {
  & node (Join-Path $RepoRoot "tools\generate_crabsync_coverage_catalog.js") --validate
  if ($LASTEXITCODE -ne 0) { throw "Generated CrabSync catalog/profile validation failed." }
  & node (Join-Path $RepoRoot "tools\generate_progressive_hook_catalog.js") --validate
  if ($LASTEXITCODE -ne 0) { throw "Generated progressive hook catalog/default validation failed." }

  New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null
  New-Item -ItemType Directory -Force -Path $ResolvedOutputDir | Out-Null

  $CrabInvSyncInput = if ([System.IO.Path]::IsPathRooted($CrabInvSyncRoot)) {
    [System.IO.Path]::GetFullPath($CrabInvSyncRoot)
  } else {
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $CrabInvSyncRoot))
  }

  if (-not (Test-Path -LiteralPath $CrabInvSyncInput)) {
    throw "CrabInvSyncRoot does not exist: $CrabInvSyncInput"
  }

  if ((Test-Path -LiteralPath $CrabInvSyncInput -PathType Leaf) -and $CrabInvSyncInput -match '\.zip$') {
    $ExtractDir = Join-Path $TempRoot "CrabInvSync"
    New-Item -ItemType Directory -Force -Path $ExtractDir | Out-Null
    Expand-Archive -LiteralPath $CrabInvSyncInput -DestinationPath $ExtractDir -Force
    $TemplateRoot = Find-CrabInvSyncRoot -Path $ExtractDir
  } elseif (Test-Path -LiteralPath $CrabInvSyncInput -PathType Container) {
    $TemplateRoot = Find-CrabInvSyncRoot -Path $CrabInvSyncInput
  } else {
    throw "CrabInvSyncRoot must be a directory or .zip file: $CrabInvSyncInput"
  }

  Assert-InsidePath -Parent $DistRoot -Child $BundleRoot
  if (Test-Path -LiteralPath $BundleRoot) {
    Remove-Item -LiteralPath $BundleRoot -Recurse -Force
  }
  New-Item -ItemType Directory -Force -Path $BundleRoot | Out-Null

  $TemplateClient = Join-Path $TemplateRoot "client"
  $SourceClient = Join-Path $RepoRoot "client"
  $BundleMods = Join-Path $BundleRoot "Mods"

  foreach ($file in @("UE4SS.dll", "dwmapi.dll", "UE4SS-settings.ini")) {
    $localSource = Join-Path $SourceClient $file
    $templateSource = Join-Path $TemplateClient $file
    if (Test-Path -LiteralPath $localSource -PathType Leaf) {
      Copy-RequiredFile -Source $localSource -Destination (Join-Path $BundleRoot $file)
    } else {
      Copy-RequiredFile -Source $templateSource -Destination (Join-Path $BundleRoot $file)
    }
  }
  if (Test-Path -LiteralPath (Join-Path $SourceClient "imgui.ini") -PathType Leaf) {
    Copy-OptionalFile -Source (Join-Path $SourceClient "imgui.ini") -Destination (Join-Path $BundleRoot "imgui.ini")
  } else {
    Copy-OptionalFile -Source (Join-Path $TemplateClient "imgui.ini") -Destination (Join-Path $BundleRoot "imgui.ini")
  }

  if (Test-Path -LiteralPath (Join-Path $RepoRoot "UE4SS-LICENSE.txt") -PathType Leaf) {
    Copy-RequiredFile -Source (Join-Path $RepoRoot "UE4SS-LICENSE.txt") -Destination (Join-Path $BundleRoot "UE4SS-LICENSE.txt")
  } else {
    Copy-RequiredFile -Source (Join-Path $TemplateRoot "UE4SS-LICENSE.txt") -Destination (Join-Path $BundleRoot "UE4SS-LICENSE.txt")
  }
  Copy-RequiredFile -Source (Join-Path $RepoRoot "LICENSE") -Destination (Join-Path $BundleRoot "CrabRuntimeProbe-LICENSE.txt")
  Copy-RequiredFile -Source (Join-Path $RepoRoot "README.md") -Destination (Join-Path $BundleRoot "CrabRuntimeProbe-README.md")

  foreach ($supportMod in @("BPML_GenericFunctions", "BPModLoaderMod", "Keybinds", "shared")) {
    $localSupport = Join-Path $SourceClient "Mods\$supportMod"
    if (Test-Path -LiteralPath $localSupport -PathType Container) {
      Copy-CleanDirectory -Source $localSupport -Destination (Join-Path $BundleMods $supportMod)
    } else {
      Copy-CleanDirectory -Source (Join-Path $TemplateClient "Mods\$supportMod") -Destination (Join-Path $BundleMods $supportMod)
    }
  }

  Copy-CleanDirectory -Source (Join-Path $RepoRoot "client\Mods\CrabRuntimeProbe") -Destination (Join-Path $BundleMods "CrabRuntimeProbe")
  Copy-CleanDirectory -Source (Join-Path $RepoRoot "campaign") -Destination (Join-Path $BundleRoot "campaign")
  Copy-CleanDirectory -Source (Join-Path $RepoRoot "schemas") -Destination (Join-Path $BundleRoot "schemas")
  foreach ($doc in @(
    "CRABSYNC_FULL_CAMPAIGN_GUIDE.md",
    "CRABSYNC_COVERAGE_CATALOG.md",
    "INCIDENT_2026-07-10_HOOK_OBSERVER_CRASH.md",
    "CRABRUNTIMEPROBE_V1.0.4_RELEASE_NOTES.md"
  )) {
    Copy-RequiredFile -Source (Join-Path $RepoRoot "docs\$doc") -Destination (Join-Path $BundleRoot "docs\$doc")
  }
  Copy-RequiredFile -Source (Join-Path $RepoRoot "CHANGELOG.md") -Destination (Join-Path $BundleRoot "CHANGELOG.md")
  Copy-RequiredFile -Source (Join-Path $RepoRoot "THIRD_PARTY_NOTICES.md") -Destination (Join-Path $BundleRoot "THIRD_PARTY_NOTICES.md")
  Write-ModsTxt -ModsDir $BundleMods
  Write-InstallTxt -BundleDir $BundleRoot

  foreach ($progressiveArtifactName in @(
    "hook_candidate_catalog.json",
    "hook_validation_ledger.json",
    "trusted_hook_manifest.json",
    "hook_quarantine.json",
    "progressive_observation.defaults.json"
  )) {
    $progressiveArtifactPath = Join-Path $BundleRoot "campaign\$progressiveArtifactName"
    if (-not (Test-Path -LiteralPath $progressiveArtifactPath -PathType Leaf)) {
      throw "Missing required progressive campaign artifact: $progressiveArtifactName"
    }
  }
  $candidateCatalog = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot "campaign\hook_candidate_catalog.json") | ConvertFrom-Json -ErrorAction Stop
  $progressiveDefaults = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot "campaign\progressive_observation.defaults.json") | ConvertFrom-Json -ErrorAction Stop
  $trustedManifest = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot "campaign\trusted_hook_manifest.json") | ConvertFrom-Json -ErrorAction Stop
  if (@($trustedManifest.candidates).Count -ne 0 -or $progressiveDefaults.trustedPoolInitiallyEmpty -ne $true -or
      $progressiveDefaults.automaticInProcessAdvance -ne $false -or $progressiveDefaults.maximumCanariesPerProcess -ne 1) {
    throw "UE4SS release defaults must be empty-trust, one-canary, and no in-process advance."
  }
  $commit = (& git -C $RepoRoot rev-parse HEAD 2>$null)
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) { $commit = "unavailable" }
  $branch = (& git -C $RepoRoot branch --show-current 2>$null)
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) { $branch = "unavailable" }
  $generatedAtUtc = [DateTime]::UtcNow.ToString('o')
  @(
    "action = release",
    "product_version = $Version",
    "git_commit = $([string]$commit)",
    "git_branch = $([string]$branch)",
    "timestamp = $generatedAtUtc"
  ) | Set-Content -LiteralPath (Join-Path $BundleMods "CrabRuntimeProbe\Scripts\build_info.txt") -Encoding ASCII

  $files = Get-ChildItem -LiteralPath $BundleRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($BundleRoot.Length).TrimStart('\').Replace('\', '/')
    [ordered]@{
      path = $relative
      size = $_.Length
      sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
  }
  $manifest = [ordered]@{
    schemaVersion = 1
    product = "CrabRuntimeProbe"
    version = $Version
    runtime = "win-x64"
    bundleFormat = "ue4ss-overlay"
    commit = [string]$commit
    generatedAtUtc = $generatedAtUtc
    schemaIdentities = [ordered]@{
      liveStatus = "live-status-v1"; snapshotObservation = "snapshot-observation-v1"
      campaignControl = "campaign-control-v1"; evidenceBundle = "evidence-bundle-v1"
      coverageCatalog = "coverage-catalog-v1"; compatibilityFingerprint = "compatibility-fingerprint-v1"
      hookBreadcrumb = "hook-breadcrumb-v1"; hookCandidateCatalog = "hook-candidate-catalog-v1"
      hookQuarantine = "hook-quarantine-v1"; hookRunClassification = "hook-run-classification-v1"
      hookRunConsumed = "hook-run-consumed-v1"
      hookRunManifest = "hook-run-manifest-v1"; hookValidationLedger = "hook-validation-ledger-v1"
      trustedHookManifest = "trusted-hook-manifest-v1"
    }
    campaignIdentities = [ordered]@{
      coverageCatalogHash = [string]$candidateCatalog.coverageCatalogHash
      hookCatalogIdentity = [string]$candidateCatalog.hookCatalogIdentity
      callbackImplementationVersion = [string]$candidateCatalog.callbackImplementationVersion
      callbackSchemaVersion = [string]$candidateCatalog.callbackSchemaVersion
      validationBehaviorVersion = [string]$candidateCatalog.validationBehaviorVersion
      initialCanaryCandidateId = [string]$progressiveDefaults.initialCanaryCandidateId
      initialCanaryDepth = [int]$progressiveDefaults.initialCanaryDepth
    }
    releaseSafety = [ordered]@{
      normalPlayGuideHookFree = $true; trustedManifestCandidateCount = 0
      canaryPrearmed = $false; maximumCanariesPerProcess = 1; automaticInProcessAdvance = $false
    }
    installTarget = "Crab Champions/CrabChampions/Binaries/Win64"
    files = @($files)
  }
  $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $BundleRoot "version-manifest.json") -Encoding UTF8

  & (Join-Path $PSScriptRoot "verify-ue4ss-bundle.ps1") $BundleRoot
  if ($LASTEXITCODE -ne 0) {
    throw "Bundle verification failed."
  }

  if (-not $NoZip) {
    Assert-InsidePath -Parent $DistRoot -Child $ZipPath
    if (Test-Path -LiteralPath $ZipPath) {
      Remove-Item -LiteralPath $ZipPath -Force
    }
    Compress-Archive -Path (Join-Path $BundleRoot "*") -DestinationPath $ZipPath -Force
    Write-Host "Wrote $ZipPath"
  }

  Write-Host "Built $BundleRoot"
} finally {
  if (Test-Path -LiteralPath $TempRoot) {
    Remove-Item -LiteralPath $TempRoot -Recurse -Force
  }
}
