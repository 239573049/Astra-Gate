// Web steps for the provider-account component scenes (tests/Astra.Desktop.UiTests/Scenes.Accounts.cs: accounts__*,
// plugin-accounts__*, account-usage__*) and the Models scenes (Scenes.Models.cs: models__*, model-detail__*).
// See scripts/visual/web-steps.mjs for the entry shape and helpers.
//
// The account scenes host a component alone (AccountsHost: no shell, left 232 / top 52, padding 20 16 40 40 on the muted
// background). The web shows the same component on the provider detail page's 账户 tab, so each scene opens
// /providers/<id>, picks the tab, cuts the tab panel out with `iso()` (below: the shared isolate, but under the dialog
// layer so a sheet the scene opens stays on top with its backdrop, as on the desktop) and then drives the component.
// The desktop never fetches the provider (the C# builds it from AccountsData), so `withProvider` adds that answer.
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = dirname(dirname(dirname(dirname(fileURLToPath(import.meta.url)))));
const fixture = (p) => JSON.parse(readFileSync(join(repo, 'tests', 'fixtures', 'ui', 'default', p), 'utf8'));

/** AccountsData.ProviderJson (Scenes.Accounts.cs): prov_anthropic.json with another id / name / template / auth scheme. */
function providerJson(id, name, templateId, authScheme, settings = {}) {
  return { ...fixture('providers/prov_anthropic.json'), id, name, templateId, authScheme, httpProxy: null, settings };
}
const CLAUDE = providerJson('prov_claude', 'Claude Max', 'anthropic-subscription', 'oauth-subscription');
const CODEX = providerJson('prov_claude', 'ChatGPT', 'openai-subscription', 'oauth-subscription');
const PLUGIN = providerJson('plugin-kimi', 'Kimi (plugin)', 'plugin-kimi', 'plugin', { plugin: { usage: true } });

const ok = (method, url, body) => ({ method, url, status: 200, contentType: 'application/json', body: typeof body === 'string' ? body : JSON.stringify(body) });

/** Adds the provider answer (and extra calls) to a recording; recorded calls keep precedence for anything they already answer. */
const withProvider = (provider, extra = []) => (rec) => {
  rec.calls = [...rec.calls, ok('GET', `/api/providers/${provider.id}`, provider), ok('GET', `/api/providers/${provider.id}/models`, []), ...extra].map(withTokens);
  return rec;
};

/**
 * The server's ProviderUsageTotals carry `tokens` (input + output, web/src/api/types.ts); the C# scene data (AccountsData.UsageJson)
 * omits it and the desktop DTO sums the two itself, so the web would show "—". Add it so both show the same numbers.
 */
function withTokens(c) {
  if (!/\/usage$/.test(new URL(c.url, 'http://x').pathname) || c.status !== 200 || !c.body) return c;
  try {
    const o = JSON.parse(c.body);
    for (const a of o.accounts ?? []) for (const k of ['today', 'total']) if (a[k] && a[k].tokens == null) a[k].tokens = (a[k].inputTokens ?? 0) + (a[k].outputTokens ?? 0);
    return { ...c, body: JSON.stringify(o) };
  } catch { return c; }
}

/** The calls of another scene's recording (account-usage scenes record none: they reuse accounts__list's data). */
function callsOf(scene) {
  const f = join(repo, 'artifacts', 'visual', 'fixtures', `${scene}.json`);
  return existsSync(f) ? JSON.parse(readFileSync(f, 'utf8')).calls : [];
}

/** web-steps' isolate, but at z-index 40 (Radix dialog overlay is 50/51) so an open sheet keeps its backdrop above it. */
async function iso(page, selector, box = {}) {
  const handle = await page.locator(selector).first().elementHandle({ timeout: 5000 });
  await page.evaluate(([el, b]) => {
    const host = document.createElement('div');
    host.id = '__iso';
    Object.assign(host.style, { position: 'fixed', inset: '0', zIndex: '40', overflow: 'hidden', background: 'var(--surface-muted, var(--bg))' });
    const bodyBg = getComputedStyle(document.body).backgroundColor;
    if (!getComputedStyle(host).backgroundColor || getComputedStyle(host).backgroundColor === 'rgba(0, 0, 0, 0)') host.style.background = bodyBg;
    const inner = document.createElement('div');
    Object.assign(inner.style, {
      position: 'absolute', left: `${b.left ?? 232}px`, top: `${b.top ?? 52}px`, right: '0', bottom: '0', boxSizing: 'border-box', overflow: 'hidden',
      padding: b.padding ?? '16px 40px 40px 20px',
    });
    if (b.width) Object.assign(inner.style, { right: 'auto', width: `${b.width}px` });
    el.style.paddingTop = '0';
    el.style.marginTop = '0';
    if (b.elWidth) Object.assign(el.style, { width: `${b.elWidth}px`, marginLeft: 'auto', marginRight: 'auto' });
    inner.appendChild(el);
    host.appendChild(inner);
    // inside React's root container: React delegates events there, so a node moved out of it stops reacting to clicks
    (document.getElementById('root') ?? document.body).appendChild(host);
    // menus / popovers portal to <body> with a z-index below the host's; lift them (dialogs are 50/51 already)
    const st = document.createElement('style');
    st.textContent = '[data-radix-popper-content-wrapper]{z-index:60 !important}';
    document.head.appendChild(st);
  }, [handle, box]);
  await page.waitForTimeout(300);
}

