[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$BundlePath
)

$ErrorActionPreference = "Stop"
$BundleRoot = [System.IO.Path]::GetFullPath($BundlePath)
$errors = New-Object System.Collections.Generic.List[string]

. (Join-Path $PSScriptRoot "Assert-CrabRuntimeProbeConfig.ps1")

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
  "Payload\Mods\CrabRuntimeProbe\Scripts\snapshot_sampler.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\peer_sampler.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\research_hook_catalog.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\progressive_json_reader.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\progressive_artifact_guard.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\progressive_config.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\progressive_breadcrumb_journal.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\progressive_run_manifest.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\progressive_depth_callbacks.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\progressive_hook_runner.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\progressive_observe_coordinator.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\relic_count_validator.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\build_info.txt",
  "Payload\Mods\CrabRuntimeProbe\Scripts\passive_hook_manager.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\inventory_stage_manager.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\full_observe_coordinator.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\readiness_observe_coordinator.lua",
  "Payload\Mods\CrabRuntimeProbe\Scripts\crabsync_catalog.lua",
  "campaign\crabsync-full-observe.profile.json",
  "campaign\crabsync-full-observe.checklist.json",
  "campaign\crabsync_coverage_catalog.json",
  "campaign\hook_candidate_catalog.json",
  "campaign\hook_validation_ledger.json",
  "campaign\trusted_hook_manifest.json",
  "campaign\hook_quarantine.json",
  "campaign\progressive_observation.defaults.json",
  "schemas\live-status-v1.schema.json",
  "schemas\campaign-control-v1.schema.json",
  "schemas\evidence-bundle-v1.schema.json",
  "schemas\coverage-catalog-v1.schema.json",
  "schemas\snapshot-observation-v1.schema.json",
  "schemas\readiness-campaign-manifest-v1.schema.json",
  "schemas\peer-snapshot-v1.schema.json",
  "schemas\terminal-lifecycle-v1.schema.json",
  "schemas\compatibility-fingerprint-v1.schema.json",
  "schemas\hook-breadcrumb-v1.schema.json",
  "schemas\hook-candidate-catalog-v1.schema.json",
  "schemas\hook-quarantine-v1.schema.json",
  "schemas\hook-run-classification-v1.schema.json",
  "schemas\hook-run-consumed-v1.schema.json",
  "schemas\hook-run-manifest-v1.schema.json",
  "schemas\hook-validation-ledger-v1.schema.json",
  "schemas\trusted-hook-manifest-v1.schema.json",
  "docs\CRABSYNC_FULL_CAMPAIGN_GUIDE.md",
  "docs\CRABSYNC_COVERAGE_CATALOG.md",
  "docs\INCIDENT_2026-07-10_HOOK_OBSERVER_CRASH.md",
  "docs\CRABRUNTIMEPROBE_V1.1.0_RELEASE_NOTES.md",
  "docs\CRABRUNTIMEPROBE_V1.0.4_RELEASE_NOTES.md",
  "CHANGELOG.md",
  "LICENSE",
  "UE4SS-LICENSE.txt",
  "THIRD_PARTY_NOTICES.md",
  "version-manifest.json"
)) { Require-File $file }

$profilePath = Join-Path $BundleRoot "campaign\crabsync-full-observe.profile.json"
if (Test-Path -LiteralPath $profilePath -PathType Leaf) {
  try {
    $profile = Get-Content -Raw -LiteralPath $profilePath | ConvertFrom-Json -ErrorAction Stop
    if ($profile.mode -ne 'snapshot-observation') {
      Add-Problem 'Campaign profile mode must be snapshot-observation.'
    }
    foreach ($field in @('gameplayHooksEnabled', 'lifecycleHooksEnabled', 'runtimeDiscoveryEnabled', 'inventoryEscalationEnabled')) {
      if ($profile.normalMode.$field -ne $false) {
        Add-Problem "Campaign profile normalMode.$field must be false."
      }
    }
    if ($profile.normalMode.snapshotSamplerEnabled -ne $true -or
        $profile.normalMode.guiOwnsChecklistQualification -ne $true) {
      Add-Problem 'Campaign profile must enable the snapshot sampler and GUI-owned qualification.'
    }
    foreach ($section in @('passiveHooks', 'inventoryEscalation', 'runtimeDiscovery')) {
      $sectionValue = $profile.$section
      if ($sectionValue.enabled -ne $false -or $sectionValue.researchOnly -ne $true) {
        Add-Problem "Campaign profile $section must be disabled and research-only."
      }
    }
  } catch {
    Add-Problem "Invalid snapshot-first campaign profile: $($_.Exception.Message)"
  }
}

