import type { MenuItemConstructorOptions } from 'electron';
import { describe, expect, it, vi } from 'vitest';

import { navHash, NAV_ORDER } from '../src/shared/chrome';
import { DEFAULT_TRAY_PREFS, parsePrefs, parseTrayPrefs } from '../src/shared/prefs';
import {
  buildTrayMenuTemplate,
  trayStateKey,
  trayTooltip,
  type TrayCallbacks,
  type TrayMenuEnv,
  type TrayState,
} from '../src/shared/trayMenu';
import { isTrayPanelCommand, sanitizeTrayTitle, trayPanelBounds } from '../src/shared/trayPanel';

function state(over: Partial<TrayState> = {}): TrayState {
  return {
    running: true,
    port: 17321,
    apiVersionMismatch: false,
    clients: [],
    providers: [],
    updateAvailable: null,
    activity: null,
    openAtLogin: false,
    ...over,
  };
}

function callbacks(): TrayCallbacks {
  return {
    onOpenWindow: vi.fn(),
    onShowPanel: vi.fn(),
    onNavigate: vi.fn(),
    onStartService: vi.fn(),
    onStopService: vi.fn(),
    onRestartService: vi.fn(),
    onCopyApiAddress: vi.fn(),
    onOpenInBrowser: vi.fn(),
    onOpenLogs: vi.fn(),
    onSwitchProvider: vi.fn(),
    onToggleOpenAtLogin: vi.fn(),
    onCheckUpdates: vi.fn(),
    onQuit: vi.fn(),
  };
}

const mac: TrayMenuEnv = { platform: 'darwin', locale: 'en-US', version: '1.2.3' };
const win: TrayMenuEnv = { platform: 'win32', locale: 'zh-CN', version: '1.2.3' };

function find(items: MenuItemConstructorOptions[], label: string): MenuItemConstructorOptions {
  const item = items.find((i) => i.label === label);
  if (!item) throw new Error(`no menu item "${label}" in ${items.map((i) => i.label).join(', ')}`);
  return item;
}

function click(item: MenuItemConstructorOptions): void {
  (item.click as () => void)();
}

