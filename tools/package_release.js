#!/usr/bin/env node
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { spawnSync } = require('child_process');

const root = process.cwd();
const distDir = path.join(root, 'dist');
const releaseVersion = '1.1.0';
const defaultZip = path.join(distDir, `CrabRuntimeProbe-v${releaseVersion}-UE4SS.zip`);
const supportMods = ['BPML_GenericFunctions', 'BPModLoaderMod', 'Keybinds', 'shared'];
const rootRuntimeFiles = ['UE4SS.dll', 'dwmapi.dll', 'UE4SS-settings.ini', 'imgui.ini'];

function arg(name) {
  const i = process.argv.indexOf(name);
  return i >= 0 ? process.argv[i + 1] : null;
}

function hasFlag(name) {
  return process.argv.includes(name);
}

function fail(message) {
  console.error(message);
  process.exit(1);
}

function ensureInside(parent, child) {
  const rel = path.relative(path.resolve(parent), path.resolve(child));
  if (rel.startsWith('..') || path.isAbsolute(rel)) {
    fail(`Refusing to operate outside ${parent}: ${child}`);
  }
}

function runCommand(command, args) {
  const result = spawnSync(command, args, {
    stdio: 'inherit'
  });
  if (result.error) fail(result.error.message);
  if (result.status !== 0) fail(`${command} failed with status ${result.status}`);
}

function gitValue(args) {
  const result = spawnSync('git', ['-C', root, ...args], { encoding: 'utf8' });
  return result.status === 0 && result.stdout.trim() ? result.stdout.trim().split(/\r?\n/)[0] : 'unavailable';
}

