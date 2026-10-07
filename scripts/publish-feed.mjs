#!/usr/bin/env node
/**
 * Publish the assembled feed directory (latest.json, latest-mac.yml, dmg/zip)
 * to the update feed host.
 *
 *   node scripts/publish-feed.mjs --dir out/feed --dest <target>
 *
 * Target forms:
 *   /path/to/dir            local directory (testing, or a mounted volume)
 *   user@host:/path         rsync over ssh
 *
 * Ordering matters: versioned artifacts first, then the latest.json /
 * latest-mac.yml pointers, so a client never sees a pointer to a missing file.
 * The script therefore uploads everything except the pointers, then the
 * pointers. (--delete is never used — other channels live in the same tree.)
 */
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

function arg(name) {
  const i = process.argv.indexOf(`--${name}`);
  if (i === -1 || i + 1 >= process.argv.length) return undefined;
  return process.argv[i + 1];
}

const dir = arg('dir');
const dest = arg('dest');
if (!dir || !dest) {
  console.error('Usage: publish-feed.mjs --dir <feed-dir> --dest <dir|user@host:/path>');
  process.exit(1);
}
if (!fs.existsSync(dir)) {
  console.error(`Feed directory not found: ${dir}`);
  process.exit(1);
}

const POINTERS = new Set(['latest.json', 'latest-mac.yml', 'latest-linux.yml', 'latest.yml']);

function listFiles(base) {
  const out = [];
  const walk = (d) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const full = path.join(d, e.name);
      if (e.isDirectory()) walk(full);
      else out.push(path.relative(base, full));
    }
  };
  walk(base);
  return out;
}

const all = listFiles(dir);
const pointers = all.filter((f) => POINTERS.has(path.basename(f)));
const artifacts = all.filter((f) => !POINTERS.has(path.basename(f)));

function run(cmd, args) {
  console.log(`+ ${cmd} ${args.join(' ')}`);
  const r = spawnSync(cmd, args, { stdio: 'inherit' });
  if (r.status !== 0) {
    console.error(`publish failed (${cmd} exited ${r.status})`);
    process.exit(1);
  }
}

if (/^[^/]+:/.test(dest) && !dest.startsWith('//')) {
  // Stage 1: versioned artifacts. Stage 2: the pointers, as a full tree, so a
  // pointer never lands before the files it references.
  const stage = fs.mkdtempSync('astra-feed-');
  for (const rel of artifacts) {
    const target = path.join(stage, rel);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(path.join(dir, rel), target);
  }
  run('rsync', ['-av', `${stage}/`, dest]);
  fs.rmSync(stage, { recursive: true, force: true });
  const pointerStage = fs.mkdtempSync('astra-feed-p-');
  for (const rel of pointers) {
    const target = path.join(pointerStage, rel);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(path.join(dir, rel), target);
  }
  run('rsync', ['-av', `${pointerStage}/`, dest]);
  fs.rmSync(pointerStage, { recursive: true, force: true });
} else {
  for (const rel of artifacts) {
    const target = path.join(dest, rel);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(path.join(dir, rel), target);
  }
  for (const rel of pointers) {
    const target = path.join(dest, rel);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(path.join(dir, rel), target);
  }
}

console.log(`Published ${all.length} files (${pointers.length} pointers last) to ${dest}`);
