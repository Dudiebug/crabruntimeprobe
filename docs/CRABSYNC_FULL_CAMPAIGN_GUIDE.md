# CrabRuntimeProbe v1.0.4 Campaign and Research Guide

CrabRuntimeProbe has two deliberately separate workflows. **Normal Play Guide**
is the hook-free campaign for ordinary testing. **Progressive Broad
Observation** is an explicit Advanced research workflow that expands passive
hook coverage one candidate and one validation depth at a time.

Both workflows are read-only. CrabRuntimeProbe is not a CrabSync transport,
gameplay authority, inventory synchronizer, or write/apply test. It never calls
unknown methods, invokes RPCs or `OnRep` functions, writes gameplay state, or
performs runtime function discovery.

## Clean install and v1.0.2 retirement

1. Close Crab Champions and every older CrabRuntimeProbe dashboard.
2. Preserve any evidence ZIPs you need outside the old release folder.
3. Extract the complete v1.0.4 ZIP into a new empty folder; do not merge release
   directories.
4. Open `CrabRuntimeProbe.Dashboard.exe`, confirm the game folder, and use
   **Start play guide** once. This installs the packaged payload and rewrites
   legacy research gates to safe defaults.
5. Do not copy old configuration, trusted manifests, or Lua modules into the
   v1.0.4 payload.

Do not use or resume the v1.0.2 full-observe hook profile. Its 111 hooks are not
grandfathered into trust. v1.0.4 ships with an empty trusted manifest and no
prearmed canary. The first recommended research candidate is
`OnRep_IslandRewardRarity` at Depth 1, but it is armed only after an explicit
Advanced research preparation.

## Workflow 1: Normal Play Guide

Normal Play Guide is the default and retains the `crabsync-full-observe`
profile identifier for campaign compatibility. Despite that historical name,
normal mode registers no gameplay, RPC, `OnRep`, multicast, HUD, lifecycle, or
research hooks. It samples only a small reviewed set of field-proven
PlayerState values after a stability barrier.

### Normal tester procedure

1. Open the v1.0.4 dashboard on each test computer.
2. Choose **I'm hosting** on the lobby host and **I'm joining a friend** on the
   other computer. Confirm the Crab Champions folder or use **Find
   automatically**.
3. Click **Start play guide**. Wait for the live display to progress from
   warming to ready/collecting and confirm that heartbeat age stays fresh and
   sequence increases.
4. Create and join the same lobby, then play normally. Follow the large action
   cards; never force a debug or synthetic action to fill a box.
5. Close both games normally.
6. Click **Finish and save results**, review the classification and missing
   actions, and export both evidence ZIPs.
7. Use **Combine Bundles** for the offline host/joined-client readiness report.

Cards update from qualifying evidence; there are no manual completion boxes.
The desktop dashboard replays append-only snapshots and applies explicit
before/after rules. A state change can prove only the mapped local read
transition. It cannot identify the exact function that caused it.

### Live collection states

The live panel must be read as a health display, not merely as a latest-file
timestamp.

| State | Meaning and response |
|---|---|
| Game unavailable | The game process or installed payload is not available. Start/locate the game and check the installation. |
| Warming | The writer is alive but the startup/stability barrier has not passed. Wait without forcing gameplay actions. |
| Stable | The reviewed context has remained valid long enough for safe sampling. |
| Ready | Stable context and a fresh writer are ready for a qualifying action. |
| Collecting | Fresh completed status slots and evidence are arriving; sequence should progress. |
| Stale | No new completed status slot arrived within the threshold. The last valid snapshot is retained but cannot imply current health. |
| Stopped | Collection ended normally or a diagnostics-only stop was acknowledged. |
| Faulted | Configuration, writer, safety, or evidence processing failed closed. Review the explanation before retrying. |

The display includes status sequence, heartbeat age, warming/stable state,
current sampling category, active profile, and collection readiness. A healthy
heartbeat without sequence progress is not healthy collection. The reader
accepts only completed immutable status slots and never consumes `.tmp` or
partially written JSON.

### Honest not-observable results

Normal sampling is paced category-by-category and is currently limited to
reviewed scalar and redacted equipment paths. Live inventory-array counting or
traversal, inventory elements, `InventoryInfo`, Enhancements, exact function
watchers, exact arguments, replication direction, persistence/UI follow-up,
and write/apply behavior remain **not observable under this profile**. The
dashboard explains this limitation and keeps the linked item in **Needs
Coverage**; it does not turn absence of evidence into failure or completion.