$progressiveArtifactPaths = [ordered]@{
  candidateCatalog = "campaign\hook_candidate_catalog.json"
  validationLedger = "campaign\hook_validation_ledger.json"
  trustedManifest = "campaign\trusted_hook_manifest.json"
  quarantine = "campaign\hook_quarantine.json"
  defaults = "campaign\progressive_observation.defaults.json"
}
$progressiveArtifacts = @{}
foreach ($entry in $progressiveArtifactPaths.GetEnumerator()) {
  $artifactPath = Join-Path $BundleRoot $entry.Value
  if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) { continue }
  try {
    $progressiveArtifacts[$entry.Key] = Get-Content -Raw -LiteralPath $artifactPath | ConvertFrom-Json -ErrorAction Stop
  } catch {
    Add-Problem "Invalid progressive artifact $($entry.Value): $($_.Exception.Message)"
  }
}
if ($progressiveArtifacts.Count -eq $progressiveArtifactPaths.Count) {
  $candidateCatalog = $progressiveArtifacts.candidateCatalog
  $validationLedger = $progressiveArtifacts.validationLedger
  $trustedManifest = $progressiveArtifacts.trustedManifest
  $quarantine = $progressiveArtifacts.quarantine
  $defaults = $progressiveArtifacts.defaults
  foreach ($artifact in @($validationLedger, $trustedManifest, $quarantine, $defaults)) {
    if ([string]$artifact.coverageCatalogHash -ne [string]$candidateCatalog.coverageCatalogHash -or
        [string]$artifact.hookCatalogIdentity -ne [string]$candidateCatalog.hookCatalogIdentity -or
        [string]$artifact.callbackImplementationVersion -ne [string]$candidateCatalog.callbackImplementationVersion -or
        [string]$artifact.callbackSchemaVersion -ne [string]$candidateCatalog.callbackSchemaVersion -or
        [string]$artifact.validationBehaviorVersion -ne [string]$candidateCatalog.validationBehaviorVersion) {
      Add-Problem 'Progressive campaign artifacts do not share the same compatibility identities.'
      break
    }
  }
  if ($candidateCatalog.candidateCount -ne 111 -or @($candidateCatalog.candidates).Count -ne 111) {
    Add-Problem 'Release hook candidate catalog must contain the preserved 111 candidates.'
  }
  if ([string]$candidateCatalog.principalCandidateId -ne 'hook-crabps-onrep-islandrewardrarity' -or
      [string]$defaults.initialCanaryCandidateId -ne 'hook-crabps-onrep-islandrewardrarity' -or
      [int]$defaults.initialCanaryDepth -ne 1) {
    Add-Problem 'Release must recommend OnRep_IslandRewardRarity at registration-only depth 1 first.'
  }
  if (@($trustedManifest.candidates).Count -ne 0 -or
      -not [string]::IsNullOrWhiteSpace([string]$trustedManifest.compatibilityFingerprint) -or
      $defaults.trustedPoolInitiallyEmpty -ne $true) {
    Add-Problem 'Release must ship with an empty, compatibility-unassigned trusted-hook manifest.'
  }
  if (@($quarantine.entries).Count -ne 0) {
    Add-Problem 'Release quarantine defaults must not contain developer or field-test state.'
  }
  if (@($validationLedger.candidates | Where-Object { $null -ne $_.trustedDepth }).Count -ne 0) {
    Add-Problem 'Release validation ledger must not pretrust any candidate depth.'
  }
  $nonBaselineLedgerEntries = @($validationLedger.candidates | Where-Object {
    ([string]$_.state) -ne 'untested' -or [int]$_.highestValidatedDepth -ne 0 -or
    $null -ne $_.trustedDepth -or [int]$_.cleanRuns -ne 0 -or [int]$_.naturalCallbacks -ne 0 -or
    @($_.evidenceSessions).Count -ne 0 -or @($_.crashSuspectRuns).Count -ne 0 -or
    -not [string]::IsNullOrWhiteSpace([string]$_.compatibilityFingerprint) -or
    $_.hasUnmatchedBreadcrumb -eq $true -or $_.hasCorrelatedCrash -eq $true -or
    $_.hasNewUe4ssCallbackError -eq $true
  })
  if ($nonBaselineLedgerEntries.Count -ne 0) {
    Add-Problem 'Release validation ledger must contain only clean untested migration-baseline entries.'
  }
  if ($defaults.normalPlayGuideHookFree -ne $true -or
      $defaults.automaticInProcessAdvance -ne $false -or
      $defaults.maximumCanariesPerProcess -ne 1 -or
      @($defaults.registrationOrder)[-1] -ne 'canary-last') {
    Add-Problem 'Progressive defaults must keep normal mode hook-free, one canary maximum, no in-process advance, and canary-last ordering.'
  }
}

