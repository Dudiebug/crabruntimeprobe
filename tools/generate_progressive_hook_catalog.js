#!/usr/bin/env node
'use strict';

// Derives the v1.0.4 progressive-research catalog from the authoritative
// generated coverage catalog. This script never discovers runtime functions and
// never treats legacy registration observations as trust.

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const SOURCE = path.join(ROOT, 'campaign', 'crabsync_coverage_catalog.json');
const OUTPUTS = {
  catalog: path.join(ROOT, 'campaign', 'hook_candidate_catalog.json'),
  ledger: path.join(ROOT, 'campaign', 'hook_validation_ledger.json'),
  trusted: path.join(ROOT, 'campaign', 'trusted_hook_manifest.json'),
  quarantine: path.join(ROOT, 'campaign', 'hook_quarantine.json'),
  defaults: path.join(ROOT, 'campaign', 'progressive_observation.defaults.json'),
  lua: path.join(ROOT, 'client', 'Mods', 'CrabRuntimeProbe', 'Scripts', 'research_hook_catalog.lua')
};

const CALLBACK_IMPLEMENTATION_VERSION = 'progressive-hook-callback-v1';
const CALLBACK_SCHEMA_VERSION = 'hook-passive-evidence-v1';
const VALIDATION_BEHAVIOR_VERSION = 'validation-depths-v1';
const SCOPE_PROPERTIES = ['OwningPS', 'PlayerState'];
const DEPTH_NAMES = [
  'static-catalog-validation',
  'registration-only',
  'callback-entry',
  'context-resolution',
  'playerstate-scope',
  'reviewed-state-reads',
  'documented-arguments',
  'full-passive-evidence'
];

const exactPriority = new Map([
  ['hook-crabps-onrep-islandrewardrarity', 0],
  ['hook-crabpc-clientonpickeduppickup', 10],
  ['hook-crabps-onrep-inventory', 20],
  ['hook-crabps-onrep-crystals', 30],
  ['hook-crabps-onrep-weaponda', 40],
  ['hook-crabps-onrep-abilityda', 50],
  ['hook-crabps-onrep-meleeda', 60],
  ['hook-crabps-serverincrementnuminventoryslots', 70]
]);

function sha256(value) {
  return crypto.createHash('sha256').update(value).digest('hex');
}

function stable(value) {
  if (Array.isArray(value)) return value.map(stable);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, stable(value[key])]));
  }
  return value;
}

function member(pathValue) {
  return String(pathValue || '').match(/:([A-Za-z0-9_]+)$/)?.[1] || 'Unknown hook';
}

function suggestedAction(hook) {
  const name = member(hook.hookPath);
  const exact = {
    OnRep_IslandRewardRarity: 'Complete an island and allow the next reward rarity to update naturally.',
    ClientOnPickedUpPickup: 'Pick up any item naturally.',
    OnRep_Inventory: 'Pick up, drop, salvage, or remove an inventory item naturally.',
    OnRep_Crystals: 'Gain or spend crystals naturally.',
    OnRep_WeaponDA: 'Change or receive a weapon naturally.',
    OnRep_AbilityDA: 'Change or receive an ability naturally.',
    OnRep_MeleeDA: 'Change or receive a melee weapon naturally.',
    ServerIncrementNumInventorySlots: 'Acquire an inventory-slot increase naturally; RuntimeProbe only observes the call.'
  };
  if (exact[name]) return exact[name];
  if (/PickedUp|Pickup/i.test(name)) return 'Pick up an item naturally.';
  if (/Chest/i.test(name)) return 'Open a chest naturally.';
  if (/Totem|Upgrade/i.test(name)) return 'Use a totem or upgrade naturally.';
  if (/Portal|Island|StreamLevel/i.test(name)) return 'Complete an island or use a portal naturally.';
  if (/Damage|Health|Armor|Eliminated|Death|Revive|Respawn/i.test(name)) return 'Take damage, recover, die, or respawn naturally as appropriate.';
  if (/Crystal|Rarity|Reward/i.test(name)) return 'Gain a reward or change crystals naturally.';
  if (/Drop|Remove|Salvage|Inventory|Equip/i.test(name)) return 'Change inventory or equipment naturally.';
  if (/Anvil|Enhancement/i.test(name)) return 'Use an anvil or enhancement naturally.';
  if (/Shop|Purchase|Interact/i.test(name)) return 'Interact with the related shop or world object naturally.';
  return 'Play normally through gameplay related to this candidate; a natural callback may not occur.';
}

