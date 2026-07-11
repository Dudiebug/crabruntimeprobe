# Campaign Status

- Campaign: `crabruntimeprobe-read-map`
- Updated: 2026-07-11T00:18:54.165Z
- Current phase: `inventory-element-da-read`
- Next recommended phase: `inventory-element-da-read`
- Latest session: 20260506T052733Z
- Latest commit: 3fbe6093cf49ac02512e3d5cff8ae2c1ecefbb40
- Latest summary: evidence/runtime/20260506T052733Z/diagnostic_summary.txt

## Completed Phases

- `smoke-startup` - Startup smoke
- `executeDelay` - executeDelay tick driver
- `observe-context` - Observe runtime context
- `equipment-property-read` - Equipment data asset property reads
- `health-playerstate-read` - PlayerState health scalar reads
- `health-playerstate-watch` - Solo PlayerState health watch
- `multiplayer-roster-read` - Multiplayer roster identity read
- `multiplayer-health-playerstate-watch` - Multiplayer PlayerState health watch
- `local-inventory-array-shape-confirm` - Local inventory array shape confirm
- `local-inventory-userdata-introspection` - Local inventory userdata introspection
- `crystals-read` - Local PlayerState crystals read
- `slots-read` - Local PlayerState slots read
- `perk-da-catalog-read` - Perk DataAsset catalog read

## Partial Phases

- `multiplayer-resource-visibility-read` - Multiplayer resource visibility read: remote_resources_partial; Multiple PlayerState candidates were sampled and some resource fields were visible remotely, but visibility was partial.
- `local-inventory-array-shallow-read` - Local inventory array shallow read: crash_suspect_local_inventory_shape_visible; Local inventory array fields were visible as shallow userdata shapes, but crash_2026_05_05_07_24_18.dmp exists after prepare/run; keep the phase crash-suspect pending safer confirmation.
- `inventory-array-count-read` - Inventory array count read: crash_suspect_inventory_array_count; Inventory array count metadata was attempted read-only, but a crash dump/folder was updated after campaign prepare/run.

## Failed Phases

- `safe-scalar-watch` - Safe scalar watch

## Blocked Phases

- `inventory-array-shallow-read` - Inventory array shallow read placeholder: Probe set is not implemented yet.
- `inventoryinfo-scalar-read` - InventoryInfo scalar read placeholder: Probe set is not implemented yet and InventoryInfo remains disabled until this explicit phase.
- `enhancements-read` - Enhancements read placeholder: Probe set is not implemented yet.
- `weaponmod-da-catalog-read` - Weapon mod DataAsset catalog placeholder: Future DataAsset catalog phase; implement after perk catalog evidence and safety review.
- `abilitymod-da-catalog-read` - Ability mod DataAsset catalog placeholder: Future DataAsset catalog phase; implement after perk catalog evidence and safety review.
- `meleemod-da-catalog-read` - Melee mod DataAsset catalog placeholder: Future DataAsset catalog phase; implement after perk catalog evidence and safety review.
- `relic-da-catalog-read` - Relic DataAsset catalog placeholder: Future DataAsset catalog phase; implement after perk catalog evidence and safety review.
- `weapon-da-catalog-read` - Weapon DataAsset catalog placeholder: Future DataAsset catalog phase; implement after perk catalog evidence and safety review.
- `ability-da-catalog-read` - Ability DataAsset catalog placeholder: Future DataAsset catalog phase; implement after perk catalog evidence and safety review.
- `melee-da-catalog-read` - Melee DataAsset catalog placeholder: Future DataAsset catalog phase; implement after perk catalog evidence and safety review.
- `event-watch-smoke` - Passive event watcher smoke placeholder: Future passive watcher track; observe naturally called events only.
- `event-watch-equipment` - Passive equipment event watcher placeholder: Future passive watcher track for naturally called OnRep_WeaponDA, OnRep_AbilityDA, and OnRep_MeleeDA.
- `event-watch-crystals` - Passive crystals event watcher placeholder: Future passive watcher track for naturally called OnRep_Crystals.
- `event-watch-slots` - Passive slots event watcher placeholder: Future passive watcher track for observing natural slot changes, including ServerIncrementNumInventorySlots only when the game calls it.
- `event-watch-pickups` - Passive pickup event watcher placeholder: Future passive watcher track for naturally called ClientOnPickedUpPickup.
- `event-watch-inventory-replication` - Passive inventory replication watcher placeholder: Future passive watcher track for naturally called OnRep_Inventory.
- `framework-skeleton` - CrabModFramework skeleton placeholder: Future CrabModFramework work; framework sits on UE4SS and is not implemented in RuntimeProbe.
- `safe-context-api` - Safe context API placeholder: Future CrabModFramework API work.
- `safe-playerstate-api` - Safe PlayerState API placeholder: Future CrabModFramework API work.
- `safe-dataasset-catalog-api` - Safe DataAsset catalog API placeholder: Future CrabModFramework API work.
- `safe-property-read-wrappers` - Safe property read wrappers placeholder: Future CrabModFramework API work.
- `safe-event-watcher-wrappers` - Safe event watcher wrappers placeholder: Future CrabModFramework API work.
- `capability-declarations` - Capability declarations placeholder: Future CrabModFramework API work.
- `direct-ue4ss-call-linting` - Direct UE4SS call linting placeholder: Future CrabModFramework validation work.
- `experimental-write-api` - Experimental write API placeholder: Future CrabModFramework-only work; never a RuntimeProbe write phase.

