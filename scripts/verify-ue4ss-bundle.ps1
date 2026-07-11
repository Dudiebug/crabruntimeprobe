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
  "UE4SS.dll",
  "dwmapi.dll",
  "UE4SS-LICENSE.txt",
  "UE4SS-settings.ini",
  "imgui.ini",
  "INSTALL.txt",
  "CrabRuntimeProbe-README.md",
  "CrabRuntimeProbe-LICENSE.txt",
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
  "Mods\CrabRuntimeProbe\Scripts\record_builder.lua",
  "Mods\CrabRuntimeProbe\Scripts\evidence_writer.lua",
  "Mods\CrabRuntimeProbe\Scripts\campaign_state.lua",
  "Mods\CrabRuntimeProbe\Scripts\status_writer.lua",
  "Mods\CrabRuntimeProbe\Scripts\snapshot_sampler.lua",
  "Mods\CrabRuntimeProbe\Scripts\peer_sampler.lua",
  "Mods\CrabRuntimeProbe\Scripts\passive_hook_manager.lua",
  "Mods\CrabRuntimeProbe\Scripts\inventory_stage_manager.lua",
  "Mods\CrabRuntimeProbe\Scripts\full_observe_coordinator.lua",
  "Mods\CrabRuntimeProbe\Scripts\readiness_observe_coordinator.lua",
  "Mods\CrabRuntimeProbe\Scripts\crabsync_catalog.lua",
  "Mods\CrabRuntimeProbe\Scripts\dashboard_autostart.lua",
  "Mods\CrabRuntimeProbe\Scripts\research_hook_catalog.lua",
  "Mods\CrabRuntimeProbe\Scripts\progressive_json_reader.lua",
  "Mods\CrabRuntimeProbe\Scripts\progressive_artifact_guard.lua",
  "Mods\CrabRuntimeProbe\Scripts\progressive_config.lua",
  "Mods\CrabRuntimeProbe\Scripts\progressive_breadcrumb_journal.lua",
  "Mods\CrabRuntimeProbe\Scripts\progressive_run_manifest.lua",
  "Mods\CrabRuntimeProbe\Scripts\progressive_depth_callbacks.lua",
  "Mods\CrabRuntimeProbe\Scripts\progressive_hook_runner.lua",
  "Mods\CrabRuntimeProbe\Scripts\progressive_observe_coordinator.lua",
  "Mods\CrabRuntimeProbe\Scripts\relic_count_validator.lua",
  "Mods\CrabRuntimeProbe\Scripts\build_info.txt",
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
  "THIRD_PARTY_NOTICES.md",
  "version-manifest.json"
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
  try {
    Assert-CrabRuntimeProbeReadinessSamplerSafety `
      -ScriptsRoot $payloadScriptsRoot `
      -Label 'UE4SS bundle readiness sampler'
  } catch {
    Add-Error $_.Exception.Message
  }
  $safeProgressiveDefaults = [ordered]@{
    progressiveObservationEnabled = 'false'
    campaignProfile = 'normal-play-guide'
    readinessCampaignEnabled = 'false'
    readinessPeerSnapshotsEnabled = 'false'
    readinessInventoryStage = 'disabled'
    canaryCandidateId = 'unassigned'
    canaryHookPathFingerprint = 'unassigned'
    canaryValidationDepth = '0'
    trustedCandidateSelections = ''
    relicCountValidationEnabled = 'false'
  }
  foreach ($expected in $safeProgressiveDefaults.GetEnumerator()) {
    $actual = Get-CrabRuntimeProbeConfigValue -ConfigPath $configPath -Key $expected.Key
    if ([string]$actual -ne [string]$expected.Value) {
      Add-Error "Unsafe progressive release default: $($expected.Key) expected '$($expected.Value)', got '$actual'."
    }
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

$evidenceBundleSchemaPath = Join-Path $BundleRoot 'schemas\evidence-bundle-v1.schema.json'
if (Test-Path -LiteralPath $evidenceBundleSchemaPath -PathType Leaf) {
  try {
    $evidenceBundleSchema = Get-Content -Raw -LiteralPath $evidenceBundleSchemaPath | ConvertFrom-Json -ErrorAction Stop
    foreach ($field in @('controlledResearchHooks', 'compatibilityValidated', 'trustedDepthEnforced')) {
      if (@($evidenceBundleSchema.properties.safety.required) -notcontains $field -or
          $evidenceBundleSchema.properties.safety.properties.$field.type -ne 'boolean') {
        Add-Error "UE4SS evidence-bundle safety must require boolean $field."
      }
    }
    $activeCanariesSchema = $evidenceBundleSchema.properties.safety.properties.activeCanaries
    if (@($evidenceBundleSchema.properties.safety.required) -notcontains 'activeCanaries' -or
        $activeCanariesSchema.type -ne 'integer' -or [int]$activeCanariesSchema.minimum -ne 0 -or
        [int]$activeCanariesSchema.maximum -ne 1) {
      Add-Error 'UE4SS evidence-bundle safety must require integer activeCanaries in the range 0..1.'
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
        $normalSafety.trustedDepthEnforced.const -ne $false -or [int]$normalSafety.activeCanaries.const -ne 0) {
      Add-Error 'UE4SS evidence-bundle normal profile has an unsafe mode-aware safety contract.'
    }
    if ($null -eq $researchRule -or $researchSafety.writesDisabled.const -ne $true -or
        $researchSafety.rpcCallsDisabled.const -ne $true -or $researchSafety.mutationDisabled.const -ne $true -or
        $researchSafety.rawIdentityDisabled.const -ne $true -or $researchSafety.hudHookDisabled.const -ne $true -or
        $researchSafety.runtimeDiscoveryDisabled.const -ne $true -or
        $researchSafety.inventoryStagesDisabled.const -ne $true -or
        $researchSafety.controlledResearchHooks.const -ne $true -or
        $researchSafety.compatibilityValidated.const -ne $true -or
        $researchSafety.trustedDepthEnforced.const -ne $true) {
      Add-Error 'UE4SS evidence-bundle progressive profile has an unsafe controlled-research contract.'
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
      Add-Error 'UE4SS evidence-bundle readiness profile must be hook-free, discovery-free, inventory-disabled, and read-only.'
    }
  } catch {
    Add-Error "Invalid UE4SS mode-aware evidence-bundle safety schema: $($_.Exception.Message)"
  }
}

try {
  $candidateCatalog = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot 'campaign\hook_candidate_catalog.json') | ConvertFrom-Json -ErrorAction Stop
  $validationLedger = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot 'campaign\hook_validation_ledger.json') | ConvertFrom-Json -ErrorAction Stop
  $trustedManifest = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot 'campaign\trusted_hook_manifest.json') | ConvertFrom-Json -ErrorAction Stop
  $quarantine = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot 'campaign\hook_quarantine.json') | ConvertFrom-Json -ErrorAction Stop
  $defaults = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot 'campaign\progressive_observation.defaults.json') | ConvertFrom-Json -ErrorAction Stop
  if ($candidateCatalog.candidateCount -ne 111 -or @($candidateCatalog.candidates).Count -ne 111 -or
      [string]$candidateCatalog.principalCandidateId -ne 'hook-crabps-onrep-islandrewardrarity') {
    Add-Error 'UE4SS bundle lost the preserved 111-candidate catalog or principal candidate.'
  }
  foreach ($artifact in @($validationLedger, $trustedManifest, $quarantine, $defaults)) {
    if ([string]$artifact.coverageCatalogHash -ne [string]$candidateCatalog.coverageCatalogHash -or
        [string]$artifact.hookCatalogIdentity -ne [string]$candidateCatalog.hookCatalogIdentity) {
      Add-Error 'UE4SS progressive artifacts have incompatible catalog identities.'
      break
    }
  }
  if (@($trustedManifest.candidates).Count -ne 0 -or
      -not [string]::IsNullOrWhiteSpace([string]$trustedManifest.compatibilityFingerprint) -or
      @($validationLedger.candidates | Where-Object { $null -ne $_.trustedDepth }).Count -ne 0 -or
      @($quarantine.entries).Count -ne 0 -or
      $defaults.trustedPoolInitiallyEmpty -ne $true -or
      $defaults.automaticInProcessAdvance -ne $false -or
      $defaults.maximumCanariesPerProcess -ne 1 -or
      [string]$defaults.initialCanaryCandidateId -ne 'hook-crabps-onrep-islandrewardrarity' -or
      [int]$defaults.initialCanaryDepth -ne 1) {
    Add-Error 'UE4SS bundle must ship empty trust/quarantine state and an unarmed Depth 1 principal-candidate recommendation.'
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
    Add-Error 'UE4SS validation ledger must contain only clean untested migration-baseline entries.'
  }
} catch {
  Add-Error "Invalid progressive campaign artifact: $($_.Exception.Message)"
}

