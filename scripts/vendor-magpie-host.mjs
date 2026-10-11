#!/usr/bin/env node
/**
 * Re-vendor magpie's plugin host (internal/plugin/host.js and pi.js, MIT) into
 * src/Astra.Plugins/Host at a magpie commit (default: main's newest), and
 * rewrite VENDORED.md with the commit and the files' SHA-256s, which
 * HostAssetsTests checks. Uses the gh CLI. Review the diff before committing:
 * host.js runs every provider plugin with the user's privileges.
 *
 *   node scripts/vendor-magpie-host.mjs [<commit>]
 */
import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const dir = path.join(root, 'src', 'Astra.Plugins', 'Host');
const gh = (...args) => execFileSync('gh', ['api', ...args], { encoding: 'utf8', maxBuffer: 64 << 20 });

const commit = process.argv[2] ?? gh('repos/yetone/magpie/commits/main', '--jq', '.sha').trim();
const raw = (file) => gh(`repos/yetone/magpie/contents/${file}?ref=${commit}`, '-H', 'Accept: application/vnd.github.raw');

const rows = [];
for (const name of ['host.js', 'pi.js']) {
  const text = raw(`internal/plugin/${name}`);
  fs.writeFileSync(path.join(dir, name), text);
  rows.push(`| ${name} | ${commit} | ${crypto.createHash('sha256').update(text).digest('hex')} |`);
}
fs.writeFileSync(path.join(dir, 'LICENSE-magpie.txt'), raw('LICENSE'));

const doc = path.join(dir, 'VENDORED.md');
const head = fs.readFileSync(doc, 'utf8').split('| File |')[0];
fs.writeFileSync(doc, `${head}| File | magpie commit | SHA-256 |\n| --- | --- | --- |\n${rows.join('\n')}\n`);
console.log(`vendored magpie ${commit}`);
