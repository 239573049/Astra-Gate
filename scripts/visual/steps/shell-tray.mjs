// Web steps for the shell, kit and tray scenes (tests/Astra.Desktop.UiTests/Scenes.cs, Scenes.Shell.cs, Scenes.Tray.cs).
// See scripts/visual/web-steps.mjs for the entry shape and helpers.
//
// Shell scenes are the Tokens route in the app shell; the recording carries the per-scene overrides (outdated clients,
// running tunnel). shell__down*: the desktop shows its "service down" state (ConnectionViewModel.AssumeDown) — the web
// shell shows it when /api/health fails, so the recording is patched to fail health, and a fake Linux desktop bridge is
// injected so the copy is the desktop variant (banner.downDesktop + "启动服务"), as the desktop app shows it (Linux: no
// traffic-light inset, no Windows caption reserve, 68 px rail — the same geometry as the plain web shell).
//
// Tray scenes: the desktop renders TrayPanelView (popup variant, close button shown) 360 px wide, centred, 40 px from the
// top, on the muted surface (Scenes.Tray.cs TrayScene). The web panel is the /tray route (TrayPanelPage), which reads its
// prefs and service state from the desktop bridge: a fake bridge supplies the scene's TrayPrefs / TrayPanelState, then
// the card is laid out like the desktop's (see trayLayout).

/** A desktop bridge good enough for the web shell and the tray panel (web/src/shell/bridge.ts DesktopBridge). */
function bridgeInit([prefs, state]) {
  const off = () => () => {};
  window.__ASTRA__ = {
    apiBase: '',
    shell: 'desktop',
    desktop: true,
    platform: 'linux',
    apiVersionMismatch: false,
    version: '0.0.0-visual',
    accentColor: null,
    getAccentColor: async () => null,
    onAccentColorChanged: off,
    setThemeSource: async () => true,
    openPath: async () => true,
    revealLogs: async () => true,
    startService: async () => ({ ok: false, error: null }),
    restartService: async () => ({ ok: false, error: null }),
    loginItem: { get: async () => ({ supported: false, enabled: false }), set: async () => ({ supported: false, enabled: false }), onChanged: off },
    updates: { check: async () => ({ message: '', action: 'none' }), apply: async () => 'none' },
    showContextMenu: async () => null,
    onMenuCommand: off,
    tray: {
      getPrefs: async () => prefs,
      setPrefs: async (p) => p,
      onPrefsChanged: off,
      getState: async () => state,
      onStateChanged: off,
      command: async () => {},
      setTitle() {},
      resize() {},
      onVisibilityChanged: off,
    },
  };
}

const ALL_SECTIONS = ['overview', 'cost', 'chart', 'topModel', 'compareYesterday', 'cacheHitRate', 'quota', 'clients'];
/** TrayPrefs.Default (src/Astra.Desktop.Core/Tray) = DEFAULT_TRAY_PREFS (web/src/components/tray/useTrayBridge.ts). */
const DEFAULT_PREFS = {
  title: 'requests',
  sections: { overview: true, cost: true, chart: true, topModel: true, compareYesterday: false, cacheHitRate: false, quota: true, clients: false },
};
const ALL_PREFS = { title: 'requests', sections: Object.fromEntries(ALL_SECTIONS.map((s) => [s, true])) };
const OVERVIEW_ONLY = { title: 'requests', sections: Object.fromEntries(ALL_SECTIONS.map((s) => [s, s === 'overview'])) };
const RUNNING = { running: true, port: 17321, activity: null, apiVersionMismatch: false, updateAvailable: null };

/** The bridge's init script for one tray scene (capture-web runs `init` before the page's scripts). */
const bridge = (prefs, state) => ({ content: `(${bridgeInit.toString()})(${JSON.stringify([prefs, state])});` });
const trayInit = (prefs, state) => ({ init: bridge(prefs, state) });

