[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Assert-CrabRuntimeProbeConfig.ps1')

$repoRoot = Resolve-CrabRuntimeProbeRepoRoot -StartPath $PSScriptRoot -RequireGit
$buildPath = Join-Path $PSScriptRoot 'build-release.ps1'
$verifyPath = Join-Path $PSScriptRoot 'verify-release.ps1'
$schemaPath = Join-Path $repoRoot 'schemas\evidence-bundle-v1.schema.json'
$liveStatusSchemaPath = Join-Path $repoRoot 'schemas\live-status-v1.schema.json'
$snapshotSchemaPath = Join-Path $repoRoot 'schemas\snapshot-observation-v1.schema.json'
$configPath = Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe\Scripts\config.txt'
$directoryPropsPath = Join-Path $repoRoot 'dashboard\Directory.Build.props'
$ue4ssBuildPath = Join-Path $PSScriptRoot 'build-ue4ss-bundle.ps1'
$ue4ssVerifyPath = Join-Path $PSScriptRoot 'verify-ue4ss-bundle.ps1'
$nodePackagePath = Join-Path $repoRoot 'tools\package_release.js'
$workflowPath = Join-Path $repoRoot '.github\workflows\dashboard-windows.yml'

foreach ($path in @($buildPath, $verifyPath, $schemaPath, $liveStatusSchemaPath, $snapshotSchemaPath,
  $directoryPropsPath, $ue4ssBuildPath, $ue4ssVerifyPath, $nodePackagePath, $workflowPath)) {
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release contract file: $path" }
}

Assert-CrabRuntimeProbeConfig -ConfigPath $configPath -Label 'release payload source config'
Assert-CrabRuntimeProbeModLayout `
  -ModRoot (Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe') `
  -Label 'release payload source mod'
Assert-CrabRuntimeProbeNormalSamplerSafety `
  -ScriptsRoot (Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe\Scripts') `
  -Label 'source normal snapshot sampler'
Assert-CrabRuntimeProbeSnapshotObservationSchema `
  -SchemaPath $snapshotSchemaPath `
  -Label 'source snapshot observation schema'

$build = Get-Content -Raw -LiteralPath $buildPath
$verify = Get-Content -Raw -LiteralPath $verifyPath
$ue4ssBuild = Get-Content -Raw -LiteralPath $ue4ssBuildPath
$ue4ssVerify = Get-Content -Raw -LiteralPath $ue4ssVerifyPath
$nodePackage = Get-Content -Raw -LiteralPath $nodePackagePath
$directoryProps = Get-Content -Raw -LiteralPath $directoryPropsPath
$workflow = Get-Content -Raw -LiteralPath $workflowPath
foreach ($module in @(
  'record_builder.lua',
  'campaign_state.lua',
  'status_writer.lua',
  'snapshot_sampler.lua',
  'passive_hook_manager.lua',
  'inventory_stage_manager.lua',
  'full_observe_coordinator.lua',
  'crabsync_catalog.lua',
  'dashboard_autostart.lua',
  'research_hook_catalog.lua',
  'progressive_json_reader.lua',
  'progressive_artifact_guard.lua',
  'progressive_config.lua',
  'progressive_breadcrumb_journal.lua',
  'progressive_run_manifest.lua',
  'progressive_depth_callbacks.lua',
  'progressive_hook_runner.lua',
  'progressive_observe_coordinator.lua',
  'relic_count_validator.lua',
  'build_info.txt'
)) {
  foreach ($verificationText in @($verify, $ue4ssVerify)) {
    if ($verificationText -notmatch [regex]::Escape($module)) {
      throw "Canonical and UE4SS release verification must require $module."
    }
  }
}
foreach ($artifact in @(
  'hook_candidate_catalog.json', 'hook_validation_ledger.json', 'trusted_hook_manifest.json',
  'hook_quarantine.json', 'progressive_observation.defaults.json'
)) {
  foreach ($text in @($build, $verify, $ue4ssBuild, $ue4ssVerify, $nodePackage)) {
    if ($text -notmatch [regex]::Escape($artifact)) { throw "Release path does not include $artifact." }
  }
}
foreach ($releaseMetadataToken in @(
  'schemaIdentities', 'campaignIdentities', 'releaseSafety', 'build_info.txt',
  'git_commit', 'trustedManifestCandidateCount', 'canaryPrearmed', 'hookRunConsumed'
)) {
  foreach ($text in @($build, $verify, $ue4ssBuild, $ue4ssVerify, $nodePackage)) {
    if ($text -notmatch [regex]::Escape($releaseMetadataToken)) {
      throw "Release path does not enforce sanitized identity/safety metadata: $releaseMetadataToken"
    }
  }
}
foreach ($progressiveSchema in @(
  'compatibility-fingerprint-v1.schema.json', 'hook-breadcrumb-v1.schema.json',
  'hook-candidate-catalog-v1.schema.json', 'hook-quarantine-v1.schema.json',
  'hook-run-classification-v1.schema.json', 'hook-run-consumed-v1.schema.json',
  'hook-run-manifest-v1.schema.json',
  'hook-validation-ledger-v1.schema.json', 'trusted-hook-manifest-v1.schema.json'
)) {
  if ($verify -notmatch [regex]::Escape($progressiveSchema) -or
      $ue4ssVerify -notmatch [regex]::Escape($progressiveSchema)) {
    throw "Release verification does not require $progressiveSchema."
  }
}
foreach ($versionToken in @(
  '<Version>1.0.4</Version>', '<AssemblyVersion>1.0.4.0</AssemblyVersion>',
  '<FileVersion>1.0.4.0</FileVersion>', '<InformationalVersion>1.0.4</InformationalVersion>'
)) {
  if ($directoryProps -notmatch [regex]::Escape($versionToken)) { throw "Dashboard metadata missing $versionToken." }
}
if ($build -notmatch '\$Version\s*=\s*"1\.0\.4"' -or $ue4ssBuild -notmatch '\$Version\s*=\s*"1\.0\.4"' -or
    $nodePackage -notmatch "releaseVersion\s*=\s*'1\.0\.4'") {
  throw 'Every release builder must default to v1.0.4.'
}
foreach ($token in @(
  'generate_progressive_hook_catalog.js', 'trustedManifestCandidateCount', 'canaryPrearmed',
  'maximumCanariesPerProcess', 'automaticInProcessAdvance', 'hookCatalogIdentity',
  'callbackImplementationVersion', 'validationBehaviorVersion', 'product_version = $Version'
)) {
  if ($build -notmatch [regex]::Escape($token)) { throw "Canonical release build is missing $token." }
}
foreach ($defaultToken in @(
  'progressiveObservationEnabled = false', 'canaryCandidateId = unassigned',
  'canaryHookPathFingerprint = unassigned', 'canaryValidationDepth = 0',
  'trustedCandidateSelections',
  'relicCountValidationEnabled = false'
)) {
  if ($verify -notmatch [regex]::Escape($defaultToken)) { throw "Release verification is missing safe default: $defaultToken" }
}
if ($verify -notmatch [regex]::Escape('coverage-catalog-v1.schema.json')) {
  throw 'Release verification does not require the coverage catalog schema.'
}
if ($verify -notmatch [regex]::Escape('snapshot-observation-v1.schema.json')) {
  throw 'Release verification does not require the snapshot observation schema.'
}
if ($verify -notmatch [regex]::Escape('INCIDENT_2026-07-10_HOOK_OBSERVER_CRASH.md')) {
  throw 'Release verification does not require the hook observer incident notice.'
}
if ($build -notmatch 'snapshotObservationSchemaVersion\s*=\s*1') {
  throw 'Release manifest does not declare snapshotObservationSchemaVersion=1.'
}
if ($build -notmatch 'generate_crabsync_coverage_catalog\.js.+--validate') {
  throw 'Release build does not validate generated CrabSync catalog/profile artifacts.'
}
if ($workflow -notmatch 'generate_progressive_hook_catalog\.js\s+--validate' -or
    $workflow -notmatch 'generate_progressive_hook_catalog\.js\s+--self-test' -or
    $workflow -notmatch [regex]::Escape('advanced-research') -or
    $workflow -notmatch [regex]::Escape('CrabRuntimeProbe-v1.0.4-win-x64.zip') -or
    $workflow -notmatch [regex]::Escape('CrabRuntimeProbe-v1.0.4-UE4SS.zip')) {
  throw 'Windows CI must validate/self-test the progressive catalog, render the research page, and verify both v1.0.4 archives.'
}
foreach ($profileToken in @(
  'snapshot-observation', 'snapshotSamplerEnabled', 'gameplayHooksEnabled',
  'lifecycleHooksEnabled', 'runtimeDiscoveryEnabled', 'inventoryEscalationEnabled',
  'guiOwnsChecklistQualification', 'researchOnly'
)) {
  if ($verify -notmatch [regex]::Escape($profileToken)) {
    throw "Release verification does not enforce snapshot-first profile field $profileToken."
  }
}
if ($verify -notmatch 'Assert-CrabRuntimeProbeNormalSamplerSafety') {
  throw 'Release verification does not enforce the normal snapshot sampler safety closure.'
}
foreach ($text in @($build, $verify)) {
  if ($text -notmatch 'ObjectDump') { throw 'Release filtering must explicitly reject object-dump text files.' }
}

$schema = Get-Content -Raw -LiteralPath $schemaPath | ConvertFrom-Json -ErrorAction Stop
$required = @($schema.required)
foreach ($name in @(
  'schemaVersion', 'bundleFormat', 'campaignId', 'campaignName', 'profileId',
  'campaignGeneration', 'machineId', 'sessionId', 'selectedRole', 'preparedAtUtc',
  'collectedAtUtc', 'catalogHash', 'safety', 'files'
)) {
  if ($required -notcontains $name) { throw "Bundle schema does not require $name." }
}
foreach ($name in @(
  'writesDisabled', 'rpcCallsDisabled', 'mutationDisabled', 'rawIdentityDisabled',
  'hudHookDisabled', 'hooksDisabled', 'runtimeDiscoveryDisabled', 'inventoryStagesDisabled'
)) {
  if (@($schema.properties.safety.required) -notcontains $name) {
    throw "Bundle safety contract does not require $name."
  }
  $property = $schema.properties.safety.properties.$name
  if ($null -eq $property -or $property.type -ne 'boolean') {
    throw "Bundle safety contract must type $name as boolean."
  }
}
foreach ($name in @('controlledResearchHooks', 'compatibilityValidated', 'trustedDepthEnforced')) {
  if (@($schema.properties.safety.required) -notcontains $name -or
      $schema.properties.safety.properties.$name.type -ne 'boolean') {
    throw "Bundle mode-aware safety contract must require boolean $name."
  }
}
$activeCanariesSchema = $schema.properties.safety.properties.activeCanaries
if (@($schema.properties.safety.required) -notcontains 'activeCanaries' -or
    $activeCanariesSchema.type -ne 'integer' -or [int]$activeCanariesSchema.minimum -ne 0 -or
    [int]$activeCanariesSchema.maximum -ne 1) {
  throw 'Bundle mode-aware safety contract must require activeCanaries integer 0..1.'
}
$normalRule = @($schema.allOf | Where-Object {
  [string]$_.'if'.properties.profileId.const -eq 'crabsync-full-observe'
}) | Select-Object -First 1
$researchRule = @($schema.allOf | Where-Object {
  [string]$_.'if'.properties.profileId.const -eq 'progressive-broad-observation'
}) | Select-Object -First 1
$normalSafety = $normalRule.then.properties.safety.properties
$researchSafety = $researchRule.then.properties.safety.properties
if ($null -eq $normalRule -or $normalSafety.hooksDisabled.const -ne $true -or
    $normalSafety.controlledResearchHooks.const -ne $false -or
    $normalSafety.compatibilityValidated.const -ne $false -or
    $normalSafety.trustedDepthEnforced.const -ne $false -or [int]$normalSafety.activeCanaries.const -ne 0) {
  throw 'Normal evidence bundles must require hook-free false/false/false/0 research safety fields.'
}
if ($null -eq $researchRule -or $researchSafety.writesDisabled.const -ne $true -or
    $researchSafety.rpcCallsDisabled.const -ne $true -or $researchSafety.mutationDisabled.const -ne $true -or
    $researchSafety.rawIdentityDisabled.const -ne $true -or $researchSafety.hudHookDisabled.const -ne $true -or
    $researchSafety.runtimeDiscoveryDisabled.const -ne $true -or
    $researchSafety.inventoryStagesDisabled.const -ne $true -or
    $researchSafety.controlledResearchHooks.const -ne $true -or
    $researchSafety.compatibilityValidated.const -ne $true -or
    $researchSafety.trustedDepthEnforced.const -ne $true) {
  throw 'Progressive evidence bundles must require compatible, depth-enforced controlled hooks with every non-hook unsafe path disabled.'
}

$liveStatusSchema = Get-Content -Raw -LiteralPath $liveStatusSchemaPath | ConvertFrom-Json -ErrorAction Stop
foreach ($name in @(
  'writesDisabled', 'rpcCallsDisabled', 'mutationDisabled', 'rawIdentityDisabled',
  'hudHookDisabled', 'hooksDisabled', 'runtimeDiscoveryDisabled', 'inventoryStagesDisabled'
)) {
  if (@($liveStatusSchema.properties.safety.required) -notcontains $name) {
    throw "Live-status safety contract does not require $name."
  }
  $property = $liveStatusSchema.properties.safety.properties.$name
  if ($null -eq $property -or $property.type -ne 'boolean') {
    throw "Live-status safety contract must type $name as boolean."
  }
}

$candidateCatalog = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'campaign\hook_candidate_catalog.json') | ConvertFrom-Json -ErrorAction Stop
$ledger = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'campaign\hook_validation_ledger.json') | ConvertFrom-Json -ErrorAction Stop
$trusted = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'campaign\trusted_hook_manifest.json') | ConvertFrom-Json -ErrorAction Stop
$quarantine = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'campaign\hook_quarantine.json') | ConvertFrom-Json -ErrorAction Stop
$defaults = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'campaign\progressive_observation.defaults.json') | ConvertFrom-Json -ErrorAction Stop
if ($candidateCatalog.candidateCount -ne 111 -or @($candidateCatalog.candidates).Count -ne 111 -or
    [string]$candidateCatalog.principalCandidateId -ne 'hook-crabps-onrep-islandrewardrarity') {
  throw 'Generated candidate catalog lost its 111 candidates or principal suspect ordering.'
}
if (@($trusted.candidates).Count -ne 0 -or @($quarantine.entries).Count -ne 0 -or
    -not [string]::IsNullOrWhiteSpace([string]$trusted.compatibilityFingerprint) -or
    @($ledger.candidates | Where-Object { $null -ne $_.trustedDepth }).Count -ne 0 -or
    $defaults.trustedPoolInitiallyEmpty -ne $true -or $defaults.normalPlayGuideHookFree -ne $true -or
    $defaults.maximumCanariesPerProcess -ne 1 -or $defaults.automaticInProcessAdvance -ne $false -or
    [string]$defaults.initialCanaryCandidateId -ne 'hook-crabps-onrep-islandrewardrarity' -or
    [int]$defaults.initialCanaryDepth -ne 1) {
  throw 'Generated release defaults are not empty-trust, hook-free normal mode, and unarmed single-canary Depth 1 recommendation.'
}
$nonBaselineLedgerEntries = @($ledger.candidates | Where-Object {
  ([string]$_.state) -ne 'untested' -or [int]$_.highestValidatedDepth -ne 0 -or
  $null -ne $_.trustedDepth -or [int]$_.cleanRuns -ne 0 -or [int]$_.naturalCallbacks -ne 0 -or
  @($_.evidenceSessions).Count -ne 0 -or @($_.crashSuspectRuns).Count -ne 0 -or
  -not [string]::IsNullOrWhiteSpace([string]$_.compatibilityFingerprint) -or
  $_.hasUnmatchedBreadcrumb -eq $true -or $_.hasCorrelatedCrash -eq $true -or
  $_.hasNewUe4ssCallbackError -eq $true
})
if ($nonBaselineLedgerEntries.Count -ne 0) {
  throw 'Generated release ledger must contain only clean untested migration-baseline entries.'
}
foreach ($schemaName in @(
  'compatibility-fingerprint-v1', 'hook-breadcrumb-v1', 'hook-candidate-catalog-v1',
  'hook-quarantine-v1', 'hook-run-classification-v1', 'hook-run-consumed-v1',
  'hook-run-manifest-v1',
  'hook-validation-ledger-v1', 'trusted-hook-manifest-v1'
)) {
  $progressiveSchemaPath = Join-Path $repoRoot "schemas\$schemaName.schema.json"
  $progressiveSchema = Get-Content -Raw -LiteralPath $progressiveSchemaPath | ConvertFrom-Json -ErrorAction Stop
  if ([string]$progressiveSchema.properties.schemaVersion.const -ne $schemaName -or
      $progressiveSchema.additionalProperties -ne $false) {
    throw "$schemaName is not a closed, versioned schema."
  }
}

$releaseDocsText = ''
foreach ($docRelative in @('CHANGELOG.md', 'docs\CRABRUNTIMEPROBE_V1.0.4_RELEASE_NOTES.md', 'docs\CRABSYNC_FULL_CAMPAIGN_GUIDE.md')) {
  $docPath = Join-Path $repoRoot $docRelative
  if (-not (Test-Path -LiteralPath $docPath -PathType Leaf)) { throw "Missing v1.0.4 release documentation: $docRelative" }
  $releaseDocsText += "`n" + (Get-Content -Raw -LiteralPath $docPath)
}
foreach ($requiredText in @(
  'Normal Play Guide', 'Progressive Broad Observation', 'OnRep_IslandRewardRarity',
  'Depth 0', 'Depth 7', 'Registered but not naturally observed', 'Needs revalidation',
  'Quarantined', 'compatibility', 'three clean runs', 'unattributed',
  'local relic count increased', 'pickup callback observed'
)) {
  if ($releaseDocsText -notmatch [regex]::Escape($requiredText)) {
    throw "Release documentation does not cover required v1.0.4 concept: $requiredText"
  }
}

Write-Host 'CrabRuntimeProbe release contract checks passed.'
