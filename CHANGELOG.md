# Changelog

All notable CrabRuntimeProbe changes are recorded here. RuntimeProbe remains a
read-only research tool; a release entry never implies CrabSync write/apply
safety.

## [1.0.4] - 2026-07-11

### Added

- Two explicit workflows: hook-free **Normal Play Guide** and
  **Progressive Broad Observation** with a safe snapshot baseline, every
  compatibility-valid trusted hook at its validated depth, and at most one
  unvalidated canary registered last.
- Versioned candidate, run-manifest, breadcrumb, validation-ledger,
  trusted-manifest, compatibility-fingerprint, run-classification, and
  quarantine contracts.
- Depth-aware validation from **Depth 0** static review through **Depth 7**
  full passive evidence, with forbidden operations documented at every depth.
- Deterministic run classification, explicit attribution confidence, durable
  crash recovery, and outcomes including **Registered but not naturally
  observed**, **Needs revalidation**, and **Quarantined**.
- A versioned atomic consumed-run marker prevents a prepared run ID from
  automatically rearming in a later game process.
- Compatibility-aware promotion requiring three clean runs and matched natural
  callbacks where practical; registration alone never promotes a candidate.
- Profile-aware evidence-bundle safety distinguishes fully hook-free normal
  evidence from compatible, depth-enforced controlled research with at most one
  canary while retaining every non-hook mutation/discovery prohibition.
- Independent relic reporting: **local relic count increased** is distinct from
  **pickup callback observed**.
- v1.0.4 release notes, clean-install guidance, field-test checklist, sanitized
  package build metadata, and expanded canonical/UE4SS release verification.

### Safety and migration

- The trusted pool ships empty. No v1.0.2 hook is pretrusted.
- `OnRep_IslandRewardRarity` is the first recommended canary at registration-only
  Depth 1, but the package does not prearm it.
- Crash-suspect and quarantined candidates cannot rearm automatically.
- A game, UE4SS, hook-catalog, callback, schema, or validation-behavior
  compatibility change invalidates affected trust instead of silently reusing it.
- Completed callback boundaries followed by a later crash remain unattributed;
  the latest timestamp is not treated as causation.

### Release packaging

- Dashboard assembly, file, product, package, and manifest versions are 1.0.4.
- Canonical and UE4SS bundles include all progressive campaign defaults,
  schemas, runtime modules, documentation, licenses, and relative hash manifests.
- Verifiers reject pretrusted manifests, prearmed canaries, unsafe configuration,
  development artifacts, local paths, raw object dumps, and stale release layout.

## [1.0.3] - 2026-07-10

- Replaced the unsafe v1.0.2 bulk-hook normal profile with a hook-free,
  snapshot-first Play Guide after the 2026-07-10 hook observer incident.
- Added deterministic snapshot replay and stricter normal-mode source closure.

## [1.0.2] - 2026-07-10

- Prevented runtime evidence writers from spawning command shells.
- This version's 111-hook full-observe profile is retired and must not be used.

## [1.0.1] - 2026-07-10

- Improved game handoff and campaign session monitoring.

## [1.0.0] - 2026-07-10

- Initial self-contained dashboard and CrabRuntimeProbe release package.
