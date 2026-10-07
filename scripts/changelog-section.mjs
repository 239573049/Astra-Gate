#!/usr/bin/env node
/**
 * Print the release notes for one version from CHANGELOG.md: the body of its
 * `## [x.y.z] - date` section, up to the next `## ` heading. Prints nothing
 * (exit 0) when the version has no section, so callers can test for empty.
 *
 *   node scripts/changelog-section.mjs <version> [--out <file>]
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

const version = process.argv[2];
const outIndex = process.argv.indexOf('--out');
const out = outIndex !== -1 ? process.argv[outIndex + 1] : undefined;
if (!version || version.startsWith('--')) {
  console.error('Usage: changelog-section.mjs <version> [--out <file>]');
  process.exit(1);
}

let text = '';
try {
  text = fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8');
} catch {
  /* no changelog = no notes */
}

const lines = text.replace(/\r\n/g, '\n').split('\n');
const escaped = version.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const heading = new RegExp(`^## \\[?v?${escaped}\\]?(\\s|$)`);
const start = lines.findIndex((l) => heading.test(l));
let notes = '';
if (start !== -1) {
  const rest = lines.slice(start + 1);
  const end = rest.findIndex((l) => l.startsWith('## '));
  notes = (end === -1 ? rest : rest.slice(0, end)).join('\n').trim();
}

if (out) {
  fs.mkdirSync(path.dirname(path.resolve(out)), { recursive: true });
  fs.writeFileSync(out, notes ? `${notes}\n` : '');
} else if (notes) {
  process.stdout.write(`${notes}\n`);
}
