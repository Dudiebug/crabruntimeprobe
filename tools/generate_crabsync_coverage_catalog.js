#!/usr/bin/env node
'use strict';

/**
 * Generate the exhaustive, read-only CrabSync evidence coverage catalog.
 *
 * Full scan:
 *   node tools/generate_crabsync_coverage_catalog.js --dump <UE4SS_ObjectDump.txt> [--reference <file-or-directory>]
 *
 * Runtime evidence refresh (does not rescan or claim to rescan the dump):
 *   node tools/generate_crabsync_coverage_catalog.js --refresh-runtime
 *
 * Validation / reproducibility:
 *   node tools/generate_crabsync_coverage_catalog.js --validate
 *   node tools/generate_crabsync_coverage_catalog.js --check --dump <dump> [--reference <file-or-directory>]
 *
 * This generator deliberately uses only Node built-ins.  It never opens a game
 * process, enumerates live UObjects, calls a UFunction, or writes gameplay state.
 */

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const assert = require('assert');

const ROOT = path.resolve(__dirname, '..');
const OUTPUTS = {
  catalogJson: path.join(ROOT, 'campaign', 'crabsync_coverage_catalog.json'),
  catalogCsv: path.join(ROOT, 'campaign', 'crabsync_coverage_catalog.csv'),
  profile: path.join(ROOT, 'campaign', 'crabsync-full-observe.profile.json'),
  checklist: path.join(ROOT, 'campaign', 'crabsync-full-observe.checklist.json'),
  documentation: path.join(ROOT, 'docs', 'CRABSYNC_COVERAGE_CATALOG.md'),
  schema: path.join(ROOT, 'schemas', 'coverage-catalog-v1.schema.json'),
  lua: path.join(ROOT, 'client', 'Mods', 'CrabRuntimeProbe', 'Scripts', 'crabsync_catalog.lua')
};

const SCHEMA_VERSION = 'coverage-catalog-v1';
const PROFILE_VERSION = 'crabsync-full-observe-v1';
const RUNTIME_CONTRACT = Object.freeze({
  snapshotSampleIntervalSeconds: 3,
  snapshotStableSamplesRequired: 10,
  snapshotStableDwellSeconds: 30,
  snapshotUnchangedHeartbeatSeconds: 30,
  fullObserveHeartbeatSeconds: 1,
  fullObserveInventoryIntervalSeconds: 2,
  fullObserveInventoryHeartbeatSeconds: 30,
  fullObserveCleanSamplesRequired: 3,
  fullObserveStableSamplesRequired: 3,
  fullObserveStableDwellSeconds: 2,
  fullObserveHookGlobalRowCap: 2048,
  fullObserveHookPerDescriptorRowCap: 128,
  fullObserveHookMinIntervalSeconds: 1,
  fullObserveHookTrackedDescriptorCap: 128,
  fullObserveSlotStabilityWindowSeconds: 30,
  fullObserveSlotStabilitySamplesRequired: 5,
  fullObserveMaxInventoryItems: 32,
  fullObserveMaxEnhancements: 16,
  fullObserveMaxStageRowsPerCategory: 256,
  maximumResolvedClassesPerGeneration: 128,
  maximumFunctionsPerResolvedClass: 128
});

const INVENTORY_STAGE_TARGETS = Object.freeze({
  WeaponMods: Object.freeze({ itemOwner: 'CrabWeaponMod', dataAssetField: 'WeaponModDA' }),
  AbilityMods: Object.freeze({ itemOwner: 'CrabAbilityMod', dataAssetField: 'AbilityModDA' }),
  MeleeMods: Object.freeze({ itemOwner: 'CrabMeleeMod', dataAssetField: 'MeleeModDA' }),
  Perks: Object.freeze({ itemOwner: 'CrabPerk', dataAssetField: 'PerkDA' }),
  Relics: Object.freeze({ itemOwner: 'CrabRelic', dataAssetField: 'RelicDA' })
});
const TERMINAL_DISPOSITIONS = new Set([
  'confirmed-clean-evidence',
  'rejected-unsafe',
  'unsupported',
  'excluded-product-policy'
]);

const PROPERTY_TYPES = new Set([
  'Property', 'ObjectProperty', 'StructProperty', 'ArrayProperty', 'ByteProperty',
  'UInt32Property', 'UInt64Property', 'UInt16Property', 'FloatProperty',
  'DoubleProperty', 'IntProperty', 'Int64Property', 'Int16Property', 'Int8Property',
  'BoolProperty', 'EnumProperty', 'NameProperty', 'StrProperty', 'TextProperty',
  'ClassProperty', 'SoftObjectProperty', 'SoftClassProperty', 'WeakObjectProperty',
  'InterfaceProperty', 'MapProperty', 'SetProperty', 'MulticastInlineDelegateProperty',
  'MulticastSparseDelegateProperty', 'DelegateProperty'
]);

const EXPLICIT_OWNER_ROOTS = new Set([
  'CrabPS', 'CrabPC', 'CrabPlayerC', 'CrabHC', 'CrabGS', 'CrabGM', 'CrabGI', 'CrabSG',
  'CrabAnvil', 'CrabChest', 'CrabPortal', 'CrabShopPedestal', 'CrabInteractPickup',
  'CrabOverlapPickup', 'CrabCrystalPickup', 'CrabHealthPickup', 'CrabInventoryUI',
  'CrabInventorySlotUI', 'CrabInventoryEventUI', 'CrabGameplayUI', 'CrabGameStateUI',
  'CrabPlayerStateUI', 'CrabInventoryDA', 'CrabPickupDA', 'CrabWeaponDA', 'CrabAbilityDA',
  'CrabMeleeDA'
]);

const ENGINE_OWNER_ROOTS = new Set([
  'Actor', 'Pawn', 'Controller', 'PlayerController', 'PlayerState', 'GameState',
  'GameStateBase', 'GameModeBase', 'GameInstance', 'HUD'
]);

const OWNER_RELEVANCE = /(?:Pickup|Inventory|Shop|Anvil|UpgradeTotem|Totem|Chest|Portal|AutoSave|SaveGame|Health|Armor|Shield|Weapon|Ability|Melee|Perk|Relic|Reward|PlayerState|GameState)/i;
const MEMBER_RELEVANCE = /(?:weapon|ability|melee|perk|relic|inventory|pickup|equip|loadout|starting|default|slot|locked|usable|maximum|maxslots|cost|crystal|currency|spend|reward|shop|totem|anvil|chest|portal|island|rarity|level|buff|enhance|stack|duplicate|cooldown|drop|remove|salvage|reroll|replace|order|health|armor|shield|heal|regen|damage|eliminat|death|reviv|respawn|outofbounds|out_of_bounds|interact|autoloot|save|restore|persist|reconnect|disconnect|join|leave|travel|playerstate|gamestate|owner|authority|replic|onrep|multicast|refresh.*ui|clienton|clientpreportal|clientpostportal|clearedisland|beginplay|endplay|destroyed|spawned)/i;
const BLUEPRINT_PATH_RELEVANCE = /(?:\/UI\/(?:Mode\/Survival|Interact)|Inventory|Pickup|Shop|Anvil|Totem|Chest|Portal|PlayerState|GameState|AutoSave|Health|Armor|Weapon|Ability|Melee|Perk|Relic|Reward|Island)/i;
const GENERATED_BLUEPRINT_FUNCTION = /(?:ExecuteUbergraph|EvaluateGraphExposedInputs|AnimGraph)/i;
const RAW_IDENTITY = /(?:UniqueId|PlayerName|Steam|PlatformId|OnlineId|Identity)/i;
const KEYS_POLICY = /(?:^|[^A-Za-z])Keys?(?:$|[^A-Za-z])/i;

function isKeysPolicyCandidate(entry) {
  const member = entry.member || '';
  const owner = entry.owner || '';
  if (/BP_(?:Chest|Totem)_Key/i.test(owner)) return true;
  if (entry.moduleOrPackage !== 'CrabChampions') return false;
  const argumentNames = (entry.argumentSchema || []).map((arg) => arg.name).join(' ');
  return /^(?:Keys|OnRep_Keys|KeysText|KeysDifferenceText|OnUpdatedKeysAnim)$/.test(member) ||
    /(?:^|_)NewKeys(?:$|_)/.test(argumentNames) ||
    /(?:KeyTotem|KeyChest|KeyIcon)/.test(`${owner}.${member}`) ||
    (KEYS_POLICY.test(member) && /(?:CrabPS|CrabSG|AutoSave)/.test(owner));
}

const CATEGORY_DEFINITIONS = [
  ['inventory-enhancements', 'Inventory enhancements', /(?:enhance|anvil|AccumulatedBuff)/i],
  ['inventory-metadata', 'Inventory metadata', /(?:InventoryInfo|InventoryCooldown|ItemRarity|ItemLevel|AccumulatedBuff|DuplicateSemantics|StackSemantics|ItemCooldown)/i],
  ['inventory-slots', 'Inventory slots', /(?:Num\w*Slots|slot|locked|usable|Cost)/i],
  ['crystals-economy', 'Crystals and economy', /(?:crystal|currency|spend|shop|reward|cost)/i],
  ['health-armor', 'Health, armor, shields, and elimination', /(?:health|armor|shield|heal|regen|damage|eliminat|death|reviv|respawn|outofbounds|out_of_bounds)/i],
  ['pickups-transactions', 'Pickups and item transactions', /(?:pickup|interact|autoloot|drop|remove|salvage|reroll|replace)/i],
  ['equipment-starting', 'Equipment and starting/default behavior', /(?:WeaponDA|AbilityDA|MeleeDA|equip|loadout|starting|default)/i],
  ['shops-chests-totems', 'Shops, chests, totems, and rewards', /(?:shop|chest|totem|reward|pedestal)/i],
  ['portal-island-lifecycle', 'Portal, island, and run lifecycle', /(?:portal|island|travel|join|leave|disconnect|reconnect|beginplay|endplay|spawn|destroy)/i],
  ['save-persistence', 'Save, restore, and persistence', /(?:save|restore|persist)/i],
  ['multiplayer-ownership', 'Multiplayer ownership, authority, and visibility', /(?:PlayerState|GameState|owner|authority|role|PlayerArray|PawnPrivate)/i],
  ['replication-rpc-events', 'Replication, RPCs, and natural events', /(?:OnRep|Server[A-Z]|Client[A-Z]|Multicast|replic)/],
  ['ui-follow-up', 'UI follow-up and player feedback', /(?:UI|Widget|HUD|Refresh|InventoryEvent)/i],
  ['weapons-abilities-melee', 'Weapon, ability, and melee behavior', /(?:weapon|ability|melee|projectile|strike)/i],
  ['inventory-items', 'All five inventory item categories', /(?:WeaponMods|AbilityMods|MeleeMods|Perks|Relics|Inventory)/i],
  ['player-runtime-state', 'Player and GameState runtime state', /(?:CrabPS|CrabPC|CrabPlayerC|CrabGS|CrabGM|CrabGI|CrabHC)/i]
];

const PRE_POST_FIELDS = {
  'inventory-items': ['CrabPS.WeaponMods.count', 'CrabPS.AbilityMods.count', 'CrabPS.MeleeMods.count', 'CrabPS.Perks.count', 'CrabPS.Relics.count'],
  'inventory-metadata': ['inventory.itemDAIdentity', 'inventory.InventoryInfo.Level', 'inventory.InventoryInfo.AccumulatedBuff', 'inventory.slotIndex'],
  'inventory-enhancements': ['inventory.InventoryInfo.Enhancements.shape', 'inventory.InventoryInfo.Enhancements.count', 'inventory.InventoryInfo.Enhancements.values'],
  'inventory-slots': ['CrabPS.NumWeaponModSlots', 'CrabPS.NumAbilityModSlots', 'CrabPS.NumMeleeModSlots', 'CrabPS.NumPerkSlots'],
  'crystals-economy': ['CrabPS.Crystals'],
  'health-armor': ['CrabPS.HealthInfo.CurrentHealth', 'CrabPS.HealthInfo.CurrentMaxHealth', 'CrabPS.BaseMaxHealth', 'CrabPS.MaxHealthMultiplier', 'CrabPS.HealthInfo.CurrentArmorPlates', 'CrabPS.HealthInfo.CurrentArmorPlateHealth'],
  'pickups-transactions': ['pickup.safeObjectSummary', 'pickup.PickupInfo.shape', 'inventory.categoryCounts', 'CrabPS.Crystals'],
  'equipment-starting': ['CrabPS.WeaponDA', 'CrabPS.AbilityDA', 'CrabPS.MeleeDA'],
  'shops-chests-totems': ['CrabPS.Crystals', 'inventory.categoryCounts', 'inventory.slotCounts'],
  'portal-island-lifecycle': ['context.world', 'context.role', 'context.lifecycleGeneration', 'CrabGS.CurrentIsland', 'CrabGS.MatchState'],
  'save-persistence': ['context.lifecycleGeneration', 'CrabAutoSave.shape', 'CrabPS.inventoryAndResources'],
  'multiplayer-ownership': ['context.role', 'context.localPlayerStateFingerprint', 'context.visiblePlayerFingerprints', 'context.lifecycleGeneration'],
  'replication-rpc-events': ['context.role', 'context.world', 'context.lifecycleGeneration'],
  'ui-follow-up': ['ui.followUpEvent', 'ui.refreshCount'],
  'weapons-abilities-melee': ['CrabPS.WeaponDA', 'CrabPS.AbilityDA', 'CrabPS.MeleeDA', 'combat.cooldowns'],
  'player-runtime-state': ['context.role', 'context.world', 'context.lifecycleGeneration', 'context.localPlayerStateFingerprint']
};

const OFFICIAL_APPLY_FUNCTIONS = /^(?:Server(?:EquipInventory|SetWeaponDA|SetAbilityDA|SetMeleeDA|IncrementNumInventorySlots|RemoveWeaponMod|RemoveAbilityMod|RemoveMeleeMod|RemovePerk|RemoveRelic|Interact|AutoLoot|DropPickup|ApplyEnhancement|UpgradeTotemPurchase|Salvage|RestoreAutoSave|DealDamage|DealFallDamage)|MulticastApplyEnhancement)$/;

// Engine UFunctions are not implicitly safe just because their names match a
// lifecycle term.  Only this deliberately tiny, reviewed set can become a
// passive descriptor; all other Engine rows remain catalog-only.
const REVIEWED_ENGINE_PASSIVE_HOOKS = new Set([
  '/Script/Engine.Pawn:OnRep_PlayerState',
  '/Script/Engine.PlayerController:ClientRestart',
  '/Script/Engine.PlayerController:ClientRetryClientRestart',
  '/Script/Engine.PlayerState:OnRep_bIsInactive',
  '/Script/Engine.PlayerState:ReceiveCopyProperties',
  '/Script/Engine.PlayerState:ReceiveOverrideWith',
  '/Script/Engine.GameStateBase:OnRep_ReplicatedHasBegunPlay'
]);

const REQUIRED_PASSIVE_HOOKS = new Set([
  '/Script/CrabChampions.CrabPS:ServerIncrementNumInventorySlots',
  '/Script/CrabChampions.CrabPS:ServerEquipInventory',
  '/Script/CrabChampions.CrabPS:ServerSetWeaponDA',
  '/Script/CrabChampions.CrabPS:ServerSetAbilityDA',
  '/Script/CrabChampions.CrabPS:ServerSetMeleeDA',
  '/Script/CrabChampions.CrabPS:ServerRemoveWeaponMod',
  '/Script/CrabChampions.CrabPS:ServerRemoveAbilityMod',
  '/Script/CrabChampions.CrabPS:ServerRemoveMeleeMod',
  '/Script/CrabChampions.CrabPS:ServerRemovePerk',
  '/Script/CrabChampions.CrabPS:ServerRemoveRelic',
  '/Script/CrabChampions.CrabPS:OnRep_Inventory',
  '/Script/CrabChampions.CrabPS:OnRep_Crystals',
  '/Script/CrabChampions.CrabPC:ClientOnPickedUpPickup',
  '/Script/CrabChampions.CrabPC:ClientRefreshPSUI',
  '/Script/CrabChampions.CrabPlayerC:ServerInteract',
  '/Script/CrabChampions.CrabPlayerC:ServerAutoLoot',
  '/Script/CrabChampions.CrabPlayerC:ServerDropPickup',
  '/Script/CrabChampions.CrabPlayerC:ServerApplyEnhancement',
  '/Script/CrabChampions.CrabPlayerC:ServerUpgradeTotemPurchase',
  '/Script/CrabChampions.CrabPlayerC:ServerDealDamage',
  '/Script/CrabChampions.CrabPlayerC:ServerDealFallDamage',
  '/Script/CrabChampions.CrabAnvil:MulticastApplyEnhancement'
]);

// This callback was active in the 2026-07-10 crash dump's Lua hook stack.
// That does not prove it was the sole cause, but the current hook method is
// explicitly crash-suspect and must never be recommended by the normal guide.
const CURRENT_HOOK_METHOD_CRASH_CONTEXTS = new Set([
  '/Script/CrabChampions.CrabPS:OnRep_IslandRewardRarity'
]);

const DENIED_PASSIVE_HOOK_OWNERS = new Set([
  'CrabCosmeticSlotUI', 'CrabCosmeticsMenuUI', 'CrabDamageArea', 'CrabDestructible',
  'CrabEnemyC', 'CrabLM', 'CrabPhysicsActor', 'CrabPlayerAnimInstance',
  'CrabTargetDummyC', 'CrabTurret', 'CrabVideoMenuUI'
]);

function nativeHookIsMateriallyRelevant(row) {
  if (DENIED_PASSIVE_HOOK_OWNERS.has(row.owner)) return false;
  const member = row.member || '';
  if (/(?:^Debug|^Test|Chat|Cosmetic|UploadLobbyStats|StartSlomoRamp|SetIsCharacterInputEnabled|SpectateNextPlayer|NavigationGenerationFinished)/i.test(member)) return false;
  if (/^(?:Server|Multicast)(?:Dash|Flip|Ping|StartAim|StopAim|StartSlide|StopSlide)$/.test(member)) return false;
  if (/^OnRep_(?:PingLoc|IsAiming|IsSliding|SlideDamageIteration|CrabSkin|IsBananaActive)$/.test(member)) return false;
  if (/^Multicast(?:HideStalePing|ShockwaveFX|SonicBoomFX)$/.test(member)) return false;
  if (/^(?:ClientOnTriggeredRingOfDestruction|OnRep_ChainedToC)$/.test(member)) return false;
  return true;
}

function parseArgs(argv) {
  const result = { references: [], check: false, validate: false, refreshRuntime: false, dryRun: false, selfTest: false };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--dump') result.dump = argv[++i];
    else if (arg === '--reference') result.references.push(argv[++i]);
    else if (arg === '--refresh-runtime') result.refreshRuntime = true;
    else if (arg === '--check') result.check = true;
    else if (arg === '--validate') result.validate = true;
    else if (arg === '--dry-run') result.dryRun = true;
    else if (arg === '--self-test') result.selfTest = true;
    else if (arg === '--help' || arg === '-h') result.help = true;
    else throw new Error(`Unknown argument: ${arg}`);
  }
  if (result.refreshRuntime && result.dump) throw new Error('--refresh-runtime cannot be combined with --dump; it intentionally preserves checked-in dump provenance.');
  if (result.dryRun && result.check) throw new Error('--dry-run and --check are mutually exclusive.');
  return result;
}

function usage() {
  return [
    'Usage:',
    '  node tools/generate_crabsync_coverage_catalog.js --dump <UE4SS_ObjectDump.txt> [--reference <file-or-directory>] [--check|--dry-run]',
    '  node tools/generate_crabsync_coverage_catalog.js --refresh-runtime [--check|--dry-run]',
    '  node tools/generate_crabsync_coverage_catalog.js --validate',
    '  node tools/generate_crabsync_coverage_catalog.js --self-test'
  ].join('\n');
}

function sha256(value) {
  return crypto.createHash('sha256').update(value).digest('hex');
}

function normalizedLines(text) {
  if (text.length === 0) return [];
  const lines = text.split(/\r?\n/);
  if (lines[lines.length - 1] === '') lines.pop();
  return lines;
}

function fileRecord(filePath, logicalName, classification) {
  const buffer = fs.readFileSync(filePath);
  const text = buffer.toString('utf8');
  return {
    logicalName,
    classification,
    sha256: sha256(buffer),
    byteSize: buffer.length,
    lineCount: normalizedLines(text).length,
    text,
    mtimeMs: fs.statSync(filePath).mtimeMs
  };
}

