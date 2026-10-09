/**
 * Desktop-only UI preferences, stored as `desktop-prefs.json` in Electron's userData directory
 * (not under ~/.astra — nothing else reads them).
 */

import { EMPTY_REMINDER_STATE, parseReminderState, type UpdateReminderState } from './updateReminder';

/** What the menu bar shows next to the tray icon (macOS title; the icon tooltip on Windows / Linux). */
export const TRAY_TITLE_MODES = ['none', 'requests', 'tokens', 'cost', 'quota', 'alerts'] as const;
export type TrayTitleMode = (typeof TRAY_TITLE_MODES)[number];

/** Blocks of the tray panel, in display order. */
export const TRAY_SECTIONS = ['overview', 'cost', 'chart', 'topModel', 'compareYesterday', 'cacheHitRate', 'quota', 'clients'] as const;
export type TraySection = (typeof TRAY_SECTIONS)[number];

/** Tray panel configuration, edited on the Settings › Tray panel tab (mirrored in web/src/shell/bridge.ts). */
export interface TrayPrefs {
  title: TrayTitleMode;
  sections: Record<TraySection, boolean>;
}

export interface DesktopPrefs {
  /** The one-time "still running in the tray" hint was shown after the first window close. */
  closeHintShown: boolean;
  tray: TrayPrefs;
  /** Update reminder circuit-breaker state (see shared/updateReminder.ts). */
  updateReminder: UpdateReminderState;
}

export const DEFAULT_TRAY_PREFS: TrayPrefs = {
  title: 'requests',
  sections: {
    overview: true,
    cost: true,
    chart: true,
    topModel: true,
    compareYesterday: false,
    cacheHitRate: false,
    quota: true,
    clients: false,
  },
};

export const DEFAULT_PREFS: DesktopPrefs = {
  closeHintShown: false,
  tray: DEFAULT_TRAY_PREFS,
  updateReminder: EMPTY_REMINDER_STATE,
};

function defaults(): DesktopPrefs {
  return {
    ...DEFAULT_PREFS,
    tray: { ...DEFAULT_TRAY_PREFS, sections: { ...DEFAULT_TRAY_PREFS.sections } },
    updateReminder: { ...EMPTY_REMINDER_STATE },
  };
}

/**
 * Tolerant tray-prefs parser (also validates renderer IPC input): unknown title modes and missing or
 * non-boolean section flags fall back to the defaults one field at a time.
 */
export function parseTrayPrefs(raw: unknown): TrayPrefs {
  const o = typeof raw === 'object' && raw !== null ? (raw as Record<string, unknown>) : {};
  const title = TRAY_TITLE_MODES.includes(o.title as TrayTitleMode) ? (o.title as TrayTitleMode) : DEFAULT_TRAY_PREFS.title;
  const rawSections = typeof o.sections === 'object' && o.sections !== null ? (o.sections as Record<string, unknown>) : {};
  const sections = { ...DEFAULT_TRAY_PREFS.sections };
  for (const key of TRAY_SECTIONS) {
    const v = rawSections[key];
    if (typeof v === 'boolean') sections[key] = v;
  }
  return { title, sections };
}

/** Tolerant parser: missing or malformed files fall back to the defaults. */
export function parsePrefs(text: string | null | undefined): DesktopPrefs {
  if (text == null) return defaults();
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return defaults();
  }
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return defaults();
  const o = raw as Record<string, unknown>;
  return {
    closeHintShown: o.closeHintShown === true,
    tray: parseTrayPrefs(o.tray),
    updateReminder: parseReminderState(o.updateReminder),
  };
}
