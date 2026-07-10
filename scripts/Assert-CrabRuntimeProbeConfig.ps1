$script:CrabRuntimeProbeRequiredModFiles = @(
  "enabled.txt",
  "Scripts\config.txt",
  "Scripts\crp_log.lua",
  "Scripts\main.lua",
  "Scripts\json.lua",
  "Scripts\runtime_context.lua",
  "Scripts\safe_access.lua",
  "Scripts\probe_registry.lua",
  "Scripts\probe_runner.lua",
  "Scripts\evidence_writer.lua",
  "Scripts\result_writer.lua",
  "Scripts\record_builder.lua",
  "Scripts\campaign_state.lua",
  "Scripts\status_writer.lua",
  "Scripts\passive_hook_manager.lua",
  "Scripts\inventory_stage_manager.lua",
  "Scripts\full_observe_coordinator.lua",
  "Scripts\crabsync_catalog.lua"
)

$script:CrabRuntimeProbeRequiredConfigDefaults = [ordered]@{
  enabled = "true"
  mode = "observe"
  tickDriver = "none"
  debugBreadcrumbs = "true"
  debugTickHeartbeat = "false"
  debugWriterSelfTest = "false"
  allowHudTickHook = "false"
  writeJsonlResults = "true"
  writeMarkdownSnapshots = "false"
  observeIntervalTicks = "10"
  probeIntervalTicks = "10"
  startupWarmupTicks = "60"
  contextStableTicksRequired = "10"
  maxProbesPerSession = "100"
  repeatProbeSet = "false"
  allowUnknownRoleProbes = "false"
  allowJoinedClientDeepProbes = "false"
  allowDeepArrayProbes = "false"
  allowInventoryInfoProbes = "false"
  allowHealthProbes = "false"
  allowIdentityProbes = "false"
  allowRawIdentityEvidence = "false"
  allowResourceVisibilityProbes = "false"
  allowCrystalsReadProbes = "false"
  allowSlotsReadProbes = "false"
  allowSafeScalarWatchProbes = "false"
  allowPerkDataAssetCatalogProbes = "false"
  allowMaxSafePlayRecorderProbes = "false"
  allowInventoryArrayShallowProbes = "false"
  allowInventoryArrayShapeConfirmProbes = "false"
  allowInventoryUserdataIntrospectionProbes = "false"
  allowInventoryArrayCountProbes = "false"
  allowInventoryElementDataAssetReadProbes = "false"
  fullObserveEnabled = "false"
  allowPassiveObservationHooks = "false"
  allowFullObserveInventoryStages = "false"
  allowFullObserveRuntimeDiscovery = "false"
  statusWriterEnabled = "false"
  allowWriteProbes = "false"
  allowRpcProbes = "false"
  campaignName = "crabsync-full-observe"
  campaignId = "unassigned"
  campaignSessionId = "unassigned"
  machineId = "unassigned"
  selectedRole = "unselected"
  campaignGeneration = "0"
  resumeEvidenceSequence = "0"
  resumeStatusSequence = "0"
  statusRingSize = "4"
  fullObserveHeartbeatSeconds = "1"
  fullObserveInventoryIntervalSeconds = "2"
  fullObserveInventoryHeartbeatSeconds = "30"
  fullObserveCleanSamplesRequired = "3"
  fullObserveStableSamplesRequired = "3"
  fullObserveStableDwellSeconds = "2"
  fullObserveHookGlobalRowCap = "2048"
  fullObserveHookPerDescriptorRowCap = "128"
  fullObserveHookMinIntervalSeconds = "1"
  fullObserveHookTrackedDescriptorCap = "128"
  fullObserveSlotStabilityWindowSeconds = "30"
  fullObserveSlotStabilitySamplesRequired = "5"
  fullObserveMaxInventoryItems = "32"
  fullObserveMaxEnhancements = "16"
  fullObserveMaxStageRowsPerCategory = "256"
  resumeWeaponModsStage = "1"
  resumeAbilityModsStage = "1"
  resumeMeleeModsStage = "1"
  resumePerksStage = "1"
  resumeRelicsStage = "1"
  safeScalarWatchIntervalSeconds = "5"
  safeScalarWatchHeartbeatSeconds = "60"
  safeScalarWatchMaxSamples = "240"
  maxSafePlayIntervalSeconds = "5"
  maxSafePlayHeartbeatSeconds = "60"
  maxSafePlayMaxSamples = "720"
  maxSafePlayPerkCatalogIntervalSeconds = "60"
  maxSafePlayMaxPerkCatalogSnapshots = "60"
  maxSafePlayLogUnchangedHeartbeat = "true"
  perkDataAssetCatalogMaxCandidates = "64"
  perkDataAssetCatalogMaxFields = "32"
  perkDataAssetCatalogMaxRejectionDiagnostics = "16"
  probeSet = "shallow-core"
}

