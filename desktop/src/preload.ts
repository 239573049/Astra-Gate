import { contextBridge, ipcRenderer, type IpcRendererEvent } from 'electron';

import type { ContextMenuItem, MenuCommand, ThemeSource } from './shared/chrome';

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
  /** Native context menu; resolves with the clicked item id, or null when dismissed. */
  showContextMenu: (items: ContextMenuItem[]) =>
    ipcRenderer.invoke('astra:context-menu', items) as Promise<string | null>,
  onMenuCommand: (cb: (command: MenuCommand) => void) => subscribe('astra:menu-command', cb),
};

export type AstraBridge = typeof bridge;

contextBridge.exposeInMainWorld('__ASTRA__', bridge);
