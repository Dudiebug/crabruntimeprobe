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

if ((Get-CrabRuntimeProbeConfigValue -ConfigPath $SourceConfigPath -Key "allowInventoryUserdataIntrospectionProbes") -ne "false") {
  throw "default config expected allowInventoryUserdataIntrospectionProbes = false."
}

$plan = Get-Content -Raw -LiteralPath $PlanPath | ConvertFrom-Json -ErrorAction Stop
$phase = @($plan.phases | Where-Object { $_.phaseId -eq "local-inventory-userdata-introspection" })[0]
if ($null -eq $phase) { throw "campaign plan missing local-inventory-userdata-introspection." }
if ($phase.probeSet -ne "local-inventory-userdata-introspection") { throw "userdata introspection phase has wrong probeSet." }
if ($phase.requiredGates.allowInventoryUserdataIntrospectionProbes -ne $true) { throw "userdata introspection phase must enable allowInventoryUserdataIntrospectionProbes." }
foreach ($gate in @("allowInventoryArrayShapeConfirmProbes", "allowInventoryArrayShallowProbes", "allowDeepArrayProbes", "allowInventoryInfoProbes", "allowWriteProbes", "allowRpcProbes", "allowHudTickHook", "allowRawIdentityEvidence", "allowHealthProbes", "allowIdentityProbes", "allowResourceVisibilityProbes", "allowUnknownRoleProbes", "allowJoinedClientDeepProbes")) {
  if (@($phase.forbiddenGates) -notcontains $gate) {
    throw "userdata introspection phase must forbid $gate."
  }
}

$runner = Get-Content -Raw -LiteralPath $ProbeRunnerPath
if ($runner -notmatch [regex]::Escape("probe.set == 'local-inventory-userdata-introspection' and not config.allowInventoryUserdataIntrospectionProbes")) {
  throw "userdata introspection probes must be gated by allowInventoryUserdataIntrospectionProbes."
}

$quickCollect = Get-Content -Raw -LiteralPath $QuickCampaignCollectPath
if ($quickCollect -notmatch [regex]::Escape('$PhaseId -eq "local-inventory-userdata-introspection"')) {
  throw "quick-campaign-collect.ps1 must route local-inventory-userdata-introspection."
}
if ($quickCollect -notmatch [regex]::Escape("-CollectLocalInventoryUserdataIntrospection")) {
  throw "quick-campaign-collect.ps1 must invoke CollectLocalInventoryUserdataIntrospection."
}

$cycle = Get-Content -Raw -LiteralPath $RunLocalDiagnosticCyclePath
if ($cycle -notmatch [regex]::Escape('[switch]$CollectLocalInventoryUserdataIntrospection')) {
  throw "run-local-diagnostic-cycle.ps1 must expose CollectLocalInventoryUserdataIntrospection."
}
if ($cycle -notmatch [regex]::Escape('-AllowInventoryUserdataIntrospectionProbes:($Mode -eq "CollectLocalInventoryUserdataIntrospection")')) {
  throw "CollectLocalInventoryUserdataIntrospection must allow only allowInventoryUserdataIntrospectionProbes."
}
foreach ($expected in @(
  "local_inventory_userdata_introspection_probe_ran",
  "local_inventory_userdata_introspection_value_kinds",
  "local_inventory_userdata_introspection_metatable_kinds",
  "local_inventory_userdata_introspection_len_operator_attempted",
  "local_inventory_userdata_introspection_no_array_traversal",
  "local_inventory_userdata_introspection_no_element_dereference"
)) {
  if ($cycle -notmatch [regex]::Escape($expected)) {
    throw "run-local-diagnostic-cycle.ps1 missing userdata introspection summary field: $expected"
  }
}