## Pending Phases

- None.

## Confirmed Safe Paths

- `CrabPS.WeaponDA` via `GetPropertyValue`
- `CrabPS.AbilityDA` via `GetPropertyValue`
- `CrabPS.MeleeDA` via `GetPropertyValue`
- `CrabPC -> PlayerState -> CrabPS -> HealthInfo` read-only PlayerState health path
- `CrabPC -> PlayerState` local identity reads with redacted/fingerprinted identity values
- confirmed visible multiplayer roster reads
- partial remote multiplayer PlayerState resource reads for crystals, slots, equipment, and health scalars
- local PlayerState inventory array property shape confirmation without count, traversal, or element dereference
- local PlayerState inventory userdata wrapper metadata without traversal or element dereference
- local PlayerState inventory array wrapper count metadata without traversal or element dereference
- local PlayerState Crystals scalar read through CrabPC -> PlayerState -> CrabPS
- local PlayerState candidate slot scalar reads through CrabPC -> PlayerState -> CrabPS
- safe scalar watch over proven local scalar/property paths
- read-only perk DataAsset catalog discovery and curated field reads
- direct max-safe play recorder over proven scalars plus capped perk DataAsset snapshots

## Identity And Roster Notes

- Local player identity visible: yes
- Max visible player count observed: 2
- Any candidate exposed more than one player: yes
- Roster source candidates attempted: Identity.CrabGS.SourceCandidate, Identity.FindAll.PlayerStateCandidates, Identity.GameState.SourceCandidate, Identity.PlayerArray.Shape, Identity.PlayerControllerCandidates, Identity.VisiblePlayers.SourceCandidate
- Visible roster source resolved: yes
- Raw IDs/names emitted: no, redacted/fingerprinted by default
- PlayerName and UniqueId can be fingerprinted from PlayerState identity reads without emitting raw values.
- `solo-or-host` means local-player-present in the current detector; it is not proof that the run was solo and cannot distinguish true solo from multiplayer host-like local context.
- Visible player roster source is confirmed; future auto-room grouping still requires matched host and joined-client runs.

## Multiplayer Resource Visibility

