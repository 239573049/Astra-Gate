import { contextBridge, ipcRenderer, type IpcRendererEvent } from 'electron';

import type { ContextMenuItem, MenuCommand, ThemeSource } from './shared/chrome';
import type { TrayPrefs } from './shared/prefs';
import type { TrayPanelCommand, TrayPanelState } from './shared/trayPanel';
import type { UiApplyOutcome, UpdateSummary } from './update';

/**
 * The renderer needs apiBase synchronously at page load, so the values are
 * passed as command-line switches via webPreferences.additionalArguments
 * (works with the Chromium sandbox; no sendSync round-trip needed).
 */
function readSwitchArg(name: string): string | undefined {
  const prefix = `--${name}=`;
  for (const arg of process.argv) {
    if (arg.startsWith(prefix)) return arg.slice(prefix.length);
  }
  return undefined;
}

const apiBase = readSwitchArg('astra-api-base') ?? 'http://127.0.0.1:17321';
const apiVersionMismatch = readSwitchArg('astra-api-mismatch') === '1';
const version = readSwitchArg('astra-version') ?? '';
const accentColor = readSwitchArg('astra-accent') || null;

/** Subscribes to a main→renderer channel; returns an unsubscribe function. */
function subscribe<T>(channel: string, cb: (value: T) => void): () => void {
  const listener = (_event: IpcRendererEvent, value: T) => cb(value);
  ipcRenderer.on(channel, listener);
  return () => {
    ipcRenderer.removeListener(channel, listener);
  };
}

const bridge = {
  apiBase,
  shell: 'desktop' as const,
  desktop: true as const,
  platform: process.platform,
  apiVersionMismatch,
  version,
  /** System accent color "#rrggbb" at window creation (null = unavailable). */
  accentColor,
  getAccentColor: () => ipcRenderer.invoke('astra:get-accent') as Promise<string | null>,
  onAccentColorChanged: (cb: (color: string | null) => void) => subscribe('astra:accent-changed', cb),
  setThemeSource: (source: ThemeSource) => ipcRenderer.invoke('astra:set-theme', source) as Promise<boolean>,
  openPath: (p: string) => ipcRenderer.invoke('astra:open-path', p) as Promise<boolean>,
  revealLogs: () => ipcRenderer.invoke('astra:reveal-logs') as Promise<boolean>,
  /** Starts (or adopts) the local server; used when the UI cannot reach it. */
  startService: () => ipcRenderer.invoke('astra:start-service') as Promise<{ ok: boolean; error: string | null }>,
  /** In-app update entry (Settings › About): check, and apply server/desktop updates. */
  updates: {
    check: () => ipcRenderer.invoke('astra:update-check') as Promise<UpdateSummary>,
    apply: () => ipcRenderer.invoke('astra:update-apply') as Promise<UiApplyOutcome>,
  },
  /** Native context menu; resolves with the clicked item id, or null when dismissed. */
  showContextMenu: (items: ContextMenuItem[]) =>
    ipcRenderer.invoke('astra:context-menu', items) as Promise<string | null>,
  onMenuCommand: (cb: (command: MenuCommand) => void) => subscribe('astra:menu-command', cb),
  /** Tray panel: its configuration (Settings › Tray panel) and the panel window's link to the main process. */
  tray: {
    getPrefs: () => ipcRenderer.invoke('astra:tray-prefs-get') as Promise<TrayPrefs>,
    /** Saves (after validation) and broadcasts to every window; resolves with what was stored. */
    setPrefs: (prefs: TrayPrefs) => ipcRenderer.invoke('astra:tray-prefs-set', prefs) as Promise<TrayPrefs>,
    onPrefsChanged: (cb: (prefs: TrayPrefs) => void) => subscribe('astra:tray-prefs-changed', cb),
    getState: () => ipcRenderer.invoke('astra:tray-state-get') as Promise<TrayPanelState>,
    onStateChanged: (cb: (state: TrayPanelState) => void) => subscribe('astra:tray-state-changed', cb),
    command: (command: TrayPanelCommand) => ipcRenderer.invoke('astra:tray-command', command) as Promise<void>,
    /** Panel window only: menu-bar title (macOS) and tooltip detail line. */
    setTitle: (title: string, detail: string) => ipcRenderer.send('astra:tray-title', { title, detail }),
    /** Panel window only: content height in CSS px, so the window fits it. */
    resize: (height: number) => ipcRenderer.send('astra:tray-panel-resize', height),
    onVisibilityChanged: (cb: (visible: boolean) => void) => subscribe('astra:tray-panel-visibility', cb),
  },
};

export type AstraBridge = typeof bridge;

contextBridge.exposeInMainWorld('__ASTRA__', bridge);