const noPopups = () => { window.open = () => null; };

/** Opens the provider page's accounts tab and isolates its panel; `then` drives the component afterwards. */
function accounts(provider, then, extra = []) {
  return {
    path: `/providers/${provider.id}`,
    patch: withProvider(provider, extra),
    init: noPopups,
    async run(page, h) {
      await h.role('tab', '账户');
      await page.waitForTimeout(500);
      await iso(page, '[role=tabpanel][data-state=active]');
      if (then) await then(page, h);
    },
  };
}

const addMenu = async (page, h, item) => {
  await h.role('button', '添加账户');
  await h.role('menuitem', item);
};

const IMPORT_TEXT = 'sk-ant-ort01-AAAA…\nsk-ant-oat01-BBBB…\n# a comment\n{"claudeAiOauth":{}}';

export const STEPS = {
  // ----- subscription accounts (SubscriptionAccounts.tsx) -----
  'accounts__list': accounts(CLAUDE),
  'accounts__empty': accounts(CLAUDE),
  'accounts__loading': { ...accounts(CLAUDE), settle: 400 },
  'accounts__error': accounts(CLAUDE),
  'accounts__no-match': accounts(CLAUDE, async (page, h) => h.fill('搜索邮箱 / 名称 / Key', 'nobody')),
  'accounts__filter-cooling': accounts(CLAUDE, async (page) => {
    await page.getByRole('group').getByRole('button', { name: /^冷却/ }).first().click();
  }),
  'accounts__selection': accounts(CLAUDE, async (page) => {
    // the card's checkbox is display:none until hover: dispatch the click instead of moving the pointer
    await page.getByRole('checkbox', { name: '选择 alice@example.com', includeHidden: true }).dispatchEvent('click');
    await page.waitForTimeout(200);
    await page.getByRole('checkbox', { name: '选择 bob@example.com', includeHidden: true }).dispatchEvent('click');
  }),
  'accounts__policy': accounts(CLAUDE),
  'accounts__detail': accounts(CODEX, async (page, h) => h.click('alice@example.com')),
  'accounts__detail-dead': accounts(CLAUDE, async (page, h) => h.click('erin@example.com')),
  'accounts__detail-key': accounts(CLAUDE, async (page, h) => h.click('sk-ant-api…9f2c', { exact: false })),
  'accounts__codex-local': accounts(CODEX),
  ...Object.fromEntries(['device', 'pkce', 'paste', 'cli'].map((mode) => [`accounts__login-${mode}`, accounts(CLAUDE, async (page, h) => addMenu(page, h, '浏览器登录'))])),
  'accounts__login-error': accounts(CLAUDE, async (page, h) => {
    await addMenu(page, h, '浏览器登录');
    await h.role('button', '我已授权，立即验证');
  }),
  'accounts__proxy-login': accounts(CLAUDE, async (page, h) => {
    await addMenu(page, h, '浏览器登录（指定代理）…');
    await page.locator('[role=dialog] input').first().fill('socks5://127.0.0.1:1080');
  }),
  'accounts__import': accounts(CLAUDE, async (page, h) => {
    await h.role('button', '批量导入');

    await page.locator('[role=dialog] textarea').first().fill(IMPORT_TEXT);
  }),
  'accounts__import-result': accounts(CLAUDE, async (page, h) => {
    await h.role('button', '批量导入');
    await page.locator('[role=dialog] textarea').first().fill('sk-ant-ort01-AAAA\nsk-ant-ort01-BBBB\nsk-ant-ort01-CCCC\nbad-token');
    await page.locator('[role=dialog]').getByRole('button', { name: '预览' }).click();
  }),
  'accounts__import-apikey': accounts(CLAUDE, async (page, h) => addMenu(page, h, '添加 API Key…')),

  // ----- plugin accounts (PluginAccounts.tsx) -----
  'plugin-accounts__signed-in': accounts(PLUGIN),
  'plugin-accounts__signed-out': accounts(PLUGIN),
  'plugin-accounts__usage': accounts(PLUGIN, async (page) => {
    await page.getByRole('button', { name: '额度' }).first().click();
  }),
  'plugin-accounts__prompt': accounts(PLUGIN, async (page, h) => h.role('button', 'Kimi 账号登录')),
  'plugin-accounts__key': accounts(PLUGIN, async (page) => {
    await page.locator('[role=tabpanel] button').filter({ hasText: /API Key|API 密钥|密钥/ }).first().click();
  }),
  // the desktop follows flow1 directly; the web gets it from POST sign-in, which the desktop never sent
  'plugin-accounts__waiting': accounts(PLUGIN, async (page, h) => {
    await h.role('button', 'Kimi 账号登录');
    await page.locator('[role=dialog]').getByRole('button', { name: '打开登录页面' }).click({ timeout: 5000 });
  }, [ok('POST', '/api/plugin-providers/plugin-kimi/sign-in', { id: 'flow1', state: 'waiting', url: 'https://kimi.moonshot.cn/device', instructions: '在打开的页面登录后，复制显示的授权码粘贴到下方。', pasteCode: true, deviceCode: 'KIMI-4821' })]),
};