describe('buildTrayMenuTemplate', () => {
  it('shows version, status, and ends with Quit (⌘Q on macOS)', () => {
    const items = buildTrayMenuTemplate(state(), callbacks(), mac);
    expect(items[0]).toMatchObject({ label: 'Astra 1.2.3', enabled: false });
    expect(items[1]).toMatchObject({ label: 'Service running on port 17321', enabled: false });
    expect(items.at(-1)).toMatchObject({ label: 'Quit Astra', accelerator: 'Command+Q' });
  });

  it('uses Chinese labels and no accelerator on Windows', () => {
    const items = buildTrayMenuTemplate(state({ running: false, port: null }), callbacks(), win);
    expect(items[1]!.label).toBe('服务未运行');
    find(items, '启动服务');
    expect(items.at(-1)).toMatchObject({ label: '退出 Astra' });
    expect(items.at(-1)!.accelerator).toBeUndefined();
  });

  it('offers stop + restart while running and start while stopped', () => {
    const cb = callbacks();
    const running = buildTrayMenuTemplate(state(), cb, mac);
    click(find(running, 'Stop Service'));
    click(find(running, 'Restart Service'));
    expect(cb.onStopService).toHaveBeenCalledOnce();
    expect(cb.onRestartService).toHaveBeenCalledOnce();
    expect(running.some((i) => i.label === 'Start Service')).toBe(false);

    const stopped = buildTrayMenuTemplate(state({ running: false, port: null }), cb, mac);
    expect(stopped.some((i) => i.label === 'Stop Service')).toBe(false);
    expect(find(stopped, 'Copy API Address').enabled).toBe(false);
    expect(find(stopped, 'Open in Browser').enabled).toBe(false);
  });

  it('disables service items and shows progress during a transition', () => {
    const items = buildTrayMenuTemplate(state({ activity: 'restarting' }), callbacks(), mac);
    expect(items[1]!.label).toBe('Restarting service…');
    expect(find(items, 'Stop Service').enabled).toBe(false);
    expect(find(items, 'Restart Service').enabled).toBe(false);
  });

  it('offers the tray panel in the menu only on Linux', () => {
    const cb = callbacks();
    const linux: TrayMenuEnv = { platform: 'linux', locale: 'en', version: '1.2.3' };
    click(find(buildTrayMenuTemplate(state(), cb, linux), 'Show Tray Panel'));
    expect(cb.onShowPanel).toHaveBeenCalledOnce();
    expect(buildTrayMenuTemplate(state(), cb, mac).some((i) => i.label === 'Show Tray Panel')).toBe(false);
    expect(buildTrayMenuTemplate(state(), cb, win).some((i) => i.label === '显示托盘面板')).toBe(false);
  });

  it('navigates to every page from the Go To submenu', () => {
    const cb = callbacks();
    const goTo = find(buildTrayMenuTemplate(state(), cb, mac), 'Go To');
    const sub = goTo.submenu as MenuItemConstructorOptions[];
    expect(sub).toHaveLength(NAV_ORDER.length);
    click(sub[1]!);
    expect(cb.onNavigate).toHaveBeenCalledWith('requests');
  });

  it('lists enabled clients with their bound provider', () => {
    const cb = callbacks();
    const s = state({
      clients: [
        { kind: 'claude-code', name: 'Claude Code', enabled: true, providerId: 'p2' },
        { kind: 'codex', name: 'Codex', enabled: false, providerId: null },
      ],
      providers: [
        { id: 'p1', name: 'OpenAI', enabled: true },
        { id: 'p2', name: 'DeepSeek', enabled: false },
      ],
    });
    const macItems = buildTrayMenuTemplate(s, cb, mac);
    const client = find(macItems, 'Claude Code');
    expect(client.sublabel).toBe('DeepSeek');
    expect(client.enabled).toBe(true);
    expect(macItems.some((i) => i.label === 'Codex')).toBe(false);
    const sub = client.submenu as MenuItemConstructorOptions[];
    expect(sub.map((i) => [i.label, i.checked])).toEqual([
      ['OpenAI', false],
      ['DeepSeek (disabled)', true],
    ]);
    click(sub[0]!);
    expect(cb.onSwitchProvider).toHaveBeenCalledWith('claude-code', 'p1');

    // Windows has no sublabels: the binding goes inline.
    find(buildTrayMenuTemplate(s, cb, win), 'Claude Code · DeepSeek');
    // Switching needs the server.
    expect(find(buildTrayMenuTemplate({ ...s, running: false }, cb, mac), 'Claude Code').enabled).toBe(false);
  });

  it('shows the launch-at-login checkbox only where supported', () => {
    const cb = callbacks();
    const item = find(buildTrayMenuTemplate(state({ openAtLogin: true }), cb, mac), 'Launch at Login');
    expect(item).toMatchObject({ type: 'checkbox', checked: true });
    click(item);
    expect(cb.onToggleOpenAtLogin).toHaveBeenCalledWith(false);
    const none = buildTrayMenuTemplate(state({ openAtLogin: null }), cb, mac);
    expect(none.some((i) => i.label === 'Launch at Login')).toBe(false);
  });

  it('surfaces update availability and API mismatch', () => {
    const cb = callbacks();
    const items = buildTrayMenuTemplate(state({ updateAvailable: 'yes', apiVersionMismatch: true }), cb, mac);
    find(items, 'API version mismatch — run "astra update"');
    click(find(items, 'Update Available…'));
    expect(cb.onCheckUpdates).toHaveBeenCalledOnce();
  });
});

describe('trayTooltip / trayStateKey', () => {
  it('describes the service state', () => {
    expect(trayTooltip(state(), 'en')).toBe('Astra — running on port 17321');
    expect(trayTooltip(state({ running: false }), 'zh-CN')).toBe('Astra — 服务未运行');
    expect(trayTooltip(state({ activity: 'starting' }), 'en')).toBe('Astra — Starting service…');
  });

  it('changes only when the state does', () => {
    expect(trayStateKey(state())).toBe(trayStateKey(state()));
    expect(trayStateKey(state())).not.toBe(trayStateKey(state({ openAtLogin: true })));
  });
});

