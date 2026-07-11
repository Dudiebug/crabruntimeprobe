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

if ((Get-CrabRuntimeProbeConfigValue -ConfigPath $SourceConfigPath -Key "allowCrystalsReadProbes") -ne "false") {
  throw "default config expected allowCrystalsReadProbes = false."
}

$plan = Get-Content -Raw -LiteralPath $PlanPath | ConvertFrom-Json -ErrorAction Stop
$phase = @($plan.phases | Where-Object { $_.phaseId -eq "crystals-read" })[0]
if ($null -eq $phase) { throw "campaign plan missing crystals-read." }
if ($phase.implemented -ne $true) { throw "crystals-read must be implemented." }
if ($phase.probeSet -ne "crystals-read") { throw "crystals-read phase has wrong probeSet." }
if ($phase.requiredGates.allowCrystalsReadProbes -ne $true) { throw "crystals-read phase must enable allowCrystalsReadProbes." }
foreach ($gate in @("allowHudTickHook", "allowUnknownRoleProbes", "allowJoinedClientDeepProbes", "allowDeepArrayProbes", "allowInventoryInfoProbes", "allowHealthProbes", "allowIdentityProbes", "allowRawIdentityEvidence", "allowResourceVisibilityProbes", "allowSlotsReadProbes", "allowSafeScalarWatchProbes", "allowInventoryArrayShallowProbes", "allowInventoryArrayShapeConfirmProbes", "allowInventoryUserdataIntrospectionProbes", "allowWriteProbes", "allowRpcProbes")) {
  if (@($phase.forbiddenGates) -notcontains $gate) {
    throw "crystals-read phase must forbid $gate."
  }
}

$runner = Get-Content -Raw -LiteralPath $ProbeRunnerPath
if ($runner -notmatch [regex]::Escape("probe.set == 'crystals-read' and not config.allowCrystalsReadProbes")) {
  throw "crystals-read probes must be gated by allowCrystalsReadProbes."
}

$quickCollect = Get-Content -Raw -LiteralPath $QuickCampaignCollectPath
if ($quickCollect -notmatch [regex]::Escape('$PhaseId -eq "crystals-read"')) {
  throw "quick-campaign-collect.ps1 must route crystals-read."
}
if ($quickCollect -notmatch [regex]::Escape("-CollectCrystalsRead")) {
  throw "quick-campaign-collect.ps1 must invoke CollectCrystalsRead."
}

$cycle = Get-Content -Raw -LiteralPath $RunLocalDiagnosticCyclePath
if ($cycle -notmatch [regex]::Escape('[switch]$CollectCrystalsRead')) {
  throw "run-local-diagnostic-cycle.ps1 must expose CollectCrystalsRead."
}
if ($cycle -notmatch [regex]::Escape('-AllowCrystalsReadProbes:($Mode -eq "CollectCrystalsRead")')) {
  throw "CollectCrystalsRead must allow only allowCrystalsReadProbes."
}
foreach ($expected in @(
  "crystals_read_probe_ran",
  "crystals_read_local_playerstate_present",
  "crystals_read_attempted",
  "crystals_read_value_integer_like",
  "crystals_read_safety_violation"
)) {
  if ($cycle -notmatch [regex]::Escape($expected)) {
    throw "run-local-diagnostic-cycle.ps1 missing crystals summary field: $expected"
  }
}