function sha256(file) {
  return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

function copyFileIfExists(src, dest, required = true) {
  if (!fs.existsSync(src)) {
    if (required) fail(`Missing required template file: ${src}`);
    return false;
  }
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  fs.copyFileSync(src, dest);
  return true;
}

function copyDir(src, dest) {
  if (!fs.existsSync(src)) fail(`Missing required template directory: ${src}`);
  fs.cpSync(src, dest, {
    recursive: true,
    filter: (source) => {
      const parts = path.relative(src, source).split(path.sep);
      const base = path.basename(source).toLowerCase();
      return !parts.includes('.git')
        && !parts.includes('node_modules')
        && !parts.includes('results')
        && !/^hook_run_(?:consumed|manifest|classification)_.*\.json$/i.test(base)
        && !/\.(jsonl|log|dump|tmp)$/i.test(base);
    }
  });
}

function findTemplateRoot(inputPath) {
  const resolved = path.resolve(inputPath);
  if (fs.existsSync(path.join(resolved, 'client'))) return resolved;
  const children = fs.readdirSync(resolved, { withFileTypes: true }).filter((entry) => entry.isDirectory());
  for (const child of children) {
    const candidate = path.join(resolved, child.name);
    if (fs.existsSync(path.join(candidate, 'client'))) return candidate;
  }
  fail(`Could not find template root with a client/ directory under ${inputPath}`);
}

function extractTemplateIfNeeded(templatePath, workDir) {
  const resolved = path.resolve(templatePath);
  if (!fs.existsSync(resolved)) fail(`Template path does not exist: ${resolved}`);

  if (fs.statSync(resolved).isDirectory()) return findTemplateRoot(resolved);

  if (!/\.zip$/i.test(resolved)) fail('Template must be a directory or .zip file.');

  const extractDir = path.join(workDir, 'template');
  fs.mkdirSync(extractDir, { recursive: true });
  runCommand('tar', ['-xf', resolved, '-C', extractDir]);
  return findTemplateRoot(extractDir);
}

function writeModsTxt(modsDir) {
  const text = [
    'BPModLoaderMod : 1',
    'BPML_GenericFunctions : 1',
    'CrabRuntimeProbe : 1',
    '',
    '; Built-in keybinds, do not move up!',
    'Keybinds : 1',
    ''
  ].join('\n');
  fs.writeFileSync(path.join(modsDir, 'mods.txt'), text);
}

function writeInstallTxt(stagingDir) {
  const text = [
    'CrabRuntimeProbe UE4SS Bundle',
    '',
    'Extract ZIP contents into:',
    'Crab Champions\\CrabChampions\\Binaries\\Win64',
    '',
    'For a clean install, remove an older Mods\\CrabRuntimeProbe folder first.',
    'Do not reuse prior runtime configuration, campaign manifests, or trust data.',
    '',
    'Normal Play Guide is hook-free. Progressive Broad Observation ships with',
    'an empty trusted pool and no prearmed canary.',
    '',
    'Deep inventory, InventoryInfo, health, write, and RPC probes are disabled by default.',
    '',
    'The opt-in CrabSync Readiness Campaign is local-only: it does not enumerate',
    'remote PlayerState objects, traverse inventory, or provide transport/sync/apply',
    'behavior. See docs\\CRABRUNTIMEPROBE_V1.1.0_RELEASE_NOTES.md before field testing.',
    '',
    'UE4SS is redistributed under UE4SS-LICENSE.txt.',
    '',
    'This package does not include Crab Champions game binaries.',
    '',
    'Included UE4SS support mods from the CrabInvSync template:',
    '- BPML_GenericFunctions',
    '- BPModLoaderMod',
    '- Keybinds',
    '- shared',
    '',
    'These support mods are UE4SS support files, not CrabRuntimeProbe gameplay code.',
    ''
  ].join('\n');
  fs.writeFileSync(path.join(stagingDir, 'INSTALL.txt'), text);
}

function verifyNoForbiddenFiles(stagingDir) {
  const forbidden = [
    'Mods/CrabInventorySync',
    'server',
    'objectdump',
    '.git',
    'node_modules',
    'UE4SS_ObjectDump.txt'
  ];
  const violations = [];

  function walk(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      const rel = path.relative(stagingDir, full).replace(/\\/g, '/');
      if (forbidden.some((item) => rel === item || rel.startsWith(`${item}/`) || rel.includes(`/${item}/`))) {
        violations.push(rel);
        continue;
      }
      if (entry.isFile() && /(?:UE4SS[_-]?ObjectDump|ObjectDump).*\.txt$/i.test(entry.name)) {
        violations.push(rel);
        continue;
      }
      if (entry.isFile() && /^hook_run_(?:consumed|manifest|classification)_.*\.json$/i.test(entry.name)) {
        violations.push(rel);
        continue;
      }
      if (entry.isDirectory()) walk(full);
    }
  }

  walk(stagingDir);
  if (violations.length > 0) fail(`Forbidden files would be packaged:\n${violations.join('\n')}`);
}

function listFiles(dir) {
  const out = [];
  function walk(current) {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      const rel = path.relative(dir, full).replace(/\\/g, '/');
      if (entry.isDirectory()) walk(full);
      else out.push(rel);
    }
  }
  walk(dir);
  return out.sort();
}