describe('navHash', () => {
  it('maps nav targets onto the renderer hash routes', () => {
    expect(navHash('overview')).toBe('#/');
    expect(navHash('settings')).toBe('#/settings');
  });
});

describe('parsePrefs', () => {
  it('falls back to defaults on missing or malformed input', () => {
    const defaults = { closeHintShown: false, tray: DEFAULT_TRAY_PREFS };
    expect(parsePrefs(null)).toEqual(defaults);
    expect(parsePrefs('nope')).toEqual(defaults);
    expect(parsePrefs('[1]')).toEqual(defaults);
    expect(parsePrefs('{"closeHintShown":true}')).toEqual({ ...defaults, closeHintShown: true });
  });

  it('keeps valid tray fields and defaults the rest one by one', () => {
    const tray = parseTrayPrefs({ title: 'cost', sections: { overview: false, quota: 'yes', bogus: true } });
    expect(tray.title).toBe('cost');
    expect(tray.sections.overview).toBe(false);
    expect(tray.sections.quota).toBe(DEFAULT_TRAY_PREFS.sections.quota);
    expect(Object.keys(tray.sections)).not.toContain('bogus');
    expect(parseTrayPrefs({ title: 'everything' }).title).toBe(DEFAULT_TRAY_PREFS.title);
    expect(parseTrayPrefs(null)).toEqual(DEFAULT_TRAY_PREFS);
    // Parsing never hands out the shared default object.
    expect(parseTrayPrefs(null).sections).not.toBe(DEFAULT_TRAY_PREFS.sections);
  });
});

describe('tray panel', () => {
  const size = { width: 360, height: 500 };

  it('drops down from a macOS menu-bar icon, centered on it', () => {
    const workArea = { x: 0, y: 25, width: 1512, height: 920 };
    const b = trayPanelBounds({ x: 1200, y: 0, width: 30, height: 24 }, workArea, size);
    expect(b).toEqual({ x: 1035, y: 31, width: 360, height: 500 });
  });

  it('opens upward from a bottom taskbar and stays on screen at the corner', () => {
    const workArea = { x: 0, y: 0, width: 1920, height: 1040 };
    const b = trayPanelBounds({ x: 1880, y: 1044, width: 24, height: 32 }, workArea, size);
    expect(b).toEqual({ x: 1554, y: 534, width: 360, height: 500 });
  });

  it('opens sideways from a left taskbar', () => {
    const workArea = { x: 48, y: 0, width: 1872, height: 1080 };
    const b = trayPanelBounds({ x: 8, y: 1000, width: 32, height: 32 }, workArea, size);
    expect(b).toEqual({ x: 54, y: 574, width: 360, height: 500 });
  });

  it('places below a cursor anchor, or above when there is no room', () => {
    const workArea = { x: 0, y: 0, width: 1920, height: 1080 };
    expect(trayPanelBounds({ x: 500, y: 100, width: 0, height: 0 }, workArea, size).y).toBe(106);
    expect(trayPanelBounds({ x: 500, y: 1000, width: 0, height: 0 }, workArea, size).y).toBe(494);
  });

  it('never grows taller than the work area', () => {
    const workArea = { x: 0, y: 25, width: 1440, height: 400 };
    const b = trayPanelBounds({ x: 1200, y: 0, width: 30, height: 24 }, workArea, { width: 360, height: 900 });
    expect(b.height).toBe(388);
    expect(b.y).toBe(31);
  });

  it('validates renderer input', () => {
    expect(isTrayPanelCommand('restart')).toBe(true);
    expect(isTrayPanelCommand('rm -rf')).toBe(false);
    expect(isTrayPanelCommand(1)).toBe(false);
    expect(sanitizeTrayTitle({ title: ' 7\n', detail: 'Today 7 calls' })).toEqual({ title: '7', detail: 'Today 7 calls' });
    expect(sanitizeTrayTitle({ title: 42 })).toEqual({ title: '', detail: '' });
    expect(sanitizeTrayTitle('x')).toBeNull();
  });
});