$forbidden = Get-ChildItem -LiteralPath $BundleRoot -Recurse -Force | Where-Object {
  $_.Name -in @(".git", "node_modules", "objectdump", "server", "results", "CrabInventorySync") -or
  (-not $_.PSIsContainer -and ($_.Name -match '\.(jsonl|log|dmp|dump|tmp)$' -or
    $_.Name -match '^hook_run_(?:consumed|manifest|classification)_.*\.json$' -or
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
    "snapshotSamplerEnabled = false",
    "fullObserveEnabled = false",
    "allowPassiveObservationHooks = false",
    "allowFullObserveInventoryStages = false",
    "allowFullObserveRuntimeDiscovery = false",
    "progressiveObservationEnabled = false",
    "campaignProfile = normal-play-guide",
    "readinessCampaignEnabled = false",
    "readinessPeerSnapshotsEnabled = false",
    "readinessInventoryStage = disabled",
    "canaryCandidateId = unassigned",
    "canaryHookPathFingerprint = unassigned",
    "canaryValidationDepth = 0",
    "relicCountValidationEnabled = false"
  )) {
    if ($config -notmatch [regex]::Escape($safeDefault)) { Add-Problem "Unsafe or missing config default: $safeDefault" }
  }
  try {
    Assert-CrabRuntimeProbeConfig -ConfigPath $configPath -Label 'release payload config'
  } catch {
    Add-Problem $_.Exception.Message
  }
  $trustedHooksDefault = Get-CrabRuntimeProbeConfigValue -ConfigPath $configPath -Key 'trustedCandidateSelections'
  if (-not [string]::IsNullOrWhiteSpace($trustedHooksDefault)) {
    Add-Problem 'Release payload config must ship with trustedCandidateSelections empty.'
  }
}

$payloadScriptsRoot = Join-Path $BundleRoot "Payload\Mods\CrabRuntimeProbe\Scripts"
if (Test-Path -LiteralPath $payloadScriptsRoot -PathType Container) {
  try {
    Assert-CrabRuntimeProbeModLayout `
      -ModRoot (Split-Path -Parent $payloadScriptsRoot) `
      -Label 'release payload mod'
  } catch {
    Add-Problem $_.Exception.Message
  }
  try {
    Assert-CrabRuntimeProbeNormalSamplerSafety `
      -ScriptsRoot $payloadScriptsRoot `
      -Label 'release normal snapshot sampler'
  } catch {
    Add-Problem $_.Exception.Message
  }
  try {
    Assert-CrabRuntimeProbeReadinessSamplerSafety `
      -ScriptsRoot $payloadScriptsRoot `
      -Label 'release readiness sampler'
  } catch {
    Add-Problem $_.Exception.Message
  }
}

$snapshotSchemaPath = Join-Path $BundleRoot "schemas\snapshot-observation-v1.schema.json"
if (Test-Path -LiteralPath $snapshotSchemaPath -PathType Leaf) {
  try {
    Assert-CrabRuntimeProbeSnapshotObservationSchema `
      -SchemaPath $snapshotSchemaPath `
      -Label 'release snapshot observation schema'
  } catch {
    Add-Problem $_.Exception.Message
  }
}

