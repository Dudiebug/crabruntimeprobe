# CrabRuntimeProbe

CrabRuntimeProbe is a standalone UE4SS Lua diagnostic/research mod for Crab Champions.
It helps reverse engineer **safe runtime access rules** by combining object dump presence with in-session probe observations.

## Why this exists

UE4SS object dumps show what symbols exist, but not when/where access is safe. This project captures runtime facts with paced, breadcrumbed probing.

## Documentation and CrabSyncV2 planning

The deep documentation index starts at [docs/README.md](docs/README.md).

RuntimeProbe operation docs explain the research tool and workflow:
[Field Guide](docs/CRABRUNTIMEPROBE_FIELD_GUIDE.md) and
[Script Reference](docs/CRABRUNTIMEPROBE_SCRIPT_REFERENCE.md).

Generated evidence docs remain the source for current runtime proof:
[Runtime Evidence Index](docs/RUNTIME_EVIDENCE_INDEX.md),
[Safe Access Matrix](docs/SAFE_ACCESS_MATRIX.md),
[Perk DataAsset Catalog](docs/PERK_DATAASSET_CATALOG.md),
[Weapon Mod DataAsset Catalog](docs/WEAPONMOD_DATAASSET_CATALOG.md),
[Ability Mod DataAsset Catalog](docs/ABILITYMOD_DATAASSET_CATALOG.md),
[Melee Mod DataAsset Catalog](docs/MELEEMOD_DATAASSET_CATALOG.md),
[Relic DataAsset Catalog](docs/RELIC_DATAASSET_CATALOG.md),
[Perk Catalog JSON](docs/data/perk_dataasset_catalog.latest.json),
[Weapon Mod Catalog JSON Status](docs/data/weaponmod_dataasset_catalog.latest.json),
[Probe Candidates](docs/PROBE_CANDIDATES.md),
[Probe Results](docs/PROBE_RESULTS.md), and
[Crash Phase Summary](docs/CRASH_PHASE_SUMMARY.md).

CrabSyncV2 docs are future design constraints derived from evidence, not
RuntimeProbe behavior:
[Evidence Baseline](docs/CRABSYNCV2_EVIDENCE_BASELINE.md),
[Design Rules](docs/CRABSYNCV2_DESIGN_RULES.md),
[Open Questions](docs/CRABSYNCV2_OPEN_QUESTIONS.md),
[Crash Safety](docs/CRABSYNCV2_CRASH_SAFETY.md),
[Implementation Blueprint](docs/CRABSYNCV2_IMPLEMENTATION_BLUEPRINT.md),
[Research Roadmap](docs/CRABSYNCV2_RESEARCH_ROADMAP.md), and
[Glossary](docs/CRABSYNCV2_GLOSSARY.md).

## Install

For normal use, extract the `CrabRuntimeProbe-v*-win-x64.zip` release and open
`CrabRuntimeProbe.Dashboard.exe`. The dashboard detects the game, validates the
packaged payload, and prepares the read-only campaign without Git, Node, or a
development SDK.

The Crab Champions Win64 game bin path is usually:

```text
C:\Program Files (x86)\Steam\steamapps\common\Crab Champions\CrabChampions\Binaries\Win64
```

Repository developers can still install the client payload directly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-client-to-game.ps1 "C:\Program Files (x86)\Steam\steamapps\common\Crab Champions\CrabChampions\Binaries\Win64"
```

Or export it for manual inspection:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\export-client-folder.ps1
```

