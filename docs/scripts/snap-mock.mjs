// Renders HTML mockups from shots/ to PNG screenshots (used only where the live UI
// cannot produce the capture — currently just login, which never shows on loopback).
// Usage: node scripts/snap-mock.mjs [name ...]   (no args = all mockups)
import { chromium } from 'playwright-core';
import { readdirSync, mkdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const shotsDir = join(root, 'shots');
const outDir = join(root, 'public', 'screenshots');
mkdirSync(outDir, { recursive: true });

function findHeadlessShell() {
  const cache = join(homedir(), 'Library', 'Caches', 'ms-playwright');
  const dirs = readdirSync(cache)
    .filter((d) => d.startsWith('chromium_headless_shell-'))
    .sort();
  if (dirs.length === 0) throw new Error('No chromium_headless_shell-* in ~/Library/Caches/ms-playwright — run `pnpm dlx playwright install chromium`');
  const dir = join(cache, dirs.at(-1));
  for (const sub of readdirSync(dir, { recursive: true })) {
    if (sub.endsWith('chrome-headless-shell')) return join(dir, sub);
  }
  throw new Error(`chrome-headless-shell binary not found under ${dir}`);
}

const wanted = process.argv.slice(2);
const files = readdirSync(shotsDir)
  .filter((f) => f.endsWith('.html'))
  .filter((f) => wanted.length === 0 || wanted.includes(f.replace(/\.html$/, '')));

const browser = await chromium.launch({
  headless: true,
  executablePath: findHeadlessShell(),
  args: ['--force-device-scale-factor=2', '--hide-scrollbars'],
});

try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
  for (const file of files) {
    const name = file.replace(/\.html$/, '');
    await page.goto(`file://${join(shotsDir, file)}`);
    await page.evaluate(() => document.fonts.ready);
    await page.waitForTimeout(60);
    await page.screenshot({ path: join(outDir, `${name}.png`) });
    console.log(`✓ ${name}.png (mockup)`);
  }
} finally {
  await browser.close();
}
console.log(`Done: ${files.length} mockup screenshots → public/screenshots/`);
