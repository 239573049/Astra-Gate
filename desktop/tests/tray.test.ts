import type { MenuItemConstructorOptions } from 'electron';
import { describe, expect, it, vi } from 'vitest';

import { navHash, NAV_ORDER } from '../src/shared/chrome';
import { parsePrefs } from '../src/shared/prefs';
import {
  buildTrayMenuTemplate,
  trayStateKey,
  trayTooltip,
  type TrayCallbacks,
  type TrayMenuEnv,
  type TrayState,
} from '../src/shared/trayMenu';

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
    expect(parsePrefs(null)).toEqual({ closeHintShown: false });
    expect(parsePrefs('nope')).toEqual({ closeHintShown: false });
    expect(parsePrefs('[1]')).toEqual({ closeHintShown: false });
    expect(parsePrefs('{"closeHintShown":true}')).toEqual({ closeHintShown: true });
  });
});