$schemaIdentities = [ordered]@{
  'compatibility-fingerprint-v1.schema.json' = 'compatibility-fingerprint-v1'
  'hook-breadcrumb-v1.schema.json' = 'hook-breadcrumb-v1'
  'hook-candidate-catalog-v1.schema.json' = 'hook-candidate-catalog-v1'
  'hook-quarantine-v1.schema.json' = 'hook-quarantine-v1'
  'hook-run-classification-v1.schema.json' = 'hook-run-classification-v1'
  'hook-run-consumed-v1.schema.json' = 'hook-run-consumed-v1'
  'hook-run-manifest-v1.schema.json' = 'hook-run-manifest-v1'
  'hook-validation-ledger-v1.schema.json' = 'hook-validation-ledger-v1'
  'trusted-hook-manifest-v1.schema.json' = 'trusted-hook-manifest-v1'
}
foreach ($entry in $schemaIdentities.GetEnumerator()) {
  try {
    $schema = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot "schemas\$($entry.Key)") | ConvertFrom-Json -ErrorAction Stop
    if ([string]$schema.properties.schemaVersion.const -ne $entry.Value -or $schema.additionalProperties -ne $false) {
      Add-Error "UE4SS schema is not the expected closed contract: $($entry.Key)"
    }
  } catch {
    Add-Error "Invalid UE4SS progressive schema $($entry.Key): $($_.Exception.Message)"
  }
}

