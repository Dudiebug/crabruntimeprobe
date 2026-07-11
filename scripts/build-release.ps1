[CmdletBinding()]
param(
  [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
  [string]$Version = "1.0.0",
  [string]$OutputDir = "dist",
  [ValidateSet("win-x64")]
  [string]$Runtime = "win-x64",
  [ValidateSet("Debug", "Release")]
  [string]$Configuration = "Release",
  [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$DistRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot "dist"))
$ResolvedOutputDir = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
  [System.IO.Path]::GetFullPath($OutputDir)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputDir))
}
$BundleName = "CrabRuntimeProbe-v$Version-$Runtime"
$BundleRoot = Join-Path $ResolvedOutputDir $BundleName
$ZipPath = Join-Path $ResolvedOutputDir "$BundleName.zip"
$PublishRoot = Join-Path $DistRoot (".dashboard-publish-" + [System.Guid]::NewGuid().ToString("N"))

function Assert-InsidePath {
  param([Parameter(Mandatory = $true)][string]$Parent, [Parameter(Mandatory = $true)][string]$Child)
  $parentExact = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\')
  $parentFull = $parentExact + '\'
  $childFull = [System.IO.Path]::GetFullPath($Child)
  if (-not $childFull.Equals($parentExact, [System.StringComparison]::OrdinalIgnoreCase) -and
      -not $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to operate outside $Parent`: $Child"
  }
}

function Copy-CleanTree {
  param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination)
  if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Missing directory: $Source" }
  $sourceFull = [System.IO.Path]::GetFullPath($Source).TrimEnd('\') + '\'
  Get-ChildItem -LiteralPath $Source -Recurse -Force | ForEach-Object {
    $itemFull = [System.IO.Path]::GetFullPath($_.FullName)
    if (-not $itemFull.StartsWith($sourceFull, [System.StringComparison]::OrdinalIgnoreCase)) {
      throw "Copy source escaped root: $itemFull"
    }
    $relative = $itemFull.Substring($sourceFull.Length)
    if ([string]::IsNullOrWhiteSpace($relative)) { return }
    $segments = $relative -split '[\\/]'
    if ($segments -contains "results" -or $segments -contains ".git" -or $segments -contains "node_modules") { return }
    if ($_.Name -match '\.(jsonl|log|dmp|tmp|dump)$' -or
        $_.Name -match '^(push|recv).*\.json$' -or
        $_.Name -match '(?i)(UE4SS[_-]?ObjectDump|ObjectDump).*\.txt$') { return }
    $target = Join-Path $Destination $relative
    if ($_.PSIsContainer) {
      New-Item -ItemType Directory -Force -Path $target | Out-Null
    } else {
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
      Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
  }
}

function Copy-RequiredFile {
  param([string]$Source, [string]$Destination)
  if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Missing required file: $Source" }
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
  Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

Assert-InsidePath -Parent $DistRoot -Child $ResolvedOutputDir
Assert-InsidePath -Parent $DistRoot -Child $BundleRoot
Assert-InsidePath -Parent $DistRoot -Child $PublishRoot
New-Item -ItemType Directory -Force -Path $ResolvedOutputDir | Out-Null

if (Test-Path -LiteralPath $BundleRoot) {
  Remove-Item -LiteralPath $BundleRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $BundleRoot | Out-Null

try {
  & node (Join-Path $RepoRoot "tools\generate_crabsync_coverage_catalog.js") --validate
  if ($LASTEXITCODE -ne 0) { throw "Generated CrabSync catalog/profile validation failed." }

  $dashboardProject = Join-Path $RepoRoot "dashboard\src\CrabRuntimeProbe.Dashboard\CrabRuntimeProbe.Dashboard.csproj"
  if (-not (Test-Path -LiteralPath $dashboardProject -PathType Leaf)) {
    throw "Dashboard project is missing: $dashboardProject"
  }
  & dotnet publish $dashboardProject -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $PublishRoot
  if ($LASTEXITCODE -ne 0) { throw "Dashboard publish failed." }

  Copy-CleanTree -Source $PublishRoot -Destination $BundleRoot
  Copy-CleanTree -Source (Join-Path $RepoRoot "client") -Destination (Join-Path $BundleRoot "Payload")
  Copy-CleanTree -Source (Join-Path $RepoRoot "campaign") -Destination (Join-Path $BundleRoot "campaign")
  Copy-CleanTree -Source (Join-Path $RepoRoot "schemas") -Destination (Join-Path $BundleRoot "schemas")

  foreach ($file in @("LICENSE", "UE4SS-LICENSE.txt", "THIRD_PARTY_NOTICES.md", "README.md")) {
    Copy-RequiredFile -Source (Join-Path $RepoRoot $file) -Destination (Join-Path $BundleRoot $file)
  }
  foreach ($doc in @(
    "CRABSYNC_FULL_CAMPAIGN_GUIDE.md",
    "CRABSYNC_COVERAGE_CATALOG.md",
    "INCIDENT_2026-07-10_HOOK_OBSERVER_CRASH.md"
  )) {
    Copy-RequiredFile -Source (Join-Path $RepoRoot "docs\$doc") -Destination (Join-Path $BundleRoot "docs\$doc")
  }

  $commit = (& git -C $RepoRoot rev-parse HEAD 2>$null)
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) { $commit = "unavailable" }
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
    runtime = $Runtime
    commit = [string]$commit
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    statusSchemaVersion = 1
    snapshotObservationSchemaVersion = 1
    controlSchemaVersion = 1
    evidenceBundleSchemaVersion = 1
    installTarget = "Crab Champions/CrabChampions/Binaries/Win64"
    files = @($files)
  }
  $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $BundleRoot "version-manifest.json") -Encoding UTF8

  & (Join-Path $PSScriptRoot "verify-release.ps1") -BundlePath $BundleRoot
  if ($LASTEXITCODE -ne 0) { throw "Release verification failed." }

  if (-not $NoZip) {
    Assert-InsidePath -Parent $DistRoot -Child $ZipPath
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Compress-Archive -LiteralPath $BundleRoot -DestinationPath $ZipPath -CompressionLevel Optimal
    Write-Host "Wrote $ZipPath"
  }
  Write-Host "Built $BundleRoot"
} finally {
  if (Test-Path -LiteralPath $PublishRoot) {
    Assert-InsidePath -Parent $DistRoot -Child $PublishRoot
    Remove-Item -LiteralPath $PublishRoot -Recurse -Force
  }
}