function walkFiles(start, predicate) {
  if (!fs.existsSync(start)) return [];
  const stat = fs.statSync(start);
  if (stat.isFile()) return predicate(start) ? [start] : [];
  const found = [];
  for (const entry of fs.readdirSync(start, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    const full = path.join(start, entry.name);
    if (entry.isDirectory()) found.push(...walkFiles(full, predicate));
    else if (entry.isFile() && predicate(full)) found.push(full);
  }
  return found;
}

function repositoryDocuments() {
  const candidates = [path.join(ROOT, 'README.md'), path.join(ROOT, 'CODEX.md')];
  candidates.push(...walkFiles(path.join(ROOT, 'docs'), (p) => /\.md$/i.test(p) && path.basename(p) !== 'CRABSYNC_COVERAGE_CATALOG.md'));
  candidates.push(...walkFiles(path.join(ROOT, 'wiki-src'), (p) => /\.md$/i.test(p)));
  return [...new Set(candidates.filter((p) => fs.existsSync(p)))].sort().map((p) =>
    fileRecord(p, path.relative(ROOT, p).replace(/\\/g, '/'), 'runtimeprobe_documentation'));
}

function referenceDocuments(referencePaths) {
  const files = [];
  for (const referencePath of referencePaths) {
    if (!referencePath) continue;
    if (!fs.existsSync(referencePath)) throw new Error(`Reference input does not exist: ${referencePath}`);
    files.push(...walkFiles(referencePath, (p) => /(?:OBJECTDUMP_NOTES|OBJECTDUMP_QUICK_REFERENCE)\.md$/i.test(path.basename(p))));
    if (fs.statSync(referencePath).isFile() && !files.includes(referencePath)) files.push(referencePath);
  }
  return [...new Set(files)].sort().map((p) =>
    fileRecord(p, `legacy/${path.basename(p)}`, 'legacy_unsafe_reference'));
}

function evidenceDocuments() {
  const files = walkFiles(path.join(ROOT, 'evidence'), (p) => /\.jsonl$/i.test(p));
  return files.sort().map((p) => fileRecord(p, path.relative(ROOT, p).replace(/\\/g, '/'), 'runtime_evidence'));
}

function publicFileRecord(record) {
  const { text: _text, mtimeMs: _mtimeMs, ...publicRecord } = record;
  return publicRecord;
}

function aggregateHash(records) {
  return sha256(records.map((record) => `${record.logicalName}\0${record.sha256}\0${record.lineCount}`).join('\n'));
}

function parseMetadata(rawLine) {
  const result = {};
  for (const match of rawLine.matchAll(/\[([a-z]+):\s*([^\]]+)\]/gi)) result[match[1].toLowerCase()] = match[2].trim();
  return result;
}

function parseSymbolPath(symbolPath) {
  const namespaceMatch = symbolPath.match(/^\/(Script|Game)\/(.+)$/);
  if (!namespaceMatch) return null;
  const namespace = namespaceMatch[1];
  const rest = namespaceMatch[2];
  const dotIndex = rest.indexOf('.');
  if (dotIndex < 0) return { namespace, moduleOrPackage: rest, owner: null, member: null, parameter: null };
  const moduleOrPackage = rest.slice(0, dotIndex);
  const ownerAndMembers = rest.slice(dotIndex + 1).split(':');
  return {
    namespace,
    moduleOrPackage,
    owner: ownerAndMembers[0] || null,
    member: ownerAndMembers[1] || null,
    parameter: ownerAndMembers.length > 2 ? ownerAndMembers.slice(2).join(':') : null
  };
}

function parseDump(buffer) {
  const text = buffer.toString('utf8');
  const lines = normalizedLines(text);
  const entries = [];
  const addressToEntry = new Map();
  let currentEnum = null;
  for (let index = 0; index < lines.length; index += 1) {
    const rawLine = lines[index];
    const head = rawLine.match(/^\[([0-9A-Fa-f]+)\]\s+([A-Za-z0-9_]+)\s+/);
    const symbolMatch = rawLine.match(/\/(?:Script|Game)\/[^\s\]]+/);
    if (currentEnum && !symbolMatch) {
      // UE4SS emits enum members immediately after the UEnum line as zero-address
      // records such as `ECrabDifficultyModifier::LockedSlots ... [v: 4]`.
      const valueMatch = rawLine.match(/^\[0+\]\s+([^\s]+)::([^\s]+)\s+.*\[v:\s*(-?\d+)\]/);
      if (valueMatch && ownerShortName(currentEnum.owner) === valueMatch[1]) {
        entries.push({
          address: `ENUM-${currentEnum.address}-${index + 1}`,
          dumpType: 'EnumValue',
          symbolPath: `${currentEnum.symbolPath}::${valueMatch[2]}`,
          lineNumber: index + 1,
          rawLineHash: sha256(rawLine),
          rawLine,
          metadata: parseMetadata(rawLine),
          isContainerInner: false,
          namespace: currentEnum.namespace,
          moduleOrPackage: currentEnum.moduleOrPackage,
          owner: currentEnum.owner,
          member: valueMatch[2],
          parameter: null,
          enumPath: currentEnum.symbolPath,
          enumValue: Number(valueMatch[3])
        });
        continue;
      }
    }
    if (!head || !symbolMatch) {
      currentEnum = null;
      continue;
    }
    const symbolPath = symbolMatch[0].replace(/[,;]+$/, '');
    const parsed = parseSymbolPath(symbolPath);
    if (!parsed) continue;
    const entry = {
      address: head[1].toUpperCase(),
      dumpType: head[2],
      symbolPath,
      lineNumber: index + 1,
      rawLineHash: sha256(rawLine),
      rawLine,
      metadata: parseMetadata(rawLine),
      isContainerInner: /^\[[^\]]+\]\s+\w+Property\s+[^\s]+\.\/(?:Script|Game)\//.test(rawLine),
      ...parsed
    };
    entries.push(entry);
    addressToEntry.set(entry.address, entry);
    currentEnum = entry.dumpType === 'Enum' ? entry : null;
  }

  for (const entry of entries) {
    for (const key of ['pc', 'ss', 'em', 'mc']) {
      const pointer = entry.metadata[key] && entry.metadata[key].toUpperCase();
      if (pointer && addressToEntry.has(pointer)) entry[`${key}Path`] = addressToEntry.get(pointer).symbolPath;
    }
    if (entry.metadata.sps) {
      const superEntry = addressToEntry.get(entry.metadata.sps.toUpperCase());
      entry.superPath = superEntry ? superEntry.symbolPath : null;
    }
  }

  return { lines, entries, addressToEntry };
}

function isClass(entry) {
  return entry.dumpType === 'Class' || entry.dumpType === 'BlueprintGeneratedClass' || entry.dumpType === 'WidgetBlueprintGeneratedClass';
}

function isStruct(entry) {
  return entry.dumpType === 'ScriptStruct' || entry.dumpType === 'Struct';
}

function isFunction(entry) {
  return entry.dumpType === 'Function';
}

function isProperty(entry) {
  return PROPERTY_TYPES.has(entry.dumpType);
}

function isEnumValue(entry) {
  return entry.dumpType === 'EnumValue';
}

function ownerShortName(owner) {
  if (!owner) return '';
  const segments = owner.split('.');
  return segments[segments.length - 1];
}

function createDefinitionMaps(entries) {
  const classByName = new Map();
  const structByName = new Map();
  const definitionByPath = new Map();
  for (const entry of entries) {
    if (!isClass(entry) && !isStruct(entry)) continue;
    const name = ownerShortName(entry.owner || entry.symbolPath.split('.').pop());
    definitionByPath.set(entry.symbolPath, entry);
    if (isClass(entry)) classByName.set(name, entry);
    else structByName.set(name, entry);
  }
  return { classByName, structByName, definitionByPath };
}

function actorDefinition(entry, definitionByPath) {
  if (!isClass(entry)) return false;
  const seen = new Set();
  let cursor = entry;
  while (cursor && !seen.has(cursor.symbolPath)) {
    if (cursor.symbolPath === '/Script/Engine.Actor') return true;
    seen.add(cursor.symbolPath);
    cursor = cursor.superPath ? definitionByPath.get(cursor.superPath) : null;
  }
  return false;
}

function ownerIsRelevant(owner, namespace) {
  const short = ownerShortName(owner);
  if (EXPLICIT_OWNER_ROOTS.has(short)) return true;
  if (namespace === 'Script' && OWNER_RELEVANCE.test(short)) return true;
  if (namespace === 'Game' && BLUEPRINT_PATH_RELEVANCE.test(owner || '')) return true;
  return false;
}

function entryIsRelevant(entry, definitions) {
  const owner = ownerShortName(entry.owner);
  const fullText = `${entry.symbolPath} ${owner} ${entry.member || ''}`;
  if (entry.parameter) return false;
  if (entry.moduleOrPackage === 'CrabChampions') {
    if (EXPLICIT_OWNER_ROOTS.has(owner)) return true;
    if (ownerIsRelevant(entry.owner, entry.namespace)) return true;
    if (MEMBER_RELEVANCE.test(entry.member || '')) return true;
  }
  if (entry.namespace === 'Game') {
    if (isFunction(entry) && GENERATED_BLUEPRINT_FUNCTION.test(entry.member || '')) return false;
    return BLUEPRINT_PATH_RELEVANCE.test(entry.symbolPath) && MEMBER_RELEVANCE.test(`${entry.member || ''} ${entry.owner || ''}`);
  }
  if (entry.namespace === 'Script' && ENGINE_OWNER_ROOTS.has(owner)) {
    if (entry.symbolPath === '/Script/Engine.HUD:ReceiveDrawHUD') return true;
    return MEMBER_RELEVANCE.test(entry.member || '');
  }
  if (isClass(entry) && actorDefinition(entry, definitions.definitionByPath)) {
    return ownerIsRelevant(entry.owner || entry.symbolPath.split('.').pop(), entry.namespace);
  }
  return false;
}

function categoryFor(text, owner) {
  const combined = `${owner || ''} ${text || ''}`;
  const ownerName = ownerShortName(owner);
  if (/(?:Enhancements?|ApplyEnhancement|MulticastApplyEnhancement|AccumulatedBuff|CrabAnvil)/i.test(combined)) return 'inventory-enhancements';
  if (/CrabInventoryInfo|CrabInventoryCooldown|InventoryInfo|(?:InventoryDA|PickupDA).*(?:Rarity|Cooldown|Stack|Level|Buff)/i.test(combined)) return 'inventory-metadata';
  if (/(?:Num[A-Za-z]*Slots|InventorySlot|LockedSlot|UsableSlot|Max(?:imum)?[A-Za-z]*Slots|SlotCost|IncrementNumInventorySlots)/i.test(combined)) return 'inventory-slots';
  if (/(?:^|[^A-Za-z])(?:WeaponMods|AbilityMods|MeleeMods|Perks|Relics)(?:$|[^A-Za-z])/i.test(combined) || /Crab(?:WeaponMod|AbilityMod|MeleeMod|Perk|Relic)$/.test(ownerName)) return 'inventory-items';
  for (const [id, _label, regex] of CATEGORY_DEFINITIONS) if (regex.test(combined)) return id;
  return 'player-runtime-state';
}

function typeForEntry(entry, ownerDefinition) {
  if (isClass(entry)) return 'actor';
  if (isEnumValue(entry)) return 'struct field';
  if (isFunction(entry)) {
    if (/^OnRep_/.test(entry.member || '')) return 'OnRep';
    if (/^Multicast/.test(entry.member || '')) return 'multicast';
    if (/^(?:Server|Client)/.test(entry.member || '')) return 'RPC';
    if (/^(?:Receive|On[A-Z])/.test(entry.member || '')) return 'event';
    return 'function';
  }
  if (isProperty(entry) && ownerDefinition && isStruct(ownerDefinition)) return 'struct field';
  return 'property';
}

function slug(value) {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 120);
}

function argumentSchemaFor(functionEntry, allEntries) {
  const prefix = `${functionEntry.symbolPath}:`;
  const params = allEntries.filter((entry) => isProperty(entry) && !entry.isContainerInner && entry.symbolPath.startsWith(prefix));
  return params.map((param) => {
    const name = param.parameter || param.symbolPath.slice(prefix.length);
    let redaction = 'none';
    let safeSummary = 'scalar';
    if (RAW_IDENTITY.test(name)) {
      redaction = 'fingerprint-only';
      safeSummary = 'redacted-fingerprint';
    } else if (/ObjectProperty|ClassProperty/.test(param.dumpType)) {
      redaction = 'object-identity-redacted';
      safeSummary = 'class-and-redacted-full-name';
    } else if (/ArrayProperty|MapProperty|SetProperty|StructProperty/.test(param.dumpType)) {
      redaction = 'nested-values-not-read';
      safeSummary = 'shape-and-count-only-until-staged-proof';
    } else if (/StrProperty|TextProperty|NameProperty/.test(param.dumpType)) {
      redaction = 'bounded-text-redaction';
      safeSummary = 'length-and-fingerprint';
    }
    return {
      name,
      direction: name === 'ReturnValue' ? 'return' : 'input-or-unknown',
      propertyType: param.dumpType,
      valueTypePath: param.pcPath || param.ssPath || param.emPath || param.mcPath || null,
      redaction,
      safeSummary,
      dumpLine: param.lineNumber
    };
  });
}

function parseEvidence(records) {
  const parsed = [];
  const parseErrors = [];
  for (const file of records) {
    const lines = normalizedLines(file.text);
    for (let i = 0; i < lines.length; i += 1) {
      const raw = lines[i].trim();
      if (!raw) continue;
      try {
        const value = JSON.parse(raw);
        value.__sourceFile = file.logicalName;
        value.__sourceLine = i + 1;
        parsed.push(value);
      } catch (error) {
        parseErrors.push({ logicalName: file.logicalName, lineNumber: i + 1, error: error.message });
      }
    }
  }
  return { parsed, parseErrors };
}

function normalizedRuntimeStatus(record) {
  return String(record.runtimeStatus || record.result || '').trim().toUpperCase();
}

function normalizedRoleKind(value) {
  const role = String(value || '').trim().toLowerCase();
  if (!role || /unknown|undetermined|auto|not-selected/.test(role)) return 'unknown';
  if (/joined|remote-client|client-only|non-authority|^client$/.test(role)) return 'joined-client';
  if (/host|listen-server|dedicated-server|server-authority|solo/.test(role)) return 'host-or-solo';
  return role;
}

function selectedObservedRolesCompatible(selectedRole, observedRole) {
  const selected = normalizedRoleKind(selectedRole);
  const observed = normalizedRoleKind(observedRole);
  return selected === 'unknown' || observed === 'unknown' || selected === observed;
}

function nestedBoolean(record, names) {
  for (const name of names) {
    if (typeof record[name] === 'boolean') return record[name];
  }
  return undefined;
}

function inventoryStageNumber(record) {
  const value = Number(record.inventoryStage);
  return Number.isInteger(value) && value >= 1 && value <= 14 ? value : null;
}

function stageTruncation(record) {
  const details = record.stageDetails && typeof record.stageDetails === 'object' ? record.stageDetails : {};
  if (typeof record.truncated === 'boolean') return record.truncated;
  if (typeof details.truncated === 'boolean') return details.truncated;
  if (details.iteration && typeof details.iteration.truncated === 'boolean') return details.iteration.truncated;
  return undefined;
}

function normalizeEvidenceRecord(record) {
  const status = normalizedRuntimeStatus(record);
  const selectedRole = record.selectedRole || '';
  const observedRole = record.observedRole || '';
  const roleMismatch = !selectedObservedRolesCompatible(selectedRole, observedRole);
  const markerText = [
    record.evidenceCleanliness, record.cleanliness, record.evidenceHealth,
    record.result, record.runtimeStatus, record.safetyStatus
  ].filter(Boolean).join(' ').toLowerCase();
  const crashSuspected = nestedBoolean(record, ['crashSuspected', 'crashDetected', 'crashEvidence', 'crash']) === true ||
    /(?:crash|callback[_ -]?error|lua[_ -]?error)/.test(markerText) || status === 'PASSIVE_CALLBACK_ERROR';
  const explicitlyDirty = nestedBoolean(record, ['dirtyEvidence', 'dirty', 'evidenceDirty', 'contaminated']) === true ||
    /(?:dirty|contaminated|invalid-evidence|unsafe-evidence)/.test(markerText);
  const safetyEnvelopeBroken = ['noWrites', 'noRpcs', 'noMutation', 'noHud'].some((field) => record[field] === false) ||
    record.rawIdentityEvidence === true;
  const dirty = crashSuspected || explicitlyDirty || safetyEnvelopeBroken || roleMismatch;
  const readOnlyEnvelope = ['noWrites', 'noRpcs', 'noMutation', 'noHud'].every((field) => record[field] === true) &&
    record.rawIdentityEvidence !== true;
  const passiveEnvelope = record.passiveOnly === true && record.runtimeInitiated === false;
  const stage = inventoryStageNumber(record);
  const stageStatus = String(record.inventoryStageStatus || record.stageStatus || '').trim().toLowerCase();
  const stageConfirmed = stage !== null && stageStatus === 'confirmed';
  const stageTenComplete = stage !== 10 || stageTruncation(record) === false;
  const readObserved = status === 'READ_OBSERVED' && !dirty && readOnlyEnvelope && passiveEnvelope &&
    (stage === null || (stageConfirmed && stageTenComplete));
  const naturalObserved = status === 'NATURALLY_OBSERVED' && record.event === 'PassiveHook.Observed' &&
    !dirty && readOnlyEnvelope && passiveEnvelope;
  const legacySafeRead = status === 'SAFE' && !dirty;
  const legacyNaturalObserved = legacySafeRead &&
    /(?:natural|ufunction)/i.test(`${record.accessKind || ''} ${record.event || ''}`) &&
    /(?:passive|vanilla)/i.test(`${record.mode || ''} ${record.initiator || ''}`);
  const roles = [...new Set([record.role, selectedRole, observedRole, record.contextRole]
    .filter((value) => value != null && String(value).trim()).map(String))];
  const lifecycleStates = [...new Set([
    record.lifecycleState,
    record.lifecycle && record.lifecycle.state,
    record.context && record.context.lifecycleState
  ].filter((value) => value != null && String(value).trim()).map(String))];
  return {
    status,
    clean: !dirty,
    dirty,
    crashSuspected,
    roleMismatch,
    readObserved,
    legacySafeRead,
    qualifyingRead: readObserved || legacySafeRead,
    naturalObserved: naturalObserved || legacyNaturalObserved,
    passiveCampaign: status === 'PASSIVE_CAMPAIGN' && !dirty && readOnlyEnvelope,
    runtimeDiscovered: ['RUNTIME_DISCOVERED', 'DISCOVERED_NEEDS_COVERAGE'].includes(status) && !dirty,
    unsupported: status === 'UNSUPPORTED' && !dirty,
    hookRegisteredOnly: status === 'HOOK_REGISTERED' || record.event === 'PassiveHook.Registration',
    argumentObserved: (naturalObserved || legacyNaturalObserved) &&
      (Array.isArray(record.arguments) ? record.arguments.length > 0 : Boolean(record.argumentSummary)),
    roles,
    lifecycleStates,
    stage,
    stageName: String(record.inventoryStageName || '').trim(),
    stageStatus,
    stageConfirmed,
    stageTenComplete,
    inventoryCategory: String(record.inventoryCategory || '').trim()
  };
}

function inventoryStageEvidenceKeys(record) {
  const target = INVENTORY_STAGE_TARGETS[String(record.inventoryCategory || '')];
  const stage = inventoryStageNumber(record);
  if (!target || stage === null) return [];
  const keys = new Set([`CrabPS.${record.inventoryCategory}`]);
  if (stage === 4) keys.add(`${target.itemOwner}.${target.dataAssetField}`);
  if (stage === 5) keys.add(`${target.itemOwner}.InventoryInfo`);
  if (stage === 6) {
    keys.add('CrabInventoryInfo.Level');
    keys.add('CrabInventoryInfo.AccumulatedBuff');
  }
  if (stage >= 7 && stage <= 9) keys.add('CrabInventoryInfo.Enhancements');
  return [...keys];
}

function evidenceKeys(record) {
  const keys = new Set();
  for (const value of [record.symbol, record.sourcePath, record.hookPath, record.discoveryDetails?.exactResolvedPath]) {
    if (typeof value === 'string' && value.trim()) keys.add(value.trim());
  }
  for (const key of inventoryStageEvidenceKeys(record)) keys.add(key);
  if (record.owner && record.member) {
    const members = String(record.member).split(/[\s,]+/).filter(Boolean);
    for (const member of members) keys.add(`${record.owner}.${member}`);
  }
  const owner = record.owner || record.sourceClass;
  for (const fieldName of ['fieldsReadable', 'arrayFieldNames', 'countResultFields']) {
    if (Array.isArray(record[fieldName]) && owner) for (const member of record[fieldName]) keys.add(`${owner}.${member}`);
  }
  if (record.arrayPropertiesPresent && owner && typeof record.arrayPropertiesPresent === 'object') {
    for (const member of Object.keys(record.arrayPropertiesPresent)) keys.add(`${owner}.${member}`);
  }
  if (record.fieldValues && owner && typeof record.fieldValues === 'object') {
    for (const member of Object.keys(record.fieldValues)) keys.add(`${owner}.${member}`);
  }
  if (record.fieldResults && owner && typeof record.fieldResults === 'object') {
    for (const member of Object.keys(record.fieldResults)) keys.add(`${owner}.${member}`);
  }
  if (Array.isArray(record.fieldsVisibleAcrossMultiple)) {
    for (const field of record.fieldsVisibleAcrossMultiple) {
      keys.add(`CrabPS.${field}`);
      const healthMatch = String(field).match(/^HealthInfo\.(.+)$/);
      if (healthMatch) keys.add(`CrabHealthInfo.${healthMatch[1]}`);
      const inventoryMatch = String(field).match(/^InventoryInfo\.(.+)$/);
      if (inventoryMatch) keys.add(`CrabInventoryInfo.${inventoryMatch[1]}`);
    }
  }
  for (const key of [...keys]) {
    const healthMatch = key.match(/(?:CrabPS|CrabHC)\.HealthInfo\.(.+)$/i);
    if (healthMatch) keys.add(`CrabHealthInfo.${healthMatch[1]}`);
    const inventoryMatch = key.match(/(?:Crab\w+|Runtime)\.InventoryInfo\.(.+)$/i);
    if (inventoryMatch) keys.add(`CrabInventoryInfo.${inventoryMatch[1]}`);
  }
  if (Array.isArray(record.candidateClasses)) for (const candidate of record.candidateClasses) keys.add(String(candidate));
  return [...keys];
}

