# CrabRuntimeProbe v1.1.0 release notes

CrabRuntimeProbe v1.1.0 adds the **CrabSync Readiness Campaign**: a
deliberately narrow, read-only two-machine field-test workflow for a host and a
joined client. It gathers evidence needed to decide which future CrabSyncV2
questions remain open. It is not CrabSync, does not synchronize gameplay, and
does not implement write/apply behavior.

## What this release collects

Each machine runs its own local campaign. The host prepares a short correlation
code and the joined client enters it; the dashboard derives an opaque pair ID
locally. Only that opaque ID is placed in the two campaign manifests and the
offline bundle-combination report. The human-entered code is never persisted in
runtime status, JSONL evidence, exports, release metadata, or package files.

The campaign can collect only reviewed local PlayerState scalar categories:

- health/max-health values when available;
- crystals;
- slots; and
- equipment fingerprints.

It also writes bounded peer-snapshot and terminal-lifecycle records. The
terminal record is written before a normal stop or a stable lifecycle scope
reset so that a later report can distinguish a clean terminal transition from
a missing terminal observation.

## What it does not collect or prove

v1.1.0 is intentionally local-only. It does **not** enumerate remote
PlayerState instances, so remote visibility remains deferred. Matching a host
and joined-client bundle proves only that two local sessions used the same
opaque pair ID; it is not replication proof.

The campaign also leaves these gates unresolved:

- inventory item, enhancement, and array/count evidence;
- transport or carrier behavior between machines;
- exact replication callback causation;
- persistence/UI-follow-up behavior; and
- every write/apply or RPC candidate.

No readiness result authorizes shared inventory, health synchronization, or a
CrabSync implementation. A gate marked complete means only that the recorded
read-only evidence met its stated condition.

## Safety boundary

The profile ships disabled and is rejected unless the dashboard prepares the
complete paired contract. Its runtime path is hook-free and does not use
runtime class discovery, `FindAllOf`, remote-object enumeration, inventory
traversal, writes, mutating RPCs, raw player identity, networking, sockets,
relays, or listeners. The only durable runtime output is local append-only
diagnostic evidence.

The existing **Normal Play Guide** stays hook-free. The v1.0.4
**Progressive Broad Observation** material remains available as a separate,
explicit future-process research path with an empty trusted pool and no
prearmed canary; it is not enabled by the readiness campaign.

## Field test: one host and one joined client

1. Install the complete v1.1.0 release on both machines. Close the game and
   any old dashboard first; extract into a new empty folder rather than merging
   an older release.
2. On the host, open the dashboard’s readiness preparation flow and retain the
   short correlation code only long enough to enter it on the joined client.
3. On the joined client, enter that code and prepare the matching campaign.
   Confirm both dashboards show the readiness profile, a current heartbeat,
   and only local scalar/lifecycle channels.
4. Start the game, host/join the same lobby, and play one ordinary run. Do not
   turn on Advanced progressive research, inventory options, hook options, or
   any write/RPC configuration.
5. End both games normally, finish collection in both dashboards, export the
   two evidence bundles, and combine them offline.
6. Treat the report literally. Local scalar and lifecycle gates may have
   evidence; remote visibility, inventory, transport, and apply gates should
   remain waiting or blocked in this release.

If the game crashes, preserve the exact two exported bundles, `UE4SS.log`, and
any crash artifacts before starting another run. A terminal lifecycle row or
last completed breadcrumb can narrow the time window, but it cannot prove what
caused a crash. If a hard exit prevented the final write, the report must remain
inconclusive rather than guessing.

## Packaging and verification

Both package forms include the readiness modules and closed schemas:

- `peer_sampler.lua`
- `readiness_observe_coordinator.lua`
- `readiness-campaign-manifest-v1.schema.json`
- `peer-snapshot-v1.schema.json`
- `terminal-lifecycle-v1.schema.json`

Build and verify the canonical archive with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 1.1.0
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-release.ps1 -BundlePath dist\CrabRuntimeProbe-v1.1.0-win-x64
```

For the standalone UE4SS overlay:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-ue4ss-bundle.ps1 -CrabInvSyncRoot "C:\Path\To\CleanTemplate" -Version 1.1.0
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-ue4ss-bundle.ps1 -BundlePath dist\CrabRuntimeProbe-v1.1.0-UE4SS
```

The verifiers require disabled readiness defaults in the shipped config, verify
the closed schema identities in the hash manifest, and reject release payloads
that drift onto hooks, discovery, inventory stages, raw identities, or unsafe
development artifacts.
