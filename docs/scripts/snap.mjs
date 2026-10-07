// Captures the Astra admin UI screenshots used by the docs, from the live Vite dev server.
// Usage: node scripts/snap.mjs
// Prerequisites:
//   1. astra-server running (defaults to 127.0.0.1:17321)
//   2. web dev server: pnpm --filter @aidotnet/web dev  → http://localhost:5173
//   3. Playwright chromium cached (pnpm dlx playwright install chromium)
// login.png cannot be captured on loopback (no password required there); it is rendered from
// the HTML mockup in shots/login.html via: node scripts/snap-mock.mjs login
import { chromium } from 'playwright-core';
import { readdirSync, mkdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const outDir = join(root, 'public', 'screenshots');
mkdirSync(outDir, { recursive: true });

const APP = process.env.ASTRA_DEV_URL ?? 'http://localhost:5173';
const API = process.env.ASTRA_API_URL ?? 'http://127.0.0.1:17321';

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

async function settle(page, ms = 1200) {
  await page.waitForLoadState('networkidle').catch(() => {});
  await page.waitForTimeout(ms);
}

const browser = await chromium.launch({
  headless: true,
  executablePath: findHeadlessShell(),
  args: ['--force-device-scale-factor=2', '--hide-scrollbars'],
});

async function shot(page, name) {
  await page.screenshot({ path: join(outDir, `${name}.png`) });
  console.log(`✓ ${name}.png`);
}

try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2, locale: 'zh-CN' });
  // 强制中文界面（i18n 优先读 localStorage 的 astra.locale，其次 navigator.language）
  await page.addInitScript(() => localStorage.setItem('astra.locale', 'zh'));
  await page.goto(`${APP}/`, { waitUntil: 'domcontentloaded' });

  // 概览
  await page.goto(`${APP}/`);
  await page.waitForSelector('article, .card', { timeout: 15000 });
  await settle(page, 1800); // 等计数器动画结束
  await shot(page, 'overview');

  // 请求日志 + 请求详情（点击第一行）
  await page.goto(`${APP}/requests`);
  await settle(page, 1400);
  await shot(page, 'requests');
  const row = page.locator('[aria-label*="的请求详情"], [aria-label*="request detail" i]').first();
  if (await row.count()) {
    await row.click();
    await settle(page, 1000);
    await shot(page, 'request-detail');
    await page.keyboard.press('Escape');
    await settle(page, 400);
  } else {
    console.log('· request-detail skipped (no request rows)');
  }

  // 客户端 + 未启用客户端标签（两张截图不能相同：/clients/:kind 是标签页）
  await page.goto(`${APP}/clients`);
  await settle(page, 1400);
  await shot(page, 'clients');
  await page.goto(`${APP}/clients/claude-code`);
  await settle(page, 1400);
  await shot(page, 'client-detail');

  // 提供商 + 添加弹层 + 详情(设置标签)
  await page.goto(`${APP}/providers`);
  await settle(page, 1400);
  await shot(page, 'providers');
  const addBtn = page.locator('button', { hasText: '添加提供商' }).first();
  if (await addBtn.count()) {
    await addBtn.click();
    await settle(page, 900);
    await shot(page, 'provider-add');
    await page.keyboard.press('Escape');
    await settle(page, 400);
  }
  const providers = await (await fetch(`${API}/api/providers`)).json();
  if (providers?.length) {
    await page.goto(`${APP}/providers/${providers[0].id}`);
    await settle(page, 1400);
    const tab = page.locator('[role="tab"]', { hasText: '设置' }).first();
    if (await tab.count()) {
      await tab.click();
      await settle(page, 900);
    }
    await shot(page, 'provider-detail');
  }

  // 模型 / 隐私护栏 / 设置
  await page.goto(`${APP}/models`);
  await settle(page, 1600);
  await shot(page, 'models');
  await page.goto(`${APP}/privacy`);
  await settle(page, 1400);
  await shot(page, 'privacy');
  await page.goto(`${APP}/settings`);
  await settle(page, 1400);
  await shot(page, 'settings');
} finally {
  await browser.close();
}
console.log('Done → public/screenshots/ (login.png 由 shots/login.html 样稿生成，环回访问不出登录页)');
