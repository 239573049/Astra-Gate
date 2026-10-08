/**
 * Pure helpers for the tray panel (the popover that opens from the tray / menu-bar icon): where it goes on
 * screen, which commands the panel renderer may send, and the state the main process pushes to it.
 */
import type { ServiceActivity } from './trayMenu';

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Fixed panel width in CSS px; the height follows the content (the renderer reports it). */
export const TRAY_PANEL_WIDTH = 360;
/** Height used before the renderer has measured itself. */
export const TRAY_PANEL_DEFAULT_HEIGHT = 520;
/** Gap between the panel and the icon / screen edge. */
const GAP = 6;
/** A tray icon within this distance of a work-area edge sits on the taskbar / menu bar along that edge. */
const EDGE_SLOP = 4;

/** Service state the panel header and footer show; pushed by the main process. */
export interface TrayPanelState {
  running: boolean;
  port: number | null;
  activity: ServiceActivity | null;
  apiVersionMismatch: boolean;
  /** Non-null when an update check found something newer. */
  updateAvailable: string | null;
}

/** Actions the panel renderer asks the main process to perform (untrusted IPC input; see isTrayPanelCommand). */
export const TRAY_PANEL_COMMANDS = [
  'open',
  'open-settings',
  'start',
  'stop',
  'restart',
  'quit',
  'hide',
  'menu',
  'check-updates',
] as const;
export type TrayPanelCommand = (typeof TRAY_PANEL_COMMANDS)[number];

export function isTrayPanelCommand(value: unknown): value is TrayPanelCommand {
  return typeof value === 'string' && (TRAY_PANEL_COMMANDS as readonly string[]).includes(value);
}

/** Validates the menu-bar title / tooltip detail the panel renderer computes (short single-line strings). */
export function sanitizeTrayTitle(raw: unknown): { title: string; detail: string } | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const o = raw as Record<string, unknown>;
  const clean = (v: unknown, max: number) => (typeof v === 'string' ? v.replace(/[\r\n\t]+/g, ' ').trim().slice(0, max) : '');
  return { title: clean(o.title, 24), detail: clean(o.detail, 80) };
}

/**
 * Panel bounds next to the tray icon. The icon's position relative to the display's work area tells where
 * the menu bar / taskbar is: above the work area (macOS menu bar, top taskbar) the panel drops down; below
 * it (Windows default) the panel opens upward; on a left / right taskbar it opens sideways. An anchor
 * inside the work area (Linux without icon bounds, so the cursor) opens below it, or above when it does
 * not fit. The panel is always clamped into the work area and never taller than it.
 */
export function trayPanelBounds(anchor: Rect, workArea: Rect, size: { width: number; height: number }): Rect {
  const width = Math.min(size.width, workArea.width - 2 * GAP);
  const height = Math.max(1, Math.min(size.height, workArea.height - 2 * GAP));
  const left = workArea.x;
  const top = workArea.y;
  const right = workArea.x + workArea.width;
  const bottom = workArea.y + workArea.height;
  const cx = anchor.x + anchor.width / 2;
  const cy = anchor.y + anchor.height / 2;
  const clampX = (x: number) => Math.min(Math.max(x, left + GAP), right - width - GAP);
  const clampY = (y: number) => Math.min(Math.max(y, top + GAP), bottom - height - GAP);

  let x: number;
  let y: number;
  if (anchor.y + anchor.height <= top + EDGE_SLOP) {
    x = clampX(cx - width / 2);
    y = top + GAP;
  } else if (anchor.y >= bottom - EDGE_SLOP) {
    x = clampX(cx - width / 2);
    y = bottom - height - GAP;
  } else if (anchor.x + anchor.width <= left + EDGE_SLOP) {
    x = left + GAP;
    y = clampY(cy - height / 2);
  } else if (anchor.x >= right - EDGE_SLOP) {
    x = right - width - GAP;
    y = clampY(cy - height / 2);
  } else {
    x = clampX(cx - width / 2);
    const below = anchor.y + anchor.height + GAP;
    y = below + height <= bottom - GAP ? below : clampY(anchor.y - height - GAP);
  }
  return { x: Math.round(x), y: Math.round(y), width: Math.round(width), height: Math.round(height) };
}
