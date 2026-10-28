// Web steps for the Overview / Tokens / Token detail scenes (tests/Astra.Desktop.UiTests/Scenes.OverviewAndTokens.cs).
// See scripts/visual/web-steps.mjs for the entry shape and helpers. Each entry mirrors the C# scene line of the same name.

// The breakdown's tab strip (Arc Tabs; TabsTrigger = role tab): SelectTab(v, i) in C#.
const tab = (name) => async (page, h) => {
  await page.getByRole('tab', { name, exact: true }).first().click({ timeout: 5000 });
  await h.wait(400);
};

// The range Segmented ("今天 / 7 天 / 30 天 / 90 天"): OverviewPage.SetRange in C#.
const range = (label) => async (page, h) => {
  await page.getByRole('radio', { name: label, exact: true }).or(page.getByRole('button', { name: label, exact: true })).first().click({ timeout: 5000 });
  await h.wait(500);
};

// A token row's "更多" menu, then one of its items (TokensPage TokenRow Menu).
const rowMenu = (tokenName, item) => async (page, h) => {
  const row = page.locator('article').filter({ has: page.getByRole('heading', { name: tokenName, exact: true }) }).first();
  await row.getByRole('button', { name: '更多' }).click({ timeout: 5000 });
  await h.wait(300);
  await page.getByRole('menuitem', { name: item }).first().click({ timeout: 5000 });
  await h.wait(500);
};

// Expands a token row (the chevron "展开详情"): TokensPage.SetOpen(id, true).
const expand = async (page, tokenName) => {
  const row = page.locator('article').filter({ has: page.getByRole('heading', { name: tokenName, exact: true }) }).first();
  await row.getByRole('button', { name: '展开详情' }).click({ timeout: 5000 });
  await page.waitForTimeout(300);
};

export const STEPS = {
  // ----- overview -----
  'overview__data': {},
  'overview__data-7d': { run: range('7 天') },
  'overview__data-30d': { run: range('30 天') },
  'overview__data-scrolled': { run: async (_p, h) => h.scroll(null) },
  'overview__data-mid': { run: async (_p, h) => h.scroll(470) },
  'overview__providers-tab': { run: async (page, h) => { await tab('提供商')(page, h); await h.scroll(470); } },
  'overview__models-tab': { run: async (page, h) => { await tab('模型')(page, h); await h.scroll(470); } },
  'overview__empty': {},
  // SetClient("claude-code"): the client Segmented's Claude Code glyph (title = client name).
  'overview__client-empty': {
    run: async (page, h) => {
      await page.getByTitle('Claude Code', { exact: true }).first().click({ timeout: 5000 });
      await h.wait(500);
    },
  },
  'overview__error': {},
  'overview__loading': { settle: 400 },
  // OpenRequest("req_0050"): the newest row of the recent-requests table (its model button opens RequestDetailSheet).
  'overview__request-detail': {
    run: async (page, h) => {
      // the first recent row is req_0050 (the newest); its model cell is the button that opens the sheet
      await page.getByRole('button', { name: /的请求详情$/ }).filter({ visible: true }).first().click({ timeout: 5000 });
      await h.dialog();
      await h.wait(400);
    },
  },

  // ----- tokens -----
  'tokens__data': {},
  'tokens__empty': {},
  'tokens__loading': { settle: 400 },
  'tokens__error': {},
  'tokens__data-open': {
    run: async (page) => {
      await expand(page, 'ci-bot');
      await expand(page, 'team-shared');
    },
  },
  'tokens__create-sheet': { run: async (_p, h) => { await h.role('button', '新建令牌', { exact: true }); await h.dialog(); } },
  'tokens__edit-sheet': { run: async (page, h) => { await rowMenu('ci-bot', '设置')(page, h); await h.dialog(); } },
  'tokens__pool-editor': { run: async (page, h) => { await rowMenu('team-shared', '设置')(page, h); await h.dialog(); } },
  'tokens__delete-confirm': { run: async (page, h) => { await rowMenu('ci-bot', '删除令牌')(page, h); await h.dialog(); } },
  'tokens__reset-confirm': { run: async (page, h) => { await rowMenu('default', '重置密钥')(page, h); await h.dialog(); } },
  // CopyKeyAsync("tok_default") with a clipboard that accepts: the reveal POST, then the "copied" toast.
  'tokens__copied': {
    init: () => {
      try {
        Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: () => Promise.resolve(), readText: () => Promise.resolve('') } });
      } catch { /* ignore */ }
    },
    run: async (page, h) => {
      const row = page.locator('article').filter({ has: page.getByRole('heading', { name: 'default', exact: true }) }).first();
      await row.getByRole('button', { name: '复制令牌' }).click({ timeout: 5000 });
      await h.wait(500);
    },
    after: 300,
  },

  // ----- token detail -----
  'token-detail__data': {},
  'token-detail__pool': {},
  'token-detail__empty': {},
  'token-detail__404': {},
  'token-detail__data-scrolled': { run: async (_p, h) => h.scroll(null) },
  // OpenDetail("tok_default") from the list: on the web the list row's "统计详情" link (navigates to /tokens/tok_default).
  'token-detail__pushed': {
    path: '/tokens',
    run: async (page, h) => {
      await expand(page, 'default').catch(() => {});
      await page.getByRole('link', { name: '统计详情' }).filter({ visible: true }).first().click({ timeout: 5000 });
      await h.wait(800);
    },
  },
};
export const DESKTOP_ONLY = {};
export const NOT_MIRRORED = {};
