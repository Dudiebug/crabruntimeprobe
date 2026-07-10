# Codex Guardrails for CrabRuntimeProbe

This repository exists to build **CrabRuntimeProbe**, a standalone UE4SS Lua diagnostic/research mod.

## Non-goals

- Do **not** turn this into CrabInvSync.
- Do **not** add gameplay synchronization.
- Do **not** implement shared inventory logic.
- Do **not** add write probes.
- Do **not** call mutating RPCs.
- Do **not** add deep inventory probes until runtime safety is established.

## Probe safety requirements

- No write probes.
- No mutating RPC calls.
- Every risky operation must emit a breadcrumb **before** and **after** the operation.
- Use paced probing and context gates; `pcall` alone is not considered sufficient crash protection.
- Full-observe hooks listen to natural calls only. Registering a hook is never
  evidence that the function was called or that calling it would be safe.
- The dashboard status/control channel is local diagnostics only. It must never
  carry gameplay values between computers or become gameplay authority.
- Live status must use completed atomic snapshots with bounded growth;
  canonical evidence remains append-only.
- Machine/session identifiers must be random and opaque. Do not emit hostnames,
  Windows user names, Steam names, Steam IDs, or other raw identity.
- Do not add HTTP, sockets, listeners, relays, bridges, or external CrabSync
  transport behavior.
- Inventory depth advances only through reviewed prerequisites, one access
  technique per stage, with independent category circuit breakers and no stale
  UObject references across lifecycle generations.

## Documentation requirements

Generated docs must clearly distinguish:

1. **Object dump presence** ("this symbol appears in dumps"), and
2. **Runtime validation** ("this operation was probed and observed safe/unsafe/unknown at runtime").