- Summary: partial
- Resource visibility class: remote_resources_partial
- Player count sampled: 4
- Fields visible across more than one PlayerState: AbilityDA, BaseMaxHealth, Crystals, HealthInfo, HealthInfo.CurrentHealth, HealthInfo.CurrentMaxHealth, Keys, MaxHealthMultiplier, MeleeDA, NumAbilityModSlots, NumMeleeModSlots, NumPerkSlots, NumWeaponModSlots, WeaponDA
- Fields only visible on local PlayerState: none
- Fields returning nil/errors: AbilityMods, MeleeMods, Perks, Relics, WeaponMods
- Readable categories by candidate: crystals=4/4, slots=4/4, equipment=4/4, inventory array counts=0/4, health=4/4
- Supports future P2P resource merge design: partial
- CrabInvSync v2 implication: P2P-style merge is plausible for crystals, slots, equipment, and possibly health inputs.
- Inventory item sync still needs separate research; current shallow count-only inventory array visibility is unresolved and does not expose item metadata.
- An external relay/server may still be needed for inventory until array/item metadata visibility or another safe carrier is proven.
- Raw IDs/names emitted: no, redacted/fingerprinted by default
- No writes/RPCs/HUD hooks/deep array element reads/InventoryInfo/Enhancements are part of this phase.

## Local Inventory Array Shallow/Count Visibility

- Summary: unresolved
- Local inventory array status: failed
- Local PlayerState present: yes
- Fields readable by shallow shape/count: AbilityMods, MeleeMods, Perks, Relics, WeaponMods
- Fields nil or unsupported: none
- Array value kinds: AbilityMods=userdata, MeleeMods=userdata, Perks=userdata, Relics=userdata, WeaponMods=userdata
- Array counts available: no; current helper only counts Lua tables and these values were userdata shapes
- Slot scalar values: NumAbilityModSlots=12, NumMeleeModSlots=12, NumPerkSlots=24, NumWeaponModSlots=24
- Array elements dereferenced: no
- InventoryInfo and Enhancements were not read; writes/RPCs/HUD hooks/deep arrays were disabled.
- No crash dump is associated with the imported local inventory evidence.
- Remote inventory array visibility remains unresolved separately.

## Local Inventory Array Shape Confirm

- Summary: local_inventory_shape_confirmed
- Local inventory shape confirm status: local_inventory_shape_confirmed
- Local PlayerState present: yes
- Fields readable by property shape confirm: AbilityMods, MeleeMods, Perks, Relics, WeaponMods
- Fields nil or unsupported: none
- Property present map: AbilityMods=true, MeleeMods=true, Perks=true, Relics=true, WeaponMods=true
- Array value kinds: AbilityMods=userdata, MeleeMods=userdata, Perks=userdata, Relics=userdata, WeaponMods=userdata
- Safe tostring kinds: AbilityMods=string, MeleeMods=string, Perks=string, Relics=string, WeaponMods=string
- Slot scalar values: NumAbilityModSlots=12, NumMeleeModSlots=12, NumPerkSlots=24, NumWeaponModSlots=24
- Array counts attempted: no
- Array traversal attempted: no
- Array elements dereferenced: no
- InventoryInfo read: no
- Enhancements read: no
- No crash dump is associated with the imported shape-confirm evidence.
- This phase distinguishes userdata shape visibility from countable Lua table arrays; counts remain unavailable for userdata values.

## Local Inventory Userdata Introspection

- Summary: local_inventory_userdata_introspection_confirmed
- Local inventory userdata introspection status: local_inventory_userdata_introspection_confirmed
- Local PlayerState present: yes
- Fields readable by userdata introspection: AbilityMods, MeleeMods, Perks, Relics, WeaponMods
- Fields nil or unsupported: none
- Value kinds: AbilityMods=userdata, MeleeMods=userdata, Perks=userdata, Relics=userdata, WeaponMods=userdata
- Safe tostring kinds: AbilityMods=string, MeleeMods=string, Perks=string, Relics=string, WeaponMods=string
- Safe tostring prefixes: AbilityMods=TArray: 0000020DB6F93D18, MeleeMods=TArray: 0000020DB6F94598, Perks=TArray: 0000020DB6F946A8, Relics=TArray: 0000020DB6F93C08, WeaponMods=TArray: 0000020DB6F94488
- Metatable kinds: AbilityMods=boolean, MeleeMods=boolean, Perks=boolean, Relics=boolean, WeaponMods=boolean
- Length operator attempted: AbilityMods=true, MeleeMods=true, Perks=true, Relics=true, WeaponMods=true
- Length operator results: AbilityMods=0, MeleeMods=0, Perks=0, Relics=0, WeaponMods=1
- Length operator errors: none
- Array traversal attempted: no
- Array elements dereferenced: no
- InventoryInfo read: no
- Enhancements read: no
- Writes/RPCs: no
- HUD/deep arrays: no
- No crash dump is associated with the imported userdata introspection evidence.
- Any length operator result is metadata-only; it is not proof of count traversal or item synchronization.

