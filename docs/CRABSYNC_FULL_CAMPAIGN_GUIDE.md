# CrabSync Full-Observe Campaign Guide

`crabsync-full-observe` is a coordinated, read-only evidence campaign. It runs
safe passive observations together so a host and a joined client can collect a
useful CrabSync evidence set during one deliberate multiplayer run. It is not a
CrabSync transport, gameplay authority, or write test.

## Safety boundary

The campaign never writes gameplay properties, calls mutating RPCs, invents
values, uses a gameplay field as a payload carrier, or sends state between the
two computers. It does not enable the known-unsafe HUD tick hook. Player and
machine identity is represented only by random local identifiers or redacted
fingerprints.

Passive observation means RuntimeProbe listens when the game naturally calls a
function. A registered hook is only discovery evidence. A checklist item is not
confirmed until a qualifying natural call or state transition is recorded.
Natural-call, argument, ownership, lifecycle, visibility, UI, persistence, and
future write-safety proof remain separate statuses.

## Prepare both computers

1. Extract the same CrabRuntimeProbe release on both computers.
2. Open `CrabRuntimeProbe.Dashboard.exe` on each computer.
3. Confirm or select the Crab Champions installation. The selected directory
   must contain `CrabChampions-Win64-Shipping.exe`.
4. Select **Host** on the computer that will create the lobby and **Joined
   Client** on the computer that will join it.
5. Use the same campaign name on both computers. Each dashboard still creates
   a different opaque machine ID and session ID.
6. Click **Prepare Campaign**, then **Start Monitoring**.

Prepare is idempotent. It installs the packaged RuntimeProbe payload, archives
older transient results instead of erasing canonical evidence, writes the safe
full-observe profile, and creates a new campaign generation. Resume reattaches
to an interrupted generation without clearing its evidence.

## Deliberate multiplayer run

Click **Open Crab Champions** on both computers, then perform the following
actions naturally. Skip anything the run does not offer; never use a debug or
synthetic path merely to complete a box.

1. The host creates a lobby and the joined client joins it. Wait until both
   dashboards show a stable PlayerState and two or more visible players.
2. Start a run. Pick up at least one weapon mod, ability mod, melee mod, perk,
   and relic between the two players.
3. Pick up a second copy of an item. Observe an item removal, drop, salvage, or
   replacement if the game offers one.
4. Buy a weapon-mod, ability-mod, melee-mod, or perk slot. Record the cost and
   pre/post values; more slot categories are better.
5. Gain crystals, then spend crystals in a shop or totem. Use a shop reroll if
   available.
6. Use an anvil and an upgrade totem if they appear.
7. Take damage, then heal. Observe armor if present. Allow one player to die and
   respawn or revive when practical.
8. Complete an island, choose a reward, interact with a chest/shop, and travel
   through a portal.
9. Have the joined client leave and reconnect once. Wait for stable state after
   each lifecycle transition.
10. Close both games normally.

The dashboard continuously shows the next useful action. Inventory access
advances independently for all five categories through a strict 14-stage
ladder. A stage advances only after its prerequisite is clean. An unsupported
or failed category trips only that category's circuit breaker; unrelated
passive observations continue.

## Dashboard controls

- **Detect Installation** searches Steam libraries without retaining Steam
  account data.
- **Select Installation** confirms a nonstandard game location.
- **Prepare Campaign** installs the payload and writes a safe campaign
  generation.
- **Start Monitoring** watches completed atomic status snapshots and canonical
  append-only evidence.
- **Open Crab Champions** launches Steam app `774801`.
- **Stop Campaign Safely** writes a diagnostics-only stop request. It does not
  kill the game or change gameplay.
- **Finish & Collect** finalizes status, collects RuntimeProbe/UE4SS/crash
  diagnostics, evaluates the checklist and catalog, and builds one ZIP for the
  local computer.
- **Export Evidence Bundle** copies the completed ZIP to a chosen folder.
- **Open Evidence Folder** opens the local collection directory.
- **Diagnostic Summary** shows safety, cleanliness, crash, and missing-action
  results.
- **Reset Campaign** archives the current transient session after confirmation
  and creates a clean generation.
- **Resume Campaign** reattaches to an interrupted generation.
- **Copy Support Summary** copies a short redacted status summary.
- **Combine Bundles** validates one host ZIP and one joined-client ZIP and
  creates an offline correlation report. It never starts a relay or listener.

## Status meanings

| Status | Meaning |
|---|---|
| Not observed | No qualifying evidence exists. Hook registration does not count. |
| In progress | A prerequisite or one side of a pre/post observation exists. |
| Partial | Useful evidence exists, but one or more required dimensions are missing. |
| Confirmed | Qualifying clean evidence satisfies this checklist predicate. |
| Unsupported | The tested read-only mechanism cannot support the path. |
| Blocked by prerequisite | A safer earlier inventory/lifecycle requirement is incomplete. |
| Crash-suspect | A crash or unmatched breadcrumb may be associated with the observation. |
| Dirty evidence | The data is incomplete, role-mismatched, stale, malformed, or otherwise unsuitable for promotion. |
| Not applicable | Product policy or the current role/run makes the item intentionally inapplicable. |

The heartbeat is stale when no newer completed status slot arrives within the
dashboard threshold. The dashboard retains the last valid snapshot and marks it
stale; it never consumes a `.tmp` or partially written JSON file.

## Crash recovery

Canonical evidence is append-only and each meaningful row is closed before the
next operation. Live status uses a bounded ring of completed immutable JSON
files. If the game crashes, leave the evidence directory intact, reopen the
dashboard, and choose **Resume Campaign** or **Finish & Collect**. The collector
marks a probable crash from process exit, crash-folder timing, unmatched
breadcrumbs, missing final markers, and dirty JSONL tails. A crash-suspect row
cannot become confirmed until a clean repeat or explicit safety disposition.

## Coverage catalog and Needs Coverage

The Coverage view is generated from the complete supplied object dump,
RuntimeProbe documentation/policies, imported evidence, and runtime-discovered
relevant functions. It includes properties, struct fields, actors, native and
Blueprint functions, RPC hypotheses, OnRep callbacks, multicasts, and natural
events. Static name prefixes are hypotheses until runtime evidence confirms
flags and direction.

Every row records read, natural observation, argument metadata, authority,
visibility, lifecycle, persistence/UI, write/apply, cleanliness, safety, next
observation, and checklist linkage. Unknown rows are not discarded. The default
**Needs Coverage** filter shows every row that is not one of:

- confirmed with clean evidence;
- explicitly rejected as unsafe;
- explicitly documented as unsupported;
- intentionally excluded by product policy, such as keys.

The generator records the dump hash and line count and fails rather than
silently changing the campaign denominator when complete dump provenance is
missing.

## Collection and readiness reports

Each local bundle contains a relative-path hash manifest, sanitized logs,
canonical evidence, the last valid status/control snapshots, checklist report,
missing-action list, diagnostic summary, capability-readiness report, and
coverage snapshot. The offline combined report verifies unique machines and
sessions, opposite selected roles, compatible schema/campaign generations, and
overlapping evidence time before correlating host/client observations.

The final readiness report states evidence coverage separately for inventory,
metadata/enhancements, slots, equipment, crystals, health, multiplayer
ownership/visibility, lifecycle, and official apply candidates. Current
read-path or natural-call proof never becomes write/apply proof. Any future
write smoke remains a separate, explicitly approved CrabSync experiment outside
RuntimeProbe.
