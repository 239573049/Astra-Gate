#!/usr/bin/env node
// Copies the built web UI (../web/dist) into desktop/renderer so it can be
// bundled with the Electron app and served over app://astra.
// Pass --optional (or set ASTRA_RENDERER_URL) to skip instead of failing
// when web/dist does not exist yet — used by `pnpm dev`, which loads the
// renderer from the Vite dev server instead.
import { cpSync, existsSync, readdirSync, rmSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const optional =
  process.argv.includes('--optional') ||
  Boolean(process.env.ASTRA_RENDERER_URL && process.env.ASTRA_RENDERER_URL.trim() !== '');

const desktopDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const src = path.resolve(desktopDir, '..', 'web', 'dist');
const dest = path.resolve(desktopDir, 'renderer');

if (!existsSync(src) || !statSync(src).isDirectory()) {
  if (optional) {
    console.warn(`[copy-web] ${src} not found — skipping (dev renderer URL mode).`);
    process.exit(0);
  }
  console.error(`[copy-web] web build output not found at ${src}`);
  console.error('[copy-web] Build the web app first: pnpm --filter @astragate/web build');
  process.exit(1);
}

if (!readdirSync(src).some((name) => name === 'index.html')) {
  if (optional) {
    console.warn(`[copy-web] ${src} has no index.html — skipping (dev renderer URL mode).`);
    process.exit(0);
  }
  console.error(`[copy-web] ${src} does not contain index.html — is the web build complete?`);
  console.error('[copy-web] Build the web app first: pnpm --filter @astragate/web build');
  process.exit(1);
}

rmSync(dest, { recursive: true, force: true });
cpSync(src, dest, { recursive: true });
console.log(`[copy-web] copied ${src} -> ${dest}`);