Friendly card states are **TO DO**, **IN PROGRESS**, **DONE**, **WAITING**, and
**RETRY**. A grouped action becomes DONE only when every required mapped signal
has a clean terminal result. Dirty or crash-suspect evidence produces RETRY.
Exact RPC, `OnRep`, multicast, argument, persistence, or apply-path tasks cannot
be completed by a snapshot delta.

## Workflow 2: Progressive Broad Observation

Progressive Broad Observation restores broad, mostly automatic passive
research with a controlled denominator:

```text
safe snapshot baseline
+ every compatibility-valid trusted hook at its individually validated depth
+ exactly one unvalidated canary at one selected validation depth
```

At most one canary is accepted, and it is registered last. An empty, unknown,
duplicated, quarantined, incompatible, or over-depth candidate fails closed.
The next candidate or next depth is never armed in the same game process.

### Research tester procedure

1. Open **Advanced** and review the recommended candidate, validation depth,
   and ordinary suggested action.
2. Click **Start research run** (or the equivalent enabled preparation action).
   This explicitly prepares one launch; it does not accept a free-form hook
   path.
3. Launch the game, wait for a fresh heartbeat and registration status, and
   play normally. Try the suggested action only if it occurs naturally.
4. Close the game normally. A candidate that registered but never naturally
   fired still has a valid, distinct outcome.
5. Review the final classification, attribution confidence, last completed
   breadcrumb, callback count, and circuit breakers.
6. Choose **Repeat same test**, **Prepare next depth**, **Run candidate alone**,
   **Quarantine candidate**, or **Return to safe Play Guide**. The choice
   prepares a future process launch only.

The research page reports trusted-hook count, active canary, canary validation
depth, suggested normal action, registration state, callback count, last
completed breadcrumb, circuit-breaker state, heartbeat/sequence, final run
classification, and attribution confidence. Unsafe or impossible actions are
disabled with an explanation.

### Controlled run types

| Run type | Contents | Typical purpose |
|---|---|---|
| Trusted-pool only | Safe baseline plus compatible trusted hooks at their validated depths; no canary. | Control test for an interaction or trusted-pool regression. |
| Canary only | Safe baseline plus exactly one canary; no trusted hooks. | Isolate a candidate after an ambiguous or interaction-suspect run. |
| Combined | Safe baseline, compatible trusted pool, then exactly one canary registered last. | Normal progressive research run. |

Trusted hooks are ordered deterministically. Native hooks register individually;
Blueprint hooks register only when their reviewed owner is loaded. Each trusted
hook executes only the behavior validated at its current depth. Trust at Depth
2 is not permission to execute Depth 5 behavior.

## Validation depths and hard exclusions

Callback behavior is composed by depth so a shallow candidate cannot
accidentally cross a deeper boundary.

| Depth | Allowed behavior | Forbidden at this depth |
|---|---|---|
| 0 - Static catalog validation | Validate stable candidate ID, exact path/fingerprint, owner, native/Blueprint registration kind, expected callback shape, and nonmutation policy. Register nothing. | Any hook registration, callback execution, UObject access, invocation, or mutation. |
| 1 - Registration only | Register one hook with a callback that records natural entry in the minimal safe manner and immediately returns. | Inspecting context, arguments, PlayerState, inventory, or gameplay values; any deeper callback processing. |
| 2 - Callback entry/exit | Record matched `callback-enter` and `callback-exit`. | Context resolution or inspection, arguments, ownership paths, state reads, evidence enrichment. |
| 3 - Context resolution | Bound context resolution, determine only its basic class, and emit a redacted fingerprint with matched breadcrumbs. | PlayerState ownership traversal, gameplay state, arguments, arrays, arbitrary formatting or UObject exploration. |
| 4 - PlayerState scope | Use only reviewed paths: context itself as `CrabPS`, reviewed `OwningPS`, or reviewed `PlayerState`. Local-PlayerState fallback stays explicitly unconfirmed. | Unreviewed ownership traversal, treating fallback as confirmed, state/argument reads belonging to deeper depths. |
| 5 - Reviewed state reads | Read only descriptor-approved scalar or equipment fields through the approved scope. | Array traversal, inventory elements, `InventoryInfo`, Enhancements, arbitrary UObject exploration, undocumented arguments, or unrelated state. |
| 6 - Exact documented arguments | Add only arguments documented for that exact function, with bounded redacted summaries. | Undocumented/variadic exploration, raw identity, addresses, unsafe object representations, or full passive processing not yet reviewed. |
| 7 - Full passive evidence | Execute the reviewed full passive-evidence callback and its matched state/evidence boundaries. | Unknown methods, RPC/`OnRep` invocation, writes, runtime discovery, unbounded reads, raw identity, or any behavior outside the reviewed descriptor. |

