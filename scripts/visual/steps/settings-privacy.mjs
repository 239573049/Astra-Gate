// Web steps for the Settings / Privacy scenes (tests/Astra.Desktop.UiTests/Scenes.SettingsAndPrivacy.cs).
// See scripts/visual/web-steps.mjs for the entry shape and helpers.
//
// The plain web UI hides what only the desktop shell has (the tray-panel tab, the in-app update apply button). Those scenes run
// with a fake desktop bridge (window.__ASTRA__, web/src/shell/bridge.ts): platform 'linux' so no traffic-light / Windows caption
// chrome is reserved, and the SPA then uses its HashRouter, so their paths are '/#/…'.

/** Injected before the SPA loads: a minimal desktop bridge. `applyHangs` = the update apply never answers (busy state). */
function desktopBridge() {
  const noop = () => () => {};
  const tray = {
    getPrefs: async () => ({
      title: 'requests',
      sections: { overview: true, cost: true, chart: true, topModel: true, compareYesterday: false, cacheHitRate: false, quota: true, clients: false },
    }),
    setPrefs: async (p) => p,
    onPrefsChanged: noop,
    // The scene's NullDesktopHost tray reports the service as not running, so the desktop preview shows its stopped state.
    getState: async () => ({ running: false, port: 17321, activity: null, apiVersionMismatch: false, updateAvailable: null }),
    onStateChanged: noop,
    command: async () => {},
    setTitle: () => {},
    resize: () => {},
    onVisibilityChanged: noop,
  };
  window.__ASTRA__ = {
    apiBase: location.origin,
    shell: 'desktop',
    desktop: true,
    platform: 'linux',
    apiVersionMismatch: false,
    version: '0.11.7',
    accentColor: null,
    getAccentColor: async () => null,
    onAccentColorChanged: noop,
    setThemeSource: async () => true,
    openPath: async () => true,
    revealLogs: async () => true,
    startService: async () => ({ ok: true }),
    restartService: async () => ({ ok: true }),
    loginItem: { get: async () => ({ supported: true, enabled: true }), set: async (e) => ({ supported: true, enabled: e }), onChanged: noop },
    updates: { check: async () => ({ message: '', action: 'none' }), apply: () => new Promise(() => {}) },
    showContextMenu: async () => null,
    onMenuCommand: noop,
    tray,
  };
}

/** Scrolls the page scroller like the C# Scrolled(scene, offset): min(offset, max). */
const scrollTo = (offset) => async (page, h) => h.scroll(offset);

/** Opens the "更多" menu of the `index`-th webhook row (list order: Ops channel, Feishu on-call, Slack) and picks `item`. */
const webhookMenu = (index, item) => async (page) => {
  await page.getByRole('button', { name: '更多' }).filter({ visible: true }).nth(index).click({ timeout: 5000 });
  await page.waitForTimeout(300);
  await page.getByRole('menuitem', { name: item }).or(page.getByText(item, { exact: true })).filter({ visible: true }).first().click({ timeout: 5000 });
  await page.waitForTimeout(600);
};

const DRY_RUN = {
  blocked: false,
  hits: [
    { ruleId: 'email', category: 'email', action: 'redact', count: 2, samples: ['jo••••om', 'an••••rg'] },
    { ruleId: 'api-key-openai', category: 'api_key', action: 'block', count: 1 },
  ],
  wouldRedact: 2,
  categories: ['email', 'api_key'],
};

const ENTRIES = {
  // ---- Settings › General
  'settings__data': {},
  'settings__data-lower': { run: scrollTo(1100) },
  'settings__data-bottom': { run: scrollTo(null) },
  'settings__error': {},
  'settings__update-available': { init: desktopBridge, path: '/#/settings', run: scrollTo(null) },
  'settings__update-downloading': {
    init: desktopBridge,
    path: '/#/settings',
    run: async (page, h) => {
      await h.scroll(null);
      await page.getByRole('button', { name: '立即更新' }).first().click({ timeout: 5000 });
      await page.waitForTimeout(300);
    },
  },
  'settings__proxy-custom': { run: scrollTo(560) },
  'settings__lan-pending': {},
  'settings__listen-sheet': {
    run: async (page, h) => {
      await page.getByRole('radio', { name: '局域网' }).or(page.getByText('局域网', { exact: true })).first().click();
      await page.waitForTimeout(600);
    },
  },

  // ---- Settings › Notifications
  'settings__notifications': {},
  'settings__notifications-empty': {},
  'settings__webhook-sheet': { run: async (page, h) => h.click('添加 Webhook', { wait: 600 }) },
  'settings__webhook-edit': { run: webhookMenu(1, '编辑 Webhook') },
  'settings__webhook-deliveries': { run: webhookMenu(0, '投递记录') },
  'settings__rule-sheet': { run: async (page, h) => h.click('添加规则', { wait: 600 }) },

  // ---- Settings › Remote access
  'settings__remote-off': {},
  'settings__remote-downloading': {},
  'settings__remote-running': {},
  'settings__remote-error': {},
  'settings__remote-named': {},
  'settings__remote-admin': {},
  'settings__remote-unsupported': {},

  // ---- Settings › Tray panel (desktop shell only: needs the bridge's tray API)
  'settings__tray': { init: desktopBridge, path: '/#/settings/tray' },

  // ---- Privacy
  'privacy__overview': {},
  'privacy__overview-empty': {},
  'privacy__logs': {},
  'privacy__logs-empty': {},
  'privacy__logs-paged': {},
  'privacy__rules': {},
  'privacy__rules-sheet': { run: async (page, h) => h.click('添加规则', { wait: 600 }) },
  'privacy__tester': {
    // The C# scene injects the result (DryRunTesterView.ShowResult) with an empty text box: run a dry run answered with the same
    // result, then clear the text again.
    patch: (rec) => {
      rec.calls.push({ method: 'POST', url: '/api/privacy/dry-run', status: 200, contentType: 'application/json', body: JSON.stringify(DRY_RUN) });
      return rec;
    },
    run: async (page, h) => {
      const box = page.getByPlaceholder('粘贴一段请求文本或 JSON…');
      await box.fill('mail john@example.com / anna@example.org, key sk-proj-xxxxxxxxxxxxxxxxxxxx');
      await page.getByRole('button', { name: '检测', exact: true }).first().click({ timeout: 5000 });
      await page.waitForTimeout(600);
      await box.fill('');
      await page.waitForTimeout(200);
    },
  },
  'privacy__error': {},
  'privacy__disabled': {},
};

// capture-web.mjs writes astra.accent = 'blue', which the web reads as the CSS colour `blue` (#0000ff), not the "blue" preset
// (#007aff, web/src/shell/appearance.tsx ACCENT_PRESETS) the desktop shows: store the preset's colour.
export const STEPS = Object.fromEntries(Object.entries(ENTRIES).map(([k, v]) => [k, { ...v, storage: { 'astra.accent': '#007aff', ...(v.storage ?? {}) } }]));

export const DESKTOP_ONLY = {};
export const NOT_MIRRORED = {};