function normalizeEvidenceKey(value) {
  return String(value || '').replace(/^\/(?:Script|Game)\/[^.]+\./, '').replace(/:+/g, '.').trim().toLowerCase();
}

function buildEvidenceIndex(parsedEvidence) {
  const index = new Map();
  for (const record of parsedEvidence) {
    for (const key of evidenceKeys(record)) {
      const normalized = normalizeEvidenceKey(key);
      if (!normalized) continue;
      if (!index.has(normalized)) index.set(normalized, []);
      index.get(normalized).push(record);
    }
  }
  return index;
}

function recordsForEntry(entry, evidenceIndex) {
  const keys = new Set([normalizeEvidenceKey(entry.symbolPath)]);
  if (entry.member) keys.add(normalizeEvidenceKey(`${ownerShortName(entry.owner)}.${entry.member}`));
  else keys.add(normalizeEvidenceKey(ownerShortName(entry.owner || entry.symbolPath.split('.').pop())));
  const records = [];
  const seen = new Set();
  for (const key of keys) {
    for (const record of evidenceIndex.get(key) || []) {
      const recordKey = `${record.__sourceFile}:${record.__sourceLine}`;
      if (!seen.has(recordKey)) {
        seen.add(recordKey);
        records.push(record);
      }
    }
  }
  return records;
}

function evidenceSummary(records) {
  const normalized = records.map(normalizeEvidenceRecord);
  const sessions = [...new Set(records.map((r) => r.sessionId).filter(Boolean))].sort();
  const statuses = [...new Set(normalized.map((r) => r.status).filter(Boolean))].sort();
  const roles = [...new Set(normalized.flatMap((r) => r.roles))].sort();
  const lifecycleStates = [...new Set(normalized.flatMap((r) => r.lifecycleStates))].sort();
  const cleanLifecycleStates = [...new Set(normalized.filter((r) => r.clean).flatMap((r) => r.lifecycleStates))].sort();
  const firstTimestamp = records.map((r) => r.timestamp).filter(Boolean).sort()[0] || null;
  const lastTimestamp = records.map((r) => r.timestamp).filter(Boolean).sort().slice(-1)[0] || null;
  const sourceScopes = [...new Set(records.map((r) => r.sourceScope).filter(Boolean))].sort();
  const authorityStatuses = [...new Set(records.filter((_, index) => normalized[index].clean)
    .map((r) => r.authorityStatus).filter((value) => value && value !== 'unknown'))].sort();
  const inventoryStages = [...new Set(normalized.filter((r) => r.stage !== null)
    .map((r) => `${r.stage}:${r.stageName || 'unnamed'}:${r.stageStatus || 'unknown'}`))].sort();
  return {
    observationCount: records.length,
    sessions,
    statuses,
    roles,
    lifecycleStates,
    cleanLifecycleStates,
    sourceScopes,
    firstTimestamp,
    lastTimestamp,
    authorityStatuses,
    inventoryStages,
    qualifyingReadCount: normalized.filter((r) => r.qualifyingRead).length,
    naturalObservationCount: normalized.filter((r) => r.naturalObserved).length,
    passiveCampaignCount: normalized.filter((r) => r.passiveCampaign).length,
    runtimeDiscoveryCount: normalized.filter((r) => r.runtimeDiscovered).length,
    unsupportedCount: normalized.filter((r) => r.unsupported).length,
    dirtyEvidenceCount: normalized.filter((r) => r.dirty).length,
    crashSuspectCount: normalized.filter((r) => r.crashSuspected).length,
    roleMismatchCount: normalized.filter((r) => r.roleMismatch).length
  };
}

function docsForEntry(entry, docs) {
  const owner = ownerShortName(entry.owner);
  const member = entry.member;
  const needles = [entry.symbolPath, member ? `${owner}.${member}` : owner];
  if (member && (/^(?:Server|Client|OnRep_|Multicast)/.test(member) || member.length >= 12)) needles.push(member);
  return docs.filter((doc) => needles.some((needle) => needle && doc.text.includes(needle))).map((doc) => doc.logicalName);
}

function statusFields(entry, rowType, records, policy) {
  const summary = evidenceSummary(records);
  const normalized = records.map(normalizeEvidenceRecord);
  const safe = normalized.some((record) => record.qualifyingRead);
  const natural = normalized.some((record) => record.naturalObserved);
  const argumentObserved = normalized.some((record) => record.argumentObserved);
  const contaminated = normalized.some((record) => record.dirty);
  const crashSuspected = normalized.some((record) => record.crashSuspected);
  const runtimeDiscovered = normalized.some((record) => record.runtimeDiscovered);
  const unsupported = normalized.some((record) => record.unsupported) && !safe && !natural && !runtimeDiscovered;
  const passiveCampaignOnly = normalized.some((record) => record.passiveCampaign) && !safe && !natural;
  const remoteVisible = records.some((record, index) => normalized[index].clean && Array.isArray(record.fieldsVisibleAcrossMultiple) && record.fieldsVisibleAcrossMultiple.some((field) => {
    const member = entry.member || '';
    return field === member || field === `HealthInfo.${member}` || field === `InventoryInfo.${member}`;
  })) || normalized.some((record) => record.stage === 14 && record.readObserved && record.stageStatus === 'confirmed');
  const roles = summary.roles.length ? summary.roles.join(', ') : 'untested';
  const lifecycle = summary.cleanLifecycleStates.length ? `${summary.cleanLifecycleStates.join(', ')} (${roles})` : 'untested';
  const roleKinds = new Set(summary.roles.map(normalizedRoleKind));
  const hostAndJoinedCovered = roleKinds.has('host-or-solo') && roleKinds.has('joined-client');
  const confirmedStageNames = [...new Set(normalized.filter((record) => record.readObserved && record.stage !== null)
    .map((record) => record.stageName || `stage-${record.stage}`))].sort();

  if (policy.keysExcluded) {
    return {
      readStatus: 'intentionally excluded by product policy',
      naturalObservationStatus: 'not applicable; keys are excluded',
      argumentMetadataStatus: 'not used; keys are excluded',
      ownershipAuthorityStatus: 'not investigated by policy',
      visibilityDirection: 'not investigated by policy',
      lifecycleCoverage: 'not applicable; product-policy exclusion',
      persistenceUiCoverage: 'not investigated; product-policy exclusion',
      writeApplyStatus: 'forbidden by product policy',
      safetyClassification: 'intentionally-excluded-product-policy',
      coverageDisposition: 'excluded-product-policy',
      nextRequiredObservation: 'None unless product policy explicitly re-approves keys.'
    };
  }
  if (policy.unsafeHud) {
    return {
      readStatus: 'not a read path',
      naturalObservationStatus: 'explicitly rejected; crash history',
      argumentMetadataStatus: 'object-dump signature only; must not hook',
      ownershipAuthorityStatus: 'local HUD callback; unsafe',
      visibilityDirection: 'local UI callback',
      lifecycleCoverage: 'rejected across all lifecycle windows',
      persistenceUiCoverage: 'HUD-only; unsafe',
      writeApplyStatus: 'not applicable',
      safetyClassification: 'explicitly-rejected-unsafe',
      coverageDisposition: 'rejected-unsafe',
      nextRequiredObservation: 'None. Keep ReceiveDrawHUD disabled and use the safe delayed driver.'
    };
  }
  if (policy.unscopedHealth) {
    return {
      readStatus: 'runtime evidence rejected this unscoped path',
      naturalObservationStatus: 'wrong-object evidence captured',
      argumentMetadataStatus: 'not applicable',
      ownershipAuthorityStatus: 'unscoped and may resolve non-player actors',
      visibilityDirection: 'unknown/wrong-object',
      lifecycleCoverage: lifecycle,
      persistenceUiCoverage: 'not applicable',
      writeApplyStatus: 'forbidden',
      safetyClassification: 'explicitly-rejected-unsafe',
      coverageDisposition: 'rejected-unsafe',
      nextRequiredObservation: 'None. Use CrabPC -> PlayerState -> CrabPS -> HealthInfo only.'
    };
  }
  if (policy.rawIdentity) {
    const fingerprintEvidence = records.some((record) => record.identityRawRedacted === true || record.rawIdentityEvidence === false || /fingerprint/i.test(record.valueKind || ''));
    return {
      readStatus: fingerprintEvidence ? 'confirmed redacted fingerprint-only observation; raw identity was not emitted' : 'raw identity excluded; fingerprint-only observation untested',
      naturalObservationStatus: 'not applicable to raw identity; only redacted fingerprints may be observed',
      argumentMetadataStatus: 'raw text/value access forbidden; length/fingerprint metadata only',
      ownershipAuthorityStatus: 'identity correlation only; never gameplay authority',
      visibilityDirection: fingerprintEvidence ? 'redacted local/visible-player correlation evidence only' : 'untested with raw identity disabled',
      lifecycleCoverage: fingerprintEvidence ? lifecycle : 'untested with raw identity disabled',
      persistenceUiCoverage: 'raw identity must not be persisted or displayed by RuntimeProbe',
      writeApplyStatus: 'forbidden; identity is not an apply or carrier path',
      safetyClassification: 'redacted-fingerprint-only',
      coverageDisposition: 'needs-coverage',
      nextRequiredObservation: 'Use only bounded fingerprints to correlate host/joined-client sessions across join, travel, reconnect, and disconnect; never emit raw identity.'
    };
  }

  let authority = 'unknown; requires host and joined-client natural observation';
  let direction = 'unknown';
  if (/^Server/.test(entry.member || '')) {
    authority = 'server-authority candidate inferred from RPC name; runtime unproven';
    direction = 'client-to-server candidate';
  } else if (/^Client/.test(entry.member || '')) {
    authority = 'owning-client target candidate inferred from RPC name; runtime unproven';
    direction = 'server-to-owning-client candidate';
  } else if (/^Multicast/.test(entry.member || '')) {
    authority = 'server-origin multicast candidate; runtime unproven';
    direction = 'server-to-relevant-clients candidate';
  } else if (/^OnRep_/.test(entry.member || '')) {
    authority = 'replication receiver callback candidate; sender authority unproven';
    direction = 'replicated-state follow-up on receiving peer';
  } else if (/PlayerState|CrabPS/.test(entry.owner || '')) {
    authority = remoteVisible ? 'read across multiple visible PlayerStates; exact server authority remains unproven' : 'PlayerState-scoped replicated candidate; exact authority unproven';
    direction = remoteVisible ? 'peer-visible in one runtime process; two-computer direction/ownership still unresolved' : 'local and/or peer-visible; requires two-computer evidence';
  } else if (safe) {
    authority = `read observed in roles: ${roles}`;
    direction = 'local read visibility only unless remote evidence is explicitly present';
  }
  if (remoteVisible && !/PlayerState|CrabPS/.test(entry.owner || '')) {
    authority = 'embedded field observed across multiple visible PlayerStates; exact authority unproven';
    direction = 'peer-visible through scoped PlayerState samples; two-computer direction still unresolved';
  }
  if (summary.authorityStatuses.length) authority = `runtime authority evidence: ${summary.authorityStatuses.join(', ')}`;

  let argumentMetadataStatus = 'not applicable';
  if (['function', 'RPC', 'OnRep', 'multicast', 'event'].includes(rowType)) {
    const argCount = entry.argumentSchema ? entry.argumentSchema.length : 0;
    const signatureSource = entry.isRuntimeFunction ? 'runtime-discovery metadata' : 'object-dump signature';
    argumentMetadataStatus = argumentObserved
      ? `runtime argument evidence observed; ${signatureSource} exposes ${argCount} parameter(s)`
      : `${signatureSource} exposes ${argCount} parameter(s); runtime values unobserved`;
  } else if (entry.dumpType) argumentMetadataStatus = `object-dump property type: ${entry.dumpType}`;

  const exactCallCandidate = ['function', 'RPC', 'OnRep', 'multicast', 'event'].includes(rowType);
  const currentHookCrashContext = CURRENT_HOOK_METHOD_CRASH_CONTEXTS.has(entry.symbolPath);
  let readStatus = 'untested';
  if (exactCallCandidate) readStatus = 'not applicable; exact-call observation is disabled in normal mode';
  else if (safe && confirmedStageNames.length) readStatus = `confirmed clean staged read: ${confirmedStageNames.join(', ')}`;
  else if (safe) readStatus = 'confirmed read-only in qualifying recorded runtime evidence';
  else if (unsupported && !contaminated) readStatus = 'explicitly unsupported by recorded runtime evidence';
  else if (passiveCampaignOnly) readStatus = 'passive campaign/lifecycle evidence only; no symbol read proof';
  else if (runtimeDiscovered) readStatus = 'exact runtime discovery recorded; value/behavior read remains unproven';
  else if (records.length) readStatus = `partial/unknown runtime result: ${summary.statuses.join(', ') || 'unknown'}`;
  if (contaminated) readStatus = `${readStatus}; dirty/crash-suspect evidence cannot qualify`;

  let naturalObservationStatus = natural ? 'confirmed natural state/event evidence' : 'not observed naturally';
  if (exactCallCandidate) naturalObservationStatus = natural
    ? 'confirmed prior natural-call evidence; current normal-mode hooks remain disabled'
    : 'not observed naturally; exact-call watcher disabled in normal mode';
  if (rowType === 'actor' && safe) naturalObservationStatus = 'runtime presence observed';
  if (runtimeDiscovered && !natural) naturalObservationStatus = 'runtime-discovered only; hook registration/discovery is not a natural call';
  if (normalized.some((record) => record.hookRegisteredOnly) && !natural) naturalObservationStatus = 'hook registered only; no natural call observed';
  if (contaminated) naturalObservationStatus = `${naturalObservationStatus}; contaminated evidence does not qualify`;

  let writeApplyStatus = 'no official apply behavior identified';
  if (OFFICIAL_APPLY_FUNCTIONS.test(entry.member || '')) writeApplyStatus = 'plausible official apply candidate; requires a future isolated safe method; normal-mode hooks/calls disabled';
  else if (/^OnRep_|Refresh.*UI|InventoryEvent/i.test(entry.member || '')) writeApplyStatus = 'possible official UI/replication follow-up; current hook method and invocation remain disabled';
  else if (rowType === 'property' || rowType === 'struct field') writeApplyStatus = 'raw write forbidden; seek official function/event alternative';

  let safetyClassification = 'untested-read-only-candidate';
  if (crashSuspected) safetyClassification = 'crash-suspect-evidence';
  else if (contaminated) safetyClassification = 'dirty-evidence';
  else if (unsupported) safetyClassification = 'explicitly-unsupported';
  else if (safe) safetyClassification = 'runtime-safe-read-only';
  else if (runtimeDiscovered) safetyClassification = 'research-only-disabled-runtime-discovery';
  else if (exactCallCandidate) safetyClassification = 'research-only-disabled-current-hook-method';
  else if (/ArrayProperty|MapProperty|SetProperty|StructProperty/.test(entry.dumpType || '')) safetyClassification = 'staged-read-required';
  if (currentHookCrashContext) safetyClassification = 'crash-suspect-current-hook-method-disabled';

  let coverageDisposition = 'needs-coverage';
  if (unsupported && !contaminated) coverageDisposition = 'unsupported';
  else if (safe && !['function', 'RPC', 'OnRep', 'multicast', 'event'].includes(rowType) &&
           !contaminated && summary.cleanLifecycleStates.length >= 2 && hostAndJoinedCovered) {
    coverageDisposition = 'confirmed-clean-evidence';
  }

  let nextRequiredObservation = 'Capture a clean, scoped read in stable host and joined-client contexts, then repeat across lifecycle transitions.';
  if (exactCallCandidate) {
    nextRequiredObservation = natural
      ? 'Preserve the prior observation and correlate hook-free state snapshots; any new exact-call or argument work requires a separately reviewed isolated method.'
      : 'Use hook-free state correlation where possible. Exact-call and argument coverage requires a separately reviewed isolated method; do not enable normal-mode passive hooks.';
  } else if (unsupported && !contaminated) nextRequiredObservation = 'None unless a separately reviewed safe official alternative becomes available.';
  else if (safe) nextRequiredObservation = 'Repeat the proven read on host and joined client, check peer visibility, and cover join/travel/death/respawn/reconnect as applicable.';
  if (contaminated) nextRequiredObservation = crashSuspected
    ? 'Repeat only after a clean restart and stable dwell; isolate the crash-suspect path without writes, RPC calls, stale references, or unsafe hooks.'
    : 'Repeat in a clean stable session with selected/observed roles aligned and all read-only safety flags intact.';
  if (currentHookCrashContext) {
    nextRequiredObservation = 'Do not hook this path with the current method. Use hook-free state correlation; exact-call research requires a separately reviewed isolated mechanism after the 2026-07-10 crash incident.';
  }

  return {
    readStatus,
    naturalObservationStatus,
    argumentMetadataStatus,
    ownershipAuthorityStatus: authority,
    visibilityDirection: direction,
    lifecycleCoverage: lifecycle,
    persistenceUiCoverage: /UI|OnRep|Save|AutoSave|InventoryInfo|Crystals|Health/i.test(`${entry.owner || ''}.${entry.member || ''}`)
      ? 'candidate relationship identified; qualifying persistence/UI evidence incomplete'
      : 'not yet assessed',
    writeApplyStatus,
    safetyClassification,
    coverageDisposition,
    nextRequiredObservation
  };
}

function relevanceFor(entry, category) {
  const member = entry.member || ownerShortName(entry.owner || entry.symbolPath.split('.').pop());
  const categoryLabel = (CATEGORY_DEFINITIONS.find(([id]) => id === category) || [null, category])[1];
  if (OFFICIAL_APPLY_FUNCTIONS.test(member)) return `${categoryLabel}; plausible game-native alternative to a raw write, requiring passive evidence before any separate future sandbox.`;
  if (/OnRep_|Client|Multicast|UI|Refresh/.test(member)) return `${categoryLabel}; may reveal replication direction, natural follow-up, ownership, or UI behavior.`;
  if (/InventoryInfo|Enhancements|AccumulatedBuff|Level/.test(member)) return `${categoryLabel}; required to preserve per-item metadata and avoid destructive reconstruction.`;
  if (/Health|Armor|Shield|Damage|Heal|Eliminat|Respawn/i.test(member)) return `${categoryLabel}; may affect health correctness, player scoping, damage/death flow, or recovery.`;
  return `${categoryLabel}; object or state candidate materially connected to complete CrabSync evidence coverage.`;
}

function rowId(entry, rowType) {
  return `${slug(entry.namespace || 'runtime')}-${slug(entry.moduleOrPackage || 'runtime')}-${slug(entry.owner || 'root')}-${slug(entry.member || entry.symbolPath.split('.').pop())}-${slug(rowType)}`;
}

