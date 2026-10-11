#!/usr/bin/env node
/**
 * Fail a Native AOT publish log that has any IL trim/AOT warning other than
 * Jint's own. astra-server.csproj keeps IL2026/IL2104/IL3053 as warnings for
 * the ILC step only, because Jint's unused CLR-interop and System.Text.Json
 * paths trip them; this scan keeps every other IL warning — ours, or a new
 * package's — fatal for the release.
 *
 *   node scripts/check-aot-warnings.mjs <publish.log>
 */
import fs from 'node:fs';

const file = process.argv[2];
if (!file) {
  console.error('Usage: check-aot-warnings.mjs <publish.log>');
  process.exit(1);
}

// Jint's lines name its package (the summary IL2104/IL3053 on Jint.dll) or
// start the message with a Jint member (the detailed IL2026 lines).
const fromJint = (line) => /[\\/]jint[\\/][^\\/]+[\\/]lib[\\/]/i.test(line) || /\bIL\d{4}: Jint\./.test(line);

const seen = new Set();
const offending = [];
for (const raw of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
  if (!/\b(warning|error) IL\d{4}\b/.test(raw)) continue;
  // MSBuild repeats a line with the project suffix; count each once
  const line = raw.replace(/\s*\[[^\]]*\.csproj[^\]]*\]\s*$/, '').trim();
  if (seen.has(line)) continue;
  seen.add(line);
  if (!fromJint(line)) offending.push(line);
}

if (offending.length > 0) {
  console.error(`AOT publish has ${offending.length} IL warning(s) not from Jint:`);
  for (const line of offending) console.error(`  ${line}`);
  process.exit(1);
}
console.log(`AOT warnings: ${seen.size} from Jint (allowed), none elsewhere`);