Registration success alone never promotes a hook. Depth 1 also supports the
valid **Registered but not naturally observed** outcome when no callback occurs.

## Breadcrumb journal and interrupted-run recovery

Before registration work, the runner persists run identity and writes
`registration-begin`; it writes `registration-complete` only after success.
The first callback breadcrumb is written before any context or argument
inspection. Enabled boundaries receive matched begin/complete or enter/exit
records:

```text
callback-enter
context-resolve-begin / context-resolve-complete
scope-resolve-begin / scope-resolve-complete
prestate-read-begin / prestate-read-complete
arguments-read-begin / arguments-read-complete
poststate-read-begin / poststate-read-complete
evidence-write-begin / evidence-write-complete
callback-exit
```

Each bounded record carries a monotonic sequence, stable candidate ID, exact
hook-path fingerprint, depth, invocation ID, lifecycle generation, boundary,
and safe high-resolution timing when available. It contains no raw player
identity, memory address, or unsafe object representation. Journal failure
trips its circuit breaker and prevents unsafe continuation.

After a crash or forced termination, leave the evidence directory intact.
Reopen the dashboard and choose **Resume Campaign** or **Finish & Collect**.
Startup ignores a partial final write, validates sequence/lifecycle/phase, and
uses the last justified unmatched boundary. It never automatically rearms a
crash-suspect canary.

Each prepared research run ID is one-shot. A small atomic consumed-run marker
is written before hook registration and collected as provenance; a later
process refuses to reuse that run ID. Retrying the same candidate still
requires a new, explicit dashboard preparation and a new run identity.

## Candidate states

Every candidate has one of these exact persisted outcomes:

| State | Meaning |
|---|---|
| Untested | No compatible run has armed the candidate. |
| Armed | Explicitly prepared for the next process launch. |
| Registration clean | Registration completed cleanly, without proving a natural callback. |
| Registered but not naturally observed | Registration was clean, but no natural invocation occurred. |
| Natural callback clean | Matched callback boundaries completed cleanly at the tested depth. |
| Provisional | Useful clean evidence exists, but the promotion threshold is incomplete. |
| Trusted | The candidate met promotion rules for this exact depth and compatibility fingerprint. |
| Needs revalidation | Previously useful/trusted evidence is incompatible with current inputs or behaved inconsistently. |
| Unsupported | The reviewed passive mechanism cannot support the candidate. |
| Quarantined | A user or classifier isolated the candidate; it cannot auto-arm. |
| Crash-suspect | An interrupted boundary or correlated crash justifies suspicion; it cannot resume automatically. |

Recommendation and execution remain separate. A recommendation does not change
an outcome, arm a candidate, advance a depth, or launch a run.

## Promotion and compatibility rules

The normal threshold for trusting a candidate at one depth is:

- three clean runs at that exact validation depth;
- three matched natural callback executions where practical;
- host and joined-client evidence when relevant;
- at least one island or lifecycle transition;
- no unmatched breadcrumb;
- no correlated crash artifact;
- no new UE4SS callback error;
- matching compatibility fingerprint; and
- automated fixture coverage for the reducer.

The compatibility fingerprint covers game version/build where available,
UE4SS version, generated coverage-catalog identity, hook-catalog identity,
callback implementation and schema versions, and validation-behavior version.
Missing, ambiguous, stale, partially written, unknown, or mismatched inputs fail
closed. A mismatch marks affected trust **Needs revalidation**; it never silently
reuses old trust.

The v1.0.4 priority begins with `OnRep_IslandRewardRarity`, then
`ClientOnPickedUpPickup`, `OnRep_Inventory`, `OnRep_Crystals`,
`OnRep_WeaponDA`, `OnRep_AbilityDA`, `OnRep_MeleeDA`, passively observed slot
increment, drop/removal/salvage, enhancement/anvil, health/death/revive/respawn,
shops/chests/totems/portals/lifecycle, then Blueprint-only and uncommon
functions. These are observations only; RuntimeProbe does not call them.

## Classification, attribution, and quarantine

The deterministic classifier consumes the run manifest, compatibility,
breadcrumbs, live heartbeat/status, passive evidence, UE4SS callback errors,
and correlated crash artifacts when available. It distinguishes clean shutdown,
interrupted run, external termination, stale writer, registration failure,
callback-boundary failure, evidence failure, and unattributed post-callback
crash.

