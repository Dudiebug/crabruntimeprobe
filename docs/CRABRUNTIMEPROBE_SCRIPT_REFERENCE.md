# CrabRuntimeProbe Script Reference

This map is based on the current `scripts/*.ps1` and `tools/*.js` files. Prefer these helpers over manually copying old folders into the game install.

## PowerShell Scripts

| Script | Category | What it does | When to run it | Inputs/parameters | Outputs | Touches game install | Changes RuntimeProbe config | Imports/regenerates docs |
|---|---|---|---|---|---|---|---|---|
| `Assert-CrabRuntimeProbeConfig.ps1` | validation | Shared functions for locating the repo, validating mod layout, validating safe config defaults, and writing `build_info.txt`. | Dot-sourced by other scripts. | Start path, config path, mod root, optional validation switches. | Validation errors or build info. | No direct install action. | No, validates defaults. | No. |
| `build-release.ps1` | packaging | Builds the canonical v1.1.0 self-contained Windows dashboard and payload, validates both generated catalogs, requires empty trust/no prearmed canary, includes readiness modules/schemas, writes sanitized build metadata and a relative hash manifest, verifies, then optionally zips. | Canonical release packaging. | `-OutputDir`, `-Version` (default `1.1.0`), `-Runtime`, `-Configuration`, `-NoZip`. | `dist/CrabRuntimeProbe-v1.1.0-win-x64` and optional ZIP. | No. | No, rejects unsafe defaults. | No. |
| `build-ue4ss-bundle.ps1` | packaging | Builds `dist/CrabRuntimeProbe-v<Version>-UE4SS`, copies UE4SS support plus progressive and readiness artifacts/schemas/runtime modules, writes clean-install guidance and sanitized manifests, verifies, then optionally zips. | Standalone UE4SS release packaging. | `-CrabInvSyncRoot`, `-OutputDir`, `-Version` (default `1.1.0`), `-NoZip`. | Bundle folder and optional zip under `dist/`. | No. | No, rejects unsafe defaults. | No. |
| `export-client-folder.ps1` | packaging | Exports a clean copy of repo `client/` to `dist/CrabRuntimeProbe-client`, stamps build info, optionally zips. | Manual copy/export testing. | `-OutputPath`, `-Zip`. | Export folder and optional zip. | No. | No, validates source/exported config. | No. |
| `import-latest-runtime-evidence.ps1` | import | Imports game-side results, regenerates access docs, and stages wiki docs. | After a play/test pass should become repo evidence. | `-From` results directory; defaults to Steam game results path. | New/updated `evidence/runtime/<session>/`, docs, `dist/wiki/`. | No. | No. | Yes. |
| `install-client-to-game.ps1` | install | Installs `client/Mods/CrabRuntimeProbe` into game `Mods`, updates `Mods/mods.txt`, stamps build info, validates config. | Before running game-side probes. | Required `GameBinPath`. | Installed mod under game bin and `build_info.txt`. | Yes. | No, copies source defaults. | No. |
| `quick-campaign-prepare.ps1` | campaign | Installs, selects the next runnable campaign phase from campaign state, sets installed config for that phase, writes `prepare_marker.json`, updates campaign docs/state. | Preferred guided phase workflow. | `-GameBin` optional default Steam path. | Installed config, marker, campaign state/docs. | Yes. | Yes, installed game config only. | Regenerates campaign docs. |
| `quick-campaign-collect.ps1` | campaign | Routes collection for the prepared campaign phase, validates stale/crash/raw identity issues, updates campaign state/docs. | After the manual game run from campaign prepare. | `-GameBin` optional default Steam path. | `diagnostic_summary.txt`, campaign state/docs, status. | Reads game install. | No. | Regenerates campaign docs. |
| `quick-collect-diagnostics.ps1` | collect | Wrapper around smoke collection using default Steam path. | After smoke/startup run. | None. | Diagnostic summary. | Reads game install. | No. | No. |
| `quick-equipment-property-prepare.ps1` | prepare | Prepares installed config for read-only `equipment-property-read`. | Before equipment property run. | None. | Installed config/marker. | Yes. | Yes, installed config. | No. |
| `quick-equipment-property-collect.ps1` | collect | Collects and validates equipment property evidence. | After equipment property run. | None. | Diagnostic summary. | Reads game install. | No. | No. |
| `quick-gameplay-observe-prepare.ps1` | prepare | Prepares observe mode with `executeDelay`. | Before passive gameplay context run. | None. | Installed config/marker. | Yes. | Yes, installed config. | No. |
| `quick-gameplay-observe-collect.ps1` | collect | Collects observe/context evidence and expects `Observe.Context`. | After gameplay observe run. | None. | Diagnostic summary. | Reads game install. | No. | No. |
| `quick-health-baseline-prepare.ps1` | health | Prepares broad read-only health baseline with `allowHealthProbes = true`. | Legacy/baseline health phase. | None. | Installed config/marker. | Yes. | Yes, installed config. | No. |
| `quick-health-baseline-collect.ps1` | health | Collects health baseline evidence. | After baseline health run. | None. | Diagnostic summary. | Reads game install. | No. | No. |
| `quick-health-playerstate-prepare.ps1` | health | Prepares player-state-scoped health read. | Preferred health snapshot phase. | None. | Installed config/marker. | Yes. | Yes, installed config. | No. |
| `quick-health-playerstate-collect.ps1` | health | Collects player-state health and runs stale/crash validation for `health-playerstate-read`. | After player-state health run. | None. | Diagnostic summary and validator output. | Reads game install. | No. | No. |
| `quick-health-playerstate-watch-prepare.ps1` | health | Prepares repeated player-state health watch. | Before 60 to 120 second health watch run. | None. | Installed config/marker. | Yes. | Yes, installed config. | No. |
| `quick-health-playerstate-watch-collect.ps1` | health | Collects watch evidence and validates `Health.PlayerState.Sample` without `CrabHC`. | After health watch run. | None. | Diagnostic summary and validator output. | Reads game install. | No. | No. |
| `quick-install-and-prepare.ps1` | install | Wrapper around `run-local-diagnostic-cycle.ps1 -PrepareSmoke` using the default Steam path. | Convenience path before the first smoke run. | None. | Installed mod/config and prepare marker. | Yes. | Yes, installed config for smoke. | No. |
| `quick-smoke-prepare.ps1` | prepare | Installs/prepares smoke startup with `tickDriver = none`. | First run. | None. | Installed config/marker. | Yes. | Yes, installed config. | No. |
| `quick-smoke-collect.ps1` | collect | Collects smoke startup artifacts. | After first menu smoke run. | None. | Diagnostic summary. | Reads game install. | No. | No. |
| `quick-tickdriver-prepare.ps1` | prepare | Prepares a selected tick driver, normally `executeDelay`; helper refuses unsafe HUD fallback. | After smoke passes. | Required `-TickDriver` from `none`, `registerTick`, `executeDelay`, `loopAsync`, `hud`. | Installed config/marker. | Yes. | Yes, installed config. | No. |
| `quick-tickdriver-collect.ps1` | collect | Collects tick-driver evidence. | After tick-driver run. | None. | Diagnostic summary. | Reads game install. | No. | No. |
| `run-local-diagnostic-cycle.ps1` | collect | Central prepare/collect engine. Installs, mutates installed config for explicit phases, clears stale diagnostics, reads JSONL/logs/manifests, writes `diagnostic_summary.txt`, and validates phase-specific safety. | Use for custom `-GameBin` or advanced workflow. | `-GameBin` plus exactly one prepare/collect switch. | Installed config, marker, summary, validation result. | Yes for prepare/install; reads for collect. | Yes on prepare modes. | No. |
| `test-campaign.ps1` | validation | Tests campaign plan/state/doc behavior and can invoke campaign prepare against a temp game bin. | Local validation; inspect before running because it may update campaign docs/state. | None. | Pass/fail and temp `dist` work. | Uses temp game bin. | Yes in temp install. | May update campaign docs/state. |
| `test-evidence-docs.ps1` | validation | Tests evidence doc generation and classifiers. | Local docs validation. | None. | Pass/fail. | No. | No. | Uses generated docs in test workspace. |
| `test-identity-probes.ps1` | identity | Validates identity probe gates, redaction, and docs/classification behavior. | Before changing identity probe/docs logic. | None. | Pass/fail. | No. | No. | Uses generated docs in test workspace. |
| `test-inventory-array-shallow-probes.ps1` | inventory | Validates shallow inventory array gate/default behavior and docs classification. | Before changing shallow inventory research. | None. | Pass/fail. | No. | No. | Uses generated docs in test workspace. |
| `test-inventory-array-shape-confirm-probes.ps1` | inventory | Validates shape-confirm gates and no-count/no-traversal markers. | Before changing shape-confirm research. | None. | Pass/fail. | No. | No. | Uses generated docs in test workspace. |
| `test-inventory-userdata-introspection-probes.ps1` | inventory | Validates userdata introspection gate, metadata-only behavior, and docs fields. | Before changing userdata introspection research. | None. | Pass/fail. | No. | No. | Uses generated docs in test workspace. |
| `test-packaging.ps1` | validation | End-to-end local packaging/install/prepare/collect validation with synthetic game bin/evidence. | Before release or helper changes. | Optional `-GameBinPath`. | Pass/fail and temp `dist` work. | Uses temp/specified test bin. | Yes in test install. | No production docs import. |
| `test-resource-visibility-probes.ps1` | validation | Validates resource visibility gates/classification and generated docs in a test workspace. | Before changing resource visibility logic. | None. | Pass/fail. | No. | No. | Uses generated docs in test workspace. |
| `validate-latest-crash-bundle.ps1` | validation | Checks installed config, latest manifest/results/evidence, UE4SS log session/commit, crash folder, prepare marker, and stale artifact conditions. | After prepared health/watch/campaign runs. | `-GameBin`, `-ExpectedProbeSet`, `-ExpectedTickDriver`, `-ExpectedMode`, `-RequirePreparedRun`. | Validator report and exit code. | Reads game install. | No. | No. |
| `verify-installed-client.ps1` | verify | Validates installed mod files, safe config defaults, and `Mods/mods.txt`. | After install. | Required `GameBinPath`. | Pass/fail plus installed paths. | Reads game install. | No. | No. |
| `verify-release.ps1` | verify | Verifies canonical layout, dashboard version, progressive artifacts/schemas/modules, campaign identities, relative hashes, sanitized metadata, empty trust/no canary, and forbidden-file exclusions. | After canonical build or extracted ZIP. | Required `-BundlePath`. | Pass/fail. | No. | No. | No. |
| `verify-ue4ss-bundle.ps1` | verify | Verifies UE4SS layout/support mods, v1.1.0 readiness identities, retained progressive identities, safe defaults, empty trust/no canary, hashes, sanitized metadata, and forbidden-file exclusions. | After UE4SS build or extracted ZIP. | Optional `BundlePath`; defaults latest bundle in `dist`. | Pass/fail. | No. | No. | No. |

