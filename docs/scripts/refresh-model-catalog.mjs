#!/usr/bin/env node
/**
 * Refresh the model catalog snapshot served at
 * https://astra-gate.si/api/models-catalog.json (docs/public/api/models-catalog.json).
 *
 * The Astra server's "同步模型目录" pulls from that URL — this script is how
 * the snapshot stays current:
 *
 *   node docs/scripts/refresh-model-catalog.mjs
 *
 * Validates the upstream shape (providers → models) before writing; exits 1
 * without touching the file when the fetch or validation fails.
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const target = path.join(root, 'public', 'api', 'models-catalog.json');
const source = process.env.CATALOG_URL ?? 'https://models.dev/api.json';

const res = await fetch(source, { signal: AbortSignal.timeout(60_000) });
if (!res.ok) {
  console.error(`Fetch failed: ${res.status} ${res.statusText} from ${source}`);
  process.exit(1);
}
let catalog;
try {
  catalog = await res.json();
} catch (err) {
  console.error(`Upstream is not valid JSON: ${err.message}`);
  process.exit(1);
}

if (typeof catalog !== 'object' || catalog === null || Array.isArray(catalog)) {
  console.error('Unexpected catalog shape: expected an object of providers.');
  process.exit(1);
}
const providers = Object.entries(catalog);
let models = 0;
let withPrices = 0;
for (const [id, provider] of providers) {
  if (typeof provider !== 'object' || provider === null || typeof provider.models !== 'object' || provider.models === null) {
    console.error(`Provider ${id} has no models object.`);
    process.exit(1);
  }
  for (const model of Object.values(provider.models)) {
    models++;
    if (model && typeof model.cost === 'object' && model.cost !== null) withPrices++;
  }
}
if (providers.length < 10 || models < 100) {
  console.error(`Catalog looks truncated (${providers.length} providers, ${models} models) — refusing to write.`);
  process.exit(1);
}

fs.mkdirSync(path.dirname(target), { recursive: true });
fs.writeFileSync(target, `${JSON.stringify(catalog)}\n`);
console.log(
  `models-catalog.json refreshed: ${providers.length} providers, ${models} models (${withPrices} priced), ${(fs.statSync(target).size / 1024 / 1024).toFixed(1)} MB`,
);