## Inventory Array Count Read

- Summary: inventory_array_count_confirmed
- Inventory array count status: inventory_array_count_confirmed
- Local PlayerState present: yes
- Properties classified: all five
- Value kinds: AbilityMods=userdata, MeleeMods=userdata, Perks=userdata, Relics=userdata, WeaponMods=userdata
- Count attempted: AbilityMods=true, MeleeMods=true, Perks=true, Relics=true, WeaponMods=true
- Count methods: AbilityMods=lua_len_operator_pcall, MeleeMods=lua_len_operator_pcall, Perks=lua_len_operator_pcall, Relics=lua_len_operator_pcall, WeaponMods=lua_len_operator_pcall
- Count results: AbilityMods=0, MeleeMods=0, Perks=0, Relics=0, WeaponMods=1
- Count errors: none
- Array traversal attempted: no
- Array elements dereferenced: no
- Item DataAsset fields read: no
- InventoryInfo read: no
- Enhancements read: no
- Writes/RPCs/HUD/deep arrays: no
- No crash dump is associated with the imported count-read evidence.
- Any count result is wrapper metadata only. It does not authorize traversal, element dereference, item DataAsset reads, InventoryInfo reads, Enhancements reads, or item sync.

## Inventory Element DA Read

- Summary: unresolved; no `inventory-element-da-read` evidence has been imported yet.
- Purpose: prove whether one capped first element per non-empty local inventory array can safely expose element or DataAsset identity.
- This phase is not full inventory sync, full traversal, InventoryInfo evidence, Enhancement evidence, Level evidence, or AccumulatedBuff evidence.

## Local Crystals Read

- Summary: crystals_read_confirmed
- Crystals read status: crystals_read_confirmed
- Local PlayerState present: yes
- Crystals read attempted: yes
- Crystals value present: yes
- Crystals value integer-like when present: yes
- Writes/RPCs: no
- HUD/deep arrays: no
- Inventory arrays/InventoryInfo/Enhancements: no
- No crash dump is associated with the imported crystals-read evidence.
- UInt32 range is documentation only for this read-only phase; RuntimeProbe does not write or clamp the value.

## Local Slots Read

- Summary: slots_read_confirmed
- Slots read status: slots_read_confirmed
- Local PlayerState present: yes
- Slot read attempted: yes
- Present slot values: NumAbilityModSlots=12, NumMeleeModSlots=12, NumPerkSlots=24, NumWeaponModSlots=24
- Present slot values integer-like: yes
- Present slot values within 0..255: yes
- Writes/RPCs: no
- HUD/deep arrays: no
- Inventory arrays/InventoryInfo/Enhancements: no
- No crash dump is associated with the imported slots-read evidence.
- These are observed scalar slot counters / candidate unlocked slot counters only; they are not proven total capacity or locked-slot state.

## Safe Scalar Watch

