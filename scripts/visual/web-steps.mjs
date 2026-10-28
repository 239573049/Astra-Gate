// The web-side half of every desktop scene (tests/Astra.Desktop.UiTests/Scenes.*.cs), keyed by the desktop scene name, for
// scripts/visual/capture-web.mjs's replay mode. The data is not here: it is the scene's recording (ASTRA_VISUAL_RECORD=1 →
// artifacts/visual/fixtures/<scene>.json). Here is only what the WEB must do to reach the same state the C# scene put the
// desktop page in — the route (default: the recording's route + param via webPathFor), the sidebar mode (default: the
// recording's), and `run(page, h)`: click to open a sheet / tab / dialog, type, scroll — mirroring the C# scene step by step.
//
// Entry shape  { path?, sidebar?: 'expanded'|'collapsed', storage?: {k: v}, settle?: ms, run?: async (page, h) => {}, after?: ms,
//                isolate?: selector, patch?: (recording) => recording, mouse?: [x, y], init?: fn | { content } }
//   init     an extra page init script (Playwright addInitScript: a function or { content }), e.g. a fake window.__ASTRA__
//            bridge; a bridge switches the SPA to HashRouter, so such an entry's path is '/#/…'.
//   isolate  the desktop scene hosts a component on its own (no shell): the web element is cut out and laid at the same
//            place (the shell's content area) on the page background, so the two frames line up.
//   patch    edits the recording before it is served (rare; e.g. a web-only call the desktop answers differently).
// DESKTOP_ONLY  scenes with no web counterpart (reason). NOT_MIRRORED  scenes that have one but cannot be reproduced yet (reason).
//
// The per-area tables live in scripts/visual/steps/<area>.mjs; this file merges them and holds the shared helpers.
import * as overview from './steps/overview-tokens.mjs';
import * as shell from './steps/shell-tray.mjs';
import * as providers from './steps/providers.mjs';
import * as accountsModels from './steps/accounts-models.mjs';
import * as requestsTraces from './steps/requests-traces.mjs';
import * as clientsPlugins from './steps/clients-plugins.mjs';
import * as settingsPrivacy from './steps/settings-privacy.mjs';

const areas = [overview, shell, providers, accountsModels, requestsTraces, clientsPlugins, settingsPrivacy];

export const STEPS = Object.assign({}, ...areas.map((a) => a.STEPS ?? {}));
export const DESKTOP_ONLY = Object.assign({}, ...areas.map((a) => a.DESKTOP_ONLY ?? {}));
export const NOT_MIRRORED = Object.assign({}, ...areas.map((a) => a.NOT_MIRRORED ?? {}));

/** The web path of a recorded desktop route (src/Astra.Desktop/Shell/AppRoute.cs RouteInfo + the page's Param). */
export function webPathFor(rec) {
  const p = rec.param ? encodeURIComponent(rec.param) : null;
  switch (rec.route) {
    case 'Overview': return '/';
    case 'Requests': return '/requests';
    case 'Traces': return rec.param ?? '/traces'; // the desktop param already is the web path (TraceRoutes)
    case 'Tokens': return '/tokens';
    case 'TokenDetail': return `/tokens/${p}`;
    case 'Clients': return p ? `/clients/${p}` : '/clients';
    case 'Providers': return rec.param === 'new' ? '/providers?new=1' : '/providers';
    case 'ProviderDetail': return `/providers/${p}`;
    case 'Models': return '/models';
    case 'ModelDetail': return `/models/${p}`;
    case 'Plugins': return p ? `/plugins/${p}` : '/plugins';
    case 'Privacy': return p ? `/privacy/${p}` : '/privacy';
    case 'SmartRouting': return '/smart-routing';
    case 'Settings': return p ? `/settings/${p}` : '/settings';
    default: return '/';
  }
}