foreach ($safetySchemaRelative in @(
  "schemas\live-status-v1.schema.json",
  "schemas\evidence-bundle-v1.schema.json"
)) {
  $safetySchemaPath = Join-Path $BundleRoot $safetySchemaRelative
  if (-not (Test-Path -LiteralPath $safetySchemaPath -PathType Leaf)) { continue }
  try {
    $safetySchema = Get-Content -Raw -LiteralPath $safetySchemaPath | ConvertFrom-Json -ErrorAction Stop
    foreach ($field in @(
      'writesDisabled', 'rpcCallsDisabled', 'mutationDisabled', 'rawIdentityDisabled',
      'hudHookDisabled', 'hooksDisabled', 'runtimeDiscoveryDisabled', 'inventoryStagesDisabled'
    )) {
      if (@($safetySchema.properties.safety.required) -notcontains $field -or
          $safetySchema.properties.safety.properties.$field.type -ne 'boolean') {
        Add-Problem "$safetySchemaRelative must require boolean safety.$field."
      }
    }
  } catch {
    Add-Problem "Invalid safety schema $safetySchemaRelative`: $($_.Exception.Message)"
  }
}

$evidenceBundleSchemaPath = Join-Path $BundleRoot 'schemas\evidence-bundle-v1.schema.json'
if (Test-Path -LiteralPath $evidenceBundleSchemaPath -PathType Leaf) {
  try {
    $evidenceBundleSchema = Get-Content -Raw -LiteralPath $evidenceBundleSchemaPath | ConvertFrom-Json -ErrorAction Stop
    foreach ($field in @('controlledResearchHooks', 'compatibilityValidated', 'trustedDepthEnforced')) {
      if (@($evidenceBundleSchema.properties.safety.required) -notcontains $field -or
          $evidenceBundleSchema.properties.safety.properties.$field.type -ne 'boolean') {
        Add-Problem "Evidence-bundle safety must require boolean $field."
      }
    }
    $activeCanariesSchema = $evidenceBundleSchema.properties.safety.properties.activeCanaries
    if (@($evidenceBundleSchema.properties.safety.required) -notcontains 'activeCanaries' -or
        $activeCanariesSchema.type -ne 'integer' -or
        [int]$activeCanariesSchema.minimum -ne 0 -or [int]$activeCanariesSchema.maximum -ne 1) {
      Add-Problem 'Evidence-bundle safety must require integer activeCanaries in the range 0..1.'
    }
    $normalRule = @($evidenceBundleSchema.allOf | Where-Object {
      [string]$_.'if'.properties.profileId.const -eq 'crabsync-full-observe'
    }) | Select-Object -First 1
    $researchRule = @($evidenceBundleSchema.allOf | Where-Object {
      [string]$_.'if'.properties.profileId.const -eq 'progressive-broad-observation'
    }) | Select-Object -First 1
    $readinessRule = @($evidenceBundleSchema.allOf | Where-Object {
      [string]$_.'if'.properties.profileId.const -eq 'crabsync-readiness-campaign'
    }) | Select-Object -First 1
    $normalSafety = $normalRule.then.properties.safety.properties
    $researchSafety = $researchRule.then.properties.safety.properties
    $readinessSafety = $readinessRule.then.properties.safety.properties
    if ($null -eq $normalRule -or $normalSafety.hooksDisabled.const -ne $true -or
        $normalSafety.controlledResearchHooks.const -ne $false -or
        $normalSafety.compatibilityValidated.const -ne $false -or
        $normalSafety.trustedDepthEnforced.const -ne $false -or
        [int]$normalSafety.activeCanaries.const -ne 0) {
      Add-Problem 'Evidence-bundle normal profile must require all hooks disabled and research safety false/false/false/0.'
    }
    if ($null -eq $researchRule -or $researchSafety.writesDisabled.const -ne $true -or
        $researchSafety.rpcCallsDisabled.const -ne $true -or
        $researchSafety.mutationDisabled.const -ne $true -or
        $researchSafety.rawIdentityDisabled.const -ne $true -or
        $researchSafety.hudHookDisabled.const -ne $true -or
        $researchSafety.runtimeDiscoveryDisabled.const -ne $true -or
        $researchSafety.inventoryStagesDisabled.const -ne $true -or
        $researchSafety.controlledResearchHooks.const -ne $true -or
        $researchSafety.compatibilityValidated.const -ne $true -or
        $researchSafety.trustedDepthEnforced.const -ne $true) {
      Add-Problem 'Evidence-bundle progressive profile must require controlled, compatible, depth-enforced hooks with all non-hook mutation/discovery paths disabled.'
    }
    if ($null -eq $readinessRule -or $readinessSafety.writesDisabled.const -ne $true -or
        $readinessSafety.rpcCallsDisabled.const -ne $true -or
        $readinessSafety.mutationDisabled.const -ne $true -or
        $readinessSafety.rawIdentityDisabled.const -ne $true -or
        $readinessSafety.hudHookDisabled.const -ne $true -or
        $readinessSafety.hooksDisabled.const -ne $true -or
        $readinessSafety.runtimeDiscoveryDisabled.const -ne $true -or
        $readinessSafety.inventoryStagesDisabled.const -ne $true -or
        $readinessSafety.controlledResearchHooks.const -ne $false -or
        $readinessSafety.compatibilityValidated.const -ne $false -or
        $readinessSafety.trustedDepthEnforced.const -ne $false -or
        [int]$readinessSafety.activeCanaries.const -ne 0) {
      Add-Problem 'Evidence-bundle readiness profile must require hook-free, discovery-free, inventory-disabled, read-only safety.'
    }
  } catch {
    Add-Problem "Invalid mode-aware evidence-bundle safety schema: $($_.Exception.Message)"
  }
}