An unmatched `context-resolve-begin`, for example, can make that exact boundary
a high-confidence crash suspect when run and crash correlation support it. If
all callback boundaries completed and the game crashed later, the result is
**unattributed**. The latest hook timestamp is never sufficient causation.

When interaction is plausible, test in this order:

1. trusted pool without the canary;
2. canary alone;
3. trusted pool plus canary; and
4. a controlled trusted subset if the first three indicate interaction.

Quarantine is explicit and persistent. Quarantined and crash-suspect candidates
cannot auto-arm or auto-resume. Retrying requires a reviewed disposition and an
explicit future-run preparation; the dashboard disables unsafe choices and
explains why.

## Relic observation: two independent claims

The safer snapshot route validates the `Relics` wrapper first, then in a later
generation validates only its count, establishes a stable baseline, observes a
natural count increase, and repeats on host and joined client before proposing
`relicCount` for normal sampling. Its label is **local relic count increased**.

Separately, `ClientOnPickedUpPickup` progresses through the exact callback depth
ladder. Its label is **pickup callback observed**. It may eventually support
reviewed context and argument claims, but it does not prove the count path.

Neither claim proves persistence, another player's visibility, replication
direction, inventory contents, or write/apply safety. The UI and evidence keep
the labels and promotion histories independent.

## Deliberate multiplayer campaign

Use the same campaign name on both computers while retaining their distinct
opaque machine/session IDs. Skip anything the run does not naturally offer.

1. Create/join a lobby and wait for both dashboards to show a stable local
   PlayerState.
2. Start a run. Between the players, naturally collect a weapon mod, ability
   mod, melee mod, perk, and relic.
3. If offered, collect a duplicate and observe a removal, drop, salvage, or
   replacement.
4. Buy a weapon/ability/melee/perk slot and record clean pre/post values.
5. Gain and spend crystals; reroll a shop if naturally available.
6. Use an anvil and upgrade totem if they appear.
7. Take damage and heal; observe armor if present; allow death and revive or
   respawn when practical.
8. Complete an island, choose a reward, use a chest/shop, and travel through a
   portal.
9. Have the joined client leave and reconnect once, waiting for stability after
   each lifecycle transition.
10. Close both games normally and collect both bundles.

## Dashboard controls and evidence states

- **Find automatically** / **Detect Installation** searches Steam libraries
  without retaining Steam account data.
- **Start play guide** installs the safe payload, prepares normal mode, and
  starts live monitoring.
- **Prepare Campaign** and **Start Monitoring** expose the same safe operations
  in Advanced.
- **Open Crab Champions** launches Steam app `774801`.
- **Stop Campaign Safely** writes a diagnostics-only request; it does not kill
  the game or alter gameplay.
- **Finish and save results** / **Finish & Collect** collects redacted logs,
  evaluates evidence, and builds one local ZIP.
- **Reset Campaign** archives transient state after confirmation; **Resume
  Campaign** reattaches without erasing canonical evidence.
- **Combine Bundles** validates and correlates one host and one joined-client
  bundle offline. It starts no relay or listener.

Technical evidence states remain **Not observed**, **In progress**, **Partial**,
**Confirmed**, **Unsupported**, **Blocked by prerequisite**, **Crash-suspect**,
**Dirty evidence**, and **Not applicable**. Filters never alter catalog counts,
readiness denominators, or promotion rules.

## Coverage, collection, and readiness

The Coverage view is generated from complete object-dump provenance,
RuntimeProbe policy/evidence, and catalog-generated relevant functions. It
retains unknown properties, struct fields, actors, native/Blueprint functions,
RPC hypotheses, `OnRep` callbacks, multicasts, and natural events. Static names
remain hypotheses until runtime evidence supports their behavior.

The default **Needs Coverage** filter hides only clean confirmed rows, explicit
unsafe rejections, documented unsupported rows, and deliberate product-policy
exclusions. A successful snapshot campaign is read/state-transition evidence,
never exact-call or write/apply proof.

Each collection bundle uses relative hashes and contains sanitized logs,
canonical evidence, last valid status/control snapshots, checklist and missing
actions, diagnostics, capability readiness, and a coverage snapshot. Combined
reports verify distinct machines/sessions, opposite roles, compatible
schema/campaign generations, and overlapping evidence time before correlation.

Bundle safety is profile-aware. A Normal Play Guide bundle requires
`hooksDisabled=true` and reports controlled-research/compatibility/depth flags as
false with zero active canaries. A Progressive bundle may report
`hooksDisabled=false` only when `controlledResearchHooks`,
`compatibilityValidated`, and `trustedDepthEnforced` are all true, active
canaries is 0 or 1, and every write, RPC, mutation, raw-identity, HUD,
runtime-discovery, and inventory-stage safety flag remains disabled. This keeps
a valid controlled research run from being mislabeled unsafe without weakening
the normal hook-free contract.