/** Helpers handed to a step's run(page, h). Every one waits for what it acts on (Playwright auto-wait, 5 s). */
export function helpers(page) {
  const T = 5000;
  const h = {
    page,
    wait: (ms) => page.waitForTimeout(ms),
    /** Clicks the first visible element with exactly this text (or a Locator). */
    async click(target, opts = {}) {
      const loc = typeof target === 'string' ? page.getByText(target, { exact: opts.exact ?? true }).filter({ visible: true }).first() : target;
      await loc.click({ timeout: T, ...opts.click });
      await page.waitForTimeout(opts.wait ?? 300);
    },
    /** Clicks a role element by accessible name (button, tab, menuitem, link, checkbox, switch, option, row …). */
    async role(role, name, opts = {}) {
      const loc = page.getByRole(role, { name, exact: opts.exact ?? false }).filter({ visible: true }).nth(opts.nth ?? 0);
      await loc.click({ timeout: T });
      await page.waitForTimeout(opts.wait ?? 300);
    },
    /** Fills an input found by placeholder, label or a Locator. */
    async fill(target, text, opts = {}) {
      let loc = target;
      if (typeof target === 'string') {
        const byPh = page.getByPlaceholder(target, { exact: opts.exact ?? false }).filter({ visible: true });
        loc = (await byPh.count()) ? byPh.first() : page.getByLabel(target, { exact: opts.exact ?? false }).filter({ visible: true }).first();
      }
      await loc.fill(text, { timeout: T });
      await page.waitForTimeout(opts.wait ?? 300);
    },
    /** Waits for an open dialog (Sheet / ConfirmDialog / Radix dialog). */
    dialog: (name) => page.getByRole(name ? 'dialog' : 'dialog', name ? { name } : {}).filter({ visible: true }).first().waitFor({ timeout: T }),
    /**
     * Scrolls the page's scroller (the largest scrollable element, as the C# Scrolled() picks the ScrollViewer with the largest
     * extent) to `offset` px, or to the end when null. `within` narrows the search to a container (e.g. '[role=dialog]').
     */
    async scroll(offset = null, within = 'body') {
      await page.evaluate(([off, sel]) => {
        const root = document.querySelector(sel) ?? document.body;
        const all = [root, ...root.querySelectorAll('*')].filter((e) => {
          const s = getComputedStyle(e);
          return /(auto|scroll)/.test(s.overflowY) && e.scrollHeight > e.clientHeight + 1;
        });
        all.sort((a, b) => b.scrollHeight - a.scrollHeight);
        const el = all[0];
        if (el) el.scrollTop = off ?? el.scrollHeight;
      }, [offset, within]);
      await page.waitForTimeout(300);
    },
    async hover(target) {
      const loc = typeof target === 'string' ? page.getByText(target, { exact: true }).filter({ visible: true }).first() : target;
      await loc.hover({ timeout: T });
      await page.waitForTimeout(300);
    },
    /**
     * Lays the element matching `selector` (or a Locator) alone on the page background at the place the desktop's
     * AccountsHost puts a component: left 232, top 52, padding 20 16 40 40 (the shell's content area), width to the right edge.
     */
    async isolate(selector, box = {}) {
      const handle = typeof selector === 'string' ? await page.locator(selector).first().elementHandle({ timeout: T }) : await selector.elementHandle({ timeout: T });
      await page.evaluate(([el, b]) => {
        const host = document.createElement('div');
        host.id = '__iso';
        const bg = getComputedStyle(document.documentElement).getPropertyValue('--surface-muted') || getComputedStyle(document.body).backgroundColor;
        Object.assign(host.style, {
          position: 'fixed', left: '0', top: '0', right: '0', bottom: '0', zIndex: '40', overflow: 'hidden',
          background: b.background ?? (bg.trim() || 'var(--bg)'),
        });
        const inner = document.createElement('div');
        Object.assign(inner.style, {
          position: 'absolute', left: `${b.left ?? 232}px`, top: `${b.top ?? 52}px`, right: '0', bottom: '0',
          padding: b.padding ?? '16px 40px 40px 20px', boxSizing: 'border-box', overflow: 'hidden',
        });
        if (b.width) Object.assign(inner.style, { right: 'auto', width: `${b.width}px` });
        const clone = el; // move, not clone: keeps React state and listeners alive
        inner.appendChild(clone);
        host.appendChild(inner);
        // inside #root: React delegates events from its root, so a node moved under <body> no longer gets clicks or input
        (document.getElementById('root') ?? document.body).appendChild(host);
        // z 40 keeps Radix dialogs (50) above it; popper menus are raised over it too
        for (const d of document.querySelectorAll('[data-radix-popper-content-wrapper]')) if (!host.contains(d)) d.style.zIndex = '60';
      }, [handle, box]);
      await page.waitForTimeout(300);
    },
  };
  return h;
}