$registry = Get-Content -Raw -LiteralPath $ProbeRegistryPath
$helperStart = $registry.IndexOf("local function buildCrystalsReadCache")
$helperEnd = $registry.IndexOf("local function buildSlotsReadCache")
$probeStart = $registry.IndexOf("Resource.Crystals.Read")
$probeEnd = $registry.IndexOf("Resource.Slots.Read")
if ($helperStart -lt 0 -or $helperEnd -le $helperStart -or $probeStart -lt 0 -or $probeEnd -le $probeStart) { throw "could not isolate crystals-read probe block." }
$crystalsBlock = $registry.Substring($helperStart, $helperEnd - $helperStart) + "`n" + $registry.Substring($probeStart, $probeEnd - $probeStart)
foreach ($required in @(
  "Resource.Crystals.Read",
  "buildCrystalsReadCache",
  "safe.getProperty(playerState, 'Crystals')",
  "noElementDereference = true",
  "noArrayCount = true",
  "noArrayTraversal = true",
  "noInventoryInfo = true",
  "noEnhancements = true",
  "noWrites = true",
  "noRpcs = true",
  "noHud = true",
  "noDeepArrays = true",
  "crashAttributionMarker = 'crystals-read'"
)) {
  if ($crystalsBlock -notmatch [regex]::Escape($required)) {
    throw "crystals-read probe block missing $required."
  }
}
foreach ($forbidden in @("safe.countArrayLimited", "safe.forEachArrayLimited", "safe.getArrayElement", "getArrayElement", "forEachArrayLimited", ":get(", "InventoryInfo')", "Enhancements')", "ServerIncrementNumInventorySlots", "allowWriteProbes = true", "allowRpcProbes = true", "allowHudTickHook = true", "allowDeepArrayProbes = true")) {
  if ($crystalsBlock -match [regex]::Escape($forbidden)) {
    throw "crystals-read probe block must not contain $forbidden."
  }
}

$WorkRoot = Join-Path $RepoRoot "dist\test-crystals-read-work"
if (Test-Path -LiteralPath $WorkRoot) {
  Remove-Item -LiteralPath $WorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $WorkRoot "evidence\runtime\crystals") | Out-Null
Copy-Item -LiteralPath (Join-Path $RepoRoot "campaign") -Destination (Join-Path $WorkRoot "campaign") -Recurse

$safeGates = '"allowHudTickHook":false,"allowUnknownRoleProbes":false,"allowJoinedClientDeepProbes":false,"allowDeepArrayProbes":false,"allowInventoryInfoProbes":false,"allowHealthProbes":false,"allowIdentityProbes":false,"allowRawIdentityEvidence":false,"allowResourceVisibilityProbes":false,"allowCrystalsReadProbes":true,"allowSlotsReadProbes":false,"allowInventoryArrayShallowProbes":false,"allowInventoryArrayShapeConfirmProbes":false,"allowInventoryUserdataIntrospectionProbes":false,"allowWriteProbes":false,"allowRpcProbes":false'
$SessionDir = Join-Path $WorkRoot "evidence\runtime\crystals"
$crystalsRow = ('{"timestamp":"2026-05-05T10:00:01Z","sessionId":"crystals","probeId":"Resource.Crystals.Read","probeName":"Resource.Crystals.Read","probeSet":"crystals-read","category":"resource-crystals","symbol":"CrabPS.Crystals","owner":"CrabPS","member":"Crystals","accessMethod":"GetPropertyValue","accessKind":"localCrystalsRead","mode":"active","tickDriver":"executeDelay","tick":100,"context":"solo","role":"solo-or-host","lifecycleState":"stable","result":"ok","runtimeStatus":"SAFE","valueKind":"crystals_read","valueSummary":"category=crystals-read localPlayerStatePresent=true crystalsReadAttempted=true crystalsPresent=true crystalsValueKind=number crystalsIntegerLike=true crystalsInUInt32Range=true noArrayTraversal=true noElementDereference=true noInventoryInfo=true noEnhancements=true noWrites=true noRpcs=true noHud=true noDeepArrays=true crashAttributionMarker=crystals-read","sourceScope":"local_player_state_crystals","sourcePath":"CrabPC.PlayerState","sourceClass":"CrabPS","localPlayerStatePresent":true,"crystalsReadAttempted":true,"crystalsPresent":true,"crystalsValue":1234,"crystalsValueKind":"number","crystalsIntegerLike":true,"crystalsInUInt32Range":true,"noElementDereference":true,"noArrayCount":true,"noArrayTraversal":true,"noInventoryInfo":true,"noEnhancements":true,"noWrites":true,"noRpcs":true,"noHud":true,"noDeepArrays":true,"crashAttributionMarker":"crystals-read","safetyGates":{' + $safeGates + '}}')
Set-Content -LiteralPath (Join-Path $SessionDir "access_evidence.jsonl") -Encoding ASCII -Value @($crystalsRow)
Set-Content -LiteralPath (Join-Path $SessionDir "probe_results.jsonl") -Encoding ASCII -Value @($crystalsRow)
Set-Content -LiteralPath (Join-Path $SessionDir "session_manifest.json") -Encoding ASCII -Value ('{"sessionId":"crystals","probeSet":"crystals-read","tickDriver":"executeDelay","config":{"mode":"active","probeSet":"crystals-read","tickDriver":"executeDelay",' + $safeGates + '},"safetyGates":{' + $safeGates + '},"activeResearchGates":["allowCrystalsReadProbes"]}')
Set-Content -LiteralPath (Join-Path $SessionDir "diagnostic_summary.txt") -Encoding ASCII -Value @(
  "sessionId = crystals",
  "crystals_read_probe_ran = True",
  "crystals_read_local_playerstate_present = True",
  "crystals_read_attempted = True",
  "crystals_read_value = 1234",
  "crystals_read_value_kind = number",
  "crystals_read_value_integer_like = True",
  "crystals_read_safety_violation = False",
  "crash_after_prepare = False",
  "crash_suspect = False"
)

