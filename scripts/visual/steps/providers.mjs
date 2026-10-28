// Web steps for the Providers list / add wizard / import sheet (tests/Astra.Desktop.UiTests/Scenes.Providers.cs) and the provider
// detail page (Scenes.ProviderDetail.cs). See scripts/visual/web-steps.mjs for the entry shape and helpers. Web code mirrored:
// web/src/pages/ProvidersPage.tsx (AddProviderSheet, ImportProvidersSheet), web/src/pages/ProviderDetailPage.tsx (ModelsTab,
// ProviderModelSheet, AddModelsSheet, RemoteModelsSheet, ModelMapSheet, ApiKeyTab + ProviderQuotaSection, SettingsTab,
// AddEndpointSheet, TemplateUpdateSheet), components/TemplateSwitch.tsx, components/ProviderTestDialog.tsx.

const T = 5000;

/** Opens an Arc Select / Combobox (trigger = Locator) and picks the option whose accessible name starts with `name`. */
async function pick(page, trigger, name) {
  await trigger.click({ timeout: T });
  await page.waitForTimeout(250);
  const opt = page.getByRole('option', { name }).filter({ visible: true }).first();
  await opt.click({ timeout: T });
  await page.waitForTimeout(300);
}

/** The open dialog (the last visible one). */
const dlg = (page) => page.getByRole('dialog').filter({ visible: true }).last();

/** Clicks the detail page's tab. */
const tab = (name) => async (page, h) => h.role('tab', name, { exact: true });

/** Wizard: open "add provider", choose a template tile (RadioCards role=radio), Continue. */
async function wizardTo(page, h, template) {
  await h.role('button', '添加提供商', { exact: true });
  await h.dialog();
  await page.getByRole('radio', typeof template === 'string' ? { name: template, exact: true } : { name: template }).first().click({ timeout: T });
  await page.waitForTimeout(250);
  await h.role('button', '继续', { exact: true });
}

/** Opens the header's "更多" menu and picks an item. */
async function more(page, h, item) {
  await h.role('button', '更多', { exact: true });
  await page.getByRole('menuitem', { name: item }).first().click({ timeout: T });
  await page.waitForTimeout(400);
}

/** The header's "测试连接" dialog. */
async function openTest(page, h) {
  await h.role('button', '测试连接', { exact: true });
  await h.dialog();
}

const QUOTA_TEST_RESULT = JSON.stringify({
  ok: false, errorCode: 'unauthorized', error: 'HTTP 401: invalid x-api-key', httpStatus: 401,
  url: 'https://api.anthropic.com/v1/organizations/usage', raw: { error: { type: 'authentication_error' } }, snapshot: null, template: 'anthropic',
});

