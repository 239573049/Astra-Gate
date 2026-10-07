#!/usr/bin/env node
/**
 * Copy a published self-contained server binary + the web dist into the
 * matching npm platform package.
 *
 * Usage:
 *   node scripts/pack-platform.mjs --rid <rid> --publish-dir <dir> [--web-dist <dir>]
 *
 * rid:         osx-arm64 | osx-x64 | linux-x64 | linux-arm64 | win-x64 | win-arm64
 * publish-dir: output of `dotnet publish ... -o <dir>` (contains astra-server[.exe])
 * web-dist:    Vite build output to place in bin/wwwroot (optional)
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ridToServerDir } from './rid-map.mjs';

function arg(name) {
  const i = process.argv.indexOf(`--${name}`);
  return i === -1 ? undefined : process.argv[i + 1];
}

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const rid = arg('rid');
const publishDir = arg('publish-dir');
const webDist = arg('web-dist');

if (!rid || !publishDir) {
  console.error('Usage: node scripts/pack-platform.mjs --rid <rid> --publish-dir <dir> [--web-dist <dir>]');
  process.exit(1);
}

let serverDir;
try {
  serverDir = ridToServerDir(rid);
} catch (err) {
  console.error(String(err.message ?? err));
  process.exit(1);
}

const isWindows = rid.startsWith('win');
const binaryName = isWindows ? 'astra-server.exe' : 'astra-server';
const pkgDir = path.join(root, 'npm', serverDir);
const binDir = path.join(pkgDir, 'bin');
const srcBinary = path.join(path.resolve(root, publishDir), binaryName);

if (!fs.existsSync(srcBinary)) {
  console.error(`Published binary not found: ${srcBinary}`);
  console.error('Run `dotnet publish src/Astra.Server -c Release -r <rid> --self-contained -p:PublishSingleFile=true -o <publish-dir>` first.');
  process.exit(1);
}

fs.rmSync(binDir, { recursive: true, force: true });
fs.mkdirSync(binDir, { recursive: true });
fs.copyFileSync(srcBinary, path.join(binDir, binaryName));
if (!isWindows) fs.chmodSync(path.join(binDir, binaryName), 0o755);

if (webDist) {
  const src = path.resolve(root, webDist);
  if (fs.existsSync(src)) {
    fs.cpSync(src, path.join(binDir, 'wwwroot'), { recursive: true });
  } else {
    console.warn(`warning: web dist not found at ${src}; packing without wwwroot`);
  }
}

function describeSize(p) {
  const st = fs.statSync(p);
  return st.isDirectory() ? 'dir' : `${(st.size / 1024 / 1024).toFixed(1)} MB`;
}

console.log(`Packed npm/${serverDir}:`);
console.log(`  bin/${binaryName} (${describeSize(path.join(binDir, binaryName))})`);
console.log(
  fs.existsSync(path.join(binDir, 'wwwroot')) ? '  bin/wwwroot/' : '  (no wwwroot — the UI will not be served)',
);
