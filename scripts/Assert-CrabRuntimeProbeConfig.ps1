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
  "Scripts\snapshot_sampler.lua",
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
  snapshotSamplerEnabled = "false"
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

function Get-CrabRuntimeProbeLuaRequireClosure {
  param(
    [Parameter(Mandatory = $true)][string]$ScriptsRoot,
    [Parameter(Mandatory = $true)][string[]]$EntryModules
  )

  if (-not (Test-Path -LiteralPath $ScriptsRoot -PathType Container)) {
    throw "Lua scripts directory is missing: $ScriptsRoot"
  }

  $queue = New-Object System.Collections.Generic.Queue[string]
  $visited = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
  $files = New-Object System.Collections.Generic.List[object]

  foreach ($entry in $EntryModules) {
    $moduleName = ([string]$entry).Trim()
    if ($moduleName.EndsWith('.lua', [System.StringComparison]::OrdinalIgnoreCase)) {
      $moduleName = $moduleName.Substring(0, $moduleName.Length - 4)
    }
    if (-not [string]::IsNullOrWhiteSpace($moduleName)) { $queue.Enqueue($moduleName) }
  }

  while ($queue.Count -gt 0) {
    $moduleName = $queue.Dequeue()
    if (-not $visited.Add($moduleName)) { continue }

    $relativePath = ($moduleName -replace '\.', '\') + '.lua'
    $modulePath = Join-Path $ScriptsRoot $relativePath
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
      throw "Normal-mode Lua dependency is missing: $moduleName ($modulePath)"
    }

    $source = Get-Content -Raw -LiteralPath $modulePath
    $files.Add([pscustomobject]@{
      Module = $moduleName
      Path = $modulePath
      Source = $source
    }) | Out-Null

    $requirePatterns = @(
      '[^A-Za-z0-9_]require\s*\(\s*[''"](?<module>[A-Za-z0-9_.]+)[''"]\s*\)',
      '[^A-Za-z0-9_]pcall\s*\(\s*require\s*,\s*[''"](?<module>[A-Za-z0-9_.]+)[''"]\s*\)'
    )
    foreach ($requirePattern in $requirePatterns) {
      foreach ($match in [regex]::Matches("`n$source", $requirePattern)) {
        $dependency = $match.Groups['module'].Value
        $dependencyPath = Join-Path $ScriptsRoot (($dependency -replace '\.', '\') + '.lua')
        if (Test-Path -LiteralPath $dependencyPath -PathType Leaf) {
          $queue.Enqueue($dependency)
        }
      }
    }
  }

  return @($files | ForEach-Object { $_ })
}

function Assert-CrabRuntimeProbeNormalSamplerSafety {
  param(
    [Parameter(Mandatory = $true)][string]$ScriptsRoot,
    [string]$Label = 'normal snapshot sampler'
  )

  $closure = @(Get-CrabRuntimeProbeLuaRequireClosure `
    -ScriptsRoot $ScriptsRoot `
    -EntryModules @('full_observe_coordinator', 'snapshot_sampler'))
  $modules = @($closure | ForEach-Object { [string]$_.Module })

  foreach ($expertModule in @('passive_hook_manager', 'inventory_stage_manager')) {
    if ($modules -contains $expertModule) {
      throw "Unsafe $Label dependency: expert module '$expertModule' is reachable from the normal sampler/coordinator path."
    }
  }

  $combined = ($closure | ForEach-Object {
    "`n-- module: $($_.Module)`n$($_.Source)"
  }) -join "`n"

  $forbidden = [ordered]@{
    '(?<![A-Za-z0-9_])RegisterHook\s*\(' = 'gameplay/native RegisterHook call'
    '(?<![A-Za-z0-9_])UnregisterHook\s*\(' = 'gameplay/native UnregisterHook call'
    '(?<![A-Za-z0-9_])RegisterBeginPlay(?:Pre|Post)?Hook\s*\(' = 'global BeginPlay lifecycle hook'
    '(?<![A-Za-z0-9_])RegisterLoadMap(?:Pre|Post)?Hook\s*\(' = 'global map lifecycle hook'
    '(?<![A-Za-z0-9_])RegisterInitGameState(?:Pre|Post)?Hook\s*\(' = 'global GameState lifecycle hook'
    '(?<![A-Za-z0-9_])ForEachFunction\s*\(' = 'runtime UFunction reflection'
    '(?<![A-Za-z0-9_])ForEachUObject\s*\(' = 'arbitrary UObject crawl'
    '(?<![A-Za-z0-9_])NotifyOnNewObject\s*\(' = 'runtime object discovery callback'
    '(?<![A-Za-z0-9_])FindAllOf\s*\(' = 'runtime class instance enumeration'
    '(?i)(?:\.|:)\s*findAll\s*\(' = 'runtime class instance enumeration helper'
    '(?i)\b(?:runtimeDiscover(?:y|Candidates)?|discoverRuntimeCandidates|runRuntimeDiscovery)\s*\(' = 'runtime discovery execution'
    '(?i)\bregisterAll\s*\(' = 'bulk hook registration'
    '(?i)\bregisterLifecycleHooks\s*\(' = 'lifecycle hook registration'
    '(?i)\binventory\s*:\s*(?:onTick|runStage)\s*\(' = 'legacy inventory stage execution'
    '(?i)\b(?:dofile|loadfile|loadstring)\s*\(' = 'dynamic Lua code/module loading'
    '(?i)\brequire\s*\((?!\s*[''"])' = 'dynamic Lua module loading'
    '(?i)\bpcall\s*\(\s*require\s*,(?!\s*[''"])' = 'dynamic protected Lua module loading'
    'require\s*\(\s*[''"]passive_hook_manager[''"]\s*\)' = 'passive hook manager import'
    'require\s*\(\s*[''"]inventory_stage_manager[''"]\s*\)' = 'inventory stage manager import'
  }

  foreach ($entry in $forbidden.GetEnumerator()) {
    if ($combined -match $entry.Key) {
      throw "Unsafe $Label path contains $($entry.Value): $($entry.Key)"
    }
  }

  $samplerPath = Join-Path $ScriptsRoot 'snapshot_sampler.lua'
  $sampler = Get-Content -Raw -LiteralPath $samplerPath
  foreach ($inventoryProperty in @('WeaponMods', 'AbilityMods', 'MeleeMods', 'Perks', 'Relics')) {
    if ($sampler -match ("\b" + [regex]::Escape($inventoryProperty) + '\b')) {
      throw "$Label may not access crash-suspect inventory property '$inventoryProperty'."
    }
  }
  if ($sampler -match '(?i)\bgetArrayLength\s*\(') {
    throw "$Label may not count crash-suspect inventory wrappers with getArrayLength."
  }
  $allowedLengthTargets = @('errors', 'parts', '(errors or {})', 'CATEGORY_DEFINITIONS')
  foreach ($lengthMatch in [regex]::Matches($sampler, '#\s*(?<target>\([^\r\n\)]*\)|[A-Za-z_][A-Za-z0-9_\.]*)')) {
    $lengthTarget = $lengthMatch.Groups['target'].Value.Trim()
    if ($allowedLengthTargets -notcontains $lengthTarget) {
      throw "$Label contains an unreviewed Lua length operation '#$lengthTarget'; inventory-wrapper counts are crash-suspect."
    }
  }
  foreach ($requiredSafetyField in @(
    'writesDisabled',
    'rpcCallsDisabled',
    'mutationDisabled',
    'hooksDisabled',
    'runtimeDiscoveryDisabled',
    'inventoryStagesDisabled',
    'rawIdentityDisabled'
  )) {
    if ($sampler -notmatch ("\b" + [regex]::Escape($requiredSafetyField) + '\s*=\s*true')) {
      throw "$Label must emit the safety field '$requiredSafetyField' as true."
    }
  }

  $coordinatorPath = Join-Path $ScriptsRoot 'full_observe_coordinator.lua'
  $coordinator = Get-Content -Raw -LiteralPath $coordinatorPath
  if ($coordinator -notmatch 'require\s*\(\s*[''"]snapshot_sampler[''"]\s*\)') {
    throw "$Label coordinator must require snapshot_sampler."
  }

  $mainPath = Join-Path $ScriptsRoot 'main.lua'
  if (Test-Path -LiteralPath $mainPath -PathType Leaf) {
    $main = Get-Content -Raw -LiteralPath $mainPath
    foreach ($expertModule in @('passive_hook_manager', 'inventory_stage_manager')) {
      $expertImport = '(?:require\s*\(\s*|pcall\s*\(\s*require\s*,\s*)[''"]' + [regex]::Escape($expertModule) + '[''"]'
      if ($main -match $expertImport) {
        throw "Unsafe $Label entrypoint imports expert module '$expertModule'."
      }
    }
    if ($main -notmatch 'pcall\s*\(\s*require\s*,\s*[''"]full_observe_coordinator[''"]\s*\)') {
      throw "$Label entrypoint must load full_observe_coordinator through the protected literal import."
    }
    foreach ($defaultFalseGate in @(
      'snapshotSamplerEnabled',
      'allowPassiveObservationHooks',
      'allowFullObserveInventoryStages',
      'allowFullObserveRuntimeDiscovery'
    )) {
      if ($main -notmatch ("\b" + [regex]::Escape($defaultFalseGate) + '\s*=\s*false')) {
        throw "$Label entrypoint must default $defaultFalseGate=false."
      }
    }
  }
}

function Assert-CrabRuntimeProbeSnapshotObservationSchema {
  param(
    [Parameter(Mandatory = $true)][string]$SchemaPath,
    [string]$Label = 'snapshot observation schema'
  )

  if (-not (Test-Path -LiteralPath $SchemaPath -PathType Leaf)) {
    throw "Missing required $Label`: $SchemaPath"
  }

  try {
    $schema = Get-Content -Raw -LiteralPath $SchemaPath | ConvertFrom-Json -ErrorAction Stop
  } catch {
    throw "Invalid $Label JSON at $SchemaPath`: $($_.Exception.Message)"
  }

  if ($schema.properties.schemaVersion.const -ne 1) {
    throw "$Label must require schemaVersion=1."
  }
  if ([string]$schema.properties.recordType.const -ne 'snapshot-observation') {
    throw "$Label must require recordType=snapshot-observation."
  }

  $topRequired = @($schema.required)
  foreach ($field in @(
    'schemaVersion',
    'recordType',
    'sessionId',
    'campaignId',
    'campaignGeneration',
    'machineId',
    'sequence',
    'timestampUtc',
    'lifecycleGeneration',
    'context',
    'selectedRole',
    'observedRole',
    'worldFingerprint',
    'playerStateFingerprint',
    'category',
    'stability',
    'fields',
    'safety',
    'dirtyEvidence',
    'crashSuspected'
  )) {
    if ($topRequired -notcontains $field) {
      throw "$Label does not require '$field'."
    }
  }

  $stabilityRequired = @($schema.properties.stability.required)
  foreach ($field in @('stable', 'sampleCount', 'dwellSeconds', 'worldStable', 'playerStateStable')) {
    if ($stabilityRequired -notcontains $field) {
      throw "$Label stability contract does not require '$field'."
    }
  }
  $stableContract = @($schema.properties.stability.allOf) | Select-Object -First 1
  if ($null -eq $stableContract -or
      $stableContract.then.properties.sampleCount.minimum -ne 10 -or
      $stableContract.then.properties.dwellSeconds.minimum -ne 30 -or
      $stableContract.then.properties.worldStable.const -ne $true -or
      $stableContract.then.properties.playerStateStable.const -ne $true) {
    throw "$Label must enforce 10 samples, 30 seconds, and stable world/PlayerState when stable=true."
  }

  $safetyRequired = @($schema.properties.safety.required)
  foreach ($field in @(
    'writesDisabled',
    'rpcCallsDisabled',
    'mutationDisabled',
    'hooksDisabled',
    'runtimeDiscoveryDisabled',
    'inventoryStagesDisabled',
    'rawIdentityDisabled'
  )) {
    if ($safetyRequired -notcontains $field) {
      throw "$Label safety contract does not require '$field'."
    }
    $property = $schema.properties.safety.properties.$field
    if ($null -eq $property -or $property.const -ne $true) {
      throw "$Label safety contract must require $field=true."
    }
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
