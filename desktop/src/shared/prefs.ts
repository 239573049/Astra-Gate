/**
 * Desktop-only UI preferences, stored as `desktop-prefs.json` in Electron's userData directory
 * (not under ~/.astra — nothing else reads them).
 */
export interface DesktopPrefs {
  /** The one-time "still running in the tray" hint was shown after the first window close. */
  closeHintShown: boolean;
}

export const DEFAULT_PREFS: DesktopPrefs = { closeHintShown: false };

/** Tolerant parser: missing or malformed files fall back to the defaults. */
export function parsePrefs(text: string | null | undefined): DesktopPrefs {
  if (text == null) return { ...DEFAULT_PREFS };
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return { ...DEFAULT_PREFS };
  }
  if (typeof raw !== 'object' || raw === null) return { ...DEFAULT_PREFS };
  const o = raw as Record<string, unknown>;
  return { closeHintShown: o.closeHintShown === true };
}
