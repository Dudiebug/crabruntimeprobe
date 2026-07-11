# 2026-07-10 full-observe hook incident

## Status

The v1.0.2 full-observe build must not be used for gameplay evidence. Its normal
profile registered the complete native hook set, and Crab Champions crashed
immediately after coordinator startup. The replacement architecture removes all
normal-mode gameplay and lifecycle hooks.

## Evidence

- The affected run registered 111 native hooks immediately before the crash.
- The coordinator started at approximately `2026-07-10 22:16:05` local time;
  the game wrote `crash_2026_07_10_22_16_05.dmp` in the same second.
- The dump reports Windows exception `0xC0000264`
  (`STATUS_RESOURCE_NOT_OWNED`).
- The captured Lua stack passes through `passive_hook_manager.lua` while
  resolving and summarizing a hook context. An
  `OnRep_IslandRewardRarity` context is present in the dump.
- No inventory-stage or runtime-discovery work had started, so this incident
  does not support blaming those later stages.

The timing and stack make the passive hook callback path the leading cause.
They do not prove that one specific UFunction is solely responsible.

## Containment

Normal Play Guide mode now:

- registers zero gameplay, RPC, OnRep, multicast, HUD, or lifecycle hooks;
- does no runtime UFunction discovery;
- does no inventory-stage escalation or inventory-array access;
- samples only previously reviewed, PlayerState-scoped scalar/equipment paths;
- applies a stable-world/PlayerState dwell barrier and paced category sampling;
- writes strict append-only snapshot rows with explicit safety flags; and
- leaves evidence qualification to the desktop GUI's deterministic reducer.

Exact-call and apply-path research remains in the exhaustive catalog as
**Needs Coverage**. It is not silently treated as completed by a state delta.

## Release gate

A replacement release must pass source-closure checks proving that the normal
coordinator cannot reach legacy hook or inventory-stage modules. Packaging and
desktop replay tests cannot prove in-game crash freedom; field validation must
be reported separately and must start with the hook-free normal profile.