function baseChecklist() {
  const S = ['not-observed', 'in-progress', 'partial', 'confirmed', 'unsupported', 'blocked-by-prerequisite', 'crash-suspect', 'dirty-evidence', 'not-applicable'];
  const entries = [];
  const add = (section, id, label, nextAction, completionRule = 'qualifying-evidence') => entries.push({
    id, section, label, initialStatus: completionRule === 'prerequisite' ? 'blocked-by-prerequisite' : 'not-observed',
    allowedStatuses: S, observationCount: 0, firstTimestamp: null, mostRecentTimestamp: null,
    sources: [], evidenceSessionReferences: [], nextAction, completionRule, catalogRowIds: []
  });

  add('Session and multiplayer', 'session-runtimeprobe-loaded', 'RuntimeProbe loaded', 'Launch Crab Champions after Prepare reports success.');
  add('Session and multiplayer', 'session-fresh-generation', 'Fresh campaign generation created', 'Click Prepare Campaign once on each computer.');
  add('Session and multiplayer', 'session-local-playerstate-stable', 'Local PlayerState stable', 'Enter a lobby or run and wait for stability.');
  add('Session and multiplayer', 'session-role-determined', 'Role determined', 'Select Host or Joined Client before preparing.');
  add('Session and multiplayer', 'session-host-detected', 'Host detected', 'Create the lobby on the Host computer.');
  add('Session and multiplayer', 'session-joined-client-detected', 'Joined client detected', 'Join the host from the Joined Client computer.');
  add('Session and multiplayer', 'session-two-visible-players', 'Two or more visible players', 'Keep both players in the same stable world.');
  add('Session and multiplayer', 'session-host-client-correlation', 'Host/client evidence correlation established', 'Keep both dashboards monitoring the same deliberate run.');
  add('Session and multiplayer', 'session-stable-multiplayer-sample', 'Stable in-run multiplayer sample captured', 'Play together for a stable observation window.');
  add('Session and multiplayer', 'session-island-travel', 'Island travel observed', 'Complete an island and enter a portal.');
  add('Session and multiplayer', 'session-late-join-reconnect', 'Late join or reconnect observed', 'Have the joined client leave and reconnect.');
  add('Session and multiplayer', 'session-disconnect', 'Disconnect observed', 'Have the joined client disconnect cleanly after a stable sample.');
  add('Session and multiplayer', 'session-join-leave', 'Join and leave lifecycle observed', 'Join, play briefly, then leave normally.');
  add('Session and multiplayer', 'session-host-migration', 'Host migration applicability resolved', 'If the game supports host migration, observe it; otherwise record Not applicable.');
  add('Session and multiplayer', 'session-death-respawn', 'Death and respawn observed', 'Allow one player to die and respawn.');
  add('Session and multiplayer', 'ownership-playerstate-gamestate', 'PlayerState/GameState authority and visibility resolved', 'Compare local and remote PlayerState/GameState observations on both computers.');

  for (const [kind, label] of [['weapon-mod', 'Weapon mod'], ['ability-mod', 'Ability mod'], ['melee-mod', 'Melee mod'], ['perk', 'Perk'], ['relic', 'Relic']]) {
    add('Inventory', `inventory-${kind}-pickup`, `${label} pickup observed`, `Pick up a ${label.toLowerCase()}.`);
  }
  add('Inventory', 'inventory-starting-defaults', 'Starting/default weapons, abilities, melee, and items captured', 'Start a fresh run and record the automatic starting loadout before any pickup.');
  add('Inventory', 'inventory-duplicate-acquisition', 'Duplicate item acquisition observed', 'Pick up a second copy of an item.');
  add('Inventory', 'inventory-array-counts', 'Inventory array counts observed', 'Acquire at least one item in every category.');
  add('Inventory', 'inventory-first-da-identity', 'First-element DA identity observed', 'Keep one safe item in each category while staged reads advance.', 'prerequisite');
  add('Inventory', 'inventory-info-parent', 'InventoryInfo parent observed', 'Wait for the staged inventory reader to reach InventoryInfo.', 'prerequisite');
  add('Inventory', 'inventory-level', 'Item Level observed', 'Pick up a duplicate to increase an item level.', 'prerequisite');
  add('Inventory', 'inventory-accumulated-buff', 'AccumulatedBuff observed', 'Use an item whose accumulated buff changes.', 'prerequisite');
  add('Inventory', 'inventory-enhancements-shape', 'Enhancements shape observed', 'Use an anvil after enhancement prerequisites pass.', 'prerequisite');
  add('Inventory', 'inventory-enhancements-values', 'Enhancements values observed', 'Use an anvil and retain the upgraded item.', 'prerequisite');
  add('Inventory', 'inventory-capped-iteration', 'Capped full local inventory iteration completed', 'Carry several items while the final local stage runs.', 'prerequisite');
  add('Inventory', 'inventory-duplicate-semantics', 'Duplicate/stack semantics captured', 'Pick up duplicate same-DA items and compare level, count, and entry shape.');
  add('Inventory', 'inventory-order-index-stability', 'Ordering and slot/index stability captured', 'Reopen inventory, travel, and acquire/drop items while preserving evidence.');
  add('Inventory', 'inventory-rarity-cooldown-stack', 'Rarity, cooldown, and stack semantics captured', 'Acquire items of different rarities and observe cooldown/stack changes.');
  add('Inventory', 'inventory-joined-client-reads', 'Joined-client inventory reads completed', 'Repeat every proven local inventory stage as Joined Client.', 'prerequisite');
  add('Inventory', 'inventory-remote-visibility', 'Remote inventory visibility checked', 'Compare each player from the other computer without deep arbitrary crawling.', 'prerequisite');

  add('Transactions', 'transaction-server-interact', 'ServerInteract observed with pickup actor', 'Interact with a pedestal or pickup.');
  add('Transactions', 'transaction-server-autoloot', 'ServerAutoLoot observed if naturally called', 'Walk over eligible loot and allow normal auto-loot.');
  add('Transactions', 'transaction-client-picked-up', 'ClientOnPickedUpPickup observed', 'Pick up any inventory item.');
  add('Transactions', 'transaction-onrep-inventory', 'OnRep_Inventory observed', 'Change inventory while the joined client is present.');
  add('Transactions', 'transaction-drop', 'Dropping observed', 'Drop an item from inventory.');
  add('Transactions', 'transaction-typed-removal', 'Typed inventory removal observed', 'Remove one item from each supported category through normal gameplay.');
  add('Transactions', 'transaction-salvage', 'Salvaging observed', 'Salvage an offered pickup.');
  add('Transactions', 'transaction-reroll', 'Rerolling observed', 'Reroll a shop or reward selection.');
  add('Transactions', 'transaction-replacement', 'Item replacement observed', 'Replace equipment or an item through normal gameplay.');
  add('Transactions', 'transaction-anvil', 'Anvil enhancement observed', 'Use an anvil.');
  add('Transactions', 'transaction-upgrade-totem', 'Upgrade-totem purchase observed', 'Purchase an upgrade from a totem.');
  add('Transactions', 'transaction-server-apply-enhancement', 'ServerApplyEnhancement observed', 'Use an anvil while monitoring.');
  add('Transactions', 'transaction-multicast-enhancement', 'MulticastApplyEnhancement observed', 'Use an anvil with both players nearby.');
  add('Transactions', 'transaction-equipment-change', 'Equipment change observed', 'Change weapon, ability, or melee normally.');
  add('Transactions', 'transaction-official-equipment-rpc', 'ServerEquipInventory or ServerSet* observed', 'Change equipment through the normal inventory UI.');
  add('Transactions', 'transaction-pickup-ownership', 'Pickup ownership and activation resolved', 'Observe OwningPS, activation, deactivation, and pickup ownership before/after interaction.');
  add('Transactions', 'transaction-multi-pickup-replication', 'Multi-pickup count and replication resolved', 'Use a chest or reward that spawns multiple pickups with both players present.');

  add('Resources and slots', 'resource-crystal-gain', 'Crystal gain observed', 'Earn crystals from combat or rewards.');
  add('Resources and slots', 'resource-crystal-spend', 'Crystal spending observed', 'Buy a chest, shop item, or slot.');
  add('Resources and slots', 'resource-crystal-drop-reward', 'Crystal drops and rewards observed', 'Collect a crystal drop and an island reward.');
  add('Resources and slots', 'resource-crystal-range-overflow', 'Crystal UInt32 range/overflow policy resolved', 'Record bounded values only; never synthesize an overflow value.');
  add('Resources and slots', 'resource-onrep-crystals', 'OnRep_Crystals observed', 'Gain and spend crystals with a joined client present.');
  for (const [kind, label] of [['weapon', 'Weapon-mod'], ['ability', 'Ability-mod'], ['melee', 'Melee-mod'], ['perk', 'Perk']]) {
    add('Resources and slots', `slot-${kind}-increment`, `${label} slot increment observed`, `Purchase a ${label.toLowerCase()} slot.`);
  }
  add('Resources and slots', 'slot-increment-arguments', 'ServerIncrementNumInventorySlots arguments captured', 'Purchase one slot of each type.');
  add('Resources and slots', 'slot-cost-behavior', 'Slot cost behavior captured', 'Record crystals and displayed cost before/after slot purchases.');
  add('Resources and slots', 'slot-pre-post', 'Slot pre/post values captured', 'Purchase a slot and wait for UI/OnRep follow-up.');
  add('Resources and slots', 'slot-locked-usable-max', 'Locked, usable, and maximum slot semantics captured', 'Fill inventory, inspect locked slots, and purchase toward the maximum.');
  add('Resources and slots', 'slot-persistence', 'Slot persistence across travel/save/reconnect captured', 'Purchase slots, travel, reconnect, and restore a run.');

  add('Health', 'health-damage', 'Damage observed', 'Take damage.');
  add('Health', 'health-healing', 'Healing observed', 'Take damage, then heal.');
  add('Health', 'health-current-change', 'Current health change observed', 'Take damage and heal in a stable world.');
  add('Health', 'health-current-max-change', 'Current max-health change observed', 'Acquire a max-health modifier.');
  add('Health', 'health-base-max-change', 'Base max-health change observed', 'Acquire an effect that changes base max health.');
  add('Health', 'health-max-multiplier-change', 'Max-health multiplier change observed', 'Acquire a max-health multiplier perk.');
  add('Health', 'health-armor', 'Armor state and break behavior observed', 'Acquire armor and allow a plate to take damage.');
  add('Health', 'health-shield', 'Shield presence or absence resolved', 'Use any shield-like effect if present; otherwise record unsupported/not applicable.');
  add('Health', 'health-regeneration', 'Regeneration observed', 'Trigger natural health regeneration.');
  add('Health', 'health-elimination-death', 'Elimination and death observed', 'Allow one player to be eliminated.');
  add('Health', 'health-revival', 'Revival support resolved', 'Observe revival if supported; otherwise record not applicable.');
  add('Health', 'health-respawn', 'Respawn observed', 'Respawn after death.');
  add('Health', 'health-out-of-bounds', 'Out-of-bounds behavior observed', 'Enter and recover from an out-of-bounds state safely.');
  add('Health', 'health-playerstate-scoped', 'PlayerState-scoped health confirmed', 'Verify CrabPC -> PlayerState -> CrabPS -> HealthInfo.');
  add('Health', 'health-no-unscoped-hc', 'No unscoped CrabHC used', 'Keep unscoped FindFirstOf(CrabHC) disabled.');

  add('World interactions and persistence', 'world-chest', 'Chest interaction and multi-pickup result observed', 'Open a chest with both players present.');
  add('World interactions and persistence', 'world-shop', 'Shop purchase and reroll observed', 'Purchase and reroll in a shop.');
  add('World interactions and persistence', 'world-island-completion', 'Island completion observed', 'Complete an island and wait for ClientOnClearedIsland or equivalent qualifying evidence.');
  add('World interactions and persistence', 'world-reward-selection', 'Island reward selection observed', 'Complete an island and choose a reward.');
  add('World interactions and persistence', 'world-portal', 'Portal selection and travel observed', 'Choose and enter a portal.');
  add('World interactions and persistence', 'world-save-restore', 'AutoSave save/restore observed', 'Continue/restore a run without modifying save data.');
  add('World interactions and persistence', 'world-ui-follow-up', 'UI/OnRep follow-up correlated', 'After each action, wait for natural UI and replication follow-up.');
  add('World interactions and persistence', 'world-persistence', 'Persistence across travel/reconnect captured', 'Compare state before/after travel and reconnect.');
  add('Policy and official alternatives', 'policy-keys-excluded', 'Keys intentionally excluded', 'No action; keys remain product-policy excluded.');
  add('Policy and official alternatives', 'policy-unsafe-paths-rejected', 'Known unsafe paths explicitly rejected', 'Keep HUD hooks, unscoped CrabHC, raw writes, and mutating calls disabled.');
  add('Policy and official alternatives', 'official-apply-candidates-observed', 'Official apply candidates passively observed', 'Exercise normal gameplay paths; RuntimeProbe must never call them.');

  return entries;
}

function checklistLinksFor(row) {
  const links = [`coverage-${row.category}`];
  const text = `${row.symbolPath} ${row.relevanceToCrabSync}`;
  const mappings = [
    [/Starting|Default|starting/i, 'inventory-starting-defaults'],
    [/WeaponMods/, 'inventory-weapon-mod-pickup'], [/AbilityMods/, 'inventory-ability-mod-pickup'],
    [/MeleeMods/, 'inventory-melee-mod-pickup'], [/Perks/, 'inventory-perk-pickup'], [/Relics/, 'inventory-relic-pickup'],
    [/InventoryInfo/, 'inventory-info-parent'], [/AccumulatedBuff/, 'inventory-accumulated-buff'],
    [/(?:InventoryInfo.*Level|:Level$)/, 'inventory-level'], [/Enhancements/, 'inventory-enhancements-values'],
    [/Cooldown/, 'inventory-rarity-cooldown-stack'], [/(?:Rarity|Stack)/, 'inventory-rarity-cooldown-stack'],
    [/(?:Index|Order)/, 'inventory-order-index-stability'], [/ServerInteract/, 'transaction-server-interact'],
    [/ServerAutoLoot/, 'transaction-server-autoloot'], [/ClientOnPickedUpPickup/, 'transaction-client-picked-up'],
    [/OnRep_Inventory/, 'transaction-onrep-inventory'], [/DropPickup/, 'transaction-drop'],
    [/ServerRemove/, 'transaction-typed-removal'], [/Salvage/, 'transaction-salvage'], [/Reroll/, 'transaction-reroll'],
    [/ApplyEnhancement/, 'transaction-server-apply-enhancement'], [/MulticastApplyEnhancement/, 'transaction-multicast-enhancement'],
    [/UpgradeTotem/, 'transaction-upgrade-totem'], [/Server(?:EquipInventory|SetWeaponDA|SetAbilityDA|SetMeleeDA)/, 'transaction-official-equipment-rpc'],
    [/NumWeaponModSlots/, 'slot-weapon-increment'], [/NumAbilityModSlots/, 'slot-ability-increment'],
    [/NumMeleeModSlots/, 'slot-melee-increment'], [/NumPerkSlots/, 'slot-perk-increment'],
    [/ServerIncrementNumInventorySlots/, 'slot-increment-arguments'], [/(?:Locked|Usable|Max.*Slot)/i, 'slot-locked-usable-max'],
    [/Crystals/, 'resource-crystal-gain'], [/OnRep_Crystals/, 'resource-onrep-crystals'],
    [/(?:Armor)/, 'health-armor'], [/(?:Shield)/, 'health-shield'], [/(?:Regen)/, 'health-regeneration'],
    [/(?:CurrentHealth|Damage|TookDamage)/, 'health-current-change'], [/(?:Heal)/, 'health-healing'],
    [/(?:Eliminat|Death)/, 'health-elimination-death'], [/(?:Respawn)/, 'health-respawn'], [/(?:Revive)/, 'health-revival'],
    [/(?:OutOfBounds|Out_Of_Bounds)/i, 'health-out-of-bounds'], [/(?:PlayerState|CrabPS)/, 'ownership-playerstate-gamestate'],
    [/(?:GameState|CrabGS)/, 'ownership-playerstate-gamestate'], [/(?:Portal|Travel)/, 'world-portal'],
    [/(?:ClientOnClearedIsland|ClearedIsland|IslandComplete)/i, 'world-island-completion'],
    [/(?:Chest)/, 'world-chest'], [/(?:Shop)/, 'world-shop'], [/(?:Reward)/, 'world-reward-selection'],
    [/(?:AutoSave|Restore|Save)/, 'world-save-restore'], [/(?:UI|OnRep_|ClientRefresh)/, 'world-ui-follow-up'],
    [/(?:Keys|BP_(?:Chest|Totem)_Key)/, 'policy-keys-excluded'], [/(?:ReceiveDrawHUD|FindFirstOf.*CrabHC|policy:\/\/unsafe\/)/, 'policy-unsafe-paths-rejected'],
    [/FindFirstOf.*CrabHC/, 'health-no-unscoped-hc']
  ];
  for (const [regex, id] of mappings) if (regex.test(text)) links.push(id);
  if (/official apply candidate/i.test(row.writeApplyStatus)) links.push('official-apply-candidates-observed');
  return [...new Set(links)];
}

function terminalDispositionFor(coverageDisposition) {
  if (coverageDisposition === 'confirmed-clean-evidence') return 'confirmed_clean';
  if (coverageDisposition === 'rejected-unsafe') return 'unsafe_rejected';
  if (coverageDisposition === 'unsupported') return 'unsupported';
  if (coverageDisposition === 'excluded-product-policy') return 'policy_excluded';
  return 'needs_coverage';
}

function coverageCapabilitiesFor(row) {
  const capabilities = new Set();
  const text = `${row.symbolPath} ${(row.checklistLinkage || []).join(' ')}`;
  if (row.category === 'inventory-items' || row.category === 'pickups-transactions' ||
      /(?:WeaponMods|AbilityMods|MeleeMods|Perks|Relics|inventory-(?:weapon|ability|melee|perk|relic|duplicate|order)|transaction-(?:drop|typed-removal|salvage|reroll|replacement|client-picked-up|onrep-inventory))/i.test(text)) capabilities.add('inventory');
  if (['inventory-metadata', 'inventory-enhancements'].includes(row.category) || /InventoryInfo|Enhancements|AccumulatedBuff/.test(text)) capabilities.add('metadata-and-enhancements');
  if (row.category === 'inventory-slots') capabilities.add('slots');
  if (['equipment-starting', 'weapons-abilities-melee'].includes(row.category) && /WeaponDA|AbilityDA|MeleeDA|Equip|Starting|Default|Loadout/i.test(text)) capabilities.add('equipment');
  if (row.category === 'crystals-economy' || /Crystals/.test(text)) capabilities.add('crystals');
  if (row.category === 'health-armor') capabilities.add('health');
  if (row.category === 'multiplayer-ownership' || /PlayerState|GameState|ownership-playerstate-gamestate/.test(text)) capabilities.add('multiplayer-ownership-and-visibility');
  if (['portal-island-lifecycle', 'save-persistence'].includes(row.category) || /(?:Join|Leave|Disconnect|Reconnect|Respawn|Travel|BeginPlay|EndPlay|world-(?:portal|save-restore|persistence|island-completion))/i.test(text)) capabilities.add('lifecycle');
  if (row.officialAlternativeToRawWrite) capabilities.add('official-apply-candidates');
  return [...capabilities].sort();
}

function buildChecklist(rows, generatedAt) {
  const entries = baseChecklist();
  for (const [id, label] of CATEGORY_DEFINITIONS) {
    entries.push({
      id: `coverage-${id}`,
      section: 'Coverage matrix',
      label: `Resolve every ${label.toLowerCase()} catalog row`,
      initialStatus: 'not-observed',
      allowedStatuses: ['not-observed', 'in-progress', 'partial', 'confirmed', 'unsupported', 'blocked-by-prerequisite', 'crash-suspect', 'dirty-evidence', 'not-applicable'],
      observationCount: 0,
      firstTimestamp: null,
      mostRecentTimestamp: null,
      sources: [],
      evidenceSessionReferences: [],
      nextAction: `Open Needs Coverage and complete or explicitly resolve every ${label.toLowerCase()} candidate.`,
      completionRule: 'all-linked-catalog-rows-terminal',
      catalogRowIds: []
    });
  }
  const byId = new Map(entries.map((entry) => [entry.id, entry]));
  for (const row of rows) {
    row.checklistLinkage = checklistLinksFor(row);
    row.rowId = row.id;
    row.relevance = row.relevanceToCrabSync;
    row.terminalDisposition = terminalDispositionFor(row.coverageDisposition);
    row.checklistLinks = row.checklistLinkage;
    row.coverageCapabilities = coverageCapabilitiesFor(row);
    for (const link of row.checklistLinkage) {
      const checklistEntry = byId.get(link);
      if (checklistEntry) checklistEntry.catalogRowIds.push(row.id);
    }
  }
  for (const entry of entries) entry.catalogRowIds.sort();
  return {
    schemaVersion: 'crabsync-checklist-v1',
    profileId: 'crabsync-full-observe',
    generatedAt,
    completionPolicy: 'Hook registration never completes an entry. Completion requires qualifying evidence; unsupported, unsafe, and not-applicable outcomes must be explicit.',
    entries
  };
}

function functionRowType(member, explicitType) {
  const explicit = String(explicitType || '').trim().toLowerCase();
  if (explicit === 'rpc') return 'RPC';
  if (explicit === 'onrep') return 'OnRep';
  if (explicit === 'multicast') return 'multicast';
  if (explicit === 'event') return 'event';
  if (/^OnRep_/.test(member || '')) return 'OnRep';
  if (/^Multicast/.test(member || '')) return 'multicast';
  if (/^(?:Server|Client)/.test(member || '')) return 'RPC';
  if (/^(?:Receive|On[A-Z])/.test(member || '')) return 'event';
  return 'function';
}

function runtimeArgumentSchema(record) {
  const candidate = Array.isArray(record.argumentSchema) ? record.argumentSchema
    : (Array.isArray(record.discoveryDetails?.argumentSchema) ? record.discoveryDetails.argumentSchema : []);
  return candidate.map((argument, index) => ({
    name: String(argument.name || `Argument${index + 1}`),
    direction: String(argument.direction || 'input-or-unknown'),
    propertyType: String(argument.propertyType || argument.type || 'runtime-unknown'),
    valueTypePath: argument.valueTypePath || null,
    redaction: String(argument.redaction || 'runtime-metadata-only'),
    safeSummary: String(argument.safeSummary || 'runtime-metadata-only')
  }));
}