- Summary: safe_scalar_watch_observed_change
- Safe scalar watch status: safe_scalar_watch_observed_change
- Sample count: 119
- Logged row count: 87
- First values: AbilityDA=exists=true isValid=true fullName=CrabAbilityDA /Game/Blueprint/Ability/DA_Ability_BlackHole.DA_Ability_BlackHole name=DA_Ability_BlackHole nameSource=fullNameFallback, BaseMaxHealth=250, Crystals=0, CurrentHealth=250, CurrentMaxHealth=250, MaxHealthMultiplier=1, MeleeDA=exists=true isValid=true fullName=CrabMeleeDA /Game/Blueprint/Melee/DA_Melee_Hammer.DA_Melee_Hammer name=DA_Melee_Hammer nameSource=fullNameFallback, NumAbilityModSlots=12, NumMeleeModSlots=12, NumPerkSlots=24, NumWeaponModSlots=24, WeaponDA=exists=true isValid=true fullName=CrabWeaponDA /Game/Blueprint/Weapon/Minigun/DA_Weapon_Minigun.DA_Weapon_Minigun name=DA_Weapon_Minigun nameSource=fullNameFallback, context=solo, lifecycleState=stable, playerStatePresent=true, role=solo-or-host
- Latest values: AbilityDA=exists=true isValid=true fullName=CrabAbilityDA /Game/Blueprint/Ability/DA_Ability_BlackHole.DA_Ability_BlackHole name=DA_Ability_BlackHole nameSource=fullNameFallback, BaseMaxHealth=288, Crystals=1481, CurrentHealth=270.08383178711, CurrentMaxHealth=345.60000610352, MaxHealthMultiplier=1, MeleeDA=exists=true isValid=true fullName=CrabMeleeDA /Game/Blueprint/Melee/DA_Melee_Hammer.DA_Melee_Hammer name=DA_Melee_Hammer nameSource=fullNameFallback, NumAbilityModSlots=8, NumMeleeModSlots=8, NumPerkSlots=8, NumWeaponModSlots=8, WeaponDA=exists=true isValid=true fullName=CrabWeaponDA /Game/Blueprint/Weapon/Minigun/DA_Weapon_Minigun.DA_Weapon_Minigun name=DA_Weapon_Minigun nameSource=fullNameFallback, context=solo, lifecycleState=stable, playerStatePresent=true, role=solo-or-host
- Min numeric values: BaseMaxHealth=250, Crystals=0, CurrentHealth=18.423839569092, CurrentMaxHealth=250, MaxHealthMultiplier=1, NumAbilityModSlots=8, NumMeleeModSlots=8, NumPerkSlots=8, NumWeaponModSlots=8
- Max numeric values: BaseMaxHealth=288, Crystals=2962, CurrentHealth=344.40002441406, CurrentMaxHealth=345.60000610352, MaxHealthMultiplier=1, NumAbilityModSlots=12, NumMeleeModSlots=12, NumPerkSlots=24, NumWeaponModSlots=24
- Changed fields: AbilityDA, BaseMaxHealth, Crystals, CurrentHealth, CurrentMaxHealth, MeleeDA, NumAbilityModSlots, NumMeleeModSlots, NumPerkSlots, NumWeaponModSlots, WeaponDA
- Change counts: AbilityDA=2, BaseMaxHealth=5, Crystals=40, CurrentHealth=56, CurrentMaxHealth=6, MeleeDA=2, NumAbilityModSlots=1, NumMeleeModSlots=1, NumPerkSlots=1, NumWeaponModSlots=1, WeaponDA=2
- First/last context: solo / solo
- First/last role: solo-or-host / solo-or-host
- Slot model status: observed scalar slot counters / candidate unlocked or usable slot counters; locked/max/total slot model unresolved
- Writes/RPCs/HUD/deep arrays: no
- Inventory arrays/count/traversal/elements, InventoryInfo, Enhancements: no
- No crash dump is associated with the imported safe-scalar-watch evidence.

## Perk DataAsset Catalog

