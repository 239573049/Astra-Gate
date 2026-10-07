#!/usr/bin/env node
/**
 * Keep one version across cli/package.json, every npm/* platform package,
 * desktop/package.json (the Electron app version), the cli's
 * optionalDependencies pins and the .NET Directory.Build.props.
 *
 * Usage:
 *   node scripts/sync-versions.mjs [--version <semver>] [--check]
 *
 * Without --version the version in cli/package.json is the source of truth.
 * --check validates and exits 1 on mismatch without writing anything.
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const checkOnly = process.argv.includes('--check');
const versionArgIndex = process.argv.indexOf('--version');
const explicitVersion = versionArgIndex !== -1 ? process.argv[versionArgIndex + 1] : undefined;

function readJson(p) {
  return JSON.parse(fs.readFileSync(p, 'utf8'));
}

function writeJson(p, data) {
  fs.writeFileSync(p, `${JSON.stringify(data, null, 2)}\n`);
}

const cliPkgPath = path.join(root, 'cli', 'package.json');
const cliPkg = readJson(cliPkgPath);
const version = explicitVersion ?? cliPkg.version;
if (!version || !/^\d+\.\d+\.\d+/.test(version)) {
  console.error(`No valid version found (got: ${version}).`);
  process.exit(1);
}

const npmDir = path.join(root, 'npm');
const targets = fs
  .readdirSync(npmDir)
  .filter((d) => fs.existsSync(path.join(npmDir, d, 'package.json')))
  .map((d) => `npm/${d}`)
  .sort();
// desktop/package.json is what electron-builder stamps into the app
// (app.getVersion(), installer names, electron-updater's version compare) —
// left behind, every installer and desktop npm package reports a stale version.
if (fs.existsSync(path.join(root, 'desktop', 'package.json'))) targets.push('desktop');

let mismatch = false;
let cliDirty = false;

for (const dir of targets) {
  const pkgPath = path.join(root, dir, 'package.json');
  const pkg = readJson(pkgPath);
  if (pkg.version !== version) {
    if (checkOnly) {
      console.error(`version mismatch: ${dir} has ${pkg.version}, expected ${version}`);
      mismatch = true;
      continue;
    }
    const old = pkg.version;
    pkg.version = version;
    writeJson(pkgPath, pkg);
    console.log(`${dir}: ${old} -> ${version}`);
  }
}

for (const [name, ver] of Object.entries(cliPkg.optionalDependencies ?? {})) {
  if (ver !== version) {
    if (checkOnly) {
      console.error(
        `version mismatch: cli optionalDependencies "${name}" has ${ver}, expected ${version}`,
      );
      mismatch = true;
      continue;
    }
    cliPkg.optionalDependencies[name] = version;
    cliDirty = true;
    console.log(`cli optionalDependencies ${name}: ${ver} -> ${version}`);
  }
}

if (!checkOnly && (cliDirty || explicitVersion)) {
  const old = cliPkg.version;
  cliPkg.version = version;
  writeJson(cliPkgPath, cliPkg);
  if (old !== version) console.log(`cli: ${old} -> ${version}`);
}

// Directory.Build.props: single version source for all six .NET projects.
const buildPropsPath = path.join(root, 'Directory.Build.props');
const buildProps = fs.readFileSync(buildPropsPath, 'utf8');
const versionTag = /<Version>([^<]+)<\/Version>/;
const propsMatch = versionTag.exec(buildProps);
if (!propsMatch) {
  console.error('Directory.Build.props has no <Version> element.');
  process.exit(1);
}
if (propsMatch[1].trim() !== version) {
  if (checkOnly) {
    console.error(
      `version mismatch: Directory.Build.props has ${propsMatch[1].trim()}, expected ${version}`,
    );
    mismatch = true;
  } else {
    fs.writeFileSync(
      buildPropsPath,
      buildProps.replace(versionTag, `<Version>${version}</Version>`),
    );
    console.log(`Directory.Build.props: ${propsMatch[1].trim()} -> ${version}`);
  }
}

if (mismatch) process.exit(1);
console.log(`All package versions are in sync at ${version}.`);
