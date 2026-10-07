/**
 * Pure helpers for the liquid-glass window chrome (plan §9.1 / §10): window options per platform,
 * system accent color normalization, menu commands, and context-menu input validation.
 */

/** Sidebar order; ⌘1–⌘6 (Ctrl on Windows/Linux) map onto it. */
export const NAV_ORDER = ['overview', 'requests', 'clients', 'providers', 'models', 'settings'] as const;
export type NavTarget = (typeof NAV_ORDER)[number];

/**
 * Hash route for a nav target (the desktop renderer uses HashRouter; see web/src/App.tsx). Used to
 * open a not-yet-created window directly on a page.
 */
export function navHash(target: NavTarget): string {
  return target === 'overview' ? '#/' : `#/${target}`;
}

/** Commands the native menu (or main process) sends to the renderer via `onMenuCommand`. */
export type MenuCommand =
  | `nav:${NavTarget}`
  | 'find'
  | 'refresh'
  | 'new-provider'
  | 'toggle-sidebar';

export type ThemeSource = 'system' | 'light' | 'dark';

/** Toolbar height in CSS px; the drag region and the Windows title-bar overlay use the same value. */
export const TOOLBAR_HEIGHT = 52;

/** Traffic lights sit inside the floating glass sidebar (8px inset + 12px padding). */
export const TRAFFIC_LIGHT_POSITION = { x: 20, y: 20 } as const;

export interface ChromeOptions {
  titleBarStyle?: 'hiddenInset' | 'hidden';
  trafficLightPosition?: { x: number; y: number };
  titleBarOverlay?: { color: string; symbolColor: string; height: number };
  backgroundColor: string;
}

/** Window background shown before the renderer paints (avoids a white flash in dark mode). */
export function themeBackground(dark: boolean): string {
  return dark ? '#1C1C1E' : '#F2F2F7';
}

export function overlaySymbolColor(dark: boolean): string {
  return dark ? '#FFFFFF' : '#1D1D1F';
}

export function windowChromeOptions(platform: NodeJS.Platform, dark: boolean): ChromeOptions {
  const backgroundColor = themeBackground(dark);
  if (platform === 'darwin') {
    return { titleBarStyle: 'hiddenInset', trafficLightPosition: { ...TRAFFIC_LIGHT_POSITION }, backgroundColor };
  }
  if (platform === 'win32') {
    return {
      titleBarStyle: 'hidden',
      titleBarOverlay: { color: '#00000000', symbolColor: overlaySymbolColor(dark), height: TOOLBAR_HEIGHT },
      backgroundColor,
    };
  }
  // Linux: keep the system title bar; the renderer omits the drag region there.
  return { backgroundColor };
}

/**
 * `systemPreferences.getAccentColor()` returns "RRGGBBAA" (no '#'). Returns "#rrggbb", or null when
 * unavailable / malformed so the renderer falls back to CSS AccentColor or #007AFF.
 */
export function normalizeAccentColor(raw: string | null | undefined): string | null {
  if (typeof raw !== 'string') return null;
  const hex = raw.trim().replace(/^#/, '');
  if (!/^[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/.test(hex)) return null;
  return `#${hex.slice(0, 6).toLowerCase()}`;
}

export function isThemeSource(value: unknown): value is ThemeSource {
  return value === 'system' || value === 'light' || value === 'dark';
}

export interface ContextMenuItem {
  id?: string;
  label?: string;
  enabled?: boolean;
  checked?: boolean;
  type?: 'normal' | 'separator' | 'checkbox';
}

const MAX_MENU_ITEMS = 50;
const MAX_LABEL = 200;

/** Validates renderer-supplied context menu items (untrusted IPC input). Null = reject. */
export function sanitizeContextMenuItems(input: unknown): ContextMenuItem[] | null {
  if (!Array.isArray(input) || input.length === 0 || input.length > MAX_MENU_ITEMS) return null;
  const out: ContextMenuItem[] = [];
  for (const raw of input) {
    if (raw === null || typeof raw !== 'object') return null;
    const item = raw as Record<string, unknown>;
    if (item.type === 'separator') {
      out.push({ type: 'separator' });
      continue;
    }
    if (typeof item.id !== 'string' || item.id.length === 0 || item.id.length > MAX_LABEL) return null;
    if (typeof item.label !== 'string' || item.label.length === 0 || item.label.length > MAX_LABEL) return null;
    const type = item.type === 'checkbox' ? 'checkbox' : 'normal';
    out.push({
      id: item.id,
      label: item.label,
      type,
      enabled: item.enabled === undefined ? true : item.enabled === true,
      checked: type === 'checkbox' ? item.checked === true : undefined,
    });
  }
  return out;
}

/** Menu labels; Chinese when the OS locale is zh-*. */
export function menuLabels(locale: string) {
  const zh = locale.toLowerCase().startsWith('zh');
  return {
    file: zh ? '文件' : 'File',
    view: zh ? '显示' : 'View',
    help: zh ? '帮助' : 'Help',
    settings: zh ? '设置…' : 'Settings…',
    newProvider: zh ? '新建提供商…' : 'New Provider…',
    find: zh ? '搜索' : 'Find',
    refresh: zh ? '刷新数据' : 'Reload Data',
    toggleSidebar: zh ? '显示/隐藏侧边栏' : 'Toggle Sidebar',
    openLogs: zh ? '打开日志目录' : 'Open Logs Folder',
    nav: {
      overview: zh ? '概览' : 'Overview',
      requests: zh ? '请求日志' : 'Requests',
      clients: zh ? '客户端' : 'Clients',
      providers: zh ? '提供商' : 'Providers',
      models: zh ? '模型' : 'Models',
      settings: zh ? '设置' : 'Settings',
    } satisfies Record<NavTarget, string>,
  };
}