Verification:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-installed-client.ps1 "C:\Program Files (x86)\Steam\steamapps\common\Crab Champions\CrabChampions\Binaries\Win64"
```

`allowHudTickHook` must remain `false` unless you are intentionally testing the
unsafe HUD fallback that crashed immediately in this Crab Champions/UE4SS setup.
The source default remains `tickDriver = none`; the first confirmed safe
diagnostic tick driver for this UE4SS/Crab Champions setup is `executeDelay`.

## Minimal user workflow

1. Open the dashboard on both computers.
2. Select **Host** on one and **Joined Client** on the other.
3. Confirm the game installation, then click **Prepare Campaign** and **Start
   Monitoring** on both.
4. Open Crab Champions, create/join the same lobby, and play one deliberate run
   while following the dashboard's next-action checklist.
5. Close both games normally and click **Finish & Collect**.
6. Export the two evidence ZIPs and use **Combine Bundles** for the offline
   host/client readiness report.

See the [full campaign guide](docs/CRABSYNC_FULL_CAMPAIGN_GUIDE.md) for the exact
multiplayer actions, status meanings, crash recovery, coverage rules, and safety
boundary. A successful passive campaign is read/natural-observation evidence;
it is never write/apply proof.

## Evidence-driven documentation pipeline

RuntimeProbe writes three compact runtime artifacts during tests:

- `probe_results_<session>.jsonl`
- `access_evidence_<session>.jsonl`
- `session_manifest_<session>.json`

The UE4SS Lua mod only writes append-only evidence and session context. It does
not generate the full GitHub Wiki in-game. Repo tools import copied game
evidence, update docs under `docs/`, and stage wiki pages under `dist/wiki/`.
GitHub Wiki publishing comes later.

After a play/test pass, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\import-latest-runtime-evidence.ps1
```

This imports the latest game evidence into `evidence/runtime/`, regenerates the
safe access docs, regenerates the perk catalog JSON/CSV/Markdown exports, and
stages generated wiki Markdown. Repo docs remain the source of truth; staged
wiki files are derived output.

After `equipment-property-read` passes, run the next read-only health baseline
phase:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\quick-health-baseline-prepare.ps1
```

Launch Crab Champions, start a solo run, stay in-world 30 to 60 seconds, quit,
then collect:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\quick-health-baseline-collect.ps1
```

Then import the resulting evidence and rebuild docs/wiki staging:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\import-latest-runtime-evidence.ps1
```

`health-baseline-read` is read-only. It exists to prove health/max-health fields
for CrabInvSync v2 research without writing health, calling health RPCs, reading
deep arrays, or touching `InventoryInfo`. The first health baseline evidence
showed that unscoped `FindFirstOf.CrabHC` can point at non-player health
components: session `20260505T002614Z` found
`BP_Destructible_ChaoticBarrel10.HC`. Do not use unscoped `CrabHC` as the
CrabInvSync v2 player health source.

The safer player-scoped health path is currently `CrabPC -> PlayerState ->
CrabPS`. In solo evidence, `CrabPS.HealthInfo.CurrentHealth`,
`CrabPS.HealthInfo.CurrentMaxHealth`, and `CrabPS.BaseMaxHealth` returned
`250.0`, while `CrabPS.MaxHealthMultiplier` returned `1.0`. This supports a
solo base-health value of 250, but it does not prove multiplayer max-health math
yet; the 250 HP per player theory still needs multiplayer evidence.

After the broad baseline phase, prefer the player-state-only health phase:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\quick-health-playerstate-prepare.ps1
```

Launch Crab Champions, start a solo run, stay in-world 30 to 60 seconds, quit,
then collect:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\quick-health-playerstate-collect.ps1
```

Then import the resulting evidence and rebuild docs/wiki staging:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\import-latest-runtime-evidence.ps1
```

After the single player-state snapshot, run the read-only health watch phase to
capture time-series evidence from the same safe path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\quick-health-playerstate-watch-prepare.ps1
```

User run:

1. Launch Crab Champions.
2. Start a solo run.
3. Stay in-world for 60 to 120 seconds.
4. If a max-health-changing pickup/perk naturally appears, pick it up.
5. Otherwise do not force it.
6. Quit.

Then collect and validate:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\quick-health-playerstate-watch-collect.ps1
```