## Troubleshooting

**Game crashes.** Do not relaunch the prepared research run immediately. Leave
artifacts intact, collect or resume, inspect the last matched/unmatched
breadcrumb and confidence, and quarantine a suspect when justified. A
crash-suspect never auto-rearms. Return to safe Play Guide for a hook-free
control if uncertain.

**Heartbeat is missing.** Confirm Crab Champions is running, the selected folder
contains `CrabChampions-Win64-Shipping.exe`, `Mods/mods.txt` enables
CrabRuntimeProbe, and the payload was prepared by v1.0.4. Check `UE4SS.log` for
RuntimeProbe startup/config errors. Missing output must remain Game unavailable
or Faulted, not Collecting.

**Status is stale or sequence stops.** Stop treating the display as current.
Keep the game/evidence intact, wait briefly for a completed slot, then finish
and collect diagnostics. Never use the retained last snapshot as proof of a new
action. Do not read or repair `.tmp` files manually.

**No natural callback occurs.** Close normally and retain **Registered but not
naturally observed**. Verify the suggested action/lifecycle was actually
available, then repeat the same depth on a future launch. Do not manufacture an
invocation, call the function, or promote registration alone.

**UE4SS reports callback errors.** Finish collection, preserve the exact
redacted error and breadcrumbs, and leave the candidate Provisional, Needs
revalidation, Quarantined, or Crash-suspect as the classifier directs. Use
trusted-pool-only and canary-only controls before a combined retry. Never hide a
trusted-hook error behind the canary.

**An action says not observable.** This is an honest profile limitation. Use the
suggested separate research candidate only if you intend to conduct Advanced
research; do not loosen normal-mode gates.

## v1.0.4 verification checklist

"Repository/simulated" means an automated source, reducer, schema, fixture, or
package test can verify the contract without Crab Champions. "Crab Champions
field" means a real target-build run is still required; automated success must
not be reported as in-game validation.

| # | Required check | Verification venue |
|---:|---|---|
| 1 | Hook-free normal-mode control | Repository/source guard, then Crab Champions field smoke |
| 2 | Dashboard heartbeat and sequence update without reload | Repository/simulated UI/status fixture, then field observation |
| 3 | Warming-to-stable readiness transition | Repository/simulated fixture, then field observation |
| 4 | Honest not-observable state | Repository/simulated reducer/UI fixture |
| 5 | Invalid configuration fails closed | Repository/simulated config fixtures |
| 6 | Zero trusted hooks plus one registration-only canary | Repository/simulated runner fixture; field registration required |
| 7 | Trusted hooks plus exactly one canary | Repository/simulated coordinator fixture; field run after trust exists |
| 8 | Two configured canaries are rejected | Repository/simulated config fixture |
| 9 | Principal-suspect registration test | Crab Champions field: `OnRep_IslandRewardRarity`, Depth 1 |
| 10 | Principal-suspect callback entry/exit test | Crab Champions field: same candidate, Depth 2 after promotion rules allow |
| 11 | Host smoke test | Crab Champions field |
| 12 | Joined-client smoke test | Crab Champions field |
| 13 | Island/lifecycle transition | Crab Champions field |
| 14 | Registered but never naturally observed outcome | Repository/simulated classifier and Crab Champions field when encountered |
| 15 | Simulated crash after every begin boundary | Repository/simulated breadcrumb/classifier fixtures |
| 16 | Recovery from truncated final breadcrumb | Repository/simulated journal fixture |
| 17 | Completed callbacks then later crash classify unattributed | Repository/simulated classifier fixture |
| 18 | Quarantined candidates cannot auto-arm | Repository/simulated state/config fixture |
| 19 | Compatibility changes invalidate trust | Repository/simulated fingerprint fixture |
| 20 | Promotion requires every configured evidence threshold | Repository/simulated ledger/reducer fixture |
| 21 | Relic count and pickup callback stay independently labeled | Repository/simulated UI/evidence fixture; both paths need field evidence |
| 22 | Release ZIP layout and clean-install instructions | Repository/package build, extract, and verifier |

The repository can complete every automated/simulated entry and build verified
packages without the game. It must report the host, joined-client, natural
callback, lifecycle, relic, crash-dump, and target-UE4SS rows as pending until
those field runs actually occur.