$registry = Get-Content -Raw -LiteralPath $ProbeRegistryPath
$helperStart = $registry.IndexOf("local function safeTostringPrefix")
$helperEnd = $registry.IndexOf("local function buildInventoryArrayCountReadCache")
$probeStart = $registry.IndexOf("Inventory.LocalArrays.UserdataIntrospection")
$probeEnd = $registry.IndexOf("Inventory.LocalArrays.CountRead")
if ($helperStart -lt 0 -or $helperEnd -le $helperStart -or $probeStart -lt 0 -or $probeEnd -le $probeStart) { throw "could not isolate userdata introspection probe block." }
$introspectionBlock = $registry.Substring($helperStart, $helperEnd - $helperStart) + "`n" + $registry.Substring($probeStart, $probeEnd - $probeStart)
foreach ($required in @(
  "Inventory.LocalArrays.UserdataIntrospection",
  "buildLocalInventoryUserdataIntrospectionCache",
  "safe.getProperty(playerState, fieldName)",
  "getmetatable(value)",
  "return #value",
  "noElementDereference = true",
  "noArrayTraversal = true",
  "noInventoryInfo = true",
  "noEnhancements = true",
  "noWrites = true",
  "noRpcs = true",
  "noHud = true",
  "noDeepArrays = true",
  "crashAttributionMarker = 'userdata-introspection'"
)) {
  if ($introspectionBlock -notmatch [regex]::Escape($required)) {
    throw "userdata introspection probe block missing $required."
  }
}
foreach ($forbidden in @("safe.countArrayLimited", "safe.forEachArrayLimited", "safe.getArrayElement", "getArrayElement", "forEachArrayLimited", ":get(", "InventoryInfo')", "Enhancements')", "allowWriteProbes = true", "allowRpcProbes = true", "allowHudTickHook = true", "allowDeepArrayProbes = true")) {
  if ($introspectionBlock -match [regex]::Escape($forbidden)) {
    throw "userdata introspection probe block must not contain $forbidden."
  }
}

$WorkRoot = Join-Path $RepoRoot "dist\test-local-inventory-userdata-introspection-work"
if (Test-Path -LiteralPath $WorkRoot) {
  Remove-Item -LiteralPath $WorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $WorkRoot "evidence\runtime\userdata") | Out-Null
Copy-Item -LiteralPath (Join-Path $RepoRoot "campaign") -Destination (Join-Path $WorkRoot "campaign") -Recurse