/** Lays the tray card out like the desktop scene: 360 px wide, centred, 40 px from the top, on the muted surface. */
async function trayLayout(page) {
  await page.evaluate(() => {
    const card = document.querySelector('[data-tray-part="header"]')?.parentElement;
    if (!card) return;
    const muted = getComputedStyle(document.documentElement).getPropertyValue('--surface-muted').trim();
    // only the root paints (the token can be translucent: painting body too would darken it)
    document.documentElement.style.background = muted;
    document.body.style.background = 'transparent';
    document.body.style.paddingTop = '40px';
    const wrap = card.parentElement;
    Object.assign(wrap.style, { height: 'auto', width: '360px', margin: '0 auto' });
    card.classList.remove('h-full');
    card.style.height = 'auto';
    card.style.maxHeight = 'none';
    const scroller = card.querySelector('.scroll');
    if (scroller) Object.assign(scroller.style, { flex: 'none', overflow: 'visible' });
  });
  await page.waitForTimeout(300);
}

function tray(prefs, state, tab) {
  return {
    path: '/#/tray', // a bridge makes the SPA use HashRouter (web/src/App.tsx)
    ...trayInit(prefs, state),
    async run(page, h) {
      if (tab) await h.role('tab', tab);
      await trayLayout(page);
    },
  };
}

/** Makes the health probe fail (the desktop scene's ConnectionViewModel.AssumeDown). */
function healthDown(rec) {
  rec.calls = (rec.calls ?? []).filter((c) => !c.url.startsWith('/api/health'));
  rec.overrides = { ...(rec.overrides ?? {}), '/api/health': { status: 503, body: '{"error":"无法连接到 127.0.0.1:17321（连接被拒绝）"}' } };
  return rec;
}

const down = { path: '/#/tokens', init: bridge(DEFAULT_PREFS, { ...RUNNING, running: false, port: null }), patch: healthDown, settle: 7000 };

// capture-web.mjs stores astra.accent = 'blue', which web/src/shell/appearance.tsx takes as a CSS colour (#0000ff), not the
// 'blue' preset (#007aff = ThemeService.DefaultAccent): store the hex until the shared script does.
const ACCENT = { 'astra.accent': '#007aff' };
const withAccent = (table) => Object.fromEntries(Object.entries(table).map(([k, v]) => [k, { ...v, storage: { ...ACCENT, ...(v.storage ?? {}) } }]));

export const STEPS = withAccent({
  'shell__expanded': {},
  'shell__collapsed': {},
  'shell__clients-outdated': {},
  'shell__remote-on': {},
  'shell__remote-on-rail': {},
  // health is polled every 5 s with retry off: the banner opens after the first failure (settle covers it)
  'shell__down': { ...down },
  'shell__down-rail': { ...down },

  'tray__all': tray(ALL_PREFS, RUNNING),
  'tray__default': tray(DEFAULT_PREFS, RUNNING),
  'tray__overview-only': tray(OVERVIEW_ONLY, RUNNING),
  'tray__stopped': tray(DEFAULT_PREFS, { ...RUNNING, running: false, port: null }),
  'tray__starting': tray(DEFAULT_PREFS, { running: false, port: null, activity: 'starting', apiVersionMismatch: false, updateAvailable: null }),
  'tray__update-available': tray(DEFAULT_PREFS, { ...RUNNING, updateAvailable: 'yes' }),
  'tray__mismatch': tray(DEFAULT_PREFS, { ...RUNNING, apiVersionMismatch: true }),
  'tray__quota-missing': tray(DEFAULT_PREFS, RUNNING),
  'tray__quota-tab': tray(ALL_PREFS, RUNNING, '额度'),
  'tray__clients-tab': tray(ALL_PREFS, RUNNING, '客户端'),
});

export const DESKTOP_ONLY = {
  'kit__gallery': 'KitGallerySmoke.Build(): the desktop kit gallery (every kit control on one scroll page); the web has no gallery page.',
  'kit__buttons': 'A bare panel of Arc buttons (sizes x variants) built in C#; the web has no page that shows that grid.',
  'kit__empty-cjk': 'Four EmptyState/ErrorState cards with test copy (incl. English) laid out in a C# grid; no web page shows that set.',
  'kit__sheet-640': 'A C#-built test Sheet ("宽表单", SheetWidth 640) over Tokens; no web page opens a sheet with that content.',
  'kit__sheet-780': 'A C#-built test Sheet ("宽表单", SheetWidth 780) over Tokens; no web page opens a sheet with that content.',
  'kit__confirm': 'ConfirmDialog.Build with fixed test copy over Tokens (the real token-delete confirm is tokens__delete-confirm).',
  'kit__error-toast': 'ErrorToasts.Show with a fixed test message; a web error toast needs a failing mutation (no scene reproduces it).',
};

export const NOT_MIRRORED = {};