Then import the resulting evidence and rebuild docs/wiki staging:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\import-latest-runtime-evidence.ps1
```

`health-playerstate-watch` repeatedly emits one combined
`Health.PlayerState.Sample` row with `CurrentHealth`, `CurrentMaxHealth`,
`BaseMaxHealth`, and `MaxHealthMultiplier`. It exists to gather time-series
evidence before any CrabInvSync v2 health math is designed. It is read-only,
uses `CrabPC -> PlayerState -> CrabPS -> HealthInfo`, avoids `CrabHC`, avoids
item arrays and `InventoryInfo`, and does not write or call RPCs. Multiplayer
health scaling remains unproven until multiplayer watch evidence exists; do not
infer production health math from one static solo snapshot.

Player-owned `CrabHC` discovery is a separate research phase. The
`health-hc-discovery-read` probe set currently records `FindAllOf` availability
only; capped candidate traversal and ownership linkage stay deferred until that
path is reviewed as its own explicit read-only test.

The quick scripts use the default Steam path:

```text
C:\Program Files (x86)\Steam\steamapps\common\Crab Champions\CrabChampions\Binaries\Win64
```

For a non-default install path, use the full diagnostic cycle script:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local-diagnostic-cycle.ps1 -GameBin "C:\Program Files (x86)\Steam\steamapps\common\Crab Champions\CrabChampions\Binaries\Win64" -PrepareSmoke
```

After launching and quitting:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-local-diagnostic-cycle.ps1 -GameBin "C:\Program Files (x86)\Steam\steamapps\common\Crab Champions\CrabChampions\Binaries\Win64" -CollectSmoke
```

## Quick local test install

Install the latest client mod directly into a local Crab Champions UE4SS
installation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-client-to-game.ps1 "C:\Program Files (x86)\Steam\steamapps\common\Crab Champions\CrabChampions\Binaries\Win64"
```