function reviewedStateFields(hook) {
  const fields = [...(hook.preStateFields || []), ...(hook.postStateFields || [])];
  return [...new Set(fields.filter((value) => {
    const text = String(value || '');
    if (!/^CrabPS\.[A-Za-z0-9_]+$/.test(text)) return false;
    return !/(Mods|Perks|Relics|Inventory|InventoryInfo|Enhancements)$/i.test(text);
  }))].sort();
}

function priority(hook, index) {
  if (exactPriority.has(hook.id)) return exactPriority.get(hook.id);
  const name = member(hook.hookPath);
  const categoryWeight = {
    'pickups-transactions': 100,
    'inventory-arrays': 200,
    'crystals-economy': 300,
    'equipment-starting': 400,
    'inventory-slots': 500,
    'inventory-removal-drop-salvage': 600,
    'inventory-enhancements': 700,
    'health-armor': 800,
    'shops-chests-totems': 900,
    'portal-island-lifecycle': 1000,
    'replication-rpc-events': 1100
  };
  const blueprintPenalty = String(hook.hookPath || '').startsWith('/Game/') ? 5000 : 0;
  const uncommonPenalty = /Blueprint|K2_|Event/i.test(name) ? 1000 : 0;
  return (categoryWeight[hook.category] || 2000) + blueprintPenalty + uncommonPenalty + index;
}

function normalizeArgument(argument) {
  return {
    name: String(argument.name || ''),
    propertyType: String(argument.propertyType || 'unknown'),
    valueTypePath: argument.valueTypePath == null ? '' : String(argument.valueTypePath),
    safeSummary: String(argument.safeSummary || 'redacted'),
    redaction: String(argument.redaction || 'bounded-redacted')
  };
}