## Node Tools

| Tool | What it does | Inputs | Outputs |
|---|---|---|---|
| `tools/import_runtime_evidence.js` | Copies session-named game artifacts into `evidence/runtime/<session>/`. | `--from <results dir>`. | Normalized `access_evidence.jsonl`, `probe_results.jsonl`, `session_manifest.json`, optional `diagnostic_summary.txt`. |
| `tools/generate_access_docs.js` | Builds current evidence docs from imported runtime evidence. | `evidence/runtime/`, optional objectdump index. | `RUNTIME_EVIDENCE_INDEX.md`, `SAFE_ACCESS_MATRIX.md`, `SYMBOL_ACCESS_REFERENCE.md`, `KNOWN_UNSAFE_PATHS.md`, `UNTESTED_ACCESS_PATHS.md`. |
| `tools/build_wiki_docs.js` | Stages wiki pages from `wiki-src/` plus generated docs. | `wiki-src/`, `docs/`. | `dist/wiki/*.md`. |
| `tools/generate_campaign_docs.js` | Renders campaign status from plan/state and can reconcile state. | `--state`, `--out`, `--write-state`, `--quiet`. | `docs/CAMPAIGN_STATUS.md`, optional state update. |
| `tools/update_campaign_state.js` | Marks campaign phases prepared/collected. | `init`, `prepare`, `collect` plus phase/status args. | `evidence/campaign_state.json`. |
| `tools/generate_docs.js` | Runs objectdump parse and probe candidate generation. | None. | Objectdump docs and candidates. |
| `tools/parse_objectdump.js` | Parses supported files under `objectdump/`. | `objectdump/*.txt`, `*.part*`, `*.md`. | `objectdump/objectdump_index.json`, `docs/OBJECTDUMP_INDEX.md`. |
| `tools/generate_probe_candidates.js` | Generates objectdump-backed probe candidates. | `objectdump/objectdump_index.json`. | `docs/PROBE_CANDIDATES.md`. |
| `tools/generate_progressive_hook_catalog.js` | Deterministically derives the 111-candidate hook catalog, empty trusted/quarantine state, validation ledger, safe defaults, and Lua catalog from the complete coverage catalog. Use `--validate` for release checks. | `campaign/crabsync_coverage_catalog.json` plus generator policy. | Progressive campaign JSON artifacts and `client/.../research_hook_catalog.lua`. |
| `tools/summarize_probe_results.js` | Summarizes selected result JSONL and optional UE4SS log. | `--results <file>` repeated, optional `--ue4ss-log`. | `PROBE_RESULTS.md`, `SAFE_ACCESS_MATRIX.md`, `CRASH_PHASE_SUMMARY.md`. |
| `tools/package_release.js` | Node packager for the v1.1.0 UE4SS bundle from a clean support template; it validates the staged bundle before zipping. | `--template`, optional `--out`, `--keep-staging`. | `dist/CrabRuntimeProbe-v1.1.0-UE4SS.zip`, temporary staging, sanitized external manifest. |
| `tools/campaign_helpers.js` | Shared campaign/evidence classifiers. | Required by other tools. | Module exports. |
| `tools/identity_helpers.js` | Identity parsing helpers. | Required by other tools/tests. | Module exports. |