$script:CrabRuntimeProbeAllowedTickDrivers = @("none", "registerTick", "executeDelay", "loopAsync", "hud")

function Resolve-CrabRuntimeProbeRepoRoot {
  param(
    [Parameter(Mandatory = $true)][string]$StartPath,
    [switch]$RequireGit
  )

  $item = Get-Item -LiteralPath $StartPath -ErrorAction Stop
  $current = if ($item.PSIsContainer) { $item.FullName } else { Split-Path -Parent $item.FullName }

  while (-not [string]::IsNullOrWhiteSpace($current)) {
    $configPath = Join-Path $current "client\Mods\CrabRuntimeProbe\Scripts\config.txt"
    $scriptsPath = Join-Path $current "scripts"
    $readmePath = Join-Path $current "README.md"
    if (
      (Test-Path -LiteralPath $configPath -PathType Leaf) -and
      (Test-Path -LiteralPath $scriptsPath -PathType Container) -and
      (Test-Path -LiteralPath $readmePath -PathType Leaf)
    ) {
      $gitPath = Join-Path $current ".git"
      if ($RequireGit -and -not (Test-Path -LiteralPath $gitPath)) {
        throw @"
This looks like a copied or stale CrabRuntimeProbe folder, not a real Git checkout:
$current

Missing .git. Run this script from the real Dudiebug/crabruntimeprobe- checkout on branch main, then install or export from there.
"@
      }
      return [System.IO.Path]::GetFullPath($current)
    }

    $parent = Split-Path -Parent $current
    if ($parent -eq $current) { break }
    $current = $parent
  }

  throw "Could not locate the CrabRuntimeProbe repo root from $StartPath. Expected client\Mods\CrabRuntimeProbe\Scripts\config.txt under the real checkout."
}

