[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Assert-CrabRuntimeProbeConfig.ps1")

function Assert-Contains {
  param(
    [string]$Path,
    [string]$Expected
  )

  $text = Get-Content -Raw -LiteralPath $Path
  if ($text -notmatch [regex]::Escape($Expected)) {
    throw "$Path missing expected content: $Expected"
  }
}

$RepoRoot = Resolve-CrabRuntimeProbeRepoRoot -StartPath $PSScriptRoot -RequireGit
$SourceConfigPath = Join-Path $RepoRoot "client\Mods\CrabRuntimeProbe\Scripts\config.txt"
$ProbeRegistryPath = Join-Path $RepoRoot "client\Mods\CrabRuntimeProbe\Scripts\probe_registry.lua"
$ProbeRunnerPath = Join-Path $RepoRoot "client\Mods\CrabRuntimeProbe\Scripts\probe_runner.lua"
$PlanPath = Join-Path $RepoRoot "campaign\campaign_plan.crabruntimeprobe-read-map.json"
$QuickCampaignCollectPath = Join-Path $RepoRoot "scripts\quick-campaign-collect.ps1"
$RunLocalDiagnosticCyclePath = Join-Path $RepoRoot "scripts\run-local-diagnostic-cycle.ps1"

if ((Get-CrabRuntimeProbeConfigValue -ConfigPath $SourceConfigPath -Key "allowSlotsReadProbes") -ne "false") {
  throw "default config expected allowSlotsReadProbes = false."
}

$plan = Get-Content -Raw -LiteralPath $PlanPath | ConvertFrom-Json -ErrorAction Stop
$phase = @($plan.phases | Where-Object { $_.phaseId -eq "slots-read" })[0]
if ($null -eq $phase) { throw "campaign plan missing slots-read." }
if ($phase.implemented -ne $true) { throw "slots-read must be implemented." }
if ($phase.probeSet -ne "slots-read") { throw "slots-read phase has wrong probeSet." }
if ($phase.requiredGates.allowSlotsReadProbes -ne $true) { throw "slots-read phase must enable allowSlotsReadProbes." }
foreach ($gate in @("allowHudTickHook", "allowUnknownRoleProbes", "allowJoinedClientDeepProbes", "allowDeepArrayProbes", "allowInventoryInfoProbes", "allowHealthProbes", "allowIdentityProbes", "allowRawIdentityEvidence", "allowResourceVisibilityProbes", "allowCrystalsReadProbes", "allowSafeScalarWatchProbes", "allowInventoryArrayShallowProbes", "allowInventoryArrayShapeConfirmProbes", "allowInventoryUserdataIntrospectionProbes", "allowWriteProbes", "allowRpcProbes")) {
  if (@($phase.forbiddenGates) -notcontains $gate) {
    throw "slots-read phase must forbid $gate."
  }
}

$runner = Get-Content -Raw -LiteralPath $ProbeRunnerPath
if ($runner -notmatch [regex]::Escape("probe.set == 'slots-read' and not config.allowSlotsReadProbes")) {
  throw "slots-read probes must be gated by allowSlotsReadProbes."
}

$quickCollect = Get-Content -Raw -LiteralPath $QuickCampaignCollectPath
if ($quickCollect -notmatch [regex]::Escape('$PhaseId -eq "slots-read"')) {
  throw "quick-campaign-collect.ps1 must route slots-read."
}
if ($quickCollect -notmatch [regex]::Escape("-CollectSlotsRead")) {
  throw "quick-campaign-collect.ps1 must invoke CollectSlotsRead."
}

$cycle = Get-Content -Raw -LiteralPath $RunLocalDiagnosticCyclePath
if ($cycle -notmatch [regex]::Escape('[switch]$CollectSlotsRead')) {
  throw "run-local-diagnostic-cycle.ps1 must expose CollectSlotsRead."
}
if ($cycle -notmatch [regex]::Escape('-AllowSlotsReadProbes:($Mode -eq "CollectSlotsRead")')) {
  throw "CollectSlotsRead must allow only allowSlotsReadProbes."
}
foreach ($expected in @(
  "slots_read_probe_ran",
  "slots_read_local_playerstate_present",
  "slots_read_attempted",
  "slots_read_values_integer_like",
  "slots_read_values_in_byte_range",
  "slots_read_locked_slot_model",
  "slots_read_safety_violation"
)) {
  if ($cycle -notmatch [regex]::Escape($expected)) {
    throw "run-local-diagnostic-cycle.ps1 missing slots summary field: $expected"
  }
}

$registry = Get-Content -Raw -LiteralPath $ProbeRegistryPath
$helperStart = $registry.IndexOf("local function buildSlotsReadCache")
$helperEnd = $registry.IndexOf("local function copyTable")
$probeStart = $registry.IndexOf("Resource.Slots.Read")
$probeEnd = $registry.IndexOf("SafeWatch.Scalar.Sample")
if ($helperStart -lt 0 -or $helperEnd -le $helperStart -or $probeStart -lt 0 -or $probeEnd -le $probeStart) { throw "could not isolate slots-read probe block." }
$slotsBlock = $registry.Substring($helperStart, $helperEnd - $helperStart) + "`n" + $registry.Substring($probeStart, $probeEnd - $probeStart)
foreach ($required in @(
  "Resource.Slots.Read",
  "buildSlotsReadCache",
  "safe.getProperty(playerState, fieldName)",
  "NumWeaponModSlots",
  "NumAbilityModSlots",
  "NumMeleeModSlots",
  "NumPerkSlots",
  "noElementDereference = true",
  "noArrayCount = true",
  "noArrayTraversal = true",
  "noInventoryInfo = true",
  "noEnhancements = true",
  "noWrites = true",
  "noRpcs = true",
  "noHud = true",
  "noDeepArrays = true",
  "crashAttributionMarker = 'slots-read'",
  "lockedSlotModel"
)) {
  if ($slotsBlock -notmatch [regex]::Escape($required)) {
    throw "slots-read probe block missing $required."
  }
}
foreach ($forbidden in @("safe.countArrayLimited", "safe.forEachArrayLimited", "safe.getArrayElement", "getArrayElement", "forEachArrayLimited", ":get(", "InventoryInfo')", "Enhancements')", "ServerIncrementNumInventorySlots", "lockedSlots", "allowWriteProbes = true", "allowRpcProbes = true", "allowHudTickHook = true", "allowDeepArrayProbes = true")) {
  if ($slotsBlock -match [regex]::Escape($forbidden)) {
    throw "slots-read probe block must not contain $forbidden."
  }
}

$WorkRoot = Join-Path $RepoRoot "dist\test-slots-read-work"
if (Test-Path -LiteralPath $WorkRoot) {
  Remove-Item -LiteralPath $WorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $WorkRoot "evidence\runtime\slots") | Out-Null
Copy-Item -LiteralPath (Join-Path $RepoRoot "campaign") -Destination (Join-Path $WorkRoot "campaign") -Recurse

$safeGates = '"allowHudTickHook":false,"allowUnknownRoleProbes":false,"allowJoinedClientDeepProbes":false,"allowDeepArrayProbes":false,"allowInventoryInfoProbes":false,"allowHealthProbes":false,"allowIdentityProbes":false,"allowRawIdentityEvidence":false,"allowResourceVisibilityProbes":false,"allowCrystalsReadProbes":false,"allowSlotsReadProbes":true,"allowInventoryArrayShallowProbes":false,"allowInventoryArrayShapeConfirmProbes":false,"allowInventoryUserdataIntrospectionProbes":false,"allowWriteProbes":false,"allowRpcProbes":false'
$SessionDir = Join-Path $WorkRoot "evidence\runtime\slots"
$slotsRow = ('{"timestamp":"2026-05-05T10:10:01Z","sessionId":"slots","probeId":"Resource.Slots.Read","probeName":"Resource.Slots.Read","probeSet":"slots-read","category":"resource-slots","symbol":"CrabPS.NumWeaponModSlots","owner":"CrabPS","member":"NumWeaponModSlots NumAbilityModSlots NumMeleeModSlots NumPerkSlots","accessMethod":"GetPropertyValue","accessKind":"localSlotsRead","mode":"active","tickDriver":"executeDelay","tick":100,"context":"solo","role":"solo-or-host","lifecycleState":"stable","result":"ok","runtimeStatus":"SAFE","valueKind":"slots_read","valueSummary":"category=slots-read localPlayerStatePresent=true slotsReadAttempted=true fieldsReadable=4 fieldsNilOrUnsupported=0 lockedSlotModel=unresolved noArrayCount=true noArrayTraversal=true noElementDereference=true noInventoryInfo=true noEnhancements=true noWrites=true noRpcs=true noHud=true noDeepArrays=true crashAttributionMarker=slots-read","sourceScope":"local_player_state_slots","sourcePath":"CrabPC.PlayerState","sourceClass":"CrabPS","localPlayerStatePresent":true,"slotsReadAttempted":true,"slotFieldNames":["NumWeaponModSlots","NumAbilityModSlots","NumMeleeModSlots","NumPerkSlots"],"slotScalarValues":{"NumWeaponModSlots":24,"NumAbilityModSlots":12,"NumMeleeModSlots":12,"NumPerkSlots":24},"slotValueKinds":{"NumWeaponModSlots":"number","NumAbilityModSlots":"number","NumMeleeModSlots":"number","NumPerkSlots":"number"},"slotIntegerLike":{"NumWeaponModSlots":true,"NumAbilityModSlots":true,"NumMeleeModSlots":true,"NumPerkSlots":true},"slotValuesInByteRange":{"NumWeaponModSlots":true,"NumAbilityModSlots":true,"NumMeleeModSlots":true,"NumPerkSlots":true},"lockedSlotModel":"unresolved","noElementDereference":true,"noArrayCount":true,"noArrayTraversal":true,"noInventoryInfo":true,"noEnhancements":true,"noWrites":true,"noRpcs":true,"noHud":true,"noDeepArrays":true,"crashAttributionMarker":"slots-read","safetyGates":{' + $safeGates + '}}')
Set-Content -LiteralPath (Join-Path $SessionDir "access_evidence.jsonl") -Encoding ASCII -Value @($slotsRow)
Set-Content -LiteralPath (Join-Path $SessionDir "probe_results.jsonl") -Encoding ASCII -Value @($slotsRow)
Set-Content -LiteralPath (Join-Path $SessionDir "session_manifest.json") -Encoding ASCII -Value ('{"sessionId":"slots","probeSet":"slots-read","tickDriver":"executeDelay","config":{"mode":"active","probeSet":"slots-read","tickDriver":"executeDelay",' + $safeGates + '},"safetyGates":{' + $safeGates + '},"activeResearchGates":["allowSlotsReadProbes"]}')
Set-Content -LiteralPath (Join-Path $SessionDir "diagnostic_summary.txt") -Encoding ASCII -Value @(
  "sessionId = slots",
  "slots_read_probe_ran = True",
  "slots_read_local_playerstate_present = True",
  "slots_read_attempted = True",
  "slots_read_values = NumAbilityModSlots=12, NumMeleeModSlots=12, NumPerkSlots=24, NumWeaponModSlots=24",
  "slots_read_values_integer_like = True",
  "slots_read_values_in_byte_range = True",
  "slots_read_locked_slot_model = unresolved",
  "slots_read_safety_violation = False",
  "crash_after_prepare = False",
  "crash_suspect = False"
)

$NodeTestPath = Join-Path $WorkRoot "slots-classifier-test.js"
Set-Content -LiteralPath $NodeTestPath -Encoding ASCII -Value @'
const helpers = require(process.argv[2]);
function assert(condition, message) {
  if (!condition) throw new Error(message);
}
const safeGates = {
  allowSlotsReadProbes: true,
  allowCrystalsReadProbes: false,
  allowHudTickHook: false,
  allowUnknownRoleProbes: false,
  allowJoinedClientDeepProbes: false,
  allowDeepArrayProbes: false,
  allowInventoryInfoProbes: false,
  allowHealthProbes: false,
  allowIdentityProbes: false,
  allowRawIdentityEvidence: false,
  allowResourceVisibilityProbes: false,
  allowInventoryArrayShallowProbes: false,
  allowInventoryArrayShapeConfirmProbes: false,
  allowInventoryUserdataIntrospectionProbes: false,
  allowWriteProbes: false,
  allowRpcProbes: false
};
let result = helpers.classifySlotsReadEvidence([
  { probeName: 'Resource.Slots.Read', localPlayerStatePresent: true, slotsReadAttempted: true, slotScalarValues: { NumWeaponModSlots: 24, NumAbilityModSlots: 12, NumMeleeModSlots: 12, NumPerkSlots: 24 }, slotIntegerLike: { NumWeaponModSlots: true, NumAbilityModSlots: true, NumMeleeModSlots: true, NumPerkSlots: true }, slotValuesInByteRange: { NumWeaponModSlots: true, NumAbilityModSlots: true, NumMeleeModSlots: true, NumPerkSlots: true }, noElementDereference: true, noArrayCount: true, noArrayTraversal: true, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: safeGates }
]);
assert(result.status === 'slots_read_confirmed', `slots read should confirm, got ${result.status}`);
assert(result.valuesIntegerLike === true, 'slot values should be integer-like');
assert(result.valuesInByteRange === true, 'slot values should be in byte range');
result = helpers.classifySlotsReadEvidence([
  { probeName: 'Resource.Slots.Read', localPlayerStatePresent: true, slotsReadAttempted: true, slotScalarValues: { NumWeaponModSlots: 255 }, slotIntegerLike: { NumWeaponModSlots: true }, slotValuesInByteRange: { NumWeaponModSlots: true }, noElementDereference: true, noArrayCount: true, noArrayTraversal: true, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: safeGates }
], { crashSuspect: true });
assert(result.status === 'crash_suspect_slots_read', `crash evidence should be carried separately, got ${result.status}`);
result = helpers.classifySlotsReadEvidence([
  { probeName: 'Resource.Slots.Read', localPlayerStatePresent: true, slotsReadAttempted: true, slotScalarValues: { NumWeaponModSlots: 256 }, slotIntegerLike: { NumWeaponModSlots: true }, slotValuesInByteRange: { NumWeaponModSlots: false }, noElementDereference: true, noArrayCount: true, noArrayTraversal: true, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: safeGates }
]);
assert(result.status === 'failed', `out-of-byte-range slots should fail, got ${result.status}`);
result = helpers.classifySlotsReadEvidence([
  { probeName: 'Resource.Slots.Read', localPlayerStatePresent: true, slotsReadAttempted: true, slotScalarValues: { NumWeaponModSlots: 1 }, slotIntegerLike: { NumWeaponModSlots: true }, slotValuesInByteRange: { NumWeaponModSlots: true }, noElementDereference: true, noArrayCount: true, noArrayTraversal: true, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: { ...safeGates, allowInventoryArrayShallowProbes: true } }
]);
assert(result.status === 'failed', `forbidden inventory gate should fail, got ${result.status}`);
'@
node $NodeTestPath (Join-Path $RepoRoot "tools\campaign_helpers.js")
if ($LASTEXITCODE -ne 0) { throw "slots-read classifier tests failed." }

Push-Location $WorkRoot
try {
  node (Join-Path $RepoRoot "tools\generate_access_docs.js")
  node (Join-Path $RepoRoot "tools\generate_campaign_docs.js") --state (Join-Path $WorkRoot "evidence\campaign_state.json") --out (Join-Path $WorkRoot "docs\CAMPAIGN_STATUS.md") --write-state --quiet
} finally {
  Pop-Location
}

Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Local Slots Read Summary"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Slots read status: slots_read_confirmed"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Present slot values within 0..255: yes"
Assert-Contains -Path (Join-Path $WorkRoot "docs\CAMPAIGN_STATUS.md") -Expected "Local Slots Read"
Assert-Contains -Path (Join-Path $WorkRoot "docs\CAMPAIGN_STATUS.md") -Expected "Locked slots remain unresolved"
Assert-Contains -Path (Join-Path $WorkRoot "docs\CAMPAIGN_STATUS.md") -Expected "candidate unlocked slot counters"

Write-Host "CrabRuntimeProbe slots-read probe checks passed."
