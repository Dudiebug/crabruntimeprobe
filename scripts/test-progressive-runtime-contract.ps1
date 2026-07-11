[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Assert-CrabRuntimeProbeConfig.ps1')

function Assert-Contains {
  param([string]$Text, [string]$Pattern, [string]$Message)
  if ($Text -notmatch $Pattern) { throw $Message }
}

function Assert-NotContains {
  param([string]$Text, [string]$Pattern, [string]$Message)
  if ($Text -match $Pattern) { throw $Message }
}

function Get-SourceSection {
  param([string]$Text, [string]$StartToken, [string]$EndToken)
  $start = $Text.IndexOf($StartToken, [StringComparison]::Ordinal)
  $end = $Text.IndexOf($EndToken, $start + $StartToken.Length, [StringComparison]::Ordinal)
  if ($start -lt 0 -or $end -le $start) { throw "Could not isolate source section '$StartToken'." }
  return $Text.Substring($start, $end - $start)
}

function Assert-Ordered {
  param([string]$Text, [string[]]$Tokens, [string]$Message)
  $offset = 0
  foreach ($token in $Tokens) {
    $next = $Text.IndexOf($token, $offset, [StringComparison]::Ordinal)
    if ($next -lt 0) { throw "$Message Missing or out-of-order token '$token'." }
    $offset = $next + $token.Length
  }
}

$repoRoot = Resolve-CrabRuntimeProbeRepoRoot -StartPath $PSScriptRoot -RequireGit
$luaRoot = Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe\Scripts'
$configPath = Join-Path $luaRoot 'config.txt'

Assert-CrabRuntimeProbeConfig -ConfigPath $configPath -Label 'progressive runtime source config'
Assert-CrabRuntimeProbeModLayout -ModRoot (Split-Path -Parent $luaRoot) -Label 'progressive runtime mod'
Assert-CrabRuntimeProbeNormalSamplerSafety -ScriptsRoot $luaRoot -Label 'progressive normal-mode closure'

$sources = @{}
foreach ($name in @(
  'progressive_json_reader.lua', 'progressive_artifact_guard.lua', 'progressive_config.lua',
  'progressive_breadcrumb_journal.lua', 'progressive_run_manifest.lua',
  'progressive_depth_callbacks.lua', 'progressive_hook_runner.lua',
  'progressive_observe_coordinator.lua', 'relic_count_validator.lua', 'main.lua',
  'campaign_state.lua', 'snapshot_sampler.lua'
)) {
  $path = Join-Path $luaRoot $name
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing progressive runtime module '$name'." }
  $sources[$name] = Get-Content -Raw -LiteralPath $path
}

$config = $sources['progressive_config.lua']
foreach ($token in @(
  'MAX_CONFIG_BYTES = 65536', 'MAX_CONFIG_LINES = 512', 'duplicate-config-key-',
  'unknown-research-config-key-', "parseBoolean(values, 'writeJsonlResults') ~= true",
  "state ~= 'armed'", "text ~= 'unassigned'", 'trusted-hook-selection-list-malformed',
  'artifactGuard.authorizeSelections(selection)'
)) {
  Assert-Contains $config ([regex]::Escape($token)) "Progressive config is missing fail-closed guard '$token'."
}
Assert-NotContains $config 'table\.sort\(selections' 'Runtime must preserve the immutable dashboard/manifest trusted registration order.'

$artifactGuard = $sources['progressive_artifact_guard.lua']
foreach ($token in @(
  'trusted_hook_manifest.json', 'hook_validation_ledger.json', 'hook_quarantine.json',
  'trusted-selection-ledger-policy-not-met', 'canary-ledger-state-blocked',
  'canary-validation-depth-skipped', 'validateRunManifest', 'run-manifest-order-mismatch',
  'run-manifest-safety-invalid', 'maximumCanaries ~= 1',
  'trusted-manifest-entry-omitted-from-pool', 'selection.canary.validationDepth == entry.trustedDepth + 1'
)) {
  Assert-Contains $artifactGuard ([regex]::Escape($token)) "Artifact authorization is missing '$token'."
}
$jsonReader = $sources['progressive_json_reader.lua']
foreach ($token in @(
  'maximumBytes', 'maximumNodes', 'maximumDepth', 'maximumStringBytes',
  'maximumContainerItems', 'json-duplicate-object-key', 'json-trailing-content'
)) {
  Assert-Contains $jsonReader ([regex]::Escape($token)) "Bounded JSON reader is missing '$token'."
}

$manifest = $sources['progressive_run_manifest.lua']
Assert-Contains $manifest 'artifactGuard\.validateRunManifest' 'Existing and newly written run manifests must be structurally validated.'
Assert-Contains $manifest 'hook_run_manifest_' 'Runtime run-manifest filename contract is missing.'
Assert-Contains $manifest 'canary = json\.null' 'Trusted-only manifests must encode an explicit JSON null canary.'
Assert-NotContains $manifest 'if existing ~= nil(?s).*return true' 'Existing run manifests must never be trusted by filename alone.'

$journal = $sources['progressive_breadcrumb_journal.lua']
foreach ($token in @(
  'MAX_RECORDS = 8192', 'MAX_LINE_BYTES = 1024', "io.open(path, 'a')", 'file:flush()',
  'hook_run_consumed_', 'research-run-already-consumed', 'automaticRearmAllowed = false',
  "['registration-begin']", "['callback-enter']",
  "['context-resolve-begin']", "['evidence-write-complete']", "['callback-exit']"
)) {
  Assert-Contains $journal ([regex]::Escape($token)) "Breadcrumb journal is missing '$token'."
}
Assert-NotContains $journal 'tostring\s*\(\s*candidate\.(?:hookPath|object|context)' 'Breadcrumbs must not synchronously format runtime objects or raw hook paths.'

$depths = $sources['progressive_depth_callbacks.lua']
$d1 = Get-SourceSection $depths 'local function makeDepth1' 'local function makeDepth2'
$d2 = Get-SourceSection $depths 'local function makeDepth2' 'local function makeDepth3'
$d3 = Get-SourceSection $depths 'local function makeDepth3' 'local function makeDepth4'
$d4 = Get-SourceSection $depths 'local function makeDepth4' 'local function makeDepth5'
$d5 = Get-SourceSection $depths 'local function makeDepth5' 'local function makeDepth6'
$d6 = Get-SourceSection $depths 'local function makeDepth6' 'local function makeDepth7'
$d7 = Get-SourceSection $depths 'local function makeDepth7' 'local BUILDERS'
Assert-Contains $d1 'enter\(' 'Depth 1 must record natural callback entry.'
Assert-NotContains $d1 'resolveContext|resolveScope|readState|readArguments|writeEvidence|finish\(' 'Depth 1 must inspect nothing and return immediately.'
Assert-Contains $d2 'enter\(' 'Depth 2 must record callback entry.'
Assert-Contains $d2 'finish\(' 'Depth 2 must record callback exit.'
Assert-NotContains $d2 'resolveContext|resolveScope|readState|readArguments|writeEvidence' 'Depth 2 must not inspect context, scope, state, or arguments.'
Assert-Contains $d3 'resolveContext' 'Depth 3 must add bounded context resolution.'
Assert-NotContains $d3 'resolveScope|readState|readArguments|writeEvidence' 'Depth 3 executed a deeper operation.'
Assert-Contains $d4 'resolveScope' 'Depth 4 must add reviewed PlayerState scope.'
Assert-NotContains $d4 'readState|readArguments|writeEvidence' 'Depth 4 executed a deeper operation.'
Assert-Contains $d5 'readState' 'Depth 5 must add reviewed state reads.'
Assert-NotContains $d5 'readArguments|writeEvidence' 'Depth 5 executed a deeper operation.'
Assert-Contains $d6 'readArguments' 'Depth 6 must add exact documented arguments.'
Assert-NotContains $d6 'writeEvidence' 'Depth 6 must not execute full passive evidence.'
Assert-Contains $d7 'writeEvidence' 'Depth 7 must execute reviewed passive evidence.'

$runner = $sources['progressive_hook_runner.lua']
Assert-NotContains $runner '\{\s*n\s*=\s*select\(' 'Shallow callbacks must not materialize all UE4SS arguments before the first breadcrumb.'
Assert-Contains $runner 'pcall\(callback, \.\.\.\)' 'Guarded callbacks must directly forward arguments to depth-specific signatures.'
Assert-Contains $runner 'orphan-post-callback' 'Orphan post callbacks must remain attributable failures.'
Assert-Contains $runner 'if self\.completed' 'Residual callbacks must become inert after shutdown.'
Assert-Contains $runner 'self\.config\.progressiveHooksArmed = unregisterFailed' 'Unregistration failure must keep hook safety truth false.'
Assert-Contains $runner 'ambiguousRegistrationState' 'Registration failures without proven hook IDs must preserve conservative hook-active safety truth.'
Assert-Contains $runner 'hasRuntimeFault' 'Coordinator must be able to observe callback/journal/evidence breaker faults.'
Assert-NotContains $runner '\bregisterAll\s*\(' 'Progressive runtime must register candidates individually.'
Assert-NotContains $runner 'inventoryAndResources|InventoryInfo|Enhancements|getArrayIndex' 'Executable reviewed-state path includes excluded aggregate/element reads.'
Assert-Ordered $runner @(
  'for _, trustedSelection in ipairs(self.selection.trusted or {}) do',
  "self:registerOne(trustedSelection, 'trusted')",
  'if self.selection.canary then',
  "self:registerOne(self.selection.canary, 'canary')"
) 'Trusted hooks must register deterministically before the single canary.'

$coordinator = $sources['progressive_observe_coordinator.lua']
Assert-Ordered $coordinator @('self.baseline:onTick', 'isBaselineReady', 'self.relics:onTick', 'self.hooks:registerConfiguredHooks') `
  'Safe baseline must precede relic validation and trusted/canary registration.'
Assert-Contains $coordinator 'self\.hooks:shutdown\(\)' 'Partial registration or runtime breaker faults must deactivate registered hooks.'
Assert-Contains $coordinator 'research-faulted-baseline-only' 'Faulted research must remain visibly degraded, not collecting.'

$relic = $sources['relic_count_validator.lua']
foreach ($token in @(
  'wait-next-lifecycle-generation', 'getArrayLength(wrapper)', 'WrapperReadBegin', 'CountReadBegin',
  'local-relic-count-increased', 'pickupCallbackObserved = false', 'faultEvidence'
)) {
  Assert-Contains $relic ([regex]::Escape($token)) "Relic count path is missing '$token'."
}
Assert-NotContains $relic 'getArrayIndex\s*\(|getProperty\s*\([^\)]*[''"](?:InventoryInfo|Enhancements)[''"]|ClientOnPickedUpPickup' 'Relic wrapper/count experiment crossed into elements, inventory info, enhancements, or callback claims.'

$main = $sources['main.lua']
Assert-Contains $main 'pcall\(require, "full_observe_coordinator"\)' 'Normal coordinator literal import is missing.'
Assert-Contains $main 'pcall\(require, "progressive_observe_coordinator"\)' 'Progressive coordinator literal import is missing.'
Assert-Contains $main 'snapshot campaign coordinator unavailable; no tick source will be registered' 'Coordinator failure must prevent any tick/hook registration.'
Assert-Ordered $main @('fullObserveCoordinator:start()', 'registerSelectedTickDriver(cfg.tickDriver)') `
  'Coordinator safety validation must complete before registering any tick source.'

$state = $sources['campaign_state.lua']
foreach ($token in @(
  'activeProfile = self.activeProfile', 'profileId = self.activeProfile',
  "hooksDisabled = self.config.allowPassiveObservationHooks ~= true and self.config.progressiveHooksArmed ~= true",
  'writesDisabled = self.config.allowWriteProbes ~= true', 'rpcCallsDisabled = self.config.allowRpcProbes ~= true',
  'runtimeDiscoveryDisabled = self.config.allowFullObserveRuntimeDiscovery ~= true',
  'inventoryStagesDisabled = self.config.allowFullObserveInventoryStages ~= true',
  'rawIdentityDisabled = self.config.allowRawIdentityEvidence ~= true'
)) {
  Assert-Contains $state ([regex]::Escape($token)) "Live status truth is missing '$token'."
}
Assert-Contains $sources['snapshot_sampler.lua'] "observationProfile = tostring\(self\.state\.activeProfile or 'normal-play-guide'\)" `
  'Snapshot rows must discriminate normal and progressive observation profiles.'
Assert-Contains $sources['snapshot_sampler.lua'] "self\.state\.activeProfile == 'progressive-broad-observation'" `
  'All progressive-process snapshots, including the safe baseline, must be excluded from hook-free replay.'
Assert-Contains $sources['snapshot_sampler.lua'] "result ~= 'error' and shouldWrite and writeOk" `
  'Safe baseline readiness must require a durable successful snapshot write before hook registration.'

foreach ($key in @(
  'progressiveObservationEnabled', 'relicCountValidationEnabled', 'allowPassiveObservationHooks',
  'allowFullObserveRuntimeDiscovery', 'allowFullObserveInventoryStages', 'allowWriteProbes',
  'allowRpcProbes', 'allowHudTickHook', 'allowRawIdentityEvidence'
)) {
  $value = Get-CrabRuntimeProbeConfigValue -ConfigPath $configPath -Key $key
  if ($value -ne 'false') { throw "Shipped safe config expected $key=false, got '$value'." }
}

$catalog = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'campaign\hook_candidate_catalog.json') | ConvertFrom-Json
$trusted = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'campaign\trusted_hook_manifest.json') | ConvertFrom-Json
$quarantine = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'campaign\hook_quarantine.json') | ConvertFrom-Json
$ledger = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'campaign\hook_validation_ledger.json') | ConvertFrom-Json
if ($catalog.candidateCount -ne 111 -or $catalog.candidates.Count -ne 111) { throw 'Progressive catalog must contain exactly 111 stable candidates.' }
if ($catalog.principalCandidateId -ne 'hook-crabps-onrep-islandrewardrarity' -or
    $catalog.candidates[0].id -ne $catalog.principalCandidateId -or
    $catalog.candidates[0].maximumValidationDepth -ne 7) {
  throw 'OnRep_IslandRewardRarity must remain the principal first candidate with the complete depth ladder.'
}
if ($trusted.candidates.Count -ne 0 -or -not [string]::IsNullOrEmpty([string]$trusted.compatibilityFingerprint)) {
  throw 'Release must not ship any pretrusted hook or assigned trusted compatibility.'
}
if ($quarantine.entries.Count -ne 0) { throw 'Release quarantine defaults must begin empty.' }
if (($ledger.candidates | Where-Object { $_.state -eq 'trusted' -or $null -ne $_.trustedDepth }).Count -ne 0) {
  throw 'Legacy observations must not migrate into v1.0.4 trust.'
}

$snapshotSchema = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'schemas\snapshot-observation-v1.schema.json') | ConvertFrom-Json
$profileContract = @($snapshotSchema.allOf) | Select-Object -First 1
if ($snapshotSchema.properties.observationProfile.enum -notcontains 'progressive-broad-observation' -or
    $profileContract.then.properties.safety.properties.hooksDisabled.const -ne $false -or
    $profileContract.else.properties.safety.properties.hooksDisabled.const -ne $true) {
  throw 'Snapshot schema must truthfully discriminate progressive hooks while preserving hook-free normal/backward-compatible rows.'
}

Write-Host 'Progressive runtime source and artifact contract checks passed.'
