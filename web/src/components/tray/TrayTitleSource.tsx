import { useEffect, useMemo } from 'react';

import type { StatsSummary } from '../../api/types';
import { useI18n } from '../../i18n';
import { trayTitle, windowLength, type QuotaPlan, type TrayTitleLabels } from '../../lib/tray';
import { getBridge, type TrayPanelState, type TrayPrefs } from '../../shell/bridge';
import { useTrayPrefs, useTrayState } from './useTrayBridge';
import { useTrayData } from './useTrayData';

/**
 * Menu-bar title (macOS) and tooltip for the user's "menu bar text" choice, sent to the main process.
 * Every window that has the tray data can report it: the panel window while it exists (the main process
 * prefers its word), and the main window before that — the panel is created on first use, so at startup
 * the main window is the only window there is.
 */
export function useTrayTitle(
  prefs: TrayPrefs,
  state: TrayPanelState,
  summary: StatsSummary | undefined,
  plans: QuotaPlan[],
): void {
  const { t, locale } = useI18n();
  const bridge = getBridge();
  const labels = useMemo<TrayTitleLabels>(
    () => ({
      requests: (count) => t('tray.title.requests', { count }),
      tokens: (count) => t('tray.title.tokens', { count }),
      cost: (amount) => t('tray.title.cost', { amount }),
      quota: (plan, window, remaining) => t('tray.title.quota', { plan, window, remaining }),
      failures: (count) => t('tray.title.failures', { count }),
      stopped: t('tray.status.stopped'),
      windowName: (row) =>
        row.kind === 'balance'
          ? (row.label ?? t('tray.quotaBalance'))
          : row.kind === 'credits' || row.windowMinutes == null
            ? t('tray.quotaCredits')
            : windowLength(row.windowMinutes, locale),
    }),
    [t, locale],
  );
  const title = trayTitle(prefs.title, { running: state.running, summary, plans }, labels);
  useEffect(() => {
    bridge?.tray.setTitle(title.title, title.detail);
  }, [bridge, title.title, title.detail]);
}

/**
 * Keeps the menu-bar title current from the main window (mounted by the app shell, so never alongside the
 * panel window's own copy). It polls the data the title is computed from (not the panel's sections), so the title is
 * right before the panel window has ever been created — which is the whole point of having the shell report it.
 */
export function TrayTitleSource() {
  const [prefs] = useTrayPrefs();
  const state = useTrayState();
  const data = useTrayData(prefs, { enabled: state.running, background: true, live: false, titleOnly: true });
  useTrayTitle(prefs, state, data.summary, data.plans);
  return null;
}