// ----- the usage table alone (AccountUsageStats.tsx), shown with acc_max's UsageRow(): total 48,120 req / $1,523.30 -----
const usageRow = {
  accountId: 'acc_max',
  today: { requests: 128, inputTokens: Math.floor(2_412_300 * 3 / 4), outputTokens: 2_412_300 - Math.floor(2_412_300 * 3 / 4), costNanoUsd: 1_843_200_000 },
  total: { requests: 48_120, inputTokens: Math.floor(98_120_400 * 3 / 4), outputTokens: 98_120_400 - Math.floor(98_120_400 * 3 / 4), costNanoUsd: 1_523_300_000_000 },
};
for (const [state, width] of [['normal', 420], ['compact', 200]]) {
  STEPS[`account-usage__${state}`] = {
    path: '/providers/prov_claude',
    init: noPopups,
    patch: (rec) => {
      const base = callsOf('accounts__list').filter((c) => !c.url.endsWith('/usage'));
      rec.calls = [...base, ok('GET', '/api/providers/prov_claude', CLAUDE), ok('GET', '/api/providers/prov_claude/models', []),
        ok('GET', '/api/providers/prov_claude/usage', { day: '2026-10-26', timeZone: 'Asia/Shanghai', accounts: [usageRow] })].map(withTokens);
      return rec;
    },
    async run(page, h) {
      await h.role('tab', '账户');
      await page.waitForTimeout(500);
      // normal = the detail sheet's table; compact = the card's
      if (state === 'normal') {
        await h.click('alice@example.com');
        await page.waitForTimeout(400);
        const table = page.locator('[role=dialog] [data-usage-stats], [role=dialog] table').first();
        await iso(page, await selectorOf(page, table), { elWidth: width });
        await page.keyboard.press('Escape');
      } else {
        const table = page.locator('[role=tabpanel][data-state=active] [data-usage-stats], [role=tabpanel][data-state=active] table').first();
        await iso(page, await selectorOf(page, table), { elWidth: width });
      }
    },
  };
}

/** Tags a located element so iso() can find it by selector. */
async function selectorOf(page, loc) {
  await loc.evaluate((el) => el.setAttribute('data-iso-target', '1'));
  return '[data-iso-target="1"]';
}

// ----- Models (ModelsPage.tsx / ModelDetailPage.tsx) -----
Object.assign(STEPS, {
  'models__data': {},
  'models__empty': {},
  'models__error': {},
  'models__search-no-result': { async run(page, h) { await h.fill('搜索模型、别名', 'gpt-9'); await h.wait(500); } },
  'models__create-sheet': { async run(page, h) { await h.role('button', '新建模型'); } },
  'models__sync-preview': {
    async run(page, h) {
      await h.role('button', '同步模型目录');
      await page.locator('[role=dialog]').getByRole('button', { name: '检查更新' }).click();
      await h.wait(500);
    },
  },
  'model-detail__data': {},
  'model-detail__user': {},
  'model-detail__error': {},
  'model-detail__price-editor': {
    async run(page) {
      await page.getByRole('button', { name: '编辑' }).filter({ visible: true }).first().click();
      await page.waitForTimeout(400);
    },
  },
  'model-detail__rule-dialog': {
    async run(page) {
      await page.getByRole('button', { name: '编辑' }).filter({ visible: true }).first().click();
      await page.waitForTimeout(400);
      await page.locator('[role=dialog]').getByRole('button', { name: '添加时段' }).first().click();
      await page.waitForTimeout(400);
    },
  },
  'model-detail__dirty': {
    async run(page) {
      await page.getByLabel('显示名称', { exact: true }).fill('GPT-4.1 (edited)');
      await page.getByLabel('output', { exact: true }).first().fill('9');
      await page.waitForTimeout(200);
    },
  },
  'model-detail__simulator': {
    async run(page, h) {
      await page.getByRole('button', { name: '计算' }).click();
      await h.wait(400);
      await h.scroll(null);
    },
  },
  'model-detail__delete-confirm': {
    async run(page) {
      // the header's DeleteMorph (user models only) morphs into its confirm prompt
      await page.locator('header').getByRole('button', { name: '删除' }).first().click();
      await page.waitForTimeout(400);
    },
  },
});

export const DESKTOP_ONLY = {};
export const NOT_MIRRORED = {};