- Summary: perk_da_catalog_confirmed
- Perk DataAsset catalog status: perk_da_catalog_confirmed
- Discovery attempted: yes
- Catalog entries: 64
- Candidate count/cap: 64/64
- Rejected candidate count/cap: 0/16
- Top rejection reasons: none
- Perk-like class/name patterns: identity:PerkDataAsset, name:path-or-da-perk
- Field cap: 32
- Writes/RPCs/HUD/deep arrays: no
- Inventory arrays/count/traversal/elements, InventoryInfo, Enhancements: no
- DataAsset mutation/function calls/passive-only violation: no
- No crash dump is associated with the imported perk catalog evidence.
- Catalog evidence is read-path evidence only. It is not permission to mutate DataAssets.
- RuntimeProbe proves read paths only; future CrabModFramework / CrabTastyMod write or edit APIs must be designed and gated separately.
- TastyOrange is not special-cased by RuntimeProbe. It is cataloged as a normal perk if found.
- Collector is not special-cased by RuntimeProbe. It is cataloged as a normal perk if found.

## Max Safe Play Recorder

- Summary: max_safe_play_observed_change
- Max-safe play status: max_safe_play_observed_change
- Scalar samples/logged rows: 2/2
- First values: AbilityDA=exists=true isValid=true fullName=CrabAbilityDA /Game/Blueprint/Ability/DA_Ability_BlackHole.DA_Ability_BlackHole name=DA_Ability_BlackHole nameSource=fullNameFallback, BaseMaxHealth=250, Crystals=0, CurrentHealth=250, CurrentMaxHealth=250, MaxHealthMultiplier=1, MeleeDA=exists=true isValid=true fullName=CrabMeleeDA /Game/Blueprint/Melee/DA_Melee_Hammer.DA_Melee_Hammer name=DA_Melee_Hammer nameSource=fullNameFallback, NumAbilityModSlots=12, NumMeleeModSlots=12, NumPerkSlots=24, NumWeaponModSlots=24, WeaponDA=exists=true isValid=true fullName=CrabWeaponDA /Game/Blueprint/Weapon/LightningScepter/DA_Weapon_LightningScepter.DA_Weapon_LightningScepter name=DA_Weapon_LightningScepter nameSource=fullNameFallback, context=solo, lifecycleState=stable, playerStatePresent=true, role=solo-or-host
- Latest values: AbilityDA=exists=true isValid=true fullName=CrabAbilityDA /Game/Blueprint/Ability/DA_Ability_BlackHole.DA_Ability_BlackHole name=DA_Ability_BlackHole nameSource=fullNameFallback, BaseMaxHealth=250, Crystals=0, CurrentHealth=250, CurrentMaxHealth=250, MaxHealthMultiplier=1, MeleeDA=exists=true isValid=true fullName=CrabMeleeDA /Game/Blueprint/Melee/DA_Melee_Hammer.DA_Melee_Hammer name=DA_Melee_Hammer nameSource=fullNameFallback, NumAbilityModSlots=12, NumMeleeModSlots=12, NumPerkSlots=24, NumWeaponModSlots=24, WeaponDA=exists=true isValid=true fullName=CrabWeaponDA /Game/Blueprint/Weapon/Minigun/DA_Weapon_Minigun.DA_Weapon_Minigun name=DA_Weapon_Minigun nameSource=fullNameFallback, context=solo, lifecycleState=stable, playerStatePresent=true, role=solo-or-host
- Min numeric values: BaseMaxHealth=250, Crystals=0, CurrentHealth=250, CurrentMaxHealth=250, MaxHealthMultiplier=1, NumAbilityModSlots=12, NumMeleeModSlots=12, NumPerkSlots=24, NumWeaponModSlots=24
- Max numeric values: BaseMaxHealth=250, Crystals=0, CurrentHealth=250, CurrentMaxHealth=250, MaxHealthMultiplier=1, NumAbilityModSlots=12, NumMeleeModSlots=12, NumPerkSlots=24, NumWeaponModSlots=24
- Changed fields: WeaponDA
- Change counts: WeaponDA=1
- Perk catalog snapshots: 1
- Perk DA candidate count: 64
- Perk DA entry count: 64
- Perk DA rejected candidate count: 0
- Perk DA top rejection reasons: none
- Perk-like class/name patterns: identity:PerkDataAsset, name:path-or-da-perk
- TastyOrange found as normal entry: yes
- Collector found as normal entry: no
- Nil/error counts: 0/0
- Writes/RPCs/HUD/deep arrays: no
- Inventory arrays/count/traversal/elements, InventoryInfo, Enhancements: no
- DataAsset mutation/function calls/passive-only violation: no