$NodeTestPath = Join-Path $WorkRoot "crystals-classifier-test.js"
Set-Content -LiteralPath $NodeTestPath -Encoding ASCII -Value @'
const helpers = require(process.argv[2]);
function assert(condition, message) {
  if (!condition) throw new Error(message);
}
const safeGates = {
  allowCrystalsReadProbes: true,
  allowHudTickHook: false,
  allowUnknownRoleProbes: false,
  allowJoinedClientDeepProbes: false,
  allowDeepArrayProbes: false,
  allowInventoryInfoProbes: false,
  allowHealthProbes: false,
  allowIdentityProbes: false,
  allowRawIdentityEvidence: false,
  allowResourceVisibilityProbes: false,
  allowSlotsReadProbes: false,
  allowInventoryArrayShallowProbes: false,
  allowInventoryArrayShapeConfirmProbes: false,
  allowInventoryUserdataIntrospectionProbes: false,
  allowWriteProbes: false,
  allowRpcProbes: false
};
let result = helpers.classifyCrystalsReadEvidence([
  { probeName: 'Resource.Crystals.Read', localPlayerStatePresent: true, crystalsReadAttempted: true, crystalsPresent: true, crystalsValue: 1234, crystalsIntegerLike: true, noElementDereference: true, noArrayTraversal: true, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: safeGates }
]);
assert(result.status === 'crystals_read_confirmed', `crystals read should confirm, got ${result.status}`);
assert(result.valueIntegerLike === true, 'crystals value should be integer-like');
result = helpers.classifyCrystalsReadEvidence([
  { probeName: 'Resource.Crystals.Read', localPlayerStatePresent: true, crystalsReadAttempted: true, crystalsPresent: true, crystalsValue: 12.5, crystalsIntegerLike: false, noElementDereference: true, noArrayTraversal: true, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: safeGates }
]);
assert(result.status === 'failed', `fractional crystals should fail, got ${result.status}`);
result = helpers.classifyCrystalsReadEvidence([
  { probeName: 'Resource.Crystals.Read', localPlayerStatePresent: true, crystalsReadAttempted: true, crystalsPresent: true, crystalsValue: 1, crystalsIntegerLike: true, noElementDereference: true, noArrayTraversal: true, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: { ...safeGates, allowInventoryArrayShallowProbes: true } }
]);
assert(result.status === 'failed', `forbidden gate should fail, got ${result.status}`);
'@
node $NodeTestPath (Join-Path $RepoRoot "tools\campaign_helpers.js")
if ($LASTEXITCODE -ne 0) { throw "crystals-read classifier tests failed." }

Push-Location $WorkRoot
try {
  node (Join-Path $RepoRoot "tools\generate_access_docs.js")
  node (Join-Path $RepoRoot "tools\generate_campaign_docs.js") --state (Join-Path $WorkRoot "evidence\campaign_state.json") --out (Join-Path $WorkRoot "docs\CAMPAIGN_STATUS.md") --write-state --quiet
} finally {
  Pop-Location
}

Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Local Crystals Read Summary"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Crystals read status: crystals_read_confirmed"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Crystals value integer-like when present: yes"
Assert-Contains -Path (Join-Path $WorkRoot "docs\CAMPAIGN_STATUS.md") -Expected "Local Crystals Read"
Assert-Contains -Path (Join-Path $WorkRoot "docs\CAMPAIGN_STATUS.md") -Expected "UInt32 range is documentation only"

Write-Host "CrabRuntimeProbe crystals-read probe checks passed."