$safeGates = '"allowHudTickHook":false,"allowUnknownRoleProbes":false,"allowJoinedClientDeepProbes":false,"allowDeepArrayProbes":false,"allowInventoryInfoProbes":false,"allowHealthProbes":false,"allowIdentityProbes":false,"allowRawIdentityEvidence":false,"allowResourceVisibilityProbes":false,"allowInventoryArrayShallowProbes":false,"allowInventoryArrayShapeConfirmProbes":false,"allowInventoryUserdataIntrospectionProbes":true,"allowWriteProbes":false,"allowRpcProbes":false'
$SessionDir = Join-Path $WorkRoot "evidence\runtime\userdata"
$userdataRow = ('{"timestamp":"2026-05-05T09:00:01Z","sessionId":"userdata","probeId":"Inventory.LocalArrays.UserdataIntrospection","probeName":"Inventory.LocalArrays.UserdataIntrospection","probeSet":"local-inventory-userdata-introspection","category":"inventory-local-userdata-introspection","symbol":"CrabPS.WeaponMods","owner":"CrabPS","member":"WeaponMods AbilityMods MeleeMods Perks Relics","accessMethod":"GetPropertyValueUserdataMetadata","accessKind":"localInventoryUserdataIntrospection","mode":"active","tickDriver":"executeDelay","tick":100,"context":"solo","role":"solo-or-host","lifecycleState":"stable","result":"ok","runtimeStatus":"SAFE","valueKind":"local_inventory_userdata_introspection","valueSummary":"category=userdata-introspection localPlayerStatePresent=true fieldsReadable=5 fieldsNilOrUnsupported=0 lenOperatorAttempted=true noArrayTraversal=true noElementDereference=true noInventoryInfo=true noEnhancements=true crashAttributionMarker=userdata-introspection","sourceScope":"local_player_state_inventory_userdata_introspection","sourcePath":"CrabPC.PlayerState","sourceClass":"CrabPS","localPlayerStatePresent":true,"fieldNames":["WeaponMods","AbilityMods","MeleeMods","Perks","Relics"],"valueKinds":{"WeaponMods":"userdata","AbilityMods":"userdata","MeleeMods":"userdata","Perks":"userdata","Relics":"userdata"},"tostringKinds":{"WeaponMods":"string","AbilityMods":"string","MeleeMods":"string","Perks":"string","Relics":"string"},"tostringPrefixes":{"WeaponMods":"userdata:<redacted>","AbilityMods":"userdata:<redacted>","MeleeMods":"userdata:<redacted>","Perks":"userdata:<redacted>","Relics":"userdata:<redacted>"},"metatableKinds":{"WeaponMods":"table","AbilityMods":"table","MeleeMods":"table","Perks":"table","Relics":"table"},"metatableKeys":{"WeaponMods":["__len"],"AbilityMods":["__len"],"MeleeMods":["__len"],"Perks":["__len"],"Relics":["__len"]},"lenOperatorAttempted":{"WeaponMods":true,"AbilityMods":true,"MeleeMods":true,"Perks":true,"Relics":true},"lenOperatorResults":{"WeaponMods":0,"AbilityMods":0,"MeleeMods":0,"Perks":0,"Relics":0},"fieldResults":{"WeaponMods":"metadata","AbilityMods":"metadata","MeleeMods":"metadata","Perks":"metadata","Relics":"metadata"},"fieldsReadable":["WeaponMods","AbilityMods","MeleeMods","Perks","Relics"],"fieldsNilOrUnsupported":[],"noElementDereference":true,"noArrayTraversal":true,"noInventoryInfo":true,"noEnhancements":true,"noWrites":true,"noRpcs":true,"noHud":true,"noDeepArrays":true,"crashAttributionMarker":"userdata-introspection","safetyGates":{' + $safeGates + '}}')
Set-Content -LiteralPath (Join-Path $SessionDir "access_evidence.jsonl") -Encoding ASCII -Value @($userdataRow)
Set-Content -LiteralPath (Join-Path $SessionDir "probe_results.jsonl") -Encoding ASCII -Value @($userdataRow)
Set-Content -LiteralPath (Join-Path $SessionDir "session_manifest.json") -Encoding ASCII -Value ('{"sessionId":"userdata","probeSet":"local-inventory-userdata-introspection","tickDriver":"executeDelay","config":{"mode":"active","probeSet":"local-inventory-userdata-introspection","tickDriver":"executeDelay",' + $safeGates + '},"safetyGates":{' + $safeGates + '},"activeResearchGates":["allowInventoryUserdataIntrospectionProbes"]}')
Set-Content -LiteralPath (Join-Path $SessionDir "diagnostic_summary.txt") -Encoding ASCII -Value @(
  "sessionId = userdata",
  "local_inventory_userdata_introspection_probe_ran = True",
  "local_inventory_userdata_introspection_value_kinds = AbilityMods=userdata, MeleeMods=userdata, Perks=userdata, Relics=userdata, WeaponMods=userdata",
  "local_inventory_userdata_introspection_len_operator_attempted = AbilityMods=True, MeleeMods=True, Perks=True, Relics=True, WeaponMods=True",
  "local_inventory_userdata_introspection_no_array_traversal = True",
  "local_inventory_userdata_introspection_no_element_dereference = True",
  "crash_after_prepare = False",
  "crash_suspect = False"
)

