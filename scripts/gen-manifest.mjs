#!/usr/bin/env node
/**
 * Build the update feed manifest (latest.json) shared by the Astra server,
 * CLI and desktop app.
 *
 * Two modes, matching the release matrix:
 *
 *   # One matrix job: hash this RID's server binary into a partial entry.
 *   gen-manifest.mjs partial --rid osx-arm64 --binary out/publish/astra-server \
 *     --out out/feed/partial-osx-arm64.json
 *
 *   # Publish job: merge the partials into the feed manifest.
 *   gen-manifest.mjs merge --version 0.2.0 --api-version 1.0 \
 *     --partial out/feed/partial-osx-arm64.json [--partial ...] \
 *     [--notes-file notes.md] --out out/feed/stable/latest.json
 */
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { ridToServerDir } from './rid-map.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

function arg(name, fallback) {
  const i = process.argv.indexOf(`--${name}`);
  if (i === -1) return fallback;
  if (i + 1 >= process.argv.length || process.argv[i + 1].startsWith('--')) return true;
  return process.argv[i + 1];
}

function readJson(p) {
  return JSON.parse(fs.readFileSync(p, 'utf8'));
}

const mode = process.argv[2];

if (mode === 'partial') {
  const rid = arg('rid');
  const binary = arg('binary');
  const out = arg('out');
  if (!rid || !binary || !out || typeof binary !== 'string') {
    console.error('Usage: gen-manifest.mjs partial --rid <rid> --binary <path> --out <file>');
    process.exit(1);
  }
  if (!fs.existsSync(binary)) {
    console.error(`Server binary not found: ${binary}`);
    process.exit(1);
  }
  const bytes = fs.readFileSync(binary);
  const partial = {
    rid,
    server: `@aidotnet/${ridToServerDir(rid).replace(/^server-/, 'server-')}`,
    serverSha256: createHash('sha256').update(bytes).digest('hex'),
    serverSize: bytes.length,
  };
  fs.mkdirSync(path.dirname(path.resolve(out)), { recursive: true });
  fs.writeFileSync(out, `${JSON.stringify(partial, null, 2)}\n`);
  console.log(`partial ${rid}: sha256 ${partial.serverSha256} (${(partial.serverSize / 1024 / 1024).toFixed(1)} MB) -> ${out}`);
} else if (mode === 'merge') {
  const version = arg('version');
  const apiVersion = arg('api-version', '1.0');
  const out = arg('out');
  if (!version || !out) {
    console.error('Usage: gen-manifest.mjs merge --version <v> [--api-version 1.0] --partial <file>... --out <file>');
    process.exit(1);
  }
  const partialArgs = process.argv.flatMap((a, i) => (a === '--partial' ? [process.argv[i + 1]] : []));
  if (partialArgs.length === 0) {
    console.error('merge needs at least one --partial <file>');
    process.exit(1);
  }
  const platforms = {};
  for (const p of partialArgs) {
    const entry = readJson(p);
    platforms[entry.rid] = { server: entry.server, serverSha256: entry.serverSha256, serverSize: entry.serverSize };
  }
  let notes = null;
  const notesFile = arg('notes-file');
  if (typeof notesFile === 'string' && fs.existsSync(notesFile)) {
    notes = fs.readFileSync(notesFile, 'utf8').trim();
  }
  const manifest = {
    version,
    apiVersion,
    releasedAt: new Date().toISOString(),
    ...(notes ? { notes } : {}),
    platforms,
  };
  fs.mkdirSync(path.dirname(path.resolve(out)), { recursive: true });
  fs.writeFileSync(out, `${JSON.stringify(manifest, null, 2)}\n`);
  console.log(`manifest ${version}: ${Object.keys(platforms).length} platforms -> ${out}`);
} else {
  console.error('Usage: gen-manifest.mjs <partial|merge> …');
  process.exit(1);
}