function runtimeFunctionCandidate(record) {
  const status = normalizedRuntimeStatus(record);
  const explicitType = record.functionType || record.type || record.accessKind;
  const functionLikeEvidence = record.event === 'RuntimeDiscovery.Function' || record.event === 'PassiveHook.Observed' ||
    record.event === 'PassiveHook.Registration' ||
    ['RUNTIME_DISCOVERED', 'DISCOVERED_NEEDS_COVERAGE', 'NATURALLY_OBSERVED', 'HOOK_REGISTERED'].includes(status) ||
    /^(?:function|rpc|onrep|multicast|event)$/i.test(String(explicitType || ''));
  if (!functionLikeEvidence) return null;
  const exactPath = [record.hookPath, record.discoveryDetails?.exactResolvedPath, record.symbol]
    .find((value) => typeof value === 'string' && /^\/(?:Script|Game)\/.+:[^:]+$/.test(value));
  if (!exactPath) return null;
  const parsed = parseSymbolPath(exactPath);
  if (!parsed || !parsed.owner || !parsed.member || parsed.parameter) return null;
  return {
    namespace: parsed.namespace,
    moduleOrPackage: parsed.moduleOrPackage,
    owner: parsed.owner,
    member: parsed.member,
    symbolPath: exactPath,
    dumpType: 'RuntimeDiscoveredFunction',
    argumentSchema: runtimeArgumentSchema(record),
    rowType: functionRowType(parsed.member, explicitType),
    isRuntimeFunction: true
  };
}

function runtimeOnlyEntries(parsedEvidence, existingRows, evidenceIndex) {
  const existingKeys = new Set();
  for (const row of existingRows) {
    existingKeys.add(normalizeEvidenceKey(row.symbolPath));
    if (row.owner && row.member) existingKeys.add(normalizeEvidenceKey(`${row.owner}.${row.member}`));
  }
  const candidates = new Map();
  const functionRecords = new Set();
  for (const record of parsedEvidence) {
    const candidate = runtimeFunctionCandidate(record);
    if (!candidate) continue;
    functionRecords.add(record);
    const normalized = normalizeEvidenceKey(candidate.symbolPath);
    if (existingKeys.has(normalized)) continue;
    if (!candidates.has(normalized)) candidates.set(normalized, { entry: candidate, key: candidate.symbolPath, records: [] });
    const grouped = candidates.get(normalized);
    if ((candidate.argumentSchema || []).length > (grouped.entry.argumentSchema || []).length) grouped.entry.argumentSchema = candidate.argumentSchema;
    grouped.records.push(record);
  }
  for (const record of parsedEvidence) {
    if (functionRecords.has(record)) continue;
    for (const key of evidenceKeys(record)) {
      const normalized = normalizeEvidenceKey(key);
      if (!normalized || existingKeys.has(normalized)) continue;
      const relevanceText = `${key} ${record.category || ''} ${record.probeName || ''} ${record.accessKind || ''}`;
      if (!MEMBER_RELEVANCE.test(relevanceText) && !/(?:Runtime\.Context|PlayerState\.Identity|GameState\.PlayerArray|FindFirstOf\(CrabHC\))/i.test(relevanceText)) continue;
      if (!candidates.has(normalized)) candidates.set(normalized, { key, records: [] });
      candidates.get(normalized).records.push(record);
    }
  }
  const unscopedHealthRecords = parsedEvidence.filter((record) =>
    record.probeId === 'FindFirstOf.CrabHC' || record.probeName === 'FindFirstOf.CrabHC');
  if (unscopedHealthRecords.length) {
    candidates.set('findfirstof.crabhc.unsafe-policy-path', {
      key: 'FindFirstOf(CrabHC)',
      records: unscopedHealthRecords
    });
  }
  return [...candidates.values()].map(({ entry, key, records }) => {
    if (entry) return { ...entry, records };
    const pieces = String(key).split('.');
    const owner = pieces[0] || 'Runtime';
    const member = pieces.slice(1).join('.') || owner;
    return {
      namespace: 'runtime', moduleOrPackage: 'evidence', owner, member,
      symbolPath: `runtime://${String(key).replace(/\\/g, '/')}`,
      dumpType: null, argumentSchema: [], records
    };
  });
}

function policyFor(entry) {
  const full = `${entry.symbolPath} ${entry.owner || ''} ${entry.member || ''}`;
  return {
    keysExcluded: isKeysPolicyCandidate(entry),
    unsafeHud: entry.symbolPath === '/Script/Engine.HUD:ReceiveDrawHUD',
    unscopedHealth: entry.symbolPath === 'runtime://FindFirstOf(CrabHC)' || /FindFirstOf.*CrabHC/i.test(entry.symbolPath),
    rawIdentity: RAW_IDENTITY.test(`${entry.symbolPath} ${entry.owner || ''} ${entry.member || ''}`)
  };
}