$progressiveSchemaIdentities = [ordered]@{
  "schemas\compatibility-fingerprint-v1.schema.json" = "compatibility-fingerprint-v1"
  "schemas\hook-breadcrumb-v1.schema.json" = "hook-breadcrumb-v1"
  "schemas\hook-candidate-catalog-v1.schema.json" = "hook-candidate-catalog-v1"
  "schemas\hook-quarantine-v1.schema.json" = "hook-quarantine-v1"
  "schemas\hook-run-classification-v1.schema.json" = "hook-run-classification-v1"
  "schemas\hook-run-consumed-v1.schema.json" = "hook-run-consumed-v1"
  "schemas\hook-run-manifest-v1.schema.json" = "hook-run-manifest-v1"
  "schemas\hook-validation-ledger-v1.schema.json" = "hook-validation-ledger-v1"
  "schemas\trusted-hook-manifest-v1.schema.json" = "trusted-hook-manifest-v1"
}
foreach ($entry in $progressiveSchemaIdentities.GetEnumerator()) {
  $schemaPath = Join-Path $BundleRoot $entry.Key
  if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) { continue }
  try {
    $schema = Get-Content -Raw -LiteralPath $schemaPath | ConvertFrom-Json -ErrorAction Stop
    if ([string]$schema.'$schema' -ne 'https://json-schema.org/draft/2020-12/schema' -or
        [string]$schema.properties.schemaVersion.const -ne $entry.Value -or
        $schema.additionalProperties -ne $false) {
      Add-Problem "$($entry.Key) does not enforce the expected closed $($entry.Value) contract."
    }
  } catch {
    Add-Problem "Invalid progressive schema $($entry.Key): $($_.Exception.Message)"
  }
}

$readinessSchemaIdentities = @(
  [pscustomobject]@{
    Relative = 'schemas\readiness-campaign-manifest-v1.schema.json'
    Identity = 'readiness-campaign-manifest-v1'
    ManifestProperty = 'readinessCampaignManifest'
    SchemaVersion = 'readiness-campaign-manifest-v1'
  },
  [pscustomobject]@{
    Relative = 'schemas\peer-snapshot-v1.schema.json'
    Identity = 'peer-snapshot-v1'
    ManifestProperty = 'peerSnapshot'
    SchemaVersion = '1'
  },
  [pscustomobject]@{
    Relative = 'schemas\terminal-lifecycle-v1.schema.json'
    Identity = 'terminal-lifecycle-v1'
    ManifestProperty = 'terminalLifecycle'
    SchemaVersion = '1'
  }
)
foreach ($entry in $readinessSchemaIdentities) {
  $schemaPath = Join-Path $BundleRoot $entry.Relative
  if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) { continue }
  try {
    $schema = Get-Content -Raw -LiteralPath $schemaPath | ConvertFrom-Json -ErrorAction Stop
    if ([string]$schema.'$schema' -ne 'https://json-schema.org/draft/2020-12/schema' -or
        [string]$schema.'$id' -notmatch [regex]::Escape("$($entry.Identity).schema.json") -or
        [string]$schema.properties.schemaVersion.const -ne [string]$entry.SchemaVersion -or
        $schema.additionalProperties -ne $false) {
      Add-Problem "$($entry.Relative) does not enforce the expected closed $($entry.Identity) contract."
    }
  } catch {
    Add-Problem "Invalid readiness schema $($entry.Relative): $($_.Exception.Message)"
  }
}

