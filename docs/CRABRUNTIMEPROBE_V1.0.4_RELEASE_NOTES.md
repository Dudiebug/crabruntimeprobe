# CrabRuntimeProbe v1.0.4 release notes

CrabRuntimeProbe v1.0.4 adds a controlled research workflow without weakening
the normal user's safety boundary. It is a read-only diagnostic tool, not a
CrabSync transport or an inventory synchronization release. Snapshot changes
and naturally observed callbacks are evidence only; they do not prove
persistence, remote visibility, replication direction, or write/apply safety.

## Two workflows

**Normal Play Guide** remains the default. It is hook-free, performs no runtime
function discovery, makes no RPC or unknown-method calls, and writes no gameplay
state. The live dashboard reports game availability, heartbeat age, sequence,
warming/stable readiness, current sampling category, active profile, and honest
not-observable explanations.

**Progressive Broad Observation** is the Advanced research workflow. A prepared
research launch combines the safe snapshot baseline, every compatible trusted
hook at its individually validated depth, and at most one unvalidated canary.
The canary is registered last. The dashboard may recommend the next candidate
or depth after classification, but it never advances within the same game
process.

The v1.0.4 package intentionally contains an empty trusted manifest and no
prearmed canary. `OnRep_IslandRewardRarity` is the first recommended candidate
at Depth 1 (registration only); starting it requires an explicit research-run
preparation. No v1.0.2 hook is trusted merely because it once registered.

## Validation depths

Candidates advance from Depth 0 static catalog checks through Depth 7 reviewed
passive evidence. Shallow depths cannot execute deeper operations. In
particular, context and arguments are excluded before their named depths, and
array traversal, inventory-element reads, `InventoryInfo`, enhancements, and
arbitrary UObject exploration remain excluded at Depth 5. Trust is always
candidate-, compatibility-, and depth-specific. See the
[campaign and research guide](CRABSYNC_FULL_CAMPAIGN_GUIDE.md) for the full
allowed/forbidden table and promotion threshold.

## Classification and recovery

The research runner records a bounded, identity-safe breadcrumb journal. Its
first callback record is written before context or argument inspection. A
deterministic classifier distinguishes registration failure, callback-boundary
failure, evidence failure, stale writer, interrupted/external termination,
clean shutdown, and a later unattributed crash. A last timestamp is never
treated as proof of causation. Crash-suspect and quarantined candidates cannot
rearm automatically.

Prepared research run IDs are one-shot. An atomic consumed-run marker prevents
a later process from silently reusing the same armed run; every retry or next
depth requires a new explicit dashboard preparation.

If a game or UE4SS build, generated catalog, hook catalog, callback
implementation/schema, or validation behavior changes, affected trust becomes
**Needs revalidation**. Registration alone is not promotion: the normal
threshold is three clean runs at the same depth, three matched natural
callbacks where practical, relevant host/joined-client coverage, a lifecycle
transition, compatible fingerprints, and no unmatched breadcrumb, correlated
crash, or new UE4SS callback error.

## Relic evidence remains independent

The snapshot path reports **local relic count increased** only after progressive
wrapper/count validation and a stable natural before/after observation. The
hook path separately reports **pickup callback observed** after validating
`ClientOnPickedUpPickup`. Neither label implies the other, and neither proves
persistence, remote visibility, or write/apply safety.

## Clean install or upgrade

1. Close Crab Champions and any older CrabRuntimeProbe dashboard.
2. Do not resume the retired v1.0.2 full-observe profile.
3. Extract the complete v1.0.4 ZIP into a new empty folder. Do not merge it with
   an older release folder.
4. Open `CrabRuntimeProbe.Dashboard.exe` and use **Start play guide** once. This
   installs the packaged payload and rewrites legacy research gates to safe
   hook-free defaults.
5. Keep the trusted pool empty and the canary unarmed unless you explicitly
   prepare an Advanced research run.
6. Preserve old evidence ZIPs outside the install directory if needed; do not
   copy old manifests, configuration, or runtime modules into v1.0.4.

The standalone UE4SS bundle follows the same clean-overlay rule: close the
game, back up evidence, copy the new bundle contents as a unit, and do not
restore old `hook_*`, `progressive_*`, or trusted-manifest state afterward.

## Package contents and verification

The canonical Windows package contains the .NET 8 WPF dashboard, `Payload/`
game overlay, campaign/checklist and coverage artifacts, all progressive
campaign defaults, versioned schemas, source-visible Lua runtime modules,
documentation, licenses/attributions, sanitized `build_info.txt`, and a
relative SHA-256 version manifest. The UE4SS package additionally contains the
required UE4SS support files and mods.

Release verification rejects an unsafe config, nonempty trusted manifest,
prearmed canary, incompatible campaign identities, missing module/schema, local
source path, raw object dump, runtime log, or development-only directory.
Evidence bundles use profile-aware safety fields: normal bundles require every
hook disabled, while Progressive bundles are accepted only with compatible,
depth-enforced controlled hooks, at most one canary, and all non-hook
mutation/discovery paths disabled.

Repository release commands:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 1.0.4
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-release.ps1 -BundlePath dist\CrabRuntimeProbe-v1.0.4-win-x64
```

UE4SS release commands:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-ue4ss-bundle.ps1 -CrabInvSyncRoot "C:\Path\To\CleanTemplate" -Version 1.0.4
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-ue4ss-bundle.ps1 -BundlePath dist\CrabRuntimeProbe-v1.0.4-UE4SS
```

## Validation status

Repository builds, contract tests, simulated reducer/journal fixtures, and
release-layout verification can validate the implementation without launching
Crab Champions. They cannot establish real-game hook safety. Host,
joined-client, island/lifecycle, natural callback, relic-count, crash-dump, and
UE4SS-version behavior remain field tests until performed on the target game
build. The [field checklist](CRABSYNC_FULL_CAMPAIGN_GUIDE.md#v104-verification-checklist)
labels each required check accordingly; this release does not claim in-game
validation that was not performed.

See [CHANGELOG.md](../CHANGELOG.md) for the release history and the
[2026-07-10 hook observer incident](INCIDENT_2026-07-10_HOOK_OBSERVER_CRASH.md)
for the reason the v1.0.2 bulk-hook profile remains retired.
