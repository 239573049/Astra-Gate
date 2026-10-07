// Runtime detection of the host shell (plan §9.1). The desktop preload injects window.__ASTRA__;
// in a normal browser it is absent and the shell is "web".

export type ThemeSource = 'system' | 'light' | 'dark';
export type NavTarget = 'overview' | 'requests' | 'clients' | 'providers' | 'models' | 'privacy' | 'settings';
export type MenuCommand =
  | `nav:${NavTarget}`
  | 'find'
  | 'refresh'
  | 'new-provider'
  | 'toggle-sidebar';

export interface ContextMenuItem {
  id?: string;
  label?: string;
  enabled?: boolean;
  checked?: boolean;
  type?: 'normal' | 'separator' | 'checkbox';
}

/** Mirrors desktop/src/preload.ts. */
export interface DesktopBridge {
  apiBase: string;
  shell: 'desktop';
  desktop: true;
  platform: string;
  apiVersionMismatch: boolean;
  version: string;
  accentColor: string | null;
  getAccentColor(): Promise<string | null>;
  onAccentColorChanged(cb: (color: string | null) => void): () => void;
  setThemeSource(source: ThemeSource): Promise<boolean>;
  openPath(p: string): Promise<boolean>;
  revealLogs(): Promise<boolean>;
  startService(): Promise<{ ok: boolean; error?: string | null }>;
  showContextMenu(items: ContextMenuItem[]): Promise<string | null>;
  onMenuCommand(cb: (command: MenuCommand) => void): () => void;
}

declare global {
  interface Window {
    __ASTRA__?: DesktopBridge;
  }
}

export type Shell = 'desktop' | 'web';

export function getBridge(): DesktopBridge | undefined {
  return typeof window === 'undefined' ? undefined : window.__ASTRA__;
}

export function detectShell(bridge = getBridge()): Shell {
  return bridge?.shell === 'desktop' ? 'desktop' : 'web';
}

/** "darwin" | "win32" | "linux" | "other" — from the bridge, else guessed from the browser. */
export function detectPlatform(bridge = getBridge(), nav: Navigator | undefined = globalThis.navigator): string {
  if (bridge?.platform) return bridge.platform;
  const ua = `${nav?.userAgent ?? ''} ${nav?.platform ?? ''}`.toLowerCase();
  if (ua.includes('mac')) return 'darwin';
  if (ua.includes('win')) return 'win32';
  if (ua.includes('linux')) return 'linux';
  return 'other';
}

/** Chromium renders SVG filters in backdrop-filter (liquid glass refraction); Safari/Firefox don't. */
export function supportsRefraction(nav: Navigator | undefined = globalThis.navigator): boolean {
  const ua = nav?.userAgent ?? '';
  return /Chrome\/|Electron\//.test(ua);
}

export const shell: Shell = detectShell();
export const platform: string = detectPlatform();
export const isDesktop = shell === 'desktop';
export const isMac = platform === 'darwin';

/**
 * Collapsed sidebar width. The macOS traffic lights span x 20…74 of the window (desktop/src/shared/chrome.ts
 * TRAFFIC_LIGHT_POSITION, three 14px buttons 20px apart); the rail starts at x 8, so 78px leaves 12px
 * of glass on both sides and centers the nav icons under the buttons.
 */
export function sidebarRailWidth(desktop = isDesktop, hostPlatform = platform): number {
  return desktop && hostPlatform === 'darwin' ? 78 : 68;
}

/** Writes data-shell / data-platform (CSS hooks for window chrome) and the refraction flag. */
export function applyShellAttributes(root: HTMLElement = document.documentElement): void {
  root.dataset.shell = shell;
  root.dataset.platform = platform;
  if (supportsRefraction()) root.classList.add('refract');
}

/** Modifier label for shortcut hints. */
export const modKey = isMac ? '⌘' : 'Ctrl+';
