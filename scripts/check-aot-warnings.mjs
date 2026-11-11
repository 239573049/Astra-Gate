#!/usr/bin/env node
/**
 * Fail a Native AOT publish log that has any IL trim/AOT warning. There is no
 * allowance: the plugin runtime's engine (Jaunt) is AOT-clean, and the ILC
 * exception Jint needed went with it. A new package that trips ILC is fixed or
 * replaced, not excused here.
 *
 *   node scripts/check-aot-warnings.mjs <publish.log>
 */
import fs from 'node:fs';

const file = process.argv[2];
if (!file) {
  console.error('Usage: check-aot-warnings.mjs <publish.log>');
  process.exit(1);
}

const seen = new Set();
for (const raw of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
  if (!/\b(warning|error) IL\d{4}\b/.test(raw)) continue;
  // MSBuild repeats a line with the project suffix; count each once
  seen.add(raw.replace(/\s*\[[^\]]*\.csproj[^\]]*\]\s*$/, '').trim());
}

if (seen.size > 0) {
  console.error(`AOT publish has ${seen.size} IL warning(s):`);
  for (const line of seen) console.error(`  ${line}`);
  process.exit(1);
}
console.log('AOT warnings: none');
