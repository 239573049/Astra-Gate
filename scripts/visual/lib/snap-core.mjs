// Browser plumbing of scripts/visual/capture-web.mjs (visual-regression captures from fixture data). It came from the
// website's screenshot scripts (scripts/lib/snap-core.mjs in astra-gate-web, which keeps its own copy). playwright-core
// is a devDependency of the workspace root, so `pnpm install` at the repository root provides it.
import { chromium } from 'playwright-core';
import { readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

export { chromium };

/** The Playwright-cached chrome-headless-shell (macOS cache location, as before). */
export function findHeadlessShell() {
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

export async function settle(page, ms = 1200) {
  await page.waitForLoadState('networkidle').catch(() => {});
  await page.waitForTimeout(ms);
}

/** Launches the headless shell; `scale` is the device scale factor the pages will be created with (default 2). */
export function launchBrowser(scale = 2) {
  return chromium.launch({
    headless: true,
    executablePath: findHeadlessShell(),
    args: [`--force-device-scale-factor=${scale}`, '--hide-scrollbars'],
  });
}

/** Saves `<outDir>/<name>.png` and logs it. */
export async function shot(page, outDir, name) {
  await page.screenshot({ path: join(outDir, `${name}.png`) });
  console.log(`✓ ${name}.png`);
}