$readinessSchemaIdentities = @(
  [pscustomobject]@{
    FileName = 'readiness-campaign-manifest-v1.schema.json'
    Identity = 'readiness-campaign-manifest-v1'
    ManifestProperty = 'readinessCampaignManifest'
    SchemaVersion = 'readiness-campaign-manifest-v1'
  },
  [pscustomobject]@{
    FileName = 'peer-snapshot-v1.schema.json'
    Identity = 'peer-snapshot-v1'
    ManifestProperty = 'peerSnapshot'
    SchemaVersion = '1'
  },
  [pscustomobject]@{
    FileName = 'terminal-lifecycle-v1.schema.json'
    Identity = 'terminal-lifecycle-v1'
    ManifestProperty = 'terminalLifecycle'
    SchemaVersion = '1'
  }
)
foreach ($entry in $readinessSchemaIdentities) {
  try {
    $schema = Get-Content -Raw -LiteralPath (Join-Path $BundleRoot "schemas\$($entry.FileName)") | ConvertFrom-Json -ErrorAction Stop
    if ([string]$schema.'$schema' -ne 'https://json-schema.org/draft/2020-12/schema' -or
        [string]$schema.'$id' -notmatch [regex]::Escape("$($entry.Identity).schema.json") -or
        [string]$schema.properties.schemaVersion.const -ne [string]$entry.SchemaVersion -or
        $schema.additionalProperties -ne $false) {
      Add-Error "UE4SS schema is not the expected closed readiness contract: $($entry.FileName)"
    }
  } catch {
    Add-Error "Invalid UE4SS readiness schema $($entry.FileName): $($_.Exception.Message)"
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
    Add-Error 'UE4SS readiness schemas must bind only derived pair IDs and bounded fingerprint/scalar values.'
  }
} catch {
  Add-Error "Invalid UE4SS strict readiness schema contract: $($_.Exception.Message)"
}