function documentedUnsafeConcerns(docs) {
  const byId = new Map();
  for (const doc of docs) {
    if (!/^(?:docs\/)?(?:WRITE_PATH_UNSAFE_PATHS|P2P_CARRIER_UNSAFE_PATHS)\.md$/i.test(doc.logicalName)) continue;
    for (const [index, line] of normalizedLines(doc.text).entries()) {
      const columns = line.split('|').slice(1, -1).map((value) => value.trim());
      const id = columns[0] || '';
      if (!/^(?:unsafe-|carrier-forbidden-)[a-z0-9-]+$/.test(id)) continue;
      if (!byId.has(id)) byId.set(id, { id, documentationRefs: [], descriptions: [] });
      const concern = byId.get(id);
      concern.documentationRefs.push({ logicalName: doc.logicalName, lineNumber: index + 1 });
      concern.descriptions.push(columns.slice(1).join(' | ').replace(/`/g, ''));
    }
  }
  return [...byId.values()].sort((a, b) => a.id.localeCompare(b.id));
}

function unsafeConcernCategory(id) {
  if (/enhancement/.test(id)) return 'inventory-enhancements';
  if (/inventoryinfo|level-accumulatedbuff/.test(id)) return 'inventory-metadata';
  if (/inventory/.test(id)) return 'inventory-items';
  if (/equipment/.test(id)) return 'equipment-starting';
  if (/slots/.test(id)) return 'inventory-slots';
  if (/crystal|keys/.test(id)) return 'crystals-economy';
  if (/health|crabhc/.test(id)) return 'health-armor';
  if (/autosave|unlocks-save/.test(id)) return 'save-persistence';
  if (/rpc/.test(id)) return 'replication-rpc-events';
  if (/lifecycle/.test(id)) return 'portal-island-lifecycle';
  if (/identity|role|carrier|gameplay-fields/.test(id)) return 'multiplayer-ownership';
  return 'player-runtime-state';
}

function unsafeConcernEvidenceRegex(id) {
  if (/live-inventory-arrays?|inventory-arrays?-carrier|inventory-array-rebuild/.test(id)) return /CrabPS:(?:WeaponMods|AbilityMods|MeleeMods|Perks|Relics)$/;
  if (/inventoryinfo/.test(id)) return /(?:CrabInventoryInfo|:InventoryInfo$)/;
  if (/enhancements/.test(id)) return /CrabInventoryInfo:Enhancements$/;
  if (/level-accumulatedbuff/.test(id)) return /CrabInventoryInfo:(?:Level|AccumulatedBuff)$/;
  if (/crystal/.test(id)) return /CrabPS:(?:Crystals|OnRep_Crystals)$/;
  if (/keys/.test(id)) return /(?:CrabPS|CrabSG|AutoSave).*(?:Keys|Key)/i;
  if (/current-health/.test(id)) return /CrabHealthInfo:(?:CurrentHealth|CurrentMaxHealth)$/;
  if (/healthinfo|crabhc/.test(id)) return /(?:CrabHealthInfo|CrabPS:HealthInfo|CrabHC)/;
  if (/equipment-da/.test(id)) return /CrabPS:(?:WeaponDA|AbilityDA|MeleeDA|OnRep_(?:WeaponDA|AbilityDA|MeleeDA)|ServerSet(?:WeaponDA|AbilityDA|MeleeDA)|ServerEquipInventory)$/;
  if (/slots/.test(id)) return /CrabPS:(?:Num\w+Slots|ServerIncrementNumInventorySlots)$/;
  if (/autosave|unlocks-save/.test(id)) return /(?:AutoSave|SaveGame|RestoreAutoSave|Unlock|Progression)/i;
  if (/identity/.test(id)) return RAW_IDENTITY;
  if (/unknown-role|unstable-lifecycle|unstable-rpc/.test(id)) return /:(?:Server|Client|Multicast|OnRep_)/;
  if (/gameplay-fields/.test(id)) return /CrabPS:(?:Crystals|HealthInfo|WeaponDA|AbilityDA|MeleeDA|Num\w+Slots|WeaponMods|AbilityMods|MeleeMods|Perks|Relics)$/;
  return null;
}

function unsafeOfficialAlternative(id) {
  if (/equipment/.test(id)) return 'Observe ServerEquipInventory and ServerSetWeaponDA/ServerSetAbilityDA/ServerSetMeleeDA naturally.';
  if (/slots/.test(id)) return 'Observe ServerIncrementNumInventorySlots and its normal cost/UI/OnRep follow-up naturally.';
  if (/enhancement|inventoryinfo|level-accumulatedbuff/.test(id)) return 'Observe the anvil, ServerApplyEnhancement, and MulticastApplyEnhancement paths naturally.';
  if (/inventory/.test(id)) return 'Observe normal pickup, typed ServerRemove*, drop, salvage, reroll, replacement, and equipment paths naturally.';
  if (/crystal/.test(id)) return 'Observe normal earning, pickup, reward, shop spending, and OnRep_Crystals paths naturally.';
  if (/health|crabhc/.test(id)) return 'Use player-scoped CrabPC -> PlayerState -> CrabPS -> HealthInfo and observe normal damage/heal/death/respawn paths.';
  if (/autosave|unlocks-save/.test(id)) return 'Observe normal AutoSave creation/restoration and ServerRestoreAutoSave behavior without changing save data.';
  if (/keys/.test(id)) return 'No alternative is in scope while keys remain excluded by product policy.';
  if (/identity/.test(id)) return 'Use bounded fingerprints only for evidence correlation; identity is never an apply or carrier path.';
  if (/carrier/.test(id)) return 'No custom carrier write is permitted; retain the field only as read-only derivation evidence.';
  return 'Observe the corresponding official game path naturally in stable authority/lifecycle conditions; RuntimeProbe must never invoke it.';
}

function buildUnsafeConcernRows(objectRows, docs) {
  const concerns = documentedUnsafeConcerns(docs);
  const rows = concerns.map((concern) => {
    const category = unsafeConcernCategory(concern.id);
    const regex = unsafeConcernEvidenceRegex(concern.id);
    const linkedRows = regex ? objectRows.filter((row) => row.sourceDetail?.objectDump && regex.test(row.symbolPath)) : [];
    const objectDumpRefs = [];
    const seenRefs = new Set();
    for (const row of linkedRows.slice(0, 128)) {
      for (const ref of row.objectDumpRefs || []) {
        const key = `${ref.lineNumber}:${ref.rawLineHash}`;
        if (seenRefs.has(key)) continue;
        seenRefs.add(key);
        objectDumpRefs.push({ ...ref, sourceSymbolPath: row.symbolPath });
      }
    }
    const isCarrier = /carrier/.test(concern.id);
    const rowType = /(?:unknown-role|unstable-lifecycle|unstable-rpc)/.test(concern.id) ? 'event' : 'property';
    const symbolPath = `policy://unsafe/${concern.id}`;
    const docsList = [...new Set(concern.documentationRefs.map((ref) => ref.logicalName))].sort();
    return {
      id: `policy-${slug(concern.id)}-${slug(rowType)}`,
      category,
      symbolPath,
      symbol: concern.id,
      owner: 'RuntimeProbeSafetyPolicy',
      member: concern.id,
      type: rowType,
      propertyType: null,
      valueTypePath: null,
      source: 'object dump',
      sourceDetail: {
        objectDump: objectDumpRefs.length > 0,
        runtimeEvidence: false,
        runtimeProbeDocumentation: docsList,
        legacyUnsafeReference: [],
        policyDocumentation: concern.documentationRefs,
        documentationPolicyConcern: true,
        syntheticPolicyCandidate: objectDumpRefs.length === 0,
        sourceAxisNote: objectDumpRefs.length > 0
          ? 'The concern is materially linked to the listed object-dump rows; the rejection classification is provenance-linked to RuntimeProbe policy documentation.'
          : 'No materially mapped dump row was found. Generation must fail validation until an explicit mapping is added; documentation provenance alone must not be mislabeled as object-dump evidence.'
      },
      objectDumpRefs,
      runtimeEvidence: evidenceSummary([]),
      relevanceToCrabSync: `First-class documented ${isCarrier ? 'forbidden carrier' : 'unsafe write/apply'} concern: ${concern.descriptions.join(' / ')}`,
      argumentSchema: [],
      officialAlternativeToRawWrite: false,
      officialAlternativeObservation: unsafeOfficialAlternative(concern.id),
      unsafePathId: concern.id,
      legacyWriteAdviceClassification: 'none',
      readStatus: 'This rejection applies to mutation/carrier use; it does not erase separately cataloged safe read evidence.',
      naturalObservationStatus: 'not applicable to the rejected mutation/carrier path',
      argumentMetadataStatus: 'not applicable; underlying object-dump candidates remain linked for provenance',
      ownershipAuthorityStatus: 'rejected regardless of role or authority in RuntimeProbe',
      visibilityDirection: 'visibility does not authorize mutation or custom payload use',
      lifecycleCoverage: 'rejected across all lifecycle states in RuntimeProbe',
      persistenceUiCoverage: /save|inventory|crystal|keys|equipment|slot|health/i.test(concern.id)
        ? 'persistence/UI consequences are part of the documented rejection'
        : 'not applicable to approving this rejected path',
      writeApplyStatus: isCarrier
        ? `forbidden custom payload carrier; ${unsafeOfficialAlternative(concern.id)}`
        : `raw/direct mutation forbidden; ${unsafeOfficialAlternative(concern.id)}`,
      safetyClassification: 'explicitly-rejected-unsafe',
      coverageDisposition: 'rejected-unsafe',
      nextRequiredObservation: `None for this path in RuntimeProbe. ${unsafeOfficialAlternative(concern.id)}`,
      checklistLinkage: [],
      hookDisposition: 'rejected-unsafe-policy-row'
    };
  });
  const materialized = new Set(rows.map((row) => row.unsafePathId));
  for (const concern of concerns) if (!materialized.has(concern.id)) throw new Error(`Documented unsafe concern was not materialized: ${concern.id}`);
  return rows;
}

function buildRowsFromDump(parsedDump, evidenceIndex, docs, referenceDocs) {
  const definitions = createDefinitionMaps(parsedDump.entries);
  const relevant = parsedDump.entries.filter((entry) => {
    if (entry.isContainerInner || entry.parameter) return false;
    if (isClass(entry)) return actorDefinition(entry, definitions.definitionByPath) && entryIsRelevant(entry, definitions);
    if (!isFunction(entry) && !isProperty(entry) && !isEnumValue(entry)) return false;
    return entryIsRelevant(entry, definitions);
  });
  const rows = [];
  const seen = new Set();
  for (const entry of relevant) {
    const ownerDefinition = definitions.classByName.get(ownerShortName(entry.owner)) || definitions.structByName.get(ownerShortName(entry.owner));
    const rowType = typeForEntry(entry, ownerDefinition);
    const dedupeKey = `${entry.symbolPath}\0${rowType}`;
    if (seen.has(dedupeKey)) continue;
    seen.add(dedupeKey);
    entry.argumentSchema = isFunction(entry) ? argumentSchemaFor(entry, parsedDump.entries) : [];
    const records = recordsForEntry(entry, evidenceIndex);
    const policy = policyFor(entry);
    const category = categoryFor(`${entry.member || ''} ${entry.symbolPath}`, entry.owner);
    const status = statusFields(entry, rowType, records, policy);
    const runtimeSummary = evidenceSummary(records);
    const documentationRefs = docsForEntry(entry, docs);
    const legacyReferenceRefs = docsForEntry(entry, referenceDocs);
    rows.push({
      id: rowId(entry, rowType),
      category,
      symbolPath: entry.symbolPath,
      symbol: entry.member ? `${ownerShortName(entry.owner)}.${entry.member}` : ownerShortName(entry.owner || entry.symbolPath.split('.').pop()),
      owner: ownerShortName(entry.owner),
      member: entry.member,
      type: rowType,
      propertyType: isProperty(entry) ? entry.dumpType : null,
      valueTypePath: entry.enumPath || entry.pcPath || entry.ssPath || entry.emPath || entry.mcPath || null,
      source: records.length ? 'both' : 'object dump',
      sourceDetail: {
        objectDump: true,
        runtimeEvidence: records.length > 0,
        runtimeProbeDocumentation: documentationRefs,
        legacyUnsafeReference: legacyReferenceRefs
      },
      objectDumpRefs: [{ lineNumber: entry.lineNumber, rawLineHash: entry.rawLineHash, dumpType: entry.dumpType }],
      runtimeEvidence: runtimeSummary,
      relevanceToCrabSync: relevanceFor(entry, category),
      argumentSchema: entry.argumentSchema,
      officialAlternativeToRawWrite: OFFICIAL_APPLY_FUNCTIONS.test(entry.member || ''),
      legacyWriteAdviceClassification: legacyReferenceRefs.length ? 'legacy_unsafe_reference; never safety evidence' : 'none',
      ...status,
      checklistLinkage: [],
      hookDisposition: null
    });
  }
  return rows;
}

function buildRuntimeRows(entries, docs, referenceDocs) {
  return entries.map((entry) => {
    const records = entry.records || [];
    const rowType = entry.rowType || (/Context|Event/i.test(entry.member) ? 'event' : (/Find|Class|CrabHC/i.test(entry.symbolPath) ? 'actor' : 'property'));
    const category = categoryFor(`${entry.member} ${entry.symbolPath}`, entry.owner);
    const policy = policyFor(entry);
    const status = statusFields(entry, rowType, records, policy);
    return {
      id: rowId(entry, rowType), category, symbolPath: entry.symbolPath,
      symbol: `${entry.owner}.${entry.member}`, owner: entry.owner, member: entry.member,
      type: rowType, propertyType: null, valueTypePath: null, source: 'runtime evidence',
      sourceDetail: { objectDump: false, runtimeEvidence: true, runtimeProbeDocumentation: docsForEntry(entry, docs), legacyUnsafeReference: docsForEntry(entry, referenceDocs) },
      objectDumpRefs: [], runtimeEvidence: evidenceSummary(records), relevanceToCrabSync: relevanceFor(entry, category),
      argumentSchema: entry.argumentSchema || [], officialAlternativeToRawWrite: OFFICIAL_APPLY_FUNCTIONS.test(entry.member || ''), legacyWriteAdviceClassification: 'none',
      ...status, checklistLinkage: [], hookDisposition: entry.isRuntimeFunction ? 'runtime-discovered-exact-path-needs-object-dump-review' : 'not-a-dump-ufunction'
    };
  });
}

function reapplyRuntime(rows, parsedEvidence, evidenceIndex) {
  return rows.filter((row) => row.sourceDetail && row.sourceDetail.objectDump).map((oldRow) => {
    const entry = {
      namespace: oldRow.symbolPath.startsWith('/Game/') ? 'Game' : 'Script',
      moduleOrPackage: oldRow.symbolPath.match(/^\/(?:Script|Game)\/([^.]*)/)?.[1] || 'runtime',
      owner: oldRow.owner,
      member: oldRow.member,
      symbolPath: oldRow.symbolPath,
      dumpType: oldRow.propertyType,
      argumentSchema: oldRow.argumentSchema || []
    };
    const records = recordsForEntry(entry, evidenceIndex);
    const status = statusFields(entry, oldRow.type, records, policyFor(entry));
    return {
      ...oldRow,
      source: records.length ? 'both' : 'object dump',
      sourceDetail: { ...oldRow.sourceDetail, runtimeEvidence: records.length > 0 },
      runtimeEvidence: evidenceSummary(records),
      ...status,
      checklistLinkage: []
    };
  });
}

function hookDescriptors(rows) {
  const hooks = [];
  for (const row of rows) {
    if (row.unsafePathId) {
      row.hookDisposition = 'rejected-unsafe-policy-row';
      continue;
    }
    const hookable = ['function', 'RPC', 'OnRep', 'multicast', 'event'].includes(row.type) && row.sourceDetail.objectDump;
    if (!hookable) {
      if (row.hookDisposition === null) row.hookDisposition = 'not-a-ufunction';
      continue;
    }
    if (row.safetyClassification === 'intentionally-excluded-product-policy') {
      row.hookDisposition = 'excluded-product-policy';
      continue;
    }
    if (row.safetyClassification === 'explicitly-rejected-unsafe') {
      row.hookDisposition = 'rejected-unsafe';
      continue;
    }
    if (GENERATED_BLUEPRINT_FUNCTION.test(row.member || '')) {
      row.hookDisposition = 'unsupported-generated-blueprint-high-frequency';
      continue;
    }
    if (row.symbolPath.startsWith('/Script/Engine.') && !REVIEWED_ENGINE_PASSIVE_HOOKS.has(row.symbolPath)) {
      row.hookDisposition = 'excluded-engine-not-explicitly-reviewed';
      continue;
    }
    if (row.symbolPath.startsWith('/Script/CrabChampions.') && !nativeHookIsMateriallyRelevant(row)) {
      row.hookDisposition = 'excluded-not-materially-relevant-or-high-volume';
      continue;
    }
    if (RAW_IDENTITY.test(`${row.owner}.${row.member}`)) {
      row.hookDisposition = 'excluded-raw-identity';
      continue;
    }
    const crashContext = CURRENT_HOOK_METHOD_CRASH_CONTEXTS.has(row.symbolPath);
    row.hookDisposition = crashContext
      ? 'research-only-disabled-crash-suspect-current-method'
      : 'research-only-disabled-current-hook-method';
    const fields = PRE_POST_FIELDS[row.category] || PRE_POST_FIELDS['player-runtime-state'];
    hooks.push({
      id: `hook-${slug(row.owner)}-${slug(row.member)}`,
      category: row.category,
      symbolPath: row.symbolPath,
      hookPath: row.symbolPath,
      ownerPath: row.symbolPath.slice(0, row.symbolPath.lastIndexOf(':')),
      type: row.type,
      argumentSchema: row.argumentSchema || [],
      checklistLinks: row.checklistLinkage,
      safetyClassification: crashContext
        ? 'crash-suspect-current-hook-method-disabled'
        : 'research-only-disabled-current-hook-method',
      preStateFields: fields,
      postStateFields: fields,
      initiator: 'disabled-research-candidate',
      callPolicy: 'disabled-do-not-register-or-invoke',
      normalModeEnabled: false,
      researchOnly: true,
      knownCrashContext: crashContext,
      captureTiming: 'pre-and-post-where-supported',
      owningPlayerStateFingerprint: true,
      lifecycleGenerationRequired: true,
      staleUObjectRetention: false
    });
  }
  return hooks.sort((a, b) => a.hookPath.localeCompare(b.hookPath));
}

function readinessVerdicts(rows) {
  const areas = {
    inventory: 'inventory',
    'metadata and enhancements': 'metadata-and-enhancements',
    slots: 'slots',
    equipment: 'equipment',
    crystals: 'crystals',
    health: 'health',
    'multiplayer ownership and visibility': 'multiplayer-ownership-and-visibility',
    lifecycle: 'lifecycle',
    'official apply candidates': 'official-apply-candidates'
  };
  const result = {};
  for (const [area, capability] of Object.entries(areas)) {
    const areaRows = rows.filter((row) => (row.coverageCapabilities || []).includes(capability));
    const unresolved = areaRows.filter((row) => !TERMINAL_DISPOSITIONS.has(row.coverageDisposition));
    result[area] = {
      completeEvidenceCoverage: areaRows.length > 0 && unresolved.length === 0,
      rowCount: areaRows.length,
      unresolvedCount: unresolved.length,
      verdict: areaRows.length > 0 && unresolved.length === 0 ? 'complete' : 'incomplete'
    };
  }
  return result;
}

function summaryFor(rows, hooks, dumpProvenance) {
  const byCategory = {};
  const byDisposition = {};
  const byType = {};
  for (const row of rows) {
    byCategory[row.category] = (byCategory[row.category] || 0) + 1;
    byDisposition[row.coverageDisposition] = (byDisposition[row.coverageDisposition] || 0) + 1;
    byType[row.type] = (byType[row.type] || 0) + 1;
  }
  return {
    objectDumpLineCount: dumpProvenance.lineCount,
    relevantRowCount: rows.length,
    needsCoverageCount: rows.filter((row) => !TERMINAL_DISPOSITIONS.has(row.coverageDisposition)).length,
    passiveHookCount: hooks.length,
    categoryCount: Object.keys(byCategory).length,
    byCategory,
    byDisposition,
    byType
  };
}

function catalogSchema() {
  return {
    '$schema': 'https://json-schema.org/draft/2020-12/schema',
    '$id': 'https://github.com/Dudiebug/crabruntimeprobe/schemas/coverage-catalog-v1.schema.json',
    title: 'CrabSync Coverage Catalog v1',
    type: 'object',
    additionalProperties: false,
    required: ['schemaVersion', 'generatedAt', 'catalogHash', 'sourceProvenance', 'summary', 'policyDecisions', 'readinessVerdicts', 'views', 'hooks', 'rows'],
    properties: {
      schemaVersion: { const: SCHEMA_VERSION },
      generatedAt: { type: 'string' },
      catalogHash: { type: 'string', pattern: '^[0-9a-f]{64}$' },
      sourceProvenance: { type: 'object' },
      summary: { type: 'object' },
      policyDecisions: { type: 'array', items: { type: 'object' } },
      readinessVerdicts: { type: 'object' },
      views: { type: 'object' },
      hooks: { type: 'array', items: { '$ref': '#/$defs/hook' } },
      rows: { type: 'array', items: { '$ref': '#/$defs/row' } }
    },
    '$defs': {
      row: {
        type: 'object',
        required: ['rowId', 'id', 'category', 'symbolPath', 'type', 'source', 'relevance', 'relevanceToCrabSync', 'readStatus', 'naturalObservationStatus', 'argumentMetadataStatus', 'ownershipAuthorityStatus', 'visibilityDirection', 'lifecycleCoverage', 'persistenceUiCoverage', 'writeApplyStatus', 'safetyClassification', 'terminalDisposition', 'coverageDisposition', 'nextRequiredObservation', 'checklistLinks', 'checklistLinkage'],
        properties: {
          rowId: { type: 'string', minLength: 1 }, id: { type: 'string', minLength: 1 }, category: { type: 'string', minLength: 1 }, symbolPath: { type: 'string', minLength: 1 },
          type: { enum: ['property', 'function', 'RPC', 'OnRep', 'multicast', 'struct field', 'actor', 'event'] },
          source: { enum: ['object dump', 'runtime evidence', 'both'] }, relevance: { type: 'string', minLength: 1 }, relevanceToCrabSync: { type: 'string', minLength: 1 },
          readStatus: { type: 'string', minLength: 1 }, naturalObservationStatus: { type: 'string', minLength: 1 },
          argumentMetadataStatus: { type: 'string', minLength: 1 }, ownershipAuthorityStatus: { type: 'string', minLength: 1 },
          visibilityDirection: { type: 'string', minLength: 1 }, lifecycleCoverage: { type: 'string', minLength: 1 },
          persistenceUiCoverage: { type: 'string', minLength: 1 }, writeApplyStatus: { type: 'string', minLength: 1 },
          safetyClassification: { type: 'string', minLength: 1 }, terminalDisposition: { enum: ['confirmed_clean', 'unsafe_rejected', 'unsupported', 'policy_excluded', 'needs_coverage'] }, coverageDisposition: { type: 'string', minLength: 1 },
          nextRequiredObservation: { type: 'string', minLength: 1 }, checklistLinks: { type: 'array', minItems: 1, items: { type: 'string' } }, checklistLinkage: { type: 'array', minItems: 1, items: { type: 'string' } }
        }
      },
      hook: {
        type: 'object',
        required: ['id', 'category', 'symbolPath', 'hookPath', 'ownerPath', 'type', 'argumentSchema', 'checklistLinks', 'safetyClassification', 'preStateFields', 'postStateFields'],
        properties: {
          id: { type: 'string' }, category: { type: 'string' }, symbolPath: { type: 'string' }, hookPath: { type: 'string' }, ownerPath: { type: 'string' }, type: { type: 'string' },
          argumentSchema: { type: 'array' }, checklistLinks: { type: 'array' }, safetyClassification: { const: 'passive-observation-only' },
          preStateFields: { type: 'array' }, postStateFields: { type: 'array' }
        }
      }
    }
  };
}

function inventoryStages() {
  const stage = (number, id, label, prerequisite, allowedReads) => ({
    number, id, label, prerequisite, allowedReads, cleanEvidenceRequired: true,
    advanceAutomatically: true, flushAfterMeaningfulRow: true, preserveUnknownAsUnknown: true,
    stopAfterFirstNativeTechniqueFailure: true, categoryCircuitBreaker: `inventory.${id}`
  });
  return [
    stage(1, 'wrapper-shape', 'Confirm wrapper shape', null, ['five local inventory wrapper types only']),
    stage(2, 'count-metadata', 'Confirm count metadata', 'wrapper-shape', ['five wrapper counts only']),
    stage(3, 'first-element', 'Access at most one first element', 'count-metadata', ['index zero only in one non-empty category at a time']),
    stage(4, 'item-da-identity', 'Read item DA identity', 'first-element', ['redacted DA class/full-name summary']),
    stage(5, 'inventoryinfo-parent', 'Read InventoryInfo parent', 'item-da-identity', ['parent shape only']),
    stage(6, 'metadata-scalars', 'Read Level and AccumulatedBuff', 'inventoryinfo-parent', ['Level', 'AccumulatedBuff']),
    stage(7, 'enhancement-shape', 'Read enhancement shape', 'metadata-scalars', ['Enhancements wrapper shape']),
    stage(8, 'enhancement-count', 'Read enhancement count', 'enhancement-shape', ['Enhancements count']),
    stage(9, 'enhancement-values', 'Read capped enhancement values', 'enhancement-count', ['bounded enhancement enum values']),
    stage(10, 'capped-local-iteration', 'Perform capped local inventory iteration', 'enhancement-values', ['proven fields only; per-category caps']),
    stage(11, 'duplicate-semantics', 'Study duplicate semantics', 'capped-local-iteration', ['same-DA count, metadata, and stable fingerprints']),
    stage(12, 'slot-index-stability', 'Study slot/index stability', 'duplicate-semantics', ['bounded order/index fingerprints']),
    stage(13, 'joined-client-replay', 'Repeat proven reads as joined client', 'slot-index-stability', ['only reads proven through stage 12']),
    stage(14, 'remote-visibility', 'Check remote inventory visibility', 'joined-client-replay', ['scoped remote PlayerState candidates; no arbitrary UObject crawl'])
  ];
}

function buildProfile(hooks, catalogHash, generatedAt, rows = []) {
  const blueprintClassRoots = rows
    .filter((row) => row.type === 'actor' && row.symbolPath.startsWith('/Game/') && row.coverageDisposition !== 'excluded-product-policy' && row.coverageDisposition !== 'rejected-unsafe' && !/AnimBP|SK_.*Anim/i.test(row.symbolPath))
    .map((row) => ({ shortName: row.owner, objectDumpPath: row.symbolPath }))
    .sort((a, b) => a.objectDumpPath.localeCompare(b.objectDumpPath));
  return {
    schemaVersion: 'crabsync-full-observe-profile-v1',
    profileVersion: PROFILE_VERSION,
    id: 'crabsync-full-observe',
    name: 'CrabSync Full Observe',
    generatedAt,
    catalogHash,
    mode: 'snapshot-observation',
    description: 'A snapshot-first, read-only campaign. Normal Play Guide sessions register no gameplay or lifecycle hooks, perform no runtime discovery, and leave staged inventory research disabled.',
    runtimeContract: { ...RUNTIME_CONTRACT },
    safety: {
      writesEnabled: false, rpcInvocationEnabled: false, propertyMutationEnabled: false,
      hudHookEnabled: false, rawIdentityEnabled: false, externalRelayEnabled: false,
      syntheticValuesEnabled: false, staleUObjectRetentionEnabled: false,
      passiveHooksDoNotAuthorizeCalls: true, evidenceFlushAfterMeaningfulRow: true
    },
    normalMode: {
      snapshotSamplerEnabled: true,
      gameplayHooksEnabled: false,
      lifecycleHooksEnabled: false,
      runtimeDiscoveryEnabled: false,
      inventoryEscalationEnabled: false,
      guiOwnsChecklistQualification: true
    },
    passiveHooks: {
      enabled: false,
      registerTogether: false,
      researchOnly: true,
      globalEvidenceRowCap: RUNTIME_CONTRACT.fullObserveHookGlobalRowCap,
      perDescriptorEvidenceRowCap: RUNTIME_CONTRACT.fullObserveHookPerDescriptorRowCap,
      minimumObservationIntervalSeconds: RUNTIME_CONTRACT.fullObserveHookMinIntervalSeconds,
      trackedDescriptorCap: RUNTIME_CONTRACT.fullObserveHookTrackedDescriptorCap,
      descriptors: hooks
    },
    inventoryEscalation: {
      enabled: false, researchOnly: true, persistProgress: true, resumeAfterRestart: true, independentCategoryCircuitBreakers: true,
      maximumInventoryEntriesPerCategory: RUNTIME_CONTRACT.fullObserveMaxInventoryItems,
      maximumEnhancementValuesPerItem: RUNTIME_CONTRACT.fullObserveMaxEnhancements,
      maximumStageRowsPerCategory: RUNTIME_CONTRACT.fullObserveMaxStageRowsPerCategory,
      cleanSamplesRequiredBeforeStageAdvance: RUNTIME_CONTRACT.fullObserveCleanSamplesRequired,
      intervalSeconds: RUNTIME_CONTRACT.fullObserveInventoryIntervalSeconds,
      heartbeatSeconds: RUNTIME_CONTRACT.fullObserveInventoryHeartbeatSeconds,
      slotStabilityWindowSeconds: RUNTIME_CONTRACT.fullObserveSlotStabilityWindowSeconds,
      slotStabilitySamplesRequired: RUNTIME_CONTRACT.fullObserveSlotStabilitySamplesRequired,
      stages: inventoryStages()
    },
    runtimeDiscovery: {
      enabled: false,
      researchOnly: true,
      readOnly: true,
      enumerateArbitraryUObjects: false,
      nativeClassRoots: [
        { shortName: 'CrabPS', objectDumpPath: '/Script/CrabChampions.CrabPS' },
        { shortName: 'CrabPC', objectDumpPath: '/Script/CrabChampions.CrabPC' },
        { shortName: 'CrabPlayerC', objectDumpPath: '/Script/CrabChampions.CrabPlayerC' },
        { shortName: 'CrabHC', objectDumpPath: '/Script/CrabChampions.CrabHC' },
        { shortName: 'CrabGS', objectDumpPath: '/Script/CrabChampions.CrabGS' },
        { shortName: 'CrabInteractPickup', objectDumpPath: '/Script/CrabChampions.CrabInteractPickup' },
        { shortName: 'CrabOverlapPickup', objectDumpPath: '/Script/CrabChampions.CrabOverlapPickup' },
        { shortName: 'CrabAnvil', objectDumpPath: '/Script/CrabChampions.CrabAnvil' },
        { shortName: 'CrabChest', objectDumpPath: '/Script/CrabChampions.CrabChest' },
        { shortName: 'CrabPortal', objectDumpPath: '/Script/CrabChampions.CrabPortal' },
        { shortName: 'CrabShopPedestal', objectDumpPath: '/Script/CrabChampions.CrabShopPedestal' }
      ],
      blueprintClassRoots,
      maximumResolvedClassesPerGeneration: RUNTIME_CONTRACT.maximumResolvedClassesPerGeneration,
      maximumFunctionsPerResolvedClass: RUNTIME_CONTRACT.maximumFunctionsPerResolvedClass,
      reflectionScope: 'exact-class-roots-only',
      excludedGeneratedFunctions: ['ExecuteUbergraph_*', 'EvaluateGraphExposedInputs_*', 'AnimGraph'],
      newlyDiscoveredCandidatesDisposition: 'needs-coverage',
      normalHookRegistrationProhibited: true,
      requireExactResolvedUFunctionPathBeforeAnyFutureIsolatedMethod: true
    },
    lifecycle: {
      invalidateReferencesOnGenerationChange: true,
      gatedStates: ['startup', 'menu', 'lobby', 'loading', 'travel', 'respawn', 'join', 'disconnect', 'unstable-playerstate'],
      stableSamplesRequiredBeforeStagedRead: RUNTIME_CONTRACT.snapshotStableSamplesRequired,
      stableDwellSecondsRequiredBeforeStagedRead: RUNTIME_CONTRACT.snapshotStableDwellSeconds
    }
  };
}

function csvEscape(value) {
  const string = value == null ? '' : (Array.isArray(value) ? value.join('; ') : String(value));
  return /[",\r\n]/.test(string) ? `"${string.replace(/"/g, '""')}"` : string;
}

function renderCsv(rows) {
  const fields = ['rowId', 'id', 'category', 'symbolPath', 'symbol', 'type', 'source', 'relevance', 'relevanceToCrabSync', 'readStatus', 'naturalObservationStatus', 'argumentMetadataStatus', 'ownershipAuthorityStatus', 'visibilityDirection', 'lifecycleCoverage', 'persistenceUiCoverage', 'writeApplyStatus', 'safetyClassification', 'terminalDisposition', 'coverageDisposition', 'nextRequiredObservation', 'checklistLinks', 'checklistLinkage', 'coverageCapabilities'];
  return `${fields.join(',')}\n${rows.map((row) => fields.map((field) => csvEscape(row[field])).join(',')).join('\n')}\n`;
}

function mdEscape(value) {
  return String(value == null ? '' : value).replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
}

function renderDocumentation(catalog) {
  const lines = [];
  lines.push('# CrabSync Evidence Coverage Catalog', '');
  lines.push('This catalog is generated from the complete supplied UE4SS object dump, all checked-in RuntimeProbe documentation and JSONL evidence, and explicitly labeled legacy reference notes. Object-dump presence is not runtime proof. Normal Play Guide mode is hook-free; exact-call descriptors are disabled research records and never authorize registration or invocation.', '');
  lines.push('## Provenance', '');
  const dump = catalog.sourceProvenance.objectDump;
  lines.push(`- Object dump: \`${dump.logicalName}\`, ${dump.lineCount.toLocaleString()} lines, ${dump.byteSize.toLocaleString()} bytes, SHA-256 \`${dump.sha256}\`.`);
  lines.push(`- RuntimeProbe documentation: ${catalog.sourceProvenance.runtimeProbeDocumentation.fileCount} files, aggregate SHA-256 \`${catalog.sourceProvenance.runtimeProbeDocumentation.aggregateSha256}\`.`);
  lines.push(`- Runtime evidence: ${catalog.sourceProvenance.runtimeEvidence.fileCount} JSONL files / ${catalog.sourceProvenance.runtimeEvidence.recordCount} parsed rows, aggregate SHA-256 \`${catalog.sourceProvenance.runtimeEvidence.aggregateSha256}\`.`);
  lines.push(`- Legacy reference: ${catalog.sourceProvenance.legacyUnsafeReference.fileCount} files. It is concern provenance only; all direct-write advice remains unsafe/unproven.`);
  lines.push(`- Catalog hash: \`${catalog.catalogHash}\`.`, '');
  lines.push('No absolute source paths, raw platform identities, or UObject addresses are stored in generated artifacts.', '');
  lines.push('## Coverage Summary', '');
  lines.push(`- Relevant rows: ${catalog.summary.relevantRowCount}`);
  lines.push(`- Needs Coverage: ${catalog.summary.needsCoverageCount}`);
  lines.push(`- Disabled exact-call research descriptors: ${catalog.summary.passiveHookCount}`);
  lines.push(`- Categories: ${catalog.summary.categoryCount}`, '');
  lines.push('| Category | Rows | Needs Coverage |');
  lines.push('|---|---:|---:|');
  for (const [category, count] of Object.entries(catalog.summary.byCategory).sort()) {
    const needs = catalog.rows.filter((row) => row.category === category && !TERMINAL_DISPOSITIONS.has(row.coverageDisposition)).length;
    lines.push(`| ${mdEscape(category)} | ${count} | ${needs} |`);
  }
  lines.push('', '## Required Final Coverage Verdicts', '');
  lines.push('| Area | Complete evidence coverage? | Rows | Unresolved | Verdict |');
  lines.push('|---|---|---:|---:|---|');
  for (const [area, verdict] of Object.entries(catalog.readinessVerdicts)) {
    lines.push(`| ${mdEscape(area)} | ${verdict.completeEvidenceCoverage ? 'yes' : 'no'} | ${verdict.rowCount} | ${verdict.unresolvedCount} | ${verdict.verdict} |`);
  }
  lines.push('', '**Current conclusion:** CrabSync does not have complete evidence coverage wherever any row remains in Needs Coverage. Read evidence, natural-call evidence, argument evidence, authority, lifecycle, remote visibility, UI/persistence, and write/apply safety are independent gates.', '');
  lines.push('## Safety and Policy', '');
  lines.push('- Keys are intentionally excluded by product policy.');
  lines.push('- HUD `ReceiveDrawHUD` and unscoped `FindFirstOf(CrabHC)` are explicitly rejected unsafe paths.');
  lines.push('- Raw property writes, inventory reconstruction, nested metadata writes, carrier hijacking, and mutating RPC calls remain forbidden in RuntimeProbe.');
  lines.push('- Exact-call research descriptors are disabled in normal mode. The current passive-hook method is not recommended after the 2026-07-10 crash incident.');
  lines.push('- Official functions remain catalog candidates only. State correlation does not prove mimic/call safety.');
  lines.push('- Unknown and untested rows are retained in Needs Coverage; they are never silently converted to defaults.', '');
  lines.push('## Needs Coverage View', '');
  lines.push('| Category | Symbol/path | Type | Source | Read | Natural observation | Args/metadata | Authority/direction | Lifecycle | Persistence/UI | Write/apply | Safety | Next observation | Checklist |');
  lines.push('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|');
  for (const id of catalog.views.needsCoverage) {
    const row = catalog.rows.find((candidate) => candidate.id === id);
    lines.push(`| ${mdEscape(row.category)} | \`${mdEscape(row.symbolPath)}\` | ${row.type} | ${row.source} | ${mdEscape(row.readStatus)} | ${mdEscape(row.naturalObservationStatus)} | ${mdEscape(row.argumentMetadataStatus)} | ${mdEscape(`${row.ownershipAuthorityStatus}; ${row.visibilityDirection}`)} | ${mdEscape(row.lifecycleCoverage)} | ${mdEscape(row.persistenceUiCoverage)} | ${mdEscape(row.writeApplyStatus)} | ${mdEscape(row.safetyClassification)} | ${mdEscape(row.nextRequiredObservation)} | ${mdEscape(row.checklistLinkage.join(', '))} |`);
  }
  lines.push('', '## Terminally Resolved Rows', '');
  lines.push('| Category | Symbol/path | Disposition | Safety |');
  lines.push('|---|---|---|---|');
  for (const id of catalog.views.terminal) {
    const row = catalog.rows.find((candidate) => candidate.id === id);
    lines.push(`| ${mdEscape(row.category)} | \`${mdEscape(row.symbolPath)}\` | ${row.coverageDisposition} | ${mdEscape(row.safetyClassification)} |`);
  }
  lines.push('', '## Regeneration', '');
  lines.push('```powershell');
  lines.push('node tools/generate_crabsync_coverage_catalog.js --dump <UE4SS_ObjectDump.txt> --reference <CrabInvSync-objectdump-directory>');
  lines.push('node tools/generate_crabsync_coverage_catalog.js --refresh-runtime');
  lines.push('node tools/generate_crabsync_coverage_catalog.js --validate');
  lines.push('```', '');
  return `${lines.join('\n')}\n`;
}

function luaString(value) {
  return JSON.stringify(String(value)).replace(/\u2028|\u2029/g, (match) => match === '\\u2028' ? '\\226\\128\\168' : '\\226\\128\\169');
}

function luaSerialize(value, indent = 0) {
  if (value === null || value === undefined) return 'nil';
  if (typeof value === 'string') return luaString(value);
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  const pad = '  '.repeat(indent);
  const childPad = '  '.repeat(indent + 1);
  if (Array.isArray(value)) {
    if (value.length === 0) return '{}';
    return `{\n${value.map((item) => `${childPad}${luaSerialize(item, indent + 1)},`).join('\n')}\n${pad}}`;
  }
  const entries = Object.entries(value);
  if (entries.length === 0) return '{}';
  return `{\n${entries.map(([key, item]) => {
    const luaKey = /^[A-Za-z_][A-Za-z0-9_]*$/.test(key) ? key : `[${luaString(key)}]`;
    return `${childPad}${luaKey} = ${luaSerialize(item, indent + 1)},`;
  }).join('\n')}\n${pad}}`;
}

function renderLua(catalog) {
  const payload = {
    schemaVersion: SCHEMA_VERSION,
    catalogHash: catalog.catalogHash,
    generatedAt: catalog.generatedAt,
    source: {
      logicalName: catalog.sourceProvenance.objectDump.logicalName,
      sha256: catalog.sourceProvenance.objectDump.sha256,
      lineCount: catalog.sourceProvenance.objectDump.lineCount
    },
    safety: { observeOnly: true, invokeFunctions: false, writes: false, rawIdentity: false, hudHook: false },
    discoveryRules: buildProfile([], catalog.catalogHash, catalog.generatedAt, catalog.rows).runtimeDiscovery,
    hooks: catalog.hooks
  };
  return `-- Generated by tools/generate_crabsync_coverage_catalog.js. DO NOT EDIT.\n-- Exact passive descriptors only; this module never registers, invokes, or mutates anything.\nreturn ${luaSerialize(payload)}\n`;
}

function deterministicGeneratedAt(records, dumpMtimeMs, previousGeneratedAt) {
  const mtimes = records.map((record) => record.mtimeMs).filter(Number.isFinite);
  if (Number.isFinite(dumpMtimeMs)) mtimes.push(dumpMtimeMs);
  if (previousGeneratedAt) mtimes.push(Date.parse(previousGeneratedAt));
  return new Date(Math.max(...mtimes, 0)).toISOString();
}

function policyDecisions(rows) {
  const ids = (predicate) => rows.filter(predicate).map((row) => row.id).sort();
  return [
    { id: 'keys-excluded', classification: 'excluded-product-policy', rowIds: ids((row) => row.coverageDisposition === 'excluded-product-policy'), rule: 'Keys do not sync unless explicitly re-approved.' },
    { id: 'hud-hook-unsafe', classification: 'rejected-unsafe', rowIds: ids((row) => row.symbolPath === '/Script/Engine.HUD:ReceiveDrawHUD'), rule: 'ReceiveDrawHUD remains disabled because of crash history.' },
    { id: 'unscoped-health-unsafe', classification: 'rejected-unsafe', rowIds: ids((row) => /FindFirstOf.*CrabHC/i.test(row.symbolPath)), rule: 'Player health must use CrabPC -> PlayerState -> CrabPS -> HealthInfo.' },
    { id: 'documented-unsafe-write-and-carrier-paths', classification: 'rejected-unsafe', rowIds: ids((row) => Boolean(row.unsafePathId)), rule: 'Every unsafe write/apply and forbidden carrier entry documented in the two policy ledgers is a first-class terminal catalog row.' },
    { id: 'raw-identity-redaction', classification: 'redacted-fingerprint-only', rowIds: ids((row) => row.safetyClassification === 'redacted-fingerprint-only'), rule: 'Raw platform/name/identity values remain disabled; bounded fingerprints may be used only for session correlation.' },
    { id: 'raw-writes-forbidden', classification: 'unsafe-unproven', rowIds: ids((row) => ['property', 'struct field'].includes(row.type)), rule: 'RuntimeProbe never writes properties; official natural paths are investigated first.' },
    { id: 'official-apply-candidates', classification: 'passive-observation-only', rowIds: ids((row) => row.officialAlternativeToRawWrite), rule: 'Presence or natural observation does not authorize calls or prove write/apply safety.' },
    { id: 'legacy-reference-write-advice', classification: 'legacy_unsafe_reference', rowIds: ids((row) => (row.sourceDetail?.legacyUnsafeReference || []).length > 0), rule: 'Legacy write suggestions are concern provenance only and can never advance a safety status.' }
  ];
}

function validateCatalog(catalog, checklist, profile) {
  const errors = [];
  if (!catalog || catalog.schemaVersion !== SCHEMA_VERSION) errors.push(`schemaVersion must be ${SCHEMA_VERSION}`);
  if (!catalog.sourceProvenance?.objectDump?.sha256) errors.push('object dump provenance hash missing');
  if (!Number.isInteger(catalog.sourceProvenance?.objectDump?.lineCount) || catalog.sourceProvenance.objectDump.lineCount <= 0) errors.push('object dump line count invalid');
  if (catalog.sourceProvenance?.objectDump?.scannedLineCount !== catalog.sourceProvenance?.objectDump?.lineCount) errors.push('object dump was not scanned line-for-line');
  const serialized = JSON.stringify({ catalog, checklist, profile });
  if (/[A-Z]:\\Users\\|file:\/\//i.test(serialized)) errors.push('absolute local path leaked into generated artifacts');
  if (!Array.isArray(catalog.rows) || catalog.rows.length === 0) errors.push('catalog has no rows');
  const required = catalogSchema().$defs.row.required;
  const rowIds = new Set();
  const pathsById = new Map();
  const checklistIds = new Set((checklist?.entries || []).map((entry) => entry.id));
  for (const row of catalog.rows || []) {
    if (rowIds.has(row.id)) errors.push(`duplicate row id: ${row.id}`);
    rowIds.add(row.id);
    pathsById.set(row.id, row.symbolPath);
    for (const field of required) {
      if (!(field in row) || row[field] == null || row[field] === '' || (Array.isArray(row[field]) && row[field].length === 0)) errors.push(`${row.id}: missing ${field}`);
    }
    if (!['object dump', 'runtime evidence', 'both'].includes(row.source)) errors.push(`${row.id}: invalid source ${row.source}`);
    if (!['property', 'function', 'RPC', 'OnRep', 'multicast', 'struct field', 'actor', 'event'].includes(row.type)) errors.push(`${row.id}: invalid type ${row.type}`);
    if (row.rowId !== row.id || row.relevance !== row.relevanceToCrabSync || JSON.stringify(row.checklistLinks) !== JSON.stringify(row.checklistLinkage)) errors.push(`${row.id}: canonical row aliases are inconsistent`);
    if (row.terminalDisposition !== terminalDispositionFor(row.coverageDisposition)) errors.push(`${row.id}: terminalDisposition alias is inconsistent`);
    for (const link of row.checklistLinkage || []) if (!checklistIds.has(link)) errors.push(`${row.id}: missing checklist link ${link}`);
    for (const ref of row.objectDumpRefs || []) if (!Number.isInteger(ref.lineNumber) || ref.lineNumber < 1 || ref.lineNumber > catalog.sourceProvenance.objectDump.lineCount) errors.push(`${row.id}: invalid object dump line reference`);
    const inNeeds = catalog.views.needsCoverage.includes(row.id);
    if (inNeeds === TERMINAL_DISPOSITIONS.has(row.coverageDisposition)) errors.push(`${row.id}: Needs Coverage view disagrees with disposition ${row.coverageDisposition}`);
    if (isKeysPolicyCandidate({ moduleOrPackage: row.symbolPath.startsWith('/Script/CrabChampions.') ? 'CrabChampions' : '', owner: row.owner, member: row.member, argumentSchema: row.argumentSchema }) && row.coverageDisposition !== 'excluded-product-policy') errors.push(`${row.id}: keys are not policy excluded`);
    if (row.sourceDetail?.legacyUnsafeReference?.length && /(?:safe|proven)/i.test(row.legacyWriteAdviceClassification) && !/unsafe/i.test(row.legacyWriteAdviceClassification)) errors.push(`${row.id}: legacy reference promoted write advice`);
    if (['object dump', 'both'].includes(row.source) && row.sourceDetail?.objectDump !== true) errors.push(`${row.id}: source claims object dump without a materially mapped object-dump source`);
    if (row.sourceDetail?.objectDump === true && !(row.objectDumpRefs || []).length) errors.push(`${row.id}: object-dump source has no concrete line reference`);
    if (row.unsafePathId) {
      if (row.coverageDisposition !== 'rejected-unsafe' || row.terminalDisposition !== 'unsafe_rejected' || row.safetyClassification !== 'explicitly-rejected-unsafe') errors.push(`${row.id}: documented unsafe concern is not terminally rejected`);
      if (!(row.sourceDetail?.policyDocumentation || []).length) errors.push(`${row.id}: documented unsafe concern lacks doc+line provenance`);
      if (!/^None\b/.test(row.nextRequiredObservation || '')) errors.push(`${row.id}: rejected unsafe concern must have no RuntimeProbe observation requirement`);
      if (row.hookDisposition !== 'rejected-unsafe-policy-row') errors.push(`${row.id}: documented unsafe concern has an invalid hook disposition`);
    }
    if (/^runtime-discovered-exact-path/.test(row.hookDisposition || '')) {
      if (!['function', 'RPC', 'OnRep', 'multicast', 'event'].includes(row.type)) errors.push(`${row.id}: runtime-discovered UFunction was downgraded to ${row.type}`);
      if (!/^\/(?:Script|Game)\/.+:[^:]+$/.test(row.symbolPath)) errors.push(`${row.id}: runtime-discovered UFunction path is not exact`);
    }
  }
  const expectedUnsafeIds = new Set(documentedUnsafeConcerns(repositoryDocuments()).map((concern) => concern.id));
  const materializedUnsafeIds = new Set((catalog.rows || []).filter((row) => row.unsafePathId).map((row) => row.unsafePathId));
  for (const id of expectedUnsafeIds) if (!materializedUnsafeIds.has(id)) errors.push(`documented unsafe concern missing from catalog: ${id}`);
  for (const id of materializedUnsafeIds) if (!expectedUnsafeIds.has(id)) errors.push(`stale/unknown unsafe concern row lacks current documentation provenance: ${id}`);
  if (catalog.sourceProvenance?.refreshMode === 'full object-dump scan') {
    const lockedSlots = (catalog.rows || []).find((row) => row.symbolPath === '/Script/CrabChampions.ECrabDifficultyModifier::LockedSlots');
    if (!lockedSlots) errors.push('required enum candidate missing: ECrabDifficultyModifier::LockedSlots');
    else if (lockedSlots.type !== 'struct field' || lockedSlots.category !== 'inventory-slots' || !(lockedSlots.objectDumpRefs || []).length) errors.push('ECrabDifficultyModifier::LockedSlots is not classified as an object-dump-backed inventory-slot struct field');
  }
  const hookIds = new Set();
  for (const hook of catalog.hooks || []) {
    if (hookIds.has(hook.id)) errors.push(`duplicate hook id: ${hook.id}`);
    hookIds.add(hook.id);
    if (hook.hookPath !== hook.symbolPath || !/^\/(?:Script|Game)\//.test(hook.hookPath)) errors.push(`${hook.id}: hook path is not exact`);
    if (!['research-only-disabled-current-hook-method', 'crash-suspect-current-hook-method-disabled'].includes(hook.safetyClassification)
        || hook.callPolicy !== 'disabled-do-not-register-or-invoke'
        || hook.normalModeEnabled !== false || hook.researchOnly !== true) {
      errors.push(`${hook.id}: exact-call research descriptor is not disabled`);
    }
    if (/ReceiveDrawHUD|(?:^|[.:])Keys?(?:$|[.:])|UniqueId|PlayerName/i.test(hook.hookPath)) errors.push(`${hook.id}: forbidden hook included`);
    if (hook.hookPath.startsWith('/Script/Engine.') && !REVIEWED_ENGINE_PASSIVE_HOOKS.has(hook.hookPath)) errors.push(`${hook.id}: unreviewed Engine hook included`);
    if (hook.hookPath.startsWith('/Script/CrabChampions.') && !nativeHookIsMateriallyRelevant({ owner: hook.ownerPath.split('.').pop(), member: hook.hookPath.split(':').pop() })) errors.push(`${hook.id}: irrelevant/high-volume native hook included`);
  }
  const hookPaths = new Set((catalog.hooks || []).map((hook) => hook.hookPath));
  for (const requiredPath of REQUIRED_PASSIVE_HOOKS) if (!hookPaths.has(requiredPath)) errors.push(`required exact-call research candidate missing: ${requiredPath}`);
  for (const crashPath of CURRENT_HOOK_METHOD_CRASH_CONTEXTS) {
    const row = (catalog.rows || []).find((candidate) => candidate.symbolPath === crashPath);
    const hook = (catalog.hooks || []).find((candidate) => candidate.hookPath === crashPath);
    if (!row || row.hookDisposition !== 'research-only-disabled-crash-suspect-current-method'
        || !/Do not hook this path/.test(row.nextRequiredObservation || '')) {
      errors.push(`known crash-context path is not explicitly disabled: ${crashPath}`);
    }
    if (!hook || hook.knownCrashContext !== true || hook.normalModeEnabled !== false) {
      errors.push(`known crash-context descriptor is not disabled: ${crashPath}`);
    }
  }
  if ((catalog.rows || []).some((row) => row.hookDisposition === 'included-passive-observation')) {
    errors.push('catalog still includes normal passive-hook dispositions');
  }
  if ((catalog.rows || []).some((row) => /capture the passive hook/i.test(row.nextRequiredObservation || ''))) {
    errors.push('catalog still recommends capturing a passive hook');
  }
  for (const row of catalog.rows || []) {
    if (['function', 'RPC', 'OnRep', 'multicast', 'event'].includes(row.type) && row.sourceDetail.objectDump && !row.hookDisposition) errors.push(`${row.id}: UFunction lacks explicit hook disposition`);
  }
  if (profile?.safety?.writesEnabled !== false || profile?.safety?.rpcInvocationEnabled !== false || profile?.safety?.propertyMutationEnabled !== false || profile?.safety?.hudHookEnabled !== false || profile?.safety?.rawIdentityEnabled !== false) errors.push('profile safe defaults are not all false');
  if (profile?.mode !== 'snapshot-observation' || profile?.normalMode?.snapshotSamplerEnabled !== true ||
      profile?.normalMode?.gameplayHooksEnabled !== false || profile?.normalMode?.lifecycleHooksEnabled !== false ||
      profile?.normalMode?.runtimeDiscoveryEnabled !== false || profile?.normalMode?.inventoryEscalationEnabled !== false ||
      profile?.normalMode?.guiOwnsChecklistQualification !== true) errors.push('profile normal mode is not snapshot-first and hook-free');
  if (profile?.passiveHooks?.enabled !== false || profile?.passiveHooks?.registerTogether !== false ||
      profile?.passiveHooks?.researchOnly !== true || profile?.inventoryEscalation?.enabled !== false ||
      profile?.inventoryEscalation?.researchOnly !== true || profile?.runtimeDiscovery?.enabled !== false ||
      profile?.runtimeDiscovery?.researchOnly !== true) errors.push('profile research instrumentation is enabled in normal mode');
  for (const [field, expected] of Object.entries(RUNTIME_CONTRACT)) {
    if (profile?.runtimeContract?.[field] !== expected) errors.push(`profile runtime contract mismatch: ${field} must be ${expected}`);
  }
  if (profile?.inventoryEscalation?.stages?.length !== 14) errors.push('inventory escalation must have exactly 14 required stages');
  if (profile?.inventoryEscalation?.maximumInventoryEntriesPerCategory !== RUNTIME_CONTRACT.fullObserveMaxInventoryItems ||
      profile?.inventoryEscalation?.maximumEnhancementValuesPerItem !== RUNTIME_CONTRACT.fullObserveMaxEnhancements ||
      profile?.inventoryEscalation?.maximumStageRowsPerCategory !== RUNTIME_CONTRACT.fullObserveMaxStageRowsPerCategory ||
      profile?.inventoryEscalation?.cleanSamplesRequiredBeforeStageAdvance !== RUNTIME_CONTRACT.fullObserveCleanSamplesRequired) errors.push('profile staged-inventory caps/clean-sample contract drifted from runtime defaults');
  if (profile?.lifecycle?.stableSamplesRequiredBeforeStagedRead !== RUNTIME_CONTRACT.snapshotStableSamplesRequired ||
      profile?.lifecycle?.stableDwellSecondsRequiredBeforeStagedRead !== RUNTIME_CONTRACT.snapshotStableDwellSeconds) errors.push('profile lifecycle stability contract drifted from snapshot runtime defaults');
  if (profile?.runtimeDiscovery?.maximumResolvedClassesPerGeneration !== RUNTIME_CONTRACT.maximumResolvedClassesPerGeneration ||
      profile?.runtimeDiscovery?.maximumFunctionsPerResolvedClass !== RUNTIME_CONTRACT.maximumFunctionsPerResolvedClass) errors.push('profile runtime-discovery caps drifted from runtime defaults');
  if (profile?.passiveHooks?.globalEvidenceRowCap !== RUNTIME_CONTRACT.fullObserveHookGlobalRowCap ||
      profile?.passiveHooks?.perDescriptorEvidenceRowCap !== RUNTIME_CONTRACT.fullObserveHookPerDescriptorRowCap ||
      profile?.passiveHooks?.minimumObservationIntervalSeconds !== RUNTIME_CONTRACT.fullObserveHookMinIntervalSeconds ||
      profile?.passiveHooks?.trackedDescriptorCap !== RUNTIME_CONTRACT.fullObserveHookTrackedDescriptorCap) errors.push('profile passive-hook bounds drifted from runtime defaults');
  const runtimeConfigPath = path.join(ROOT, 'client', 'Mods', 'CrabRuntimeProbe', 'Scripts', 'config.txt');
  if (fs.existsSync(runtimeConfigPath)) {
    const runtimeConfigText = fs.readFileSync(runtimeConfigPath, 'utf8');
    for (const [field, expected] of Object.entries(RUNTIME_CONTRACT)
      .filter(([name]) => name.startsWith('fullObserve') || name.startsWith('snapshot'))) {
      const match = runtimeConfigText.match(new RegExp(`^${field}\\s*=\\s*(\\d+)\\s*$`, 'm'));
      if (!match || Number(match[1]) !== expected) errors.push(`runtime config/profile contract mismatch: ${field} must be ${expected}`);
    }
  }
  if ((profile?.passiveHooks?.descriptors || []).length !== (catalog.hooks || []).length) errors.push('profile hook descriptors do not match catalog');
  const blueprintRoots = profile?.runtimeDiscovery?.blueprintClassRoots || [];
  if (blueprintRoots.length > (profile?.runtimeDiscovery?.maximumResolvedClassesPerGeneration || 0)) errors.push('Blueprint class roots exceed the configured cap');
  for (const root of blueprintRoots) {
    if (!root.shortName || !/^\/Game\//.test(root.objectDumpPath || '') || /\*/.test(`${root.shortName}${root.objectDumpPath}`)) errors.push(`invalid/non-exact Blueprint class root: ${JSON.stringify(root)}`);
    if (/AnimBP|ExecuteUbergraph|BP_(?:Chest|Totem)_Key/i.test(`${root.shortName} ${root.objectDumpPath}`)) errors.push(`forbidden Blueprint class root: ${root.objectDumpPath}`);
  }
  if ((catalog.rows || []).some((row) => /BP_Totem_Upgrade/.test(row.symbolPath)) && !blueprintRoots.some((root) => root.shortName === 'BP_Totem_Upgrade_C')) errors.push('BP_Totem_Upgrade_C missing from exact Blueprint roots');
  for (const entry of checklist?.entries || []) {
    if (!entry.section || !entry.nextAction) errors.push(`${entry.id || 'unknown checklist entry'}: section/nextAction missing`);
  }
  if (catalog.summary.relevantRowCount !== catalog.rows.length) errors.push('summary row count mismatch');
  if (catalog.summary.passiveHookCount !== catalog.hooks.length) errors.push('summary hook count mismatch');
  if (catalog.summary.needsCoverageCount !== catalog.views.needsCoverage.length) errors.push('Needs Coverage count mismatch');
  if (errors.length) throw new Error(`Coverage catalog validation failed (${errors.length}):\n- ${errors.slice(0, 40).join('\n- ')}${errors.length > 40 ? `\n- ... ${errors.length - 40} more` : ''}`);
}

function buildArtifacts(options) {
  const docs = repositoryDocuments();
  const evidenceFiles = evidenceDocuments();
  const parsedEvidenceResult = parseEvidence(evidenceFiles);
  if (parsedEvidenceResult.parseErrors.length) {
    throw new Error(`Malformed runtime evidence JSONL: ${JSON.stringify(parsedEvidenceResult.parseErrors.slice(0, 10))}`);
  }
  const evidenceIndex = buildEvidenceIndex(parsedEvidenceResult.parsed);
  let dumpProvenance;
  let referenceDocs;
  let rows;
  let previousGeneratedAt = null;
  let dumpMtimeMs = null;

  if (options.refreshRuntime) {
    if (!fs.existsSync(OUTPUTS.catalogJson)) throw new Error('--refresh-runtime requires an existing checked-in campaign/crabsync_coverage_catalog.json.');
    const existing = JSON.parse(fs.readFileSync(OUTPUTS.catalogJson, 'utf8'));
    if (existing.schemaVersion !== SCHEMA_VERSION) throw new Error(`Cannot refresh catalog schema ${existing.schemaVersion}; run a full --dump generation.`);
    dumpProvenance = existing.sourceProvenance.objectDump;
    referenceDocs = [];
    const oldReferenceRecords = existing.sourceProvenance.legacyUnsafeReference?.files || [];
    rows = reapplyRuntime(existing.rows.filter((row) => !row.unsafePathId), parsedEvidenceResult.parsed, evidenceIndex);
    rows.push(...buildRuntimeRows(runtimeOnlyEntries(parsedEvidenceResult.parsed, rows, evidenceIndex), docs, referenceDocs));
    previousGeneratedAt = existing.generatedAt;
    options.preservedReferenceRecords = oldReferenceRecords;
  } else {
    if (!options.dump) throw new Error(`Full generation requires --dump.\n${usage()}`);
    if (!fs.existsSync(options.dump) || !fs.statSync(options.dump).isFile()) throw new Error(`Object dump does not exist or is not a file: ${options.dump}`);
    const dumpBuffer = fs.readFileSync(options.dump);
    const parsedDump = parseDump(dumpBuffer);
    if (parsedDump.lines.length === 0) throw new Error('Object dump contains zero lines.');
    dumpMtimeMs = fs.statSync(options.dump).mtimeMs;
    dumpProvenance = {
      logicalName: path.basename(options.dump),
      sha256: sha256(dumpBuffer),
      byteSize: dumpBuffer.length,
      lineCount: parsedDump.lines.length,
      scanMode: 'complete-line-scan',
      scannedLineCount: parsedDump.lines.length,
      parsedObjectRecordCount: parsedDump.entries.length
    };
    referenceDocs = referenceDocuments(options.references);
    rows = buildRowsFromDump(parsedDump, evidenceIndex, docs, referenceDocs);
    rows.push(...buildRuntimeRows(runtimeOnlyEntries(parsedEvidenceResult.parsed, rows, evidenceIndex), docs, referenceDocs));
  }

  rows.push(...buildUnsafeConcernRows(rows, docs));

  rows.sort((a, b) => a.category.localeCompare(b.category) || a.symbolPath.localeCompare(b.symbolPath) || a.type.localeCompare(b.type));
  const generatedAt = deterministicGeneratedAt([...docs, ...evidenceFiles, ...(referenceDocs || [])], dumpMtimeMs, previousGeneratedAt);
  const checklist = buildChecklist(rows, generatedAt);
  let hooks = hookDescriptors(rows);
  // Checklist links are attached before hook descriptors are created; keep descriptors in sync.
  const rowByPath = new Map(rows.map((row) => [row.symbolPath, row]));
  hooks = hooks.map((hook) => ({ ...hook, checklistLinks: rowByPath.get(hook.symbolPath)?.checklistLinkage || hook.checklistLinks }));

  const docRecords = docs.map(publicFileRecord);
  const evidenceRecords = evidenceFiles.map(publicFileRecord);
  const referenceRecords = options.refreshRuntime ? (options.preservedReferenceRecords || []) : (referenceDocs || []).map(publicFileRecord);
  const sourceProvenance = {
    objectDump: dumpProvenance,
    runtimeProbeDocumentation: { fileCount: docRecords.length, aggregateSha256: aggregateHash(docs), files: docRecords },
    runtimeEvidence: { fileCount: evidenceRecords.length, recordCount: parsedEvidenceResult.parsed.length, aggregateSha256: aggregateHash(evidenceFiles), files: evidenceRecords },
    legacyUnsafeReference: {
      classification: 'legacy_unsafe_reference',
      safetyRule: 'Concern provenance only; direct-write suggestions remain unsafe/unproven and cannot advance coverage.',
      fileCount: referenceRecords.length,
      aggregateSha256: referenceRecords.length ? sha256(referenceRecords.map((record) => `${record.logicalName}\0${record.sha256}`).join('\n')) : sha256(''),
      files: referenceRecords
    },
    refreshMode: options.refreshRuntime ? 'runtime-evidence-only; object dump not rescanned' : 'full object-dump scan'
  };
  const stablePayload = { schemaVersion: SCHEMA_VERSION, sourceProvenance, rows, hooks };
  const catalogHash = sha256(JSON.stringify(stablePayload));
  const catalog = {
    schemaVersion: SCHEMA_VERSION,
    generatedAt,
    catalogHash,
    sourceProvenance,
    summary: summaryFor(rows, hooks, dumpProvenance),
    policyDecisions: policyDecisions(rows),
    readinessVerdicts: readinessVerdicts(rows),
    views: {
      needsCoverage: rows.filter((row) => !TERMINAL_DISPOSITIONS.has(row.coverageDisposition)).map((row) => row.id),
      terminal: rows.filter((row) => TERMINAL_DISPOSITIONS.has(row.coverageDisposition)).map((row) => row.id),
      officialApplyCandidates: rows.filter((row) => row.officialAlternativeToRawWrite).map((row) => row.id),
      policyExcluded: rows.filter((row) => row.coverageDisposition === 'excluded-product-policy').map((row) => row.id),
      unsafeRejected: rows.filter((row) => row.coverageDisposition === 'rejected-unsafe').map((row) => row.id)
    },
    hooks,
    rows
  };
  checklist.catalogHash = catalogHash;
  const profile = buildProfile(hooks, catalogHash, generatedAt, rows);
  validateCatalog(catalog, checklist, profile);
  return {
    catalog,
    checklist,
    profile,
    files: {
      [OUTPUTS.catalogJson]: `${JSON.stringify(catalog, null, 2)}\n`,
      [OUTPUTS.catalogCsv]: renderCsv(rows),
      [OUTPUTS.profile]: `${JSON.stringify(profile, null, 2)}\n`,
      [OUTPUTS.checklist]: `${JSON.stringify(checklist, null, 2)}\n`,
      [OUTPUTS.documentation]: renderDocumentation(catalog),
      [OUTPUTS.schema]: `${JSON.stringify(catalogSchema(), null, 2)}\n`,
      [OUTPUTS.lua]: renderLua(catalog)
    }
  };
}

function validateExisting() {
  for (const output of Object.values(OUTPUTS)) if (!fs.existsSync(output)) throw new Error(`Missing generated artifact: ${path.relative(ROOT, output)}`);
  const catalog = JSON.parse(fs.readFileSync(OUTPUTS.catalogJson, 'utf8'));
  const checklist = JSON.parse(fs.readFileSync(OUTPUTS.checklist, 'utf8'));
  const profile = JSON.parse(fs.readFileSync(OUTPUTS.profile, 'utf8'));
  validateCatalog(catalog, checklist, profile);
  const expectedHash = sha256(JSON.stringify({ schemaVersion: catalog.schemaVersion, sourceProvenance: catalog.sourceProvenance, rows: catalog.rows, hooks: catalog.hooks }));
  if (catalog.catalogHash !== expectedHash) throw new Error(`Catalog hash mismatch: stored ${catalog.catalogHash}, recomputed ${expectedHash}`);
  return catalog;
}

function writeOrCheck(artifacts, check) {
  const mismatches = [];
  for (const [filePath, content] of Object.entries(artifacts.files)) {
    if (check) {
      const existing = fs.existsSync(filePath) ? fs.readFileSync(filePath, 'utf8') : null;
      if (existing !== content) mismatches.push(path.relative(ROOT, filePath).replace(/\\/g, '/'));
    } else {
      fs.mkdirSync(path.dirname(filePath), { recursive: true });
      fs.writeFileSync(filePath, content, 'utf8');
    }
  }
  if (mismatches.length) throw new Error(`Generated artifacts are stale:\n- ${mismatches.join('\n- ')}`);
}

function runSelfTests() {
  const passed = [];
  const test = (name, callback) => {
    callback();
    passed.push(name);
  };
  const envelope = (overrides = {}) => ({
    noWrites: true,
    noRpcs: true,
    noMutation: true,
    noHud: true,
    rawIdentityEvidence: false,
    passiveOnly: true,
    runtimeInitiated: false,
    selectedRole: 'host',
    observedRole: 'host',
    lifecycleState: 'stable',
    ...overrides
  });

  test('UE4SS enum values are parsed and retain the parent enum path', () => {
    const parsed = parseDump(Buffer.from([
      '[ABCDEF01] Enum /Script/CrabChampions.ECrabDifficultyModifier [n: 1] [c: 2] [or: 3]',
      '[0000000000000000] ECrabDifficultyModifier::LockedSlots [n: 4] [v: 7]',
      '[ABCDEF02] Class /Script/CrabChampions.CrabPS [n: 5] [c: 6] [or: 7] [sps: 0]'
    ].join('\n')));
    const value = parsed.entries.find((entry) => entry.dumpType === 'EnumValue');
    assert(value);
    assert.strictEqual(value.symbolPath, '/Script/CrabChampions.ECrabDifficultyModifier::LockedSlots');
    assert.strictEqual(value.enumPath, '/Script/CrabChampions.ECrabDifficultyModifier');
    assert.strictEqual(value.enumValue, 7);
    assert.strictEqual(typeForEntry(value), 'struct field');
  });

  test('staged inventory evidence maps only to the exact fields read by that stage', () => {
    const stageSix = envelope({
      event: 'Inventory.StageObservation', runtimeStatus: 'READ_OBSERVED', inventoryCategory: 'WeaponMods',
      inventoryStage: 6, inventoryStageName: 'metadata-scalars', stageStatus: 'confirmed'
    });
    const keys = new Set(inventoryStageEvidenceKeys(stageSix));
    assert(keys.has('CrabPS.WeaponMods'));
    assert(keys.has('CrabInventoryInfo.Level'));
    assert(keys.has('CrabInventoryInfo.AccumulatedBuff'));
    assert(!keys.has('CrabInventoryInfo.Enhancements'));
    assert(!keys.has('CrabWeaponMod.WeaponModDA'));
    assert(normalizeEvidenceRecord(stageSix).readObserved);
    const stageFourKeys = new Set(inventoryStageEvidenceKeys({ ...stageSix, inventoryStage: 4, inventoryStageName: 'item-da-identity' }));
    assert(stageFourKeys.has('CrabWeaponMod.WeaponModDA'));
    assert(!stageFourKeys.has('CrabInventoryInfo.Level'));
    const stageSevenKeys = new Set(inventoryStageEvidenceKeys({ ...stageSix, inventoryStage: 7, inventoryStageName: 'enhancement-shape' }));
    assert(stageSevenKeys.has('CrabInventoryInfo.Enhancements'));
    assert(!stageSevenKeys.has('CrabInventoryInfo.Level'));
  });

  test('stage 10 requires an explicit non-truncated result', () => {
    const base = envelope({
      event: 'Inventory.StageObservation', runtimeStatus: 'READ_OBSERVED', inventoryCategory: 'Perks',
      inventoryStage: 10, inventoryStageName: 'capped-local-iteration', stageStatus: 'confirmed'
    });
    assert.strictEqual(normalizeEvidenceRecord({ ...base, stageDetails: { truncated: true } }).readObserved, false);
    assert.strictEqual(normalizeEvidenceRecord({ ...base, stageDetails: { truncated: false } }).readObserved, true);
    assert.strictEqual(normalizeEvidenceRecord(base).readObserved, false);
  });

  test('only PassiveHook.Observed NATURALLY_OBSERVED rows prove a natural call', () => {
    const observed = envelope({ event: 'PassiveHook.Observed', runtimeStatus: 'NATURALLY_OBSERVED', arguments: [{ redacted: true }] });
    assert(normalizeEvidenceRecord(observed).naturalObserved);
    assert(normalizeEvidenceRecord(observed).argumentObserved);
    const registered = envelope({ event: 'PassiveHook.Registration', runtimeStatus: 'HOOK_REGISTERED' });
    assert.strictEqual(normalizeEvidenceRecord(registered).naturalObserved, false);
    assert(normalizeEvidenceRecord(registered).hookRegisteredOnly);
  });

  test('PASSIVE_CAMPAIGN is lifecycle evidence and never symbol read proof', () => {
    const normalized = normalizeEvidenceRecord(envelope({ event: 'FullObserve.Status', runtimeStatus: 'PASSIVE_CAMPAIGN' }));
    assert(normalized.passiveCampaign);
    assert.strictEqual(normalized.qualifyingRead, false);
    assert.strictEqual(normalized.naturalObserved, false);
  });

  test('dirty, crash-suspect, and selected/observed role mismatches cannot qualify', () => {
    const read = envelope({ runtimeStatus: 'READ_OBSERVED' });
    assert.strictEqual(normalizeEvidenceRecord({ ...read, dirtyEvidence: true }).qualifyingRead, false);
    assert(normalizeEvidenceRecord({ ...read, crashSuspected: true }).crashSuspected);
    const mismatch = normalizeEvidenceRecord({ ...read, selectedRole: 'host', observedRole: 'joined-client' });
    assert(mismatch.roleMismatch);
    assert.strictEqual(mismatch.qualifyingRead, false);
  });

  test('runtime-discovered exact UFunctions preserve path, owner, member, type, and arguments', () => {
    const record = envelope({
      event: 'RuntimeDiscovery.Function', runtimeStatus: 'DISCOVERED_NEEDS_COVERAGE',
      symbol: '/Game/Blueprint/Portal/BP_TestPortal.BP_TestPortal_C:ServerChoosePortal',
      hookPath: '/Game/Blueprint/Portal/BP_TestPortal.BP_TestPortal_C:ServerChoosePortal',
      accessKind: 'RPC', argumentSchema: [{ name: 'PortalIndex', propertyType: 'IntProperty' }]
    });
    const entries = runtimeOnlyEntries([record], [], buildEvidenceIndex([record]));
    assert.strictEqual(entries.length, 1);
    assert.strictEqual(entries[0].symbolPath, record.hookPath);
    assert.strictEqual(entries[0].owner, 'BP_TestPortal_C');
    assert.strictEqual(entries[0].member, 'ServerChoosePortal');
    assert.strictEqual(entries[0].rowType, 'RPC');
    assert.strictEqual(entries[0].argumentSchema[0].name, 'PortalIndex');
    const row = buildRuntimeRows(entries, [], [])[0];
    assert.strictEqual(row.type, 'RPC');
    assert.strictEqual(row.hookDisposition, 'runtime-discovered-exact-path-needs-object-dump-review');
  });

  test('every currently documented unsafe write/carrier concern maps to concrete dump refs', () => {
    const docs = repositoryDocuments();
    const concerns = documentedUnsafeConcerns(docs);
    assert(concerns.length > 0);
    assert(fs.existsSync(OUTPUTS.catalogJson), 'checked-in catalog is required for policy mapping self-test');
    const existing = JSON.parse(fs.readFileSync(OUTPUTS.catalogJson, 'utf8'));
    const rows = buildUnsafeConcernRows(existing.rows.filter((row) => row.sourceDetail?.objectDump && !row.unsafePathId), docs);
    assert.strictEqual(rows.length, concerns.length);
    for (const row of rows) {
      assert.strictEqual(row.coverageDisposition, 'rejected-unsafe');
      assert(row.sourceDetail.policyDocumentation.length > 0);
      assert(row.sourceDetail.objectDump, `${row.unsafePathId} lacks a material dump mapping`);
      assert(row.objectDumpRefs.length > 0, `${row.unsafePathId} lacks concrete dump refs`);
    }
  });

  test('generated profile exactly mirrors bounded runtime defaults', () => {
    const profile = buildProfile([], '0'.repeat(64), '2026-01-01T00:00:00.000Z', []);
    assert.deepStrictEqual(profile.runtimeContract, { ...RUNTIME_CONTRACT });
    assert.strictEqual(profile.mode, 'snapshot-observation');
    assert.strictEqual(profile.normalMode.snapshotSamplerEnabled, true);
    assert.strictEqual(profile.normalMode.gameplayHooksEnabled, false);
    assert.strictEqual(profile.normalMode.lifecycleHooksEnabled, false);
    assert.strictEqual(profile.normalMode.guiOwnsChecklistQualification, true);
    assert.strictEqual(profile.passiveHooks.enabled, false);
    assert.strictEqual(profile.passiveHooks.registerTogether, false);
    assert.strictEqual(profile.inventoryEscalation.enabled, false);
    assert.strictEqual(profile.runtimeDiscovery.enabled, false);
    assert.strictEqual(profile.inventoryEscalation.maximumInventoryEntriesPerCategory, 32);
    assert.strictEqual(profile.inventoryEscalation.maximumEnhancementValuesPerItem, 16);
    assert.strictEqual(profile.inventoryEscalation.maximumStageRowsPerCategory, 256);
    assert.strictEqual(profile.inventoryEscalation.cleanSamplesRequiredBeforeStageAdvance, 3);
    assert.strictEqual(profile.lifecycle.stableSamplesRequiredBeforeStagedRead, 10);
    assert.strictEqual(profile.lifecycle.stableDwellSecondsRequiredBeforeStagedRead, 30);
    assert.strictEqual(profile.runtimeDiscovery.maximumResolvedClassesPerGeneration, 128);
    assert.strictEqual(profile.runtimeDiscovery.maximumFunctionsPerResolvedClass, 128);
  });

  return { valid: true, mode: 'self-test', passed: passed.length, tests: passed };
}

function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    process.stdout.write(`${usage()}\n`);
    return;
  }
  if (options.selfTest) {
    process.stdout.write(`${JSON.stringify(runSelfTests())}\n`);
    return;
  }
  if (options.validate && !options.dump && !options.refreshRuntime) {
    const catalog = validateExisting();
    process.stdout.write(`${JSON.stringify({ valid: true, mode: 'existing-artifacts', rows: catalog.summary.relevantRowCount, hooks: catalog.summary.passiveHookCount, categories: catalog.summary.categoryCount, needsCoverage: catalog.summary.needsCoverageCount })}\n`);
    return;
  }
  const artifacts = buildArtifacts(options);
  if (!options.dryRun) writeOrCheck(artifacts, options.check);
  if (!options.check && !options.dryRun) validateExisting();
  process.stdout.write(`${JSON.stringify({ valid: true, mode: options.dryRun ? (options.refreshRuntime ? 'runtime-refresh-dry-run' : 'full-scan-dry-run') : (options.refreshRuntime ? 'runtime-refresh' : 'full-scan'), check: options.check, rows: artifacts.catalog.summary.relevantRowCount, hooks: artifacts.catalog.summary.passiveHookCount, categories: artifacts.catalog.summary.categoryCount, needsCoverage: artifacts.catalog.summary.needsCoverageCount, dumpLines: artifacts.catalog.summary.objectDumpLineCount, catalogHash: artifacts.catalog.catalogHash })}\n`);
}

try {
  main();
} catch (error) {
  process.stderr.write(`${error && error.stack ? error.stack : error}\n`);
  process.exitCode = 1;
}