$NodeTestPath = Join-Path $WorkRoot "userdata-introspection-classifier-test.js"
Set-Content -LiteralPath $NodeTestPath -Encoding ASCII -Value @'
const helpers = require(process.argv[2]);
function assert(condition, message) {
  if (!condition) throw new Error(message);
}
const safeGates = {
  allowInventoryUserdataIntrospectionProbes: true,
  allowInventoryArrayShapeConfirmProbes: false,
  allowInventoryArrayShallowProbes: false,
  allowDeepArrayProbes: false,
  allowInventoryInfoProbes: false,
  allowWriteProbes: false,
  allowRpcProbes: false,
  allowHudTickHook: false,
  allowRawIdentityEvidence: false,
  allowHealthProbes: false,
  allowIdentityProbes: false,
  allowResourceVisibilityProbes: false,
  allowUnknownRoleProbes: false,
  allowJoinedClientDeepProbes: false
};
let result = helpers.classifyLocalInventoryUserdataIntrospectionEvidence([
  { probeName: 'Inventory.LocalArrays.UserdataIntrospection', localPlayerStatePresent: true, fieldsReadable: ['WeaponMods'], valueKinds: { WeaponMods: 'userdata' }, tostringKinds: { WeaponMods: 'string' }, metatableKinds: { WeaponMods: 'table' }, lenOperatorAttempted: { WeaponMods: true }, lenOperatorResults: { WeaponMods: 0 }, noElementDereference: true, noArrayTraversal: true, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: safeGates }
]);
assert(result.status === 'local_inventory_userdata_introspection_confirmed', `userdata metadata should confirm, got ${result.status}`);
assert(result.lenOperatorResults.WeaponMods === '0', 'len operator result should be preserved as metadata');
result = helpers.classifyLocalInventoryUserdataIntrospectionEvidence([
  { probeName: 'Inventory.LocalArrays.UserdataIntrospection', localPlayerStatePresent: true, fieldsReadable: ['WeaponMods'], valueKinds: { WeaponMods: 'userdata' }, noElementDereference: true, noArrayTraversal: true, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: safeGates }
], { crashSuspect: true });
assert(result.status === 'crash_suspect_local_inventory_userdata_introspection', `crash evidence should be crash-suspect, got ${result.status}`);
result = helpers.classifyLocalInventoryUserdataIntrospectionEvidence([
  { probeName: 'Inventory.LocalArrays.UserdataIntrospection', localPlayerStatePresent: true, fieldsReadable: ['WeaponMods'], valueKinds: { WeaponMods: 'userdata' }, noElementDereference: true, noArrayTraversal: false, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: safeGates }
]);
assert(result.status === 'failed', `traversal marker should fail, got ${result.status}`);
result = helpers.classifyLocalInventoryUserdataIntrospectionEvidence([
  { probeName: 'Inventory.LocalArrays.UserdataIntrospection', localPlayerStatePresent: true, fieldsReadable: ['WeaponMods'], valueKinds: { WeaponMods: 'userdata' }, noElementDereference: true, noArrayTraversal: true, noInventoryInfo: true, noEnhancements: true, noWrites: true, noRpcs: true, noHud: true, noDeepArrays: true, safetyGates: { ...safeGates, allowInventoryArrayShapeConfirmProbes: true } }
]);
assert(result.status === 'failed', `forbidden gate should fail, got ${result.status}`);
'@
node $NodeTestPath (Join-Path $RepoRoot "tools\campaign_helpers.js")
if ($LASTEXITCODE -ne 0) { throw "userdata introspection classifier tests failed." }

Push-Location $WorkRoot
try {
  node (Join-Path $RepoRoot "tools\generate_access_docs.js")
  node (Join-Path $RepoRoot "tools\generate_campaign_docs.js") --state (Join-Path $WorkRoot "evidence\campaign_state.json") --out (Join-Path $WorkRoot "docs\CAMPAIGN_STATUS.md") --write-state --quiet
} finally {
  Pop-Location
}

Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Local Inventory Userdata Introspection Summary"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Local inventory userdata introspection status: local_inventory_userdata_introspection_confirmed"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Value kinds: AbilityMods=userdata, MeleeMods=userdata, Perks=userdata, Relics=userdata, WeaponMods=userdata"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Length operator attempted: AbilityMods=true, MeleeMods=true, Perks=true, Relics=true, WeaponMods=true"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Length operator results, if present, are metadata-only"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Array traversal attempted: no"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Array elements dereferenced: no"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "InventoryInfo read: no"
Assert-Contains -Path (Join-Path $WorkRoot "docs\RUNTIME_EVIDENCE_INDEX.md") -Expected "Enhancements read: no"
Assert-Contains -Path (Join-Path $WorkRoot "docs\CAMPAIGN_STATUS.md") -Expected "Local Inventory Userdata Introspection"
Assert-Contains -Path (Join-Path $WorkRoot "docs\CAMPAIGN_STATUS.md") -Expected "Length operator results"

Write-Host "CrabRuntimeProbe local inventory userdata introspection probe checks passed."