try {
  $versionManifestPath = Join-Path $BundleRoot 'version-manifest.json'
  $versionManifest = Get-Content -Raw -LiteralPath $versionManifestPath | ConvertFrom-Json -ErrorAction Stop
  if ($versionManifest.schemaVersion -ne 1 -or [string]$versionManifest.product -ne 'CrabRuntimeProbe' -or
      [string]$versionManifest.version -notmatch '^1\.1\.0(?:[-+][0-9A-Za-z.-]+)?$' -or
      [string]$versionManifest.runtime -ne 'win-x64' -or
      [string]$versionManifest.bundleFormat -ne 'ue4ss-overlay' -or
      [string]::IsNullOrWhiteSpace([string]$versionManifest.commit) -or
      $versionManifest.releaseSafety.normalPlayGuideHookFree -ne $true -or
      $versionManifest.releaseSafety.trustedManifestCandidateCount -ne 0 -or
      $versionManifest.releaseSafety.canaryPrearmed -ne $false -or
      $versionManifest.releaseSafety.maximumCanariesPerProcess -ne 1 -or
      $versionManifest.releaseSafety.automaticInProcessAdvance -ne $false) {
    Add-Error 'UE4SS version manifest does not identify a fail-closed v1.1.0 overlay.'
  }
  $baseSchemaIdentities = [ordered]@{
    liveStatus = 'live-status-v1'
    snapshotObservation = 'snapshot-observation-v1'
    campaignControl = 'campaign-control-v1'
    evidenceBundle = 'evidence-bundle-v1'
    coverageCatalog = 'coverage-catalog-v1'
  }
  foreach ($baseIdentity in $baseSchemaIdentities.GetEnumerator()) {
    if ([string]$versionManifest.schemaIdentities.($baseIdentity.Key) -ne [string]$baseIdentity.Value) {
      Add-Error "UE4SS version manifest schema identity missing or wrong: $($baseIdentity.Key)=$($baseIdentity.Value)"
    }
  }
  foreach ($entry in $schemaIdentities.GetEnumerator()) {
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
    if ([string]$versionManifest.schemaIdentities.$identityProperty -ne $entry.Value) {
      Add-Error "UE4SS version manifest schema identity missing or wrong: $identityProperty=$($entry.Value)"
    }
  }
  foreach ($entry in $readinessSchemaIdentities) {
    if ([string]$versionManifest.schemaIdentities.($entry.ManifestProperty) -ne [string]$entry.Identity) {
      Add-Error "UE4SS version manifest schema identity missing or wrong: $($entry.ManifestProperty)=$($entry.Identity)"
    }
  }
  if ($null -ne $candidateCatalog) {
    if ([string]$versionManifest.campaignIdentities.coverageCatalogHash -ne [string]$candidateCatalog.coverageCatalogHash -or
        [string]$versionManifest.campaignIdentities.hookCatalogIdentity -ne [string]$candidateCatalog.hookCatalogIdentity -or
        [string]$versionManifest.campaignIdentities.callbackImplementationVersion -ne [string]$candidateCatalog.callbackImplementationVersion -or
        [string]$versionManifest.campaignIdentities.callbackSchemaVersion -ne [string]$candidateCatalog.callbackSchemaVersion -or
        [string]$versionManifest.campaignIdentities.validationBehaviorVersion -ne [string]$candidateCatalog.validationBehaviorVersion -or
        [string]$versionManifest.campaignIdentities.initialCanaryCandidateId -ne [string]$defaults.initialCanaryCandidateId -or
        [int]$versionManifest.campaignIdentities.initialCanaryDepth -ne [int]$defaults.initialCanaryDepth) {
      Add-Error 'UE4SS version manifest campaign identities do not match the packaged progressive artifacts.'
    }
  }
  $manifestPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
  foreach ($file in @($versionManifest.files)) {
    $normalized = ([string]$file.path).Replace('\', '/')
    if ([IO.Path]::IsPathRooted($normalized) -or $normalized -match '\.\.' -or -not $manifestPaths.Add($normalized)) {
      Add-Error "Invalid or duplicate UE4SS manifest path: $normalized"
      continue
    }
    $full = Join-Path $BundleRoot $normalized.Replace('/', '\')
    if (-not (Test-Path -LiteralPath $full -PathType Leaf) -or
        (Get-Item -LiteralPath $full).Length -ne [long]$file.size -or
        (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ne [string]$file.sha256) {
      Add-Error "UE4SS manifest file missing or mismatched: $normalized"
    }
  }
  Get-ChildItem -LiteralPath $BundleRoot -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($BundleRoot.Length).TrimStart('\').Replace('\', '/')
    if ($relative -ne 'version-manifest.json' -and -not $manifestPaths.Contains($relative)) {
      Add-Error "UE4SS file missing from version manifest: $relative"
    }
  }
} catch {
  Add-Error "Invalid UE4SS version manifest: $($_.Exception.Message)"
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
  $_.Name -match '^recv.*\.json$' -or
  $_.Name -match '^hook_run_(?:consumed|manifest|classification)_.*\.json$' -or
  $_.Name -match '(?i)(UE4SS[_-]?ObjectDump|ObjectDump).*\.txt$'
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

$buildInfoPath = Join-Path $BundleRoot 'Mods\CrabRuntimeProbe\Scripts\build_info.txt'
if (Test-Path -LiteralPath $buildInfoPath -PathType Leaf) {
  $buildInfo = Get-Content -Raw -LiteralPath $buildInfoPath
  if ($buildInfo -notmatch '(?m)^action\s*=\s*release\s*$' -or
      $buildInfo -notmatch "(?m)^product_version\s*=\s*$([regex]::Escape([string]$versionManifest.version))\s*$" -or
      $buildInfo -notmatch "(?m)^git_commit\s*=\s*$([regex]::Escape([string]$versionManifest.commit))\s*$" -or
      $buildInfo -match '(?im)^source_repo_path\s*=|[A-Z]:\\Users\\') {
    Add-Error 'UE4SS build_info.txt is missing sanitized v1.1.0 release metadata or leaks a local path.'
  }
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
