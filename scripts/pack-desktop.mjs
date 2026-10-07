#!/usr/bin/env node
/**
 * Copy an electron-builder --dir output into the matching npm desktop package.
 *
 * Usage:
 *   node scripts/pack-desktop.mjs --rid <rid> --builder-dir <dir>
 *
 * Expects electron-builder to have been run with
 *   --config.directories.output=<dir>
 * Exits 0 without doing anything when desktop/ does not exist (CI on an
 * incomplete checkout) or when no unpacked output was produced.
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ridToDesktopDir } from './rid-map.mjs';

function arg(name) {
  const i = process.argv.indexOf(`--${name}`);
  return i === -1 ? undefined : process.argv[i + 1];
}

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const rid = arg('rid');
const builderDir = arg('builder-dir');

if (!fs.existsSync(path.join(root, 'desktop', 'package.json'))) {
  console.log('desktop/ is not present — skipping desktop packaging.');
  process.exit(0);
}
if (!rid || !builderDir) {
  console.error('Usage: node scripts/pack-desktop.mjs --rid <rid> --builder-dir <dir>');
  process.exit(1);
}

const outRoot = path.resolve(root, builderDir);
const isMac = rid.startsWith('osx');

function findMacAppBundles() {
  const bundles = [];
  let tops = [];
  try {
    tops = fs.readdirSync(outRoot, { withFileTypes: true }).filter((e) => e.isDirectory());
  } catch {
    return bundles;
  }
  for (const top of tops) {
    if (!/^mac/i.test(top.name)) continue;
    const dir = path.join(outRoot, top.name);
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      if (e.isDirectory() && e.name.endsWith('.app')) bundles.push(path.join(dir, e.name));
    }
  }
  return bundles;
}

function findUnpackedDir(name) {
  const dir = path.join(outRoot, name);
  return fs.existsSync(dir) ? dir : null;
}

const desktopDir = ridToDesktopDir(rid);
const pkgDir = path.join(root, 'npm', desktopDir);
const appDir = path.join(pkgDir, 'app');

fs.rmSync(appDir, { recursive: true, force: true });
fs.mkdirSync(appDir, { recursive: true });

if (isMac) {
  const bundles = findMacAppBundles();
  if (bundles.length === 0) {
    console.error(`No .app bundle found under ${outRoot}/mac*.`);
    process.exit(1);
  }
  for (const bundle of bundles) {
    const dest = path.join(appDir, path.basename(bundle));
    // Keep symlinks as symlinks (Electron frameworks rely on them).
    fs.cpSync(bundle, dest, { recursive: true, verbatimSymlinks: true });
    console.log(`Copied ${path.basename(bundle)} → npm/${desktopDir}/app/`);
  }
} else {
  // electron-builder adds the arch for non-x64 builds: win-arm64-unpacked,
  // linux-arm64-unpacked.
  const os = rid.startsWith('win') ? 'win' : 'linux';
  const arch = rid.split('-')[1];
  const unpacked = findUnpackedDir(`${os}-unpacked`) ?? findUnpackedDir(`${os}-${arch}-unpacked`);
  if (!unpacked) {
    console.error(`No unpacked output found under ${outRoot} (expected ${os}-unpacked or ${os}-${arch}-unpacked).`);
    process.exit(1);
  }
  // The npm desktop package runs against the CLI-managed server recorded in
  // install.json, so the server bundled for the standalone installers
  // (resources/server) is left out — with it the tarball exceeds npm's size
  // limit (E413). macOS keeps it: Resources are sealed by the code signature.
  const bundledServer = path.join(unpacked, 'resources', 'server');
  const keep = (src) => src !== bundledServer && !src.startsWith(bundledServer + path.sep);
  for (const entry of fs.readdirSync(unpacked, { withFileTypes: true })) {
    fs.cpSync(path.join(unpacked, entry.name), path.join(appDir, entry.name), {
      recursive: true,
      verbatimSymlinks: true,
      filter: keep,
    });
  }
  console.log(`Copied ${path.basename(unpacked)}/ → npm/${desktopDir}/app/ (without resources/server)`);
}

const size = (dir) => {
  let total = 0;
  const walk = (d) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isSymbolicLink()) continue;
      if (e.isDirectory()) walk(p);
      else total += fs.statSync(p).size;
    }
  };
  walk(dir);
  return `${(total / 1024 / 1024).toFixed(1)} MB`;
};
console.log(`Packed npm/${desktopDir} (${size(appDir)}).`);