export const STEPS = {
  // ---------- list ----------
  'providers__data': {},
  'providers__empty': {},
  'providers__error': {},
  'providers__states': {},
  'providers__loading': { settle: 400 },
  // the app menu's "New provider" = /providers?new=1 on the web
  'providers__add-wizard-1': {},
  'providers__add-wizard-1-search': {
    run: async (page, h) => {
      await h.role('button', '添加提供商', { exact: true });
      await h.dialog();
      await h.fill('搜索模板', 'claude');
    },
  },
  'providers__add-wizard-2': {
    run: async (page, h) => {
      await wizardTo(page, h, 'OpenAI');
      await dlg(page).getByPlaceholder('sk-…').fill('sk-proj-1234567890');
    },
  },
  'providers__add-wizard-2-more': {
    run: async (page, h) => {
      await wizardTo(page, h, 'OpenAI');
      await dlg(page).getByPlaceholder('sk-…').fill('sk-proj-1234567890');
      await h.click('更多选项');
    },
  },
  'providers__add-wizard-custom': { run: async (page, h) => wizardTo(page, h, '自定义提供商') },
  'providers__add-wizard-subscription': { run: async (page, h) => wizardTo(page, h, /^Claude 订阅/) },
  'providers__add-wizard-alternative': { run: async (page, h) => wizardTo(page, h, 'Zhipu GLM') },
  'providers__import-sheet': { run: async (page, h) => { await h.role('button', '从其他应用导入', { exact: true }); await h.dialog(); } },

  // ---------- detail: models tab ----------
  'provider-detail__models': {},
  'provider-detail__models-mapping': {},
  'provider-detail__models-empty': {},
  'provider-detail__model-sheet': {
    run: async (page, h) => { await h.click('gpt-4.1'); await h.dialog(); },
  },
  'provider-detail__model-sheet-custom': {
    run: async (page, h) => {
      await h.click('gpt-4.1');
      await h.dialog();
      await dlg(page).getByRole('switch', { name: '自定义价格' }).click({ timeout: T });
      await dlg(page).getByLabel('显示名称').first().fill('GPT 4.1 (team)');
    },
  },
  'provider-detail__add-models': {
    run: async (page, h) => {
      await h.role('button', '添加模型', { exact: true });
      await h.dialog();
      await dlg(page).locator('textarea').first().fill('gpt-5\ngpt-5-mini, o3');
    },
  },
  'provider-detail__remote-models': {
    run: async (page, h) => {
      await h.role('button', '从上游获取', { exact: true });
      await h.dialog();
      await dlg(page).getByRole('checkbox', { name: 'gpt-5', exact: true }).click({ timeout: T });
    },
  },
  'provider-detail__remote-models-401': {
    run: async (page, h) => { await h.role('button', '从上游获取', { exact: true }); await h.dialog(); },
  },
  'provider-detail__map-sheet': {
    run: async (page, h) => {
      await h.click('添加映射');
      await h.dialog();
      await dlg(page).getByPlaceholder('deepseek-v4.1').fill('gpt-5-codex');
      await pick(page, dlg(page).getByRole('combobox').first(), /^gpt-4\.1/);
    },
  },

  // ---------- detail: accounts / API key tab ----------
  'provider-detail__accounts-apikey': { run: tab('API Key') },
  'provider-detail__quota': { run: tab('API Key') },
  'provider-detail__quota-custom': {
    run: async (page, h) => {
      await h.role('tab', 'API Key', { exact: true });
      const sw = page.getByRole('switch', { name: '启用用量查询' }).first();
      if ((await sw.getAttribute('aria-checked')) !== 'true') await sw.click({ timeout: T });
      await page.waitForTimeout(300);
      await pick(page, page.getByRole('combobox', { name: '查询模板' }).first(), '自定义');
    },
  },
  'provider-detail__quota-test': {
    // the desktop scene shows a canned result without sending the request; the web has to send it, so answer it the same way
    patch: (rec) => {
      rec.calls.push({ method: 'POST', url: '/api/providers/prov_anthropic/quota/test', status: 200, contentType: 'application/json', body: QUOTA_TEST_RESULT });
      return rec;
    },
    run: async (page, h) => {
      await h.role('tab', 'API Key', { exact: true });
      await h.role('button', '测试查询', { exact: true });
      await h.dialog();
    },
  },
  'provider-detail__accounts-subscription': { run: tab('账户') },
  'provider-detail__accounts-plugin': { run: tab('账户') },
  'provider-detail__accounts-selection': {
    run: async (page, h) => {
      await h.role('tab', '账户', { exact: true });
      await page.waitForTimeout(600);
      for (const who of ['alice@example.com', 'bob@example.com']) {
        await page.locator(`[aria-label="选择 ${who}"]`).first().evaluate((el) => el.click());
        await page.waitForTimeout(250);
      }
    },
  },

  // ---------- detail: settings tab ----------
  'provider-detail__settings': { run: tab('设置') },
  'provider-detail__settings-dirty': {
    run: async (page, h) => {
      await h.role('tab', '设置', { exact: true });
      await page.getByLabel('名称', { exact: true }).first().fill('OpenAI (team)');
      // preferred order → responses, chat, gemini: the chips keep the order protocols were picked in
      const chip = (v) => page.locator(`button[data-chip="${v}"]`).first();
      const on = async (v) => (await chip(v).getAttribute('aria-pressed')) === 'true';
      for (const v of ['openai-chat', 'openai-responses', 'gemini']) if (await on(v)) { await chip(v).click(); await page.waitForTimeout(150); }
      for (const v of ['openai-responses', 'openai-chat', 'gemini']) { await chip(v).click(); await page.waitForTimeout(150); }
      await h.role('button', '添加请求头', { exact: true });
      await h.dialog();
      await dlg(page).getByPlaceholder('X-Custom-Header').fill('OpenAI-Organization');
      await dlg(page).locator('input').nth(1).fill('org-123');
      await dlg(page).getByRole('button', { name: '添加', exact: true }).click({ timeout: T });
      await page.waitForTimeout(400);
      await h.scroll(0); // typing scrolled the header row into view; the desktop scene stays at the top
    },
  },
  'provider-detail__add-endpoint': {
    run: async (page, h) => {
      await h.role('tab', '设置', { exact: true });
      await h.role('button', '添加接口', { exact: true });
      await h.dialog();
      await dlg(page).getByPlaceholder('https://api.example.com/v1').fill('ftp://bad');
    },
  },

  // ---------- detail: header sheets ----------
  'provider-detail__template-switch': {
    run: async (page, h) => {
      await more(page, h, '更换模板');
      await h.dialog();
      await pick(page, dlg(page).getByRole('combobox').first(), /^Zhipu GLM Coding Plan/);
    },
  },
  'provider-detail__template-update': { run: async (page, h) => { await more(page, h, '模板更新'); await h.dialog(); } },

  // ---------- detail: connection test ----------
  'provider-detail__test-idle': { run: openTest },
  'provider-detail__test-running': {
    run: async (page, h) => { await openTest(page, h); await h.role('button', '开始测试', { exact: true }); },
  },
  'provider-detail__test-done': {
    run: async (page, h) => { await openTest(page, h); await h.role('button', '开始测试', { exact: true }); },
  },
  'provider-detail__test-failed': {
    run: async (page, h) => {
      await openTest(page, h);
      await dlg(page).getByPlaceholder('输入任意模型 ID').fill('glm-4.6');
      // close the suggestion list: its overlay catches a click anywhere (Escape would close the dialog)
      await dlg(page).getByRole('heading').first().click({ force: true, timeout: 1000 }).catch(() => {});
      await page.waitForTimeout(200);
      await h.role('button', '开始测试', { exact: true });
    },
  },
  'provider-detail__test-batch': {
    run: async (page, h) => {
      await openTest(page, h);
      await dlg(page).getByText('批量模型', { exact: true }).click({ timeout: T });
      await page.waitForTimeout(300);
      await dlg(page).getByRole('checkbox').first().click({ timeout: T });
      await page.waitForTimeout(200);
      await dlg(page).getByRole('button', { name: /^测试 \d+/ }).first().click({ timeout: T });
      await page.waitForTimeout(800);
    },
  },

  // ---------- detail: states ----------
  'provider-detail__loading': { settle: 400 },
  'provider-detail__404': {},
};

export const DESKTOP_ONLY = {};
export const NOT_MIRRORED = {};