function main() {
  const template = arg('--template') || arg('--template-zip') || arg('--template-dir');
  if (!template) {
    fail('Usage: node tools/package_release.js --template <CrabInvSync checkout dir or zip> [--out dist/CrabRuntimeProbe-ue4ss.zip] [--keep-staging]');
  }

  const outZip = path.resolve(arg('--out') || defaultZip);
  ensureInside(root, outZip);
  fs.mkdirSync(distDir, { recursive: true });
  fs.mkdirSync(path.dirname(outZip), { recursive: true });

  const workDir = path.join(distDir, 'package-work');
  const stagingDir = path.join(workDir, 'CrabRuntimeProbe-ue4ss');
  ensureInside(distDir, workDir);
  ensureInside(distDir, stagingDir);
  fs.rmSync(workDir, { recursive: true, force: true });
  fs.mkdirSync(stagingDir, { recursive: true });

  const templateRoot = extractTemplateIfNeeded(template, workDir);
  const templateClient = path.join(templateRoot, 'client');
  const sourceClient = path.join(root, 'client');
  const stagingMods = path.join(stagingDir, 'Mods');

  for (const file of rootRuntimeFiles) {
    const localSource = path.join(sourceClient, file);
    const templateSource = path.join(templateClient, file);
    copyFileIfExists(fs.existsSync(localSource) ? localSource : templateSource, path.join(stagingDir, file), file !== 'imgui.ini');
  }

  const localUe4ssLicense = path.join(root, 'UE4SS-LICENSE.txt');
  copyFileIfExists(fs.existsSync(localUe4ssLicense) ? localUe4ssLicense : path.join(templateRoot, 'UE4SS-LICENSE.txt'), path.join(stagingDir, 'UE4SS-LICENSE.txt'), true);
  copyFileIfExists(path.join(root, 'LICENSE'), path.join(stagingDir, 'CrabRuntimeProbe-LICENSE.txt'), true);
  copyFileIfExists(path.join(root, 'README.md'), path.join(stagingDir, 'CrabRuntimeProbe-README.md'), true);

  for (const modName of supportMods) {
    const localSupport = path.join(sourceClient, 'Mods', modName);
    copyDir(fs.existsSync(localSupport) ? localSupport : path.join(templateClient, 'Mods', modName), path.join(stagingMods, modName));
  }

  copyDir(path.join(root, 'client', 'Mods', 'CrabRuntimeProbe'), path.join(stagingMods, 'CrabRuntimeProbe'));
  copyDir(path.join(root, 'campaign'), path.join(stagingDir, 'campaign'));
  copyDir(path.join(root, 'schemas'), path.join(stagingDir, 'schemas'));
  for (const doc of [
    'CRABRUNTIMEPROBE_V1.1.0_RELEASE_NOTES.md',
    'CRABSYNC_FULL_CAMPAIGN_GUIDE.md',
    'CRABSYNC_COVERAGE_CATALOG.md',
    'INCIDENT_2026-07-10_HOOK_OBSERVER_CRASH.md',
    'CRABRUNTIMEPROBE_V1.0.4_RELEASE_NOTES.md'
  ]) {
    copyFileIfExists(path.join(root, 'docs', doc), path.join(stagingDir, 'docs', doc), true);
  }
  copyFileIfExists(path.join(root, 'CHANGELOG.md'), path.join(stagingDir, 'CHANGELOG.md'), true);
  copyFileIfExists(path.join(root, 'THIRD_PARTY_NOTICES.md'), path.join(stagingDir, 'THIRD_PARTY_NOTICES.md'), true);
  writeModsTxt(stagingMods);
  writeInstallTxt(stagingDir);

  const requiredCampaignArtifacts = [
    'hook_candidate_catalog.json',
    'hook_validation_ledger.json',
    'trusted_hook_manifest.json',
    'hook_quarantine.json',
    'progressive_observation.defaults.json'
  ];
  for (const artifactName of requiredCampaignArtifacts) {
    const artifactPath = path.join(stagingDir, 'campaign', artifactName);
    if (!fs.existsSync(artifactPath)) fail(`Missing required progressive campaign artifact: ${artifactName}`);
  }
  const requiredReadinessSchemas = [
    'readiness-campaign-manifest-v1.schema.json',
    'peer-snapshot-v1.schema.json',
    'terminal-lifecycle-v1.schema.json'
  ];
  for (const schemaName of requiredReadinessSchemas) {
    if (!fs.existsSync(path.join(stagingDir, 'schemas', schemaName))) {
      fail(`Missing required readiness schema: ${schemaName}`);
    }
  }
  const candidateCatalog = JSON.parse(fs.readFileSync(path.join(stagingDir, 'campaign', 'hook_candidate_catalog.json'), 'utf8'));
  const defaults = JSON.parse(fs.readFileSync(path.join(stagingDir, 'campaign', 'progressive_observation.defaults.json'), 'utf8'));
  const trusted = JSON.parse(fs.readFileSync(path.join(stagingDir, 'campaign', 'trusted_hook_manifest.json'), 'utf8'));
  if (trusted.candidates.length !== 0 || defaults.trustedPoolInitiallyEmpty !== true ||
      defaults.maximumCanariesPerProcess !== 1 || defaults.automaticInProcessAdvance !== false) {
    fail('Release defaults must be empty-trust, one-canary, and no in-process advance.');
  }
  const commit = gitValue(['rev-parse', 'HEAD']);
  const branch = gitValue(['branch', '--show-current']);
  const generatedAtUtc = new Date().toISOString();
  fs.writeFileSync(path.join(stagingMods, 'CrabRuntimeProbe', 'Scripts', 'build_info.txt'), [
    'action = release',
    `product_version = ${releaseVersion}`,
    `git_commit = ${commit}`,
    `git_branch = ${branch}`,
    `timestamp = ${generatedAtUtc}`,
    ''
  ].join('\n'));
  verifyNoForbiddenFiles(stagingDir);

  const manifestFiles = listFiles(stagingDir).map((relative) => {
    const file = path.join(stagingDir, ...relative.split('/'));
    return { path: relative, size: fs.statSync(file).size, sha256: sha256(file) };
  });
  const versionManifest = {
    schemaVersion: 1,
    product: 'CrabRuntimeProbe',
    version: releaseVersion,
    runtime: 'win-x64',
    bundleFormat: 'ue4ss-overlay',
    commit,
    generatedAtUtc,
    schemaIdentities: {
      liveStatus: 'live-status-v1', snapshotObservation: 'snapshot-observation-v1',
      campaignControl: 'campaign-control-v1', evidenceBundle: 'evidence-bundle-v1',
      coverageCatalog: 'coverage-catalog-v1', compatibilityFingerprint: 'compatibility-fingerprint-v1',
      readinessCampaignManifest: 'readiness-campaign-manifest-v1', peerSnapshot: 'peer-snapshot-v1',
      terminalLifecycle: 'terminal-lifecycle-v1',
      hookBreadcrumb: 'hook-breadcrumb-v1', hookCandidateCatalog: 'hook-candidate-catalog-v1',
      hookQuarantine: 'hook-quarantine-v1', hookRunClassification: 'hook-run-classification-v1',
      hookRunConsumed: 'hook-run-consumed-v1',
      hookRunManifest: 'hook-run-manifest-v1', hookValidationLedger: 'hook-validation-ledger-v1',
      trustedHookManifest: 'trusted-hook-manifest-v1'
    },
    campaignIdentities: {
      coverageCatalogHash: candidateCatalog.coverageCatalogHash,
      hookCatalogIdentity: candidateCatalog.hookCatalogIdentity,
      callbackImplementationVersion: candidateCatalog.callbackImplementationVersion,
      callbackSchemaVersion: candidateCatalog.callbackSchemaVersion,
      validationBehaviorVersion: candidateCatalog.validationBehaviorVersion,
      initialCanaryCandidateId: defaults.initialCanaryCandidateId,
      initialCanaryDepth: defaults.initialCanaryDepth
    },
    releaseSafety: {
      normalPlayGuideHookFree: true, trustedManifestCandidateCount: 0, canaryPrearmed: false,
      maximumCanariesPerProcess: 1, automaticInProcessAdvance: false
    },
    installTarget: 'Crab Champions/CrabChampions/Binaries/Win64',
    files: manifestFiles
  };
  fs.writeFileSync(path.join(stagingDir, 'version-manifest.json'), JSON.stringify(versionManifest, null, 2) + '\n');
  runCommand('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
    path.join(root, 'scripts', 'verify-ue4ss-bundle.ps1'), stagingDir]);

  fs.rmSync(outZip, { force: true });
  runCommand('tar', ['-a', '-cf', outZip, '-C', stagingDir, '.']);

  const manifest = {
    generatedAt: new Date().toISOString(),
    productVersion: releaseVersion,
    templateSource: 'external-ue4ss-support-template',
    outputFile: path.basename(outZip),
    installTarget: 'Crab Champions/CrabChampions/Binaries/Win64',
    files: listFiles(stagingDir)
  };
  fs.writeFileSync(path.join(distDir, 'CrabRuntimeProbe-ue4ss-manifest.json'), JSON.stringify(manifest, null, 2));

  if (!hasFlag('--keep-staging')) {
    fs.rmSync(workDir, { recursive: true, force: true });
  }

  console.log(`Wrote ${outZip}`);
  console.log('Extract ZIP contents into Crab Champions/CrabChampions/Binaries/Win64');
}

main();