function Assert-CrabRuntimeProbeInsidePath {
  param(
    [Parameter(Mandatory = $true)][string]$Parent,
    [Parameter(Mandatory = $true)][string]$Child
  )

  $parentExact = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\')
  $parentFull = $parentExact + '\'
  $childFull = [System.IO.Path]::GetFullPath($Child)
  $childExact = $childFull.TrimEnd('\')

  if (($childExact -ne $parentExact) -and (-not $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to operate outside $parentExact`: $childFull"
  }
}

function Get-CrabRuntimeProbeConfigMatches {
  param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [Parameter(Mandatory = $true)][string]$Key
  )

  $pattern = "^\s*$([regex]::Escape($Key))\s*=\s*(.*?)\s*$"
  $lines = $null
  for ($attempt = 1; $attempt -le 50; $attempt++) {
    try {
      $lines = @(Get-Content -LiteralPath $ConfigPath -ErrorAction Stop)
      break
    } catch {
      if ($attempt -eq 50) { throw }
      Start-Sleep -Milliseconds 200
    }
  }

  return @($lines | ForEach-Object {
    if ($_ -match $pattern) {
      $matches[1].Trim()
    }
  })
}

function Get-CrabRuntimeProbeConfigValue {
  param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [Parameter(Mandatory = $true)][string]$Key
  )

  $values = @(Get-CrabRuntimeProbeConfigMatches -ConfigPath $ConfigPath -Key $Key)
  if ($values.Count -eq 0) { return $null }
  return $values[0]
}

function Assert-CrabRuntimeProbeConfig {
  param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [string]$Label = "CrabRuntimeProbe config",
    [switch]$AllowRuntimeTickDriver,
    [switch]$AllowHudTickHook
  )

  if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "Missing required $Label`: $ConfigPath"
  }

  $errors = New-Object System.Collections.Generic.List[string]
  foreach ($key in $script:CrabRuntimeProbeRequiredConfigDefaults.Keys) {
    $expected = $script:CrabRuntimeProbeRequiredConfigDefaults[$key]
    $values = @(Get-CrabRuntimeProbeConfigMatches -ConfigPath $ConfigPath -Key $key)
    if ($values.Count -eq 0) {
      $errors.Add("Missing required config key: $key") | Out-Null
      continue
    }
    if ($values.Count -gt 1) {
      $errors.Add("Duplicate config key: $key") | Out-Null
    }
    foreach ($value in $values) {
      $isRuntimeTickDriver = $AllowRuntimeTickDriver -and $key -eq "tickDriver"
      $isRuntimeHudGate = $AllowHudTickHook -and $key -eq "allowHudTickHook"
      if (-not $isRuntimeTickDriver -and -not $isRuntimeHudGate -and -not [string]::Equals($value, $expected, [System.StringComparison]::OrdinalIgnoreCase)) {
        $errors.Add("Unsafe config default: $key expected '$expected' got '$value'") | Out-Null
      }
    }
  }

  $tickDriver = Get-CrabRuntimeProbeConfigValue -ConfigPath $ConfigPath -Key "tickDriver"
  if ($null -ne $tickDriver -and $script:CrabRuntimeProbeAllowedTickDrivers -notcontains $tickDriver) {
    $errors.Add("Invalid tickDriver '$tickDriver'. Allowed values: $($script:CrabRuntimeProbeAllowedTickDrivers -join ', ')") | Out-Null
  }

  $allowHudTickHookValue = Get-CrabRuntimeProbeConfigValue -ConfigPath $ConfigPath -Key "allowHudTickHook"
  if ($tickDriver -eq "hud" -and $allowHudTickHookValue -ne "true") {
    $errors.Add("tickDriver = hud requires allowHudTickHook = true") | Out-Null
  }

  if (-not $AllowHudTickHook -and $allowHudTickHookValue -eq "true") {
    $errors.Add("allowHudTickHook must be false by default") | Out-Null
  }

  if ($errors.Count -gt 0) {
    $message = "Invalid $Label at $ConfigPath`n" + (($errors | ForEach-Object { " - $_" }) -join "`n")
    throw $message
  }
}

function Assert-CrabRuntimeProbeModLayout {
  param(
    [Parameter(Mandatory = $true)][string]$ModRoot,
    [string]$Label = "CrabRuntimeProbe mod"
  )

  if (-not (Test-Path -LiteralPath $ModRoot -PathType Container)) {
    throw "Missing required $Label directory: $ModRoot"
  }

  $errors = New-Object System.Collections.Generic.List[string]
  foreach ($relativePath in $script:CrabRuntimeProbeRequiredModFiles) {
    $full = Join-Path $ModRoot $relativePath
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
      $errors.Add("Missing required file: $relativePath") | Out-Null
    }
  }

  if ($errors.Count -gt 0) {
    $message = "Invalid $Label at $ModRoot`n" + (($errors | ForEach-Object { " - $_" }) -join "`n")
    throw $message
  }
}

function Get-CrabRuntimeProbeGitValue {
  param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string[]]$Arguments
  )

  try {
    $output = & git -C $RepoRoot @Arguments 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($output)) {
      return ($output | Select-Object -First 1).Trim()
    }
  } catch {
  }
  return "unavailable"
}

function Write-CrabRuntimeProbeBuildInfo {
  param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$ModRoot,
    [Parameter(Mandatory = $true)][string]$Action
  )

  $scriptsRoot = Join-Path $ModRoot "Scripts"
  if (-not (Test-Path -LiteralPath $scriptsRoot -PathType Container)) {
    throw "Cannot write build_info.txt because Scripts is missing: $scriptsRoot"
  }

  $commit = Get-CrabRuntimeProbeGitValue -RepoRoot $RepoRoot -Arguments @("rev-parse", "HEAD")
  $branch = Get-CrabRuntimeProbeGitValue -RepoRoot $RepoRoot -Arguments @("branch", "--show-current")
  $buildInfoPath = Join-Path $scriptsRoot "build_info.txt"

  $lines = @(
    "action = $Action",
    "git_commit = $commit",
    "git_branch = $branch",
    "timestamp = $((Get-Date).ToString('o'))",
    "source_repo_path = $RepoRoot"
  )
  Set-Content -LiteralPath $buildInfoPath -Value $lines -Encoding ASCII
  return $buildInfoPath
}
