import { useCallback, useEffect, useState } from 'react';

import { useHealth } from '../../api/hooks';
import { getBridge, TRAY_SECTIONS, type TrayPanelState, type TrayPrefs } from '../../shell/bridge';

/** Mirrors DEFAULT_TRAY_PREFS in desktop/src/shared/prefs.ts (used until the desktop answers, and on the web). */
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

/** True when the host can show a tray panel (desktop builds whose preload exposes the tray API). */
export const hasTrayPanel = (): boolean => Boolean(getBridge()?.tray);

/**
 * Tray panel configuration, stored by the desktop main process (desktop-prefs.json) and broadcast to every
 * window on change, so the panel and the Settings tab stay in sync. Without the desktop bridge it is local state.
 */
export function useTrayPrefs(): [TrayPrefs, (next: TrayPrefs) => void, boolean] {
  const [prefs, setPrefsState] = useState<TrayPrefs>(DEFAULT_TRAY_PREFS);
  const [loaded, setLoaded] = useState(!getBridge()?.tray);
  useEffect(() => {
    const tray = getBridge()?.tray;
    if (!tray) return;
    let alive = true;
    void tray.getPrefs().then((p) => {
      if (!alive) return;
      setPrefsState(p);
      setLoaded(true);
    });
    const off = tray.onPrefsChanged(setPrefsState);
    return () => {
      alive = false;
      off();
    };
  }, []);
  const setPrefs = useCallback((next: TrayPrefs) => {
    setPrefsState(next);
    const tray = getBridge()?.tray;
    if (tray) void tray.setPrefs(next).then(setPrefsState);
  }, []);
  return [prefs, setPrefs, loaded];
}

/** Service state as the main process sees it; on the web (no tray) it is derived from the health probe. */
export function useTrayState(): TrayPanelState {
  const health = useHealth();
  const [state, setState] = useState<TrayPanelState | null>(null);
  useEffect(() => {
    const tray = getBridge()?.tray;
    if (!tray) return;
    let alive = true;
    void tray.getState().then((s) => {
      if (alive) setState(s);
    });
    const off = tray.onStateChanged(setState);
    return () => {
      alive = false;
      off();
    };
  }, []);
  return state ?? { running: health.isSuccess, port: null, activity: null, apiVersionMismatch: false, updateAvailable: null };
}

/** Every section on/off flag with one changed. */
export function withSection(prefs: TrayPrefs, key: (typeof TRAY_SECTIONS)[number], on: boolean): TrayPrefs {
  return { ...prefs, sections: { ...prefs.sections, [key]: on } };
}