function build() {
  const coverage = JSON.parse(fs.readFileSync(SOURCE, 'utf8'));
  if (coverage.schemaVersion !== 'coverage-catalog-v1' || !Array.isArray(coverage.hooks)) {
    throw new Error('Authoritative coverage catalog is missing or incompatible.');
  }
  if (coverage.hooks.length !== coverage.summary.passiveHookCount) {
    throw new Error('Coverage hook count does not match its summary.');
  }

  const candidates = coverage.hooks.map((hook, index) => {
    const hookPath = String(hook.hookPath || '');
    if (!/^\/(?:Script\/(?:CrabChampions|Engine)\.|Game\/).+:[A-Za-z0-9_]+$/.test(hookPath)) {
      throw new Error(`Unsafe or invalid exact hook path for ${hook.id}: ${hookPath}`);
    }
    return {
      id: String(hook.id || ''),
      displayName: member(hookPath),
      category: String(hook.category || 'unknown'),
      hookPath,
      hookPathFingerprint: sha256(hookPath),
      ownerPath: String(hook.ownerPath || hookPath.split(':')[0]),
      ownerKind: hookPath.startsWith('/Game/') ? 'blueprint' : 'native',
      candidateType: String(hook.type || 'event'),
      priority: priority(hook, index),
      suggestedAction: suggestedAction(hook),
      roleApplicability: 'host-and-joined-client-when-relevant',
      allowedDepths: DEPTH_NAMES.map((name, depth) => ({ depth, name })),
      maximumValidationDepth: 7,
      callbackPhase: hookPath.startsWith('/Game/') ? 'blueprint-post-only' : 'post',
      scopeProperties: [...SCOPE_PROPERTIES],
      reviewedStateFields: reviewedStateFields(hook),
      argumentSchema: (hook.argumentSchema || []).map(normalizeArgument),
      checklistLinks: [...new Set(hook.checklistLinks || [])].sort(),
      knownCrashContext: hook.knownCrashContext === true || member(hookPath) === 'OnRep_IslandRewardRarity',
      staticCatalogValidated: true,
      naturalObservationOnly: true,
      neverInvoke: true,
      noMutation: true,
      staleUObjectRetention: false,
      explicitExclusions: ['array-traversal', 'inventory-elements', 'InventoryInfo', 'Enhancements', 'arbitrary-uobject-exploration']
    };
  }).sort((a, b) => a.priority - b.priority || a.id.localeCompare(b.id));

  const identityPayload = candidates.map((candidate) => ({
    id: candidate.id,
    hookPath: candidate.hookPath,
    hookPathFingerprint: candidate.hookPathFingerprint,
    argumentSchema: candidate.argumentSchema,
    reviewedStateFields: candidate.reviewedStateFields,
    maximumValidationDepth: candidate.maximumValidationDepth
  }));
  const catalogIdentity = sha256(JSON.stringify(stable(identityPayload)));
  const common = {
    generatedAtUtc: coverage.generatedAt,
    coverageCatalogHash: coverage.catalogHash,
    hookCatalogIdentity: catalogIdentity,
    callbackImplementationVersion: CALLBACK_IMPLEMENTATION_VERSION,
    callbackSchemaVersion: CALLBACK_SCHEMA_VERSION,
    validationBehaviorVersion: VALIDATION_BEHAVIOR_VERSION
  };
  const catalog = {
    schemaVersion: 'hook-candidate-catalog-v1',
    ...common,
    principalCandidateId: 'hook-crabps-onrep-islandrewardrarity',
    candidateCount: candidates.length,
    candidates
  };
  const ledger = {
    schemaVersion: 'hook-validation-ledger-v1',
    ...common,
    updatedAtUtc: coverage.generatedAt,
    initialMigrationPolicy: 'Legacy v1.0.2 registration/callback observations are retained as history only and confer no compatibility-aware trust.',
    candidates: candidates.map((candidate) => ({
      candidateId: candidate.id,
      hookPathFingerprint: candidate.hookPathFingerprint,
      state: 'untested',
      highestValidatedDepth: 0,
      trustedDepth: null,
      cleanRuns: 0,
      naturalCallbacks: 0,
      hostCleanRuns: 0,
      joinedClientCleanRuns: 0,
      lifecycleTransitionRuns: 0,
      evidenceSessions: [],
      legacyObservationMigrated: true,
      legacyObservationTrusted: false,
      crashSuspectRuns: [],
      compatibilityFingerprint: '',
      hasUnmatchedBreadcrumb: false,
      hasCorrelatedCrash: false,
      hasNewUe4ssCallbackError: false,
      reducerFixtureCovered: false
    }))
  };
  const trusted = {
    schemaVersion: 'trusted-hook-manifest-v1',
    ...common,
    compatibilityFingerprint: '',
    generatedFromLedgerAtUtc: coverage.generatedAt,
    candidates: []
  };
  const quarantine = {
    schemaVersion: 'hook-quarantine-v1',
    ...common,
    updatedAtUtc: coverage.generatedAt,
    entries: []
  };
  const defaults = {
    schemaVersion: 'progressive-observation-defaults-v1',
    ...common,
    defaultRunType: 'combined',
    initialCanaryCandidateId: 'hook-crabps-onrep-islandrewardrarity',
    initialCanaryDepth: 1,
    trustedPoolInitiallyEmpty: true,
    automaticInProcessAdvance: false,
    normalPlayGuideHookFree: true,
    maximumCanariesPerProcess: 1,
    registrationOrder: ['safe-snapshot-baseline', 'trusted-native', 'trusted-blueprint-when-loaded', 'canary-last']
  };
  return { catalog, ledger, trusted, quarantine, defaults };
}

function luaString(value) {
  return JSON.stringify(String(value));
}

function luaSerialize(value, indent = 0) {
  if (value === null || value === undefined) return 'nil';
  if (typeof value === 'string') return luaString(value);
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  const pad = '  '.repeat(indent);
  const childPad = '  '.repeat(indent + 1);
  if (Array.isArray(value)) {
    if (!value.length) return '{}';
    return `{\n${value.map((item) => `${childPad}${luaSerialize(item, indent + 1)},`).join('\n')}\n${pad}}`;
  }
  const entries = Object.entries(value);
  if (!entries.length) return '{}';
  return `{\n${entries.map(([key, item]) => {
    const luaKey = /^[A-Za-z_][A-Za-z0-9_]*$/.test(key) ? key : `[${luaString(key)}]`;
    return `${childPad}${luaKey} = ${luaSerialize(item, indent + 1)},`;
  }).join('\n')}\n${pad}}`;
}

