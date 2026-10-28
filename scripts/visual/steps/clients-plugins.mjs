// Web steps for the Clients, Plugins and Smart routing scenes (tests/Astra.Desktop.UiTests/Scenes.Clients.cs, Scenes.Plugins.cs).
// See scripts/visual/web-steps.mjs for the entry shape and helpers. Labels are the zh catalog's (web/src/i18n/messages.ts).

/** A visible button by exact accessible name. */
const button = (page, name) => page.getByRole('button', { name, exact: true }).filter({ visible: true }).first();

/**
 * The C# ClientsReveal: Avalonia BringIntoView on the LAST settings group titled `title` — scroll the least amount that shows the
 * whole group (the web Group is a <section> whose <h3> holds the title).
 */
async function reveal(page, title) {
  const heading = page.locator('section > h3', { hasText: title }).last();
  await heading.evaluate((h) => h.parentElement.scrollIntoView({ block: 'nearest', behavior: 'instant' }));
  await page.waitForTimeout(300);
}

export const STEPS = {
  // ----- Clients (route /clients[/:kind] from the recording's param)
  'clients__data': {},
  'clients__empty': {},
  // Scenes.Clients.cs ClientsHandler answers POST /api/clients/check-updates with the /api/clients override body AND status 200,
  // so the error scene's check returns {"error":"boom"} as data; the desktop ignores it, the web writes it into the clients
  // query and the whole SPA crashes (blank page). Serve that one call as the 500 it stands for. (A scene-fixture bug, reported.)
  'clients__error': {
    patch: (rec) => {
      for (const c of rec.calls) if (c.method === 'POST' && c.url === '/api/clients/check-updates') c.status = 500;
      return rec;
    },
  },
  'clients__loading': { settle: 400 },
  'clients__not-detected': {},
  'clients__detected': {},
  'clients__enabled': {},
  'clients__drifted': {},
  'clients__needs-repair': {},
  'clients__desktop-roles': {},
  'clients__options': { run: (page) => reveal(page, '模型协议') },
  'clients__claude-direct': { run: (page) => reveal(page, '本机统计') },
  // vm.BeginEnable → the header's "启用…" opens the enable sheet with its preview (diff)
  'clients__enable-preview': { run: async (page, h) => { await button(page, '启用…').click(); await h.dialog(); } },
  // vm.DisableAsync → the confirm dialog
  'clients__disable': { run: async (page, h) => { await button(page, '禁用并还原').click(); await h.dialog(); } },
  // vm.BeginBackups → the "配置备份" row opens the backups sheet
  'clients__backups': { run: async (page, h) => { await page.getByText('配置备份', { exact: true }).filter({ visible: true }).first().click(); await h.dialog(); } },
  // vm.BeginInstall(Install) → the header's "安装" button (InstallActions) opens the confirm sheet
  'clients__install-confirm': { run: async (page, h) => { await page.getByRole('button', { name: /^安装/ }).filter({ visible: true }).first().click(); await h.dialog(); } },
  // vm.BeginInstall(Log) with a busy install → the "进行中…" button opens the log sheet
  'clients__install-running': { run: async (page, h) => { await page.getByRole('button', { name: /进行中/ }).filter({ visible: true }).first().click(); await h.dialog(); } },
  // vm.BeginInstall(Uninstall) on Codex → the 版本 group's "卸载" row button opens the confirm step (with the restore warning).
  // Clicked in the DOM: Playwright's click would scroll the page to the button, and the desktop scene opens the sheet unscrolled.
  'clients__uninstall-confirm': { run: async (page, h) => { await button(page, '卸载').evaluate((b) => b.click()); await h.dialog(); } },
  // vm.BeginInstall(Log) while Codex is being uninstalled → the header's "进行中…" button opens the log sheet
  'clients__uninstall-running': { run: async (page, h) => { await page.getByRole('button', { name: /进行中/ }).filter({ visible: true }).first().evaluate((b) => b.click()); await h.dialog(); } },

  // ----- Plugins
  'plugins__installed': {},
  'plugins__installed-empty': {},
  'plugins__market': {},
  'plugins__market-search': {
    run: async (page) => {
      await page.getByPlaceholder('搜索 npm 与 GitHub 上的插件…').fill('word');
      await page.waitForTimeout(900); // the search waits for the text to rest (400 ms)
    },
  },
  'plugins__runtime-bun-missing': {},
  'plugins__runtime-bun-installing': {},
  'plugins__runtime-bun-ready': {},
  // Model.JobId = "j1": the web opens the job sheet only from a job a mutation returned, so "全部更新" (POST /api/plugins/update)
  // is answered with the scene's running job (its GET /api/plugins/jobs/j1 override).
  'plugins__job-running': {
    patch: (rec) => {
      const job = rec.overrides?.['/api/plugins/jobs/j1']?.body;
      rec.calls.push({ method: 'POST', url: '/api/plugins/update', status: 200, contentType: 'application/json', body: job });
      return rec;
    },
    run: async (page, h) => { await button(page, '全部更新').click(); await h.dialog(); },
  },
  // Model.Editing = the first plugin with middleware (word-guard, the first row) → its "更多" menu → "选项"
  'plugins__detail-sheet': {
    run: async (page, h) => {
      await page.getByRole('button', { name: '更多' }).filter({ visible: true }).first().click();
      await page.waitForTimeout(300);
      await page.getByRole('menuitem', { name: '选项' }).first().click();
      await h.dialog();
    },
  },
  'plugins__add-sheet': { run: async (page, h) => { await button(page, '添加插件').click(); await h.dialog(); } },
  // Model.Importing = true → the magpie banner's import button
  'plugins__import-sheet': {
    run: async (page, h) => {
      await button(page, '从 magpie 导入').click();
      await h.dialog();
    },
  },

  // ----- Smart routing
  'smart-routing__unconfigured': {},
  'smart-routing__configured': {},
  'smart-routing__custom-judge': {},
  // Tester.Text + RunAsync → the "试一试" text area and "判定"
  'smart-routing__test-result': {
    run: async (page, h) => {
      await page.getByLabel('试一试', { exact: true }).fill('Refactor this module so the parser streams its input and add tests.');
      await button(page, '判定').click();
      await page.waitForTimeout(600);
      // fill() scrolled the tester into view; the desktop scene never scrolls (its result is below the fold too)
      await h.scroll(0);
    },
  },
  // draft.Model = "" (the judge model) then SaveAsync(enabled: true) → clear "判定模型" and flip the switch (save(true));
  // the server refuses with four problems, listed under the switch.
  'smart-routing__validation-error': {
    run: async (page, h) => {
      await page.getByLabel('判定模型', { exact: true }).fill('');
      await h.scroll(0); // fill() scrolled the input into view; the desktop scene stays at the top
      await page.waitForTimeout(200);
      // the switch is disabled while a number is missing; the judge model is text, so it stays enabled
      await page.getByRole('switch').filter({ visible: true }).first().click({ force: true });
      await page.waitForTimeout(600);
    },
  },
};

export const DESKTOP_ONLY = {};

export const NOT_MIRRORED = {};