Verify the installed files, `Mods\mods.txt`, and safe observe defaults:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-installed-client.ps1 "C:\Program Files (x86)\Steam\steamapps\common\Crab Champions\CrabChampions\Binaries\Win64"
```

During CrabRuntimeProbe testing, disable `CrabInventorySync` so the probe log is
easy to reason about. The install helper does not delete or disable other mods.

If no JSONL appears, check `UE4SS.log` for `CrabRuntimeProbe`, confirm it has
`tick source registered`, then temporarily set `debugTickHeartbeat = true` and
`debugWriterSelfTest = true`. Confirm `Mods\mods.txt` has
`CrabRuntimeProbe : 1`, check
`Mods\CrabRuntimeProbe\Scripts\results\`, and then check the fallback
`Mods\CrabRuntimeProbe\Scripts\`. If the log says
`tick source registered: HUD ReceiveDrawHUD`, the config is unsafe for normal
Crab Champions testing; set `allowHudTickHook = false`.

## First safe run

Use:

- `mode = observe`
- `tickDriver = none`
- `observeIntervalTicks = 10`
- `allowHudTickHook = false`
- `probeSet = shallow-core`
- `allowDeepArrayProbes = false`
- `allowInventoryInfoProbes = false`
- `allowHealthProbes = false`
- `allowIdentityProbes = false`
- `allowRawIdentityEvidence = false`
- `allowResourceVisibilityProbes = false`
- `allowInventoryArrayShallowProbes = false`
- `allowWriteProbes = false`
- `allowRpcProbes = false`

Then:

1. Launch game.
2. Sit in menu long enough to clear `startupWarmupTicks`.
3. Enter lobby.
4. Start solo island.
5. Do not enable active mode for the first in-game test.
6. Quit.
7. Collect UE4SS.log and probe JSONL.
8. Run summarizer.

## Modes

- `observe`: passive low-risk context sampling while playing.
- `active`: controlled one-probe-at-a-time execution with pacing and gates.

Observe mode does not run the curated probe registry. It writes `Observe.Context`
rows only, containing timestamp/session/tick/mode, context and role guesses,
lifecycle state, and safe `CrabPC`/`PlayerState` existence and validity checks.
It waits for `startupWarmupTicks`, then writes only every
`observeIntervalTicks`.

Active mode waits for startup warmup and context stability, runs at most one
registry probe per interval, emits before/after breadcrumbs, respects safety
gates, and writes JSONL results.

## Result files

- Primary: `client/Mods/CrabRuntimeProbe/Scripts/results/*.jsonl`
- Fallback: `client/Mods/CrabRuntimeProbe/Scripts/*.jsonl`

## Troubleshooting: no probe_results JSONL appears

Check `UE4SS.log` for `tick source registered`. A normal startup should print
the config path, mode, primary result path, fallback result path, and selected
tick source. The HUD `ReceiveDrawHUD` hook is disabled by default because it
caused immediate startup crashes in Crab Champions. Leave
`allowHudTickHook = false` unless you are intentionally debugging that hook.

For a diagnostic observe-mode run, temporarily set:

- `mode = observe`
- `writeJsonlResults = true`
- `debugTickHeartbeat = true`
- `debugWriterSelfTest = true`
- `allowHudTickHook = false`

Then launch the game and check for `tick heartbeat` lines every 100 ticks. The
writer self-test should create one `Debug.WriterSelfTest` JSONL row without
touching UE objects.

Check both result locations:

- Primary: `Mods/CrabRuntimeProbe/Scripts/results/probe_results_*.jsonl`
- Fallback: `Mods/CrabRuntimeProbe/Scripts/probe_results_*.jsonl`

If the primary directory is unavailable, `UE4SS.log` should include
`primary result path unavailable; using fallback`. If neither path can be
written, it should include `ERROR: result write failed for primary and fallback`.
Keep `mode = observe` for this diagnostic pass so active probes stay disabled.
If no JSONL appears, check the tick source line in `UE4SS.log`; heartbeat lines
confirm that the selected scheduler is actually firing.

By default `tickDriver = none`, so the first smoke run should not register any
tick source. Set exactly one tick driver only for an isolated follow-up run:
`executeDelay` is the first confirmed safe diagnostic tick driver. Do not test
`registerTick`, `loopAsync`, or `hud` by default. The `hud` driver remains
blocked by the local helper because `allowHudTickHook = false` is the safe
default, and HUD `ReceiveDrawHUD` must remain disabled unless intentionally
testing that known-unsafe path.

## Release packaging

The canonical release is a self-contained Windows dashboard plus the
source-visible UE4SS payload under `client/`. It does not require an external
CrabInvSync template and excludes CrabInvSync gameplay code, servers, raw object
dumps, runtime logs, `.git`, and `node_modules`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 1.0.0
```

Build and verify the staging directory without creating a ZIP:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 1.0.0 -NoZip
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-release.ps1 -BundlePath dist\CrabRuntimeProbe-v1.0.0-win-x64
```

The ZIP contains the WPF dashboard, `Payload/` game overlay, campaign/checklist
and coverage data, schemas, user docs, licenses/attributions, and a relative
SHA-256 version manifest. The dashboard installs `Payload/` into the selected
game directory; users do not manually copy the full release there.

## Exporting the client folder for manual testing

To export only the latest CrabRuntimeProbe client/mod files for manual testing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\export-client-folder.ps1
```

The export script writes a clean copy of the current repo's `client` folder to:

```text
dist\CrabRuntimeProbe-client
```

For a full manual UE4SS client copy, copy the contents of that folder into:

```text
C:\Program Files (x86)\Steam\steamapps\common\Crab Champions\CrabChampions\Binaries\Win64
```

For a mod-only manual copy, copy
`dist\CrabRuntimeProbe-client\Mods\CrabRuntimeProbe` into the game folder's
`Mods\CrabRuntimeProbe` path. Prefer the install script when possible because
it validates the source config, writes `Scripts\build_info.txt`, and verifies
the installed config after copying.

The bundle root contains UE4SS runtime files, `INSTALL.txt`, the
CrabRuntimeProbe README/license, and `Mods/`. The copied UE4SS support mods are:

- `client/UE4SS.dll`
- `client/dwmapi.dll`
- `client/UE4SS-settings.ini`
- `client/imgui.ini`
- `Mods/BPML_GenericFunctions`
- `Mods/BPModLoaderMod`
- `Mods/Keybinds`
- `Mods/shared`

These are UE4SS support files sourced from the CrabInvSync template, not
CrabRuntimeProbe gameplay code. The generated `Mods/mods.txt` enables only
these support mods and `CrabRuntimeProbe`. CrabRuntimeProbe remains observe-mode
by default; generated objectdump candidates are documentation only and do not
run automatically.

## Docs generation

The object dump only proves a symbol exists. It does not prove that reading or
writing it is safe at runtime.

Place Crab Champions UE4SS object dump files under `objectdump/`. The dump may
be split across many files; include every part before generating docs. Supported
input names are `*.txt`, `*.part*`, and `*.md`. The parser intentionally ignores
only `objectdump/README.md` and the generated `objectdump/objectdump_index.json`.

Objectdump workflow:

1. Copy every objectdump part into `objectdump/`.
2. Run `node tools/parse_objectdump.js`.
3. Open `docs/OBJECTDUMP_INDEX.md` and verify `All discovered dump parts scanned: yes`.
4. Run `node tools/generate_probe_candidates.js`.
5. Review `docs/PROBE_CANDIDATES.md`; generated candidates are documentation only.
6. Run `node tools/generate_docs.js` to regenerate both objectdump docs and candidate docs.

Full CrabSync coverage catalog workflow (the complete dump is mandatory):

```powershell
node tools/generate_crabsync_coverage_catalog.js --dump "<UE4SS_ObjectDump.txt>" --reference "<CrabInvSync objectdump notes directory>"
node tools/generate_crabsync_coverage_catalog.js --validate
```

The full scan hashes and reads every dump line, merges all checked-in docs and
runtime evidence, and generates the dashboard JSON/CSV/Markdown plus the exact
Lua passive-hook allowlist. Missing dump provenance is an error, not a silent
skip. After importing a new evidence session, refresh runtime statuses without
changing the recorded dump denominator:

```powershell
node tools/generate_crabsync_coverage_catalog.js --refresh-runtime
```

Runtime result workflow:

- `node tools/summarize_probe_results.js --results <path> [--results <another.jsonl>] [--ue4ss-log <path>]`
- The summarizer writes `docs/PROBE_RESULTS.md`, `docs/SAFE_ACCESS_MATRIX.md`, and `docs/CRASH_PHASE_SUMMARY.md`.

Interpretation rules:

- `objectdump discovered` means the symbol exists in static dump data.
- `runtime confirmed` remains false until ProbeRunner results prove a specific access path.
- Local inventory array visibility and remote PlayerState inventory array visibility are separate research questions. `local-inventory-array-shallow-read` checks local `CrabPC -> PlayerState -> CrabPS` array shapes/counts and slot scalars; `local-inventory-array-shape-confirm` confirms userdata shape visibility without counts; `local-inventory-userdata-introspection` is limited to wrapper metadata such as type, redacted tostring, metatable keys, and guarded length-operator metadata.
- Inventory item metadata is still untested. A length-operator result, if present, is metadata-only and does not prove count traversal, element traversal, item contents, or item sync. Remote inventory array counts remain partial/unresolved in the latest resource visibility evidence.
- Deep inventory, InventoryInfo, health, write, and RPC candidates stay disabled unless a later explicit runtime research pass enables the relevant safety gate.

## Crash collection

After a crash collect:

- `UE4SS.log`
- latest probe `.jsonl`
- active `config.txt`

`tools/summarize_probe_results.js` infers `CRASH_SUSPECT` from final unmatched breadcrumb.
