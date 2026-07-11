# CrabSync Snapshot Campaign Guide

`crabsync-full-observe` is the retained profile identifier for a coordinated,
read-only, snapshot-first evidence campaign. Normal Play Guide mode samples a
small set of previously proven PlayerState paths after a stability barrier, and
the desktop dashboard derives candidate task completion from stable before/after
state. It is not a CrabSync transport, gameplay authority, or write test.

## Safety boundary

The normal campaign registers no gameplay, RPC, OnRep, multicast, HUD, or
lifecycle hooks. It performs no runtime UFunction discovery, inventory-stage
escalation, arbitrary UObject crawl, gameplay property write, RPC call, or
payload-carrier trick. Player and machine identity is represented only by
random local identifiers or redacted fingerprints.

Snapshot evidence can show that a stable value changed after natural gameplay.
It cannot prove which exact function caused the change. Exact-call, argument,
ownership, replication direction, UI follow-up, persistence, and future
write/apply safety therefore remain separate **Needs Coverage** rows. Malformed,
unstable, wrong-session, wrong-generation, dirty, crash-suspect, or unsafe rows
cannot complete a checklist item.

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
snapshot profile, and creates a new campaign generation. Resume reattaches
to an interrupted generation without clearing its evidence.

## Deliberate multiplayer run

Click **Open Crab Champions** on both computers, then perform the following
actions naturally. Skip anything the run does not offer; never use a debug or
synthetic path merely to complete a box.

1. The host creates a lobby and the joined client joins it. Wait until both
   dashboards show a stable local PlayerState.
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

The dashboard continuously shows the next useful action. Normal sampling is
paced and category-by-category rather than a bulk scan. It currently limits
itself to reviewed scalar and redacted equipment paths. Live inventory-array
counting, traversal, item metadata, Enhancements, and exact function watchers
stay out of normal mode because their clean safety evidence is incomplete.
Those tasks remain visible instead of being silently checked off.

## Play Guide (default)

The dashboard opens in **Play Guide** mode. It groups the authoritative
technical checklist into a small set of player-facing actions across setup,
pickups, equipment, shops, crystals, health, travel, reconnecting, and
automatic observations. Each card updates from evidence; players never check a
box manually.

Friendly states are **TO DO**, **IN PROGRESS**, **DONE**, **WAITING**, and
**RETRY**. A grouped action becomes DONE only when every required linked signal
has a clean terminal result. Dirty or crash-suspect evidence produces RETRY. A
stable state delta can finish only a rule explicitly mapped to that field and
scope; it never finishes exact RPC, OnRep, multicast, argument, persistence, or
apply-path tasks. Newly discovered or unmapped checklist entries remain visible
under **Watching automatically**.

Use the **To do**, **All**, and **Completed** filters to change which cards are
shown. Category counts, percentages, and readiness denominators never change
with the filter. **Advanced** retains the technical Overview, full checklist,
coverage catalog, and reports.

Play Guide is a projection only. Even if every friendly action is done, the
nine final readiness areas remain independently derived from the exhaustive
coverage catalog and stay incomplete while material rows remain unresolved.

## Dashboard controls

- **Find automatically** / **Detect Installation** searches Steam libraries without retaining Steam
  account data.
- **Select Installation** confirms a nonstandard game location.
- **Start play guide** prepares the campaign and starts monitoring with the
  selected host/joining role.
- **Prepare Campaign** in Advanced installs the payload and writes a safe campaign
  generation.
- **Start Monitoring** watches completed atomic status snapshots and canonical
  append-only evidence.
- **Open Crab Champions** launches Steam app `774801`.
- **Stop Campaign Safely** writes a diagnostics-only stop request. It does not
  kill the game or change gameplay.
- **Finish and save results** / **Finish & Collect** finalizes status, collects RuntimeProbe/UE4SS/crash
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
| Not observed | No qualifying stable snapshot evidence exists. |
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

Canonical evidence is append-only and snapshot qualification is performed by
the desktop dashboard, not by in-game Lua. Live status uses a bounded ring of
completed immutable JSON files. If the game crashes, leave the evidence
directory intact, reopen the dashboard, and choose **Resume Campaign** or
**Finish & Collect**. The collector marks probable crash and dirty-tail
conditions. A crash-suspect row cannot become confirmed until a clean repeat or
explicit safety disposition.

## Coverage catalog and Needs Coverage

The Coverage view is generated from the complete supplied object dump,
RuntimeProbe documentation/policies, imported evidence, and catalog-generated
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