## Recommended Normal Workflow

Run `quick-smoke-prepare`, launch menu, quit, then `quick-smoke-collect`. Next prove `executeDelay` with `quick-tickdriver-prepare -TickDriver executeDelay` and `quick-tickdriver-collect`. Then use README or campaign order for observe, equipment, health, identity, resource, and inventory shape phases.

## v1.1.0 Readiness and Release Workflow

Use the dashboard’s CrabSync Readiness Campaign only for the paired,
local-only host/join field test. It derives an opaque pair ID from a short
locally entered code, captures bounded local scalar/lifecycle evidence, and
keeps remote visibility, inventory, transport, and apply gates unresolved.
The packaging and verification commands now target v1.1.0 and require the
readiness modules plus their three closed schemas.

## v1.0.4 Progressive Research Baseline

Use the dashboard, not a script or free-form hook path, to prepare exactly one
future Progressive Broad Observation launch. The package defaults remain
hook-free with an empty trusted manifest and no armed canary. The dashboard may
recommend `OnRep_IslandRewardRarity` at Depth 1, but preparation is explicit and
the next candidate/depth cannot advance in the same game process.

Before packaging, validate both deterministic catalogs:

```powershell
node tools\generate_crabsync_coverage_catalog.js --validate
node tools\generate_progressive_hook_catalog.js --validate
```

Then build/extract/verify the canonical v1.1.0 archive or the UE4SS archive with
the matching scripts above. Both verifiers reject pretrusted or prearmed state.

## Evidence Import Workflow

After a run is worth preserving, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\import-latest-runtime-evidence.ps1
```

This imports the latest game-side artifacts, regenerates evidence docs, and builds wiki staging. Do not run it casually if you do not want current generated evidence summaries updated.

## Troubleshooting No JSONL

Check `UE4SS.log` for CrabRuntimeProbe startup, config path, selected tick driver, result paths, and `tick source registered`. Confirm `Mods\mods.txt` contains `CrabRuntimeProbe : 1`. Check both `Mods\CrabRuntimeProbe\Scripts\results\` and the fallback `Mods\CrabRuntimeProbe\Scripts\`. For diagnostics, keep `mode = observe`, enable heartbeat/self-test only for the diagnostic pass, and leave `allowHudTickHook = false`.

## Default Path Versus Custom GameBin

Quick scripts use the default Steam path. For another install, call `run-local-diagnostic-cycle.ps1 -GameBin "<custom Win64 path>"` with the matching prepare/collect switch, or pass `-GameBin` to campaign scripts.
