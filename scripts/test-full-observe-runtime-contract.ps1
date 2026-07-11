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

$repoRoot = Resolve-CrabRuntimeProbeRepoRoot -StartPath $PSScriptRoot -RequireGit
$luaRoot = Join-Path $repoRoot 'client\Mods\CrabRuntimeProbe\Scripts'
$inventory = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'inventory_stage_manager.lua')
$hooks = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'passive_hook_manager.lua')
$state = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'campaign_state.lua')
$status = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'status_writer.lua')
$coordinator = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'full_observe_coordinator.lua')
$sampler = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'snapshot_sampler.lua')
$safe = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'safe_access.lua')
$catalog = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'crabsync_catalog.lua')
$evidenceWriter = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'evidence_writer.lua')
$resultWriter = Get-Content -Raw -LiteralPath (Join-Path $luaRoot 'result_writer.lua')

Assert-CrabRuntimeProbeNormalSamplerSafety `
  -ScriptsRoot $luaRoot `
  -Label 'full-observe normal snapshot path'
Assert-CrabRuntimeProbeSnapshotObservationSchema `
  -SchemaPath (Join-Path $repoRoot 'schemas\snapshot-observation-v1.schema.json') `
  -Label 'snapshot observation schema'

foreach ($category in @('WeaponMods', 'AbilityMods', 'MeleeMods', 'Perks', 'Relics')) {
  Assert-Contains $inventory ("\b" + [regex]::Escape($category) + '\b') "Missing staged inventory category: $category"
}
foreach ($stage in @(
  'wrapper-shape', 'count-metadata', 'first-element', 'item-da-identity',
  'inventoryinfo-parent', 'level-accumulated-buff', 'enhancements-shape',
  'enhancements-count', 'enhancements-values', 'capped-local-iteration',
  'duplicate-semantics', 'slot-index-stability', 'joined-client-repeat',
  'remote-visibility'
)) {
  Assert-Contains $inventory ([regex]::Escape("'$stage'")) "Missing inventory stage: $stage"
}
$stageBlock = [regex]::Match($inventory, 'local STAGES = \{(?<body>.*?)\}\s*\r?\n\r?\nlocal CATEGORY_DEFINITIONS', [Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $stageBlock.Success -or [regex]::Matches($stageBlock.Groups['body'].Value, "'[^']+'" ).Count -ne 14) {
  throw 'Inventory campaign must expose exactly 14 ordered stages.'
}

Assert-Contains $safe 'index < 0' 'Reviewed TArray numeric offsets must accept zero.'
Assert-Contains $inventory 'getArrayIndex\(arrayValue, 0\)' 'First-element staging must use official zero-based TArray indexing.'
Assert-Contains $inventory 'for offset = 0, limit - 1 do' 'Capped inventory iteration must use zero-based offsets.'
Assert-Contains $inventory 'for offset = 0, enhancementLimit - 1 do' 'Capped enhancement iteration must use zero-based offsets.'
Assert-Contains $inventory 'cleanSamplesRequired' 'Stage confirmation must require consecutive clean samples.'
Assert-Contains $inventory 'fullObserveCleanSamplesRequired, 3, 3, 5' 'Clean stage evidence must fail closed at three samples minimum.'
Assert-Contains $inventory 'cleanLifecycleGeneration' 'Clean samples may not cross lifecycle generations.'
Assert-Contains $inventory 'enhancement array is empty; no values observed' 'An empty enhancement array must stay partial for value evidence.'
Assert-Contains $inventory 'capped enhancement subset read; values exceed reviewed cap' 'Over-cap enhancement values must remain partial.'
Assert-Contains $inventory 'enhancement values include unsupported or redacted types' 'Redacted or unsupported enhancement values must never confirm stage 9.'
Assert-Contains $inventory 'capped subset read; full inventory exceeds reviewed cap' 'Over-cap inventories must remain partial, not fail or claim full coverage.'
Assert-Contains $inventory 'slot/index stability window in progress' 'Slot ordering must use a meaningful stability window.'
Assert-Contains $inventory 'stableAcrossMeaningfulChange' 'Slot ordering must capture evidence across meaningful changes.'
Assert-Contains $inventory 'duplicate DA candidates lack readable per-entry metadata representation' 'Equal DA fingerprints alone must not prove duplicate semantics.'
Assert-Contains $inventory 'identical duplicate entries cannot be distinguished for stable per-instance ordering' 'Indistinguishable duplicates must keep slot/index stability partial.'
Assert-Contains $inventory "'not-applicable'" 'Joined-client-only stages must support an explicit host N/A result.'
Assert-Contains $inventory "evidenceHealth = 'role-mismatch'" 'Selected/observed multiplayer role contradictions must dirty evidence.'
Assert-Contains $inventory "circuitAllows\('inventory:' \.\. category\)" 'Each inventory category must have an independent circuit breaker.'
Assert-Contains $inventory 'full_observe_progress\.txt' 'Inventory stage progress must persist atomically.'
Assert-Contains $inventory 'campaignGeneration=' 'Persisted inventory progress must be generation keyed.'
Assert-Contains $inventory 'lastSessionId=' 'Persisted inventory progress must be session keyed.'
Assert-Contains $inventory 'selectedCategory = category' 'Inventory sampling must rotate to one selected category.'
$onTickBlock = [regex]::Match($inventory, 'function manager:onTick\(\)(?<body>.*?)\r?\n  end\r?\n\r?\n  manager\.STAGES', [Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $onTickBlock.Success -or [regex]::Matches($onTickBlock.Groups['body'].Value, 'self:runStage\(').Count -ne 1) {
  throw 'Each inventory sample may call runStage for at most one rotated category.'
}
Assert-Contains $inventory 'one-direction remote inventory candidate visibility observed; bidirectional proof remains Needs Coverage' 'Stage 14 must remain directional candidate evidence.'
Assert-Contains $inventory 'visibleDistinctPlayerStates' 'Remote visibility must deduplicate PlayerState fingerprints.'
Assert-Contains $inventory 'selectedRole ~= observedRole' 'Remote visibility must require selected/observed role consistency.'
Assert-Contains $inventory 'bidirectionalVisibilityProven = false' 'A single machine must never claim bidirectional visibility.'

Assert-Contains $catalog 'nativeClassRoots = \{' 'Catalog must provide exact native runtime-discovery roots.'
Assert-Contains $catalog 'blueprintClassRoots = \{' 'Catalog must provide exact Blueprint runtime-discovery roots.'
Assert-Contains $catalog 'objectDumpPath = ' 'Every discovery root contract must carry an object-dump path.'
Assert-Contains $catalog 'reflectionScope = "exact-class-roots-only"' 'Catalog reflection scope must remain exact-root only.'
Assert-NotContains $catalog 'ownerPatterns = ' 'Catalog must not request broad owner-pattern expansion.'
Assert-Contains $hooks 'rules\.nativeClassRoots' 'Runtime discovery must consume exact native catalog roots.'
Assert-Contains $hooks 'rules\.blueprintClassRoots' 'Runtime discovery must consume exact Blueprint catalog roots.'
Assert-NotContains $hooks 'rules\.classRoots' 'Runtime discovery must not depend on the retired untyped classRoots contract.'
Assert-Contains $hooks 'maximumResolvedClassesPerGeneration' 'Runtime discovery must honor the catalog class cap.'
Assert-Contains $hooks 'visited >= functionCap' 'Function reflection must be bounded.'
Assert-Contains $hooks 'maximumFunctionsPerResolvedClass' 'Function reflection must consume the generated cap contract.'
Assert-Contains $hooks 'self\.discoveryQueueIndex = self\.discoveryQueueIndex \+ 1' 'Runtime reflection must advance only one queued class per stable tick.'
Assert-Contains $hooks 'newlyDiscoveredCandidatesDisposition' 'Exact runtime discoveries must enter Needs Coverage.'
Assert-Contains $hooks 'validDiscoveredHookPath\(path\)' 'Runtime-discovered hooks must have an exact validated path.'
Assert-Contains $hooks "hookRegistrationStatus = 'not-reviewed-not-hooked'" 'New reflected UFunctions must remain unhooked Needs Coverage candidates.'
Assert-Contains $hooks 'exact catalog class root is not loaded in this lifecycle' 'Unloaded exact roots must remain Needs Coverage instead of becoming unsupported.'
Assert-Contains $hooks 'rootDiscoveryDescriptor' 'Every exact discovery root must emit a root-level outcome, even without descriptors.'
Assert-Contains $hooks 'hitFunctionCap and ''needs-coverage'' or ''confirmed''' 'Capped reflection must never claim complete class coverage.'
Assert-Contains $hooks "if functionFound and tostring\(descriptor\.hookPath or ''\):match" 'Blueprint hooks may register only after exact in-memory function discovery.'
Assert-Contains $hooks "ForEachFunction reflection unavailable in this lifecycle" 'Transient reflection failure must remain Needs Coverage.'
Assert-Contains $hooks "tostring\(descriptor\.hookPath or ''\):match\('\^/Game/'\)" 'Blueprint descriptors must be recognized from exact paths.'
Assert-Contains $hooks "self:guardedCallback\(descriptor, 'post'" 'Blueprint hooks must capture the one supported post callback.'
Assert-Contains $hooks "self:guardedCallback\(descriptor, 'pre'" 'Native hooks must capture the supported pre callback.'
Assert-Contains $hooks 'hookGlobalRowCap' 'Passive callback evidence must have a global row cap.'
Assert-Contains $hooks 'hookPerDescriptorRowCap' 'Passive callback evidence must have a per-descriptor row cap.'
Assert-Contains $hooks 'coalescedInvocations' 'High-frequency callbacks must be rate-coalesced and counted.'
Assert-Contains $hooks 'deferStatusFlush = true' 'Hook callbacks must not rewrite full status on every row.'
Assert-Contains $hooks 'EXACT_NATURAL_OBSERVATION_RULES' 'Checklist qualification must use an explicit exact natural-observation allowlist.'
Assert-NotContains $hooks 'qualifyingChecklistLinks = descriptor\.checklistLinks' 'A callback must never qualify every descriptor checklist link.'
Assert-Contains $hooks 'same confirmed PlayerState scope with readable pre/post state' 'Pre/post correlation must require readable state in the same confirmed scope.'
Assert-Contains $hooks "'OwningPS', 'PlayerState'" 'Ownership scope may use only the curated PlayerState relationships.'
Assert-Contains $hooks "source = 'local-playerstate-fallback-candidate'" 'Local fallback ownership must be explicitly labeled as an unconfirmed candidate.'

Assert-Contains $status "live_status\.slot' \.\. tostring\(slot\) \.\. '\.json'" 'Status files must use the bounded live_status.slotN.json contract.'
Assert-Contains $status "tempPath = finalPath \.\. .*'\.tmp'" 'Status snapshots must write a temporary file first.'
Assert-Contains $status 'os\.rename\(tempPath, finalPath\)' 'Status snapshots must publish only after close via rename.'
Assert-Contains $status 'resumeSequence' 'Status sequence must resume from completed ring files.'
Assert-Contains $state 'full_observe_sequence\.txt' 'Canonical evidence sequence must persist independently.'
Assert-Contains $state 'dashboard_stop_requested\.json' 'Dashboard stop control must use the documented marker.'
foreach ($field in @(
  'schemaVersion', 'sequence', 'writtenAtUtc', 'heartbeatAtUtc', 'campaignId',
  'campaignName', 'campaignGeneration', 'machineId', 'sessionId', 'selectedRole',
  'observedRole', 'authorityStatus', 'lifecycle', 'runtime', 'safety', 'checklist',
  'hookRegistration', 'inventoryStages', 'evidenceHealth', 'crashSuspected', 'dirtyEvidence'
)) {
  Assert-Contains $state ("\b" + [regex]::Escape($field) + '\s*=') "Status snapshot contract is missing $field."
}

foreach ($forbiddenNormalPath in @(
  'RegisterHook\s*\(',
  'UnregisterHook\s*\(',
  'RegisterLoadMap(?:Pre|Post)?Hook\s*\(',
  'RegisterInitGameState(?:Pre|Post)?Hook\s*\(',
  'registerAll\s*\(',
  'registerLifecycleHooks\s*\(',
  'inventory\s*:\s*onTick\s*\(',
  'ForEachFunction\s*\('
)) {
  Assert-NotContains $coordinator $forbiddenNormalPath "Normal coordinator contains forbidden hook/discovery/inventory path: $forbiddenNormalPath"
  Assert-NotContains $sampler $forbiddenNormalPath "Normal sampler contains forbidden hook/discovery/inventory path: $forbiddenNormalPath"
}
Assert-Contains $coordinator "require\('snapshot_sampler'\)" 'Normal coordinator must load the snapshot sampler.'
Assert-Contains $coordinator 'config\.snapshotSamplerEnabled == true' 'Snapshot sampling must require its explicit gate.'
Assert-Contains $coordinator 'config\.allowPassiveObservationHooks ~= true' 'Snapshot mode must reject legacy passive hooks.'
Assert-Contains $coordinator 'config\.allowFullObserveInventoryStages ~= true' 'Snapshot mode must reject legacy inventory stages.'
Assert-Contains $coordinator 'config\.allowFullObserveRuntimeDiscovery ~= true' 'Snapshot mode must reject runtime discovery.'
Assert-Contains $coordinator 'snapshotStableSamplesRequired, 10, 10, 120' 'Stable reads must require at least ten consecutive samples.'
Assert-Contains $coordinator 'snapshotStableDwellSeconds, 30, 30, 600' 'Stable reads must require at least a thirty-second dwell.'
Assert-Contains $coordinator "self\.stableReady and self\.state\.lifecycleState == 'stable'" 'Snapshot reads may run only after the stability barrier opens.'
Assert-Contains $coordinator 'self\.snapshots:onTick\(\)' 'The stable normal path must delegate only to the snapshot sampler.'
Assert-Contains $coordinator "transitionSource = 'stable-polling'" 'Lifecycle evidence must come from stable polling, not global hooks.'
Assert-Contains $coordinator "observedRoleFromAuthority" 'Observed role must be derived independently of the selected role declaration.'
Assert-Contains $coordinator "safe\.fingerprintValue" 'Runtime object identities must be emitted only as fingerprints.'
Assert-Contains $coordinator 'generation ~= nil and generation >= 1' 'Campaign generation zero/unassigned must fail closed.'

foreach ($field in @(
  'recordType', 'sessionId', 'campaignId', 'campaignGeneration', 'machineId',
  'sequence', 'timestampUtc', 'lifecycleGeneration', 'context', 'selectedRole',
  'observedRole', 'worldFingerprint', 'playerStateFingerprint', 'category',
  'stability', 'fields', 'safety', 'dirtyEvidence', 'crashSuspected'
)) {
  Assert-Contains $sampler ("\b" + [regex]::Escape($field) + '\s*=') "Snapshot observation source is missing $field."
}
foreach ($field in @(
  'writesDisabled', 'rpcCallsDisabled', 'mutationDisabled', 'hooksDisabled',
  'runtimeDiscoveryDisabled', 'inventoryStagesDisabled', 'rawIdentityDisabled'
)) {
  Assert-Contains $sampler ("\b" + [regex]::Escape($field) + '\s*=\s*true') "Snapshot safety field $field must be emitted as true."
}

foreach ($canonicalField in @('currentProbeStage', 'runtimeProbeLoaded', 'runtimeProbeState', 'ue4ssState', 'gameProcessState', 'rpcsDisabled')) {
  Assert-Contains $state ("\b" + $canonicalField + '\s*=') "Live status is missing canonical field $canonicalField."
}
Assert-Contains $state 'hookIo = self\.hookIo' 'Live status must expose bounded passive-hook counters.'

Assert-Contains $evidenceWriter 'existingStartedAt or now' 'Resuming the same semantic session must preserve its original manifest start time.'
Assert-Contains $evidenceWriter 'existingInitialSequence or resumeSequence' 'Resuming must preserve the initial evidence sequence.'
Assert-Contains $evidenceWriter 'activeEvidencePath' 'Evidence fallback selection must be sticky instead of alternating output paths.'
Assert-Contains $resultWriter 'activeResultPath' 'Result fallback selection must be sticky instead of alternating output paths.'
$unsafeBlock = [regex]::Match($evidenceWriter, 'local function unsafeActiveGates\(config\)(?<body>.*?)\r?\nend\r?\n\r?\nlocal function activeResearchGates', [Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $unsafeBlock.Success) { throw 'Could not isolate unsafe gate classification.' }
foreach ($safeObservationGate in @('fullObserveEnabled', 'snapshotSamplerEnabled', 'statusWriterEnabled')) {
  if ($unsafeBlock.Groups['body'].Value -match [regex]::Escape($safeObservationGate)) {
    throw "$safeObservationGate must be reported as an observation gate, not an unsafe gate."
  }
}
foreach ($unsafeLegacyGate in @('allowPassiveObservationHooks', 'allowFullObserveInventoryStages', 'allowFullObserveRuntimeDiscovery')) {
  if ($unsafeBlock.Groups['body'].Value -notmatch [regex]::Escape($unsafeLegacyGate)) {
    throw "$unsafeLegacyGate must be reported as unsafe when active."
  }
}

try {
  Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'schemas\live-status-v1.schema.json') | ConvertFrom-Json -ErrorAction Stop | Out-Null
} catch {
  throw "live-status-v1.schema.json is invalid JSON: $($_.Exception.Message)"
}

Write-Host 'Full-observe runtime contract checks passed.'