## Confirmed Unsafe Paths

- HUD ReceiveDrawHUD tick hook remains blocked by default.
- `FindFirstOf.CrabHC` is not confirmed as a player-health source; imported evidence has seen an unscoped destructible/barrel candidate.
- Writes and RPCs are disabled and are outside this campaign version.

## Untested Paths

- Vanilla multiplayer local PlayerState health visibility is confirmed only after `multiplayer-health-playerstate-watch` evidence exists; pooled/shared health is a CrabInvSync design concept, not vanilla RuntimeProbe evidence.
- Multiplayer roster identity is only complete after visible roster evidence exists; local PlayerState identity alone is partial evidence.
- Roster candidate probes currently include GameState/GameStateBase source identity, CrabGS source identity, PlayerArray shape, capped FindAll PlayerState-like candidates, capped PlayerController/CrabPC candidates, and a capped visible players source candidate.
- Local crystals are covered only by `crystals-read`; remote crystals remain covered separately by `multiplayer-resource-visibility-read` after imported resource visibility evidence exists.
- Locked slots remain unresolved; no separate locked/max/total slot-capacity field is present in the tracked objectdump-derived notes, so locked slots may be UI-derived or stored elsewhere.
- `NumWeaponModSlots`, `NumAbilityModSlots`, `NumMeleeModSlots`, and `NumPerkSlots` are only observed scalar slot counters / candidate unlocked slot counters. They are not proven total capacity or locked-slot state.
- Local inventory array shallow/count visibility is covered by `local-inventory-array-shallow-read`; property-shape confirmation is covered by `local-inventory-array-shape-confirm`; userdata wrapper metadata is covered by `local-inventory-userdata-introspection`; wrapper count metadata is covered by `inventory-array-count-read`.
- Item contents are still not proven; userdata/count metadata does not read item data asset fields or element contents.
- Perk DataAsset catalog evidence, when present, proves only curated read paths for future CrabModFramework / CrabTastyMod design; controlled write/edit APIs must be built separately.
- `InventoryInfo` and enhancements remain placeholders until explicit probe sets are implemented.
- Deep arrays and InventoryInfo gates remain off until their explicit reviewed phases.

## Safety Gate Summary

- Default config remains `tickDriver = none`, `probeSet = shallow-core`, and all research gates false.
- Campaign read phases never enable writes, RPCs, or HUD hooks.
- `allowHealthProbes` is enabled only for explicit health phases and `multiplayer-resource-visibility-read` health scalar checks.
- `allowIdentityProbes` is enabled only for the explicit multiplayer roster and resource visibility phases; `allowRawIdentityEvidence` remains false by default.
- `allowResourceVisibilityProbes` is enabled only for `multiplayer-resource-visibility-read`.
- `allowCrystalsReadProbes` is enabled only for `crystals-read`.
- `allowSlotsReadProbes` is enabled only for `slots-read`.
- `allowSafeScalarWatchProbes` is enabled only for `safe-scalar-watch`.
- `allowPerkDataAssetCatalogProbes` is enabled only for `perk-da-catalog-read`.
- `allowMaxSafePlayRecorderProbes` is enabled only for the direct `max-safe-play-recorder` profile.
- `allowInventoryArrayShallowProbes` is enabled only for `local-inventory-array-shallow-read`.
- `allowInventoryArrayShapeConfirmProbes` is enabled only for `local-inventory-array-shape-confirm`.
- `allowInventoryUserdataIntrospectionProbes` is enabled only for `local-inventory-userdata-introspection`.
- `allowInventoryArrayCountProbes` is enabled only for `inventory-array-count-read`.
- `allowDeepArrayProbes` and `allowInventoryInfoProbes` are not enabled by implemented phases.