try {
  $readinessManifestSchema = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot 'schemas\readiness-campaign-manifest-v1.schema.json') | ConvertFrom-Json -ErrorAction Stop
  $readinessPeerSchema = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot 'schemas\peer-snapshot-v1.schema.json') | ConvertFrom-Json -ErrorAction Stop
  $readinessTerminalSchema = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot 'schemas\terminal-lifecycle-v1.schema.json') | ConvertFrom-Json -ErrorAction Stop
  if ([string]$readinessManifestSchema.'$defs'.pairId.pattern -ne '^readiness-pair-[a-f0-9]{24}$' -or
      [string]$readinessPeerSchema.'$defs'.pairId.pattern -ne '^readiness-pair-[a-f0-9]{24}$' -or
      [string]$readinessTerminalSchema.'$defs'.pairId.pattern -ne '^readiness-pair-[a-f0-9]{24}$' -or
      [string]$readinessPeerSchema.properties.readinessPairId.'$ref' -ne '#/$defs/pairId' -or
      [string]$readinessTerminalSchema.properties.readinessPairId.'$ref' -ne '#/$defs/pairId' -or
      [string]$readinessPeerSchema.'$defs'.equipmentField.properties.value.'$ref' -ne '#/$defs/fingerprint') {
    Add-Problem 'Readiness schemas must bind only derived pair IDs and bounded fingerprint/scalar values.'
  }
} catch {
  Add-Problem "Invalid strict readiness schema contract: $($_.Exception.Message)"
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
    if ($manifest.schemaVersion -ne 1 -or [string]$manifest.product -ne 'CrabRuntimeProbe' -or
        [string]$manifest.version -notmatch '^1\.1\.0(?:[-+][0-9A-Za-z.-]+)?$' -or
        [string]$manifest.runtime -ne 'win-x64' -or
        [string]::IsNullOrWhiteSpace([string]$manifest.commit)) {
      Add-Problem 'Version manifest must identify CrabRuntimeProbe v1.1.0, win-x64, schema 1, and a source commit.'
    }
    $expectedBundleName = "CrabRuntimeProbe-v$($manifest.version)-$($manifest.runtime)"
    if ((Split-Path -Leaf $BundleRoot) -ne $expectedBundleName) {
      Add-Problem "Bundle directory name must match version manifest: $expectedBundleName"
    }
    if ($manifest.snapshotObservationSchemaVersion -ne 1) {
      Add-Problem "Version manifest must declare snapshotObservationSchemaVersion=1."
    }
    $baseSchemaIdentities = [ordered]@{
      liveStatus = 'live-status-v1'
      snapshotObservation = 'snapshot-observation-v1'
      campaignControl = 'campaign-control-v1'
      evidenceBundle = 'evidence-bundle-v1'
      coverageCatalog = 'coverage-catalog-v1'
    }
    foreach ($baseIdentity in $baseSchemaIdentities.GetEnumerator()) {
      if ([string]$manifest.schemaIdentities.($baseIdentity.Key) -ne [string]$baseIdentity.Value) {
        Add-Problem "Version manifest schema identity missing or wrong: $($baseIdentity.Key)=$($baseIdentity.Value)"
      }
    }
    foreach ($entry in $progressiveSchemaIdentities.GetEnumerator()) {
      $identityProperty = switch ($entry.Value) {
        'compatibility-fingerprint-v1' { 'compatibilityFingerprint' }
        'hook-breadcrumb-v1' { 'hookBreadcrumb' }
        'hook-candidate-catalog-v1' { 'hookCandidateCatalog' }
        'hook-quarantine-v1' { 'hookQuarantine' }
        'hook-run-classification-v1' { 'hookRunClassification' }
        'hook-run-consumed-v1' { 'hookRunConsumed' }
        'hook-run-manifest-v1' { 'hookRunManifest' }
        'hook-validation-ledger-v1' { 'hookValidationLedger' }
        'trusted-hook-manifest-v1' { 'trustedHookManifest' }
      }
      if ([string]$manifest.schemaIdentities.$identityProperty -ne $entry.Value) {
        Add-Problem "Version manifest schema identity missing or wrong: $identityProperty=$($entry.Value)"
      }
    }
    foreach ($entry in $readinessSchemaIdentities) {
      if ([string]$manifest.schemaIdentities.($entry.ManifestProperty) -ne [string]$entry.Identity) {
        Add-Problem "Version manifest schema identity missing or wrong: $($entry.ManifestProperty)=$($entry.Identity)"
      }
    }
    if ($manifest.releaseSafety.normalPlayGuideHookFree -ne $true -or
        $manifest.releaseSafety.trustedManifestCandidateCount -ne 0 -or
        $manifest.releaseSafety.canaryPrearmed -ne $false -or
        $manifest.releaseSafety.maximumCanariesPerProcess -ne 1 -or
        $manifest.releaseSafety.automaticInProcessAdvance -ne $false) {
      Add-Problem 'Version manifest releaseSafety must declare hook-free normal mode, empty trust, no prearmed canary, one-canary maximum, and no in-process advance.'
    }
    if ($progressiveArtifacts.Count -eq $progressiveArtifactPaths.Count) {
      if ([string]$manifest.campaignIdentities.coverageCatalogHash -ne [string]$progressiveArtifacts.candidateCatalog.coverageCatalogHash -or
          [string]$manifest.campaignIdentities.hookCatalogIdentity -ne [string]$progressiveArtifacts.candidateCatalog.hookCatalogIdentity -or
          [string]$manifest.campaignIdentities.callbackImplementationVersion -ne [string]$progressiveArtifacts.candidateCatalog.callbackImplementationVersion -or
          [string]$manifest.campaignIdentities.callbackSchemaVersion -ne [string]$progressiveArtifacts.candidateCatalog.callbackSchemaVersion -or
          [string]$manifest.campaignIdentities.validationBehaviorVersion -ne [string]$progressiveArtifacts.candidateCatalog.validationBehaviorVersion) {
        Add-Problem 'Version manifest campaign identities do not match the packaged progressive catalog.'
      }
    }
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

    $buildInfoPath = Join-Path $BundleRoot 'Payload\Mods\CrabRuntimeProbe\Scripts\build_info.txt'
    if (Test-Path -LiteralPath $buildInfoPath -PathType Leaf) {
      $buildInfo = Get-Content -Raw -LiteralPath $buildInfoPath
      if ($buildInfo -notmatch "(?m)^action\s*=\s*release\s*$" -or
          $buildInfo -notmatch "(?m)^product_version\s*=\s*$([regex]::Escape([string]$manifest.version))\s*$" -or
          $buildInfo -notmatch "(?m)^git_commit\s*=\s*$([regex]::Escape([string]$manifest.commit))\s*$" -or
          $buildInfo -match '(?im)^source_repo_path\s*=|[A-Z]:\\Users\\') {
        Add-Problem 'Packaged build_info.txt is missing sanitized release/version/commit fields or leaks a local path.'
      }
    }

    $dashboardPath = Join-Path $BundleRoot 'CrabRuntimeProbe.Dashboard.exe'
    if (Test-Path -LiteralPath $dashboardPath -PathType Leaf) {
      $versionCore = ([string]$manifest.version -split '[-+]')[0]
      $expectedFileVersion = "$versionCore.0"
      $versionInfo = (Get-Item -LiteralPath $dashboardPath).VersionInfo
      if ([string]$versionInfo.FileVersion -ne $expectedFileVersion -or
          -not ([string]$versionInfo.ProductVersion).StartsWith([string]$manifest.version, [StringComparison]::Ordinal)) {
        Add-Problem "Dashboard binary version must match release $($manifest.version); got FileVersion=$($versionInfo.FileVersion), ProductVersion=$($versionInfo.ProductVersion)."
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