function render(artifacts) {
  const json = (value) => `${JSON.stringify(value, null, 2)}\n`;
  const luaPayload = {
    schemaVersion: artifacts.catalog.schemaVersion,
    coverageCatalogHash: artifacts.catalog.coverageCatalogHash,
    hookCatalogIdentity: artifacts.catalog.hookCatalogIdentity,
    callbackImplementationVersion: artifacts.catalog.callbackImplementationVersion,
    callbackSchemaVersion: artifacts.catalog.callbackSchemaVersion,
    validationBehaviorVersion: artifacts.catalog.validationBehaviorVersion,
    principalCandidateId: artifacts.catalog.principalCandidateId,
    candidates: artifacts.catalog.candidates.map((candidate) => ({
      id: candidate.id,
      displayName: candidate.displayName,
      category: candidate.category,
      hookPath: candidate.hookPath,
      hookPathFingerprint: candidate.hookPathFingerprint,
      ownerKind: candidate.ownerKind,
      candidateType: candidate.candidateType,
      priority: candidate.priority,
      suggestedAction: candidate.suggestedAction,
      callbackPhase: candidate.callbackPhase,
      maximumValidationDepth: candidate.maximumValidationDepth,
      scopeProperties: candidate.scopeProperties,
      reviewedStateFields: candidate.reviewedStateFields,
      argumentSchema: candidate.argumentSchema,
      knownCrashContext: candidate.knownCrashContext
    }))
  };
  return new Map([
    [OUTPUTS.catalog, json(artifacts.catalog)],
    [OUTPUTS.ledger, json(artifacts.ledger)],
    [OUTPUTS.trusted, json(artifacts.trusted)],
    [OUTPUTS.quarantine, json(artifacts.quarantine)],
    [OUTPUTS.defaults, json(artifacts.defaults)],
    [OUTPUTS.lua, `-- Generated by tools/generate_progressive_hook_catalog.js. DO NOT EDIT.\n-- Reviewed exact paths only. This module never registers or invokes a function.\nreturn ${luaSerialize(luaPayload)}\n`]
  ]);
}

function validate(artifacts) {
  const ids = new Set();
  const paths = new Set();
  for (const candidate of artifacts.catalog.candidates) {
    if (!candidate.id || ids.has(candidate.id)) throw new Error(`Duplicate or empty candidate ID: ${candidate.id}`);
    if (paths.has(candidate.hookPath)) throw new Error(`Duplicate exact hook path: ${candidate.hookPath}`);
    if (sha256(candidate.hookPath) !== candidate.hookPathFingerprint) throw new Error(`Bad path fingerprint: ${candidate.id}`);
    if (candidate.maximumValidationDepth !== 7 || candidate.allowedDepths.length !== 8) throw new Error(`Bad depth ladder: ${candidate.id}`);
    ids.add(candidate.id);
    paths.add(candidate.hookPath);
  }
  if (artifacts.catalog.candidateCount !== 111 || artifacts.catalog.candidateCount !== ids.size) {
    throw new Error(`Expected the authoritative 111 candidate identities; found ${ids.size}.`);
  }
  if (!ids.has(artifacts.catalog.principalCandidateId)) throw new Error('Principal candidate is missing.');
  if (artifacts.trusted.candidates.length !== 0) throw new Error('Release defaults must not pretrust candidates.');
  if (artifacts.defaults.maximumCanariesPerProcess !== 1 || artifacts.defaults.initialCanaryDepth !== 1) {
    throw new Error('Unsafe progressive defaults.');
  }
}

function main() {
  const check = process.argv.includes('--check') || process.argv.includes('--validate');
  const artifacts = build();
  validate(artifacts);
  const outputs = render(artifacts);
  const stale = [];
  for (const [file, content] of outputs) {
    if (check) {
      if (!fs.existsSync(file) || fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n') !== content.replace(/\r\n/g, '\n')) {
        stale.push(path.relative(ROOT, file));
      }
    } else {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, content, 'utf8');
    }
  }
  if (stale.length) throw new Error(`Progressive hook artifacts are stale or missing:\n- ${stale.join('\n- ')}`);
  process.stdout.write(`${JSON.stringify({ valid: true, check, candidates: artifacts.catalog.candidateCount, hookCatalogIdentity: artifacts.catalog.hookCatalogIdentity, trusted: 0 })}\n`);
}

try {
  main();
} catch (error) {
  process.stderr.write(`${error && error.stack ? error.stack : error}\n`);
  process.exitCode = 1;
}
