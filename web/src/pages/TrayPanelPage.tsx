import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';

import { keys, useSetBinding } from '../api/hooks';
import type { ClientInfo } from '../api/types';
import { TrayPanelView, type TrayTab } from '../components/tray/TrayPanelView';
import { useTrayPrefs, useTrayState } from '../components/tray/useTrayBridge';
import { useTrayData } from '../components/tray/useTrayData';
import { errorText, useFeedback } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { trayTitle, windowLength, type TrayTitleLabels } from '../lib/tray';
import { getBridge, type TrayPanelCommand } from '../shell/bridge';
import { systemActions } from '../shell/systemActions';

/** localStorage keys whose change in another window (theme, accent, language) the panel picks up by reloading. */
const SYNCED_KEYS = new Set(['astra.theme', 'astra.accent', 'astra.locale']);

/**
 * The tray popover window (desktop only, route /tray, outside the app shell). The window is created hidden at
 * startup and lives as long as the app: it keeps polling today's numbers to compute the menu-bar title, reports
 * its content height so the window fits it, and forwards its buttons to the main process.
 */
export function TrayPanelPage() {
  const { t, locale } = useI18n();
  const qc = useQueryClient();
  const { toast } = useFeedback();
  const bridge = getBridge();
  const [prefs] = useTrayPrefs();
  const state = useTrayState();
  const [visible, setVisible] = useState(false);
  const [tab, setTab] = useState<TrayTab>('overview');
  const data = useTrayData(prefs, { enabled: state.running, background: true, live: visible });
  const setBinding = useSetBinding();
  const cardRef = useRef<HTMLDivElement>(null);

  // A transparent window: the card draws its own rounded background.
  useLayoutEffect(() => {
    const root = document.documentElement;
    root.dataset.view = 'tray';
    root.style.background = 'transparent';
    document.body.style.background = 'transparent';
  }, []);

  const refresh = useCallback(() => {
    void qc.invalidateQueries({ queryKey: ['stats'] });
    void qc.invalidateQueries({ queryKey: keys.providers });
    void qc.invalidateQueries({ queryKey: keys.clients });
    void qc.invalidateQueries({ queryKey: ['provider-accounts'] });
  }, [qc]);

  // Fresh numbers every time the panel opens.
  useEffect(
    () =>
      bridge?.tray.onVisibilityChanged((v) => {
        setVisible(v);
        // Every opening starts on the overview.
        if (v) {
          setTab('overview');
          refresh();
        }
      }),
    [bridge, refresh],
  );

  // The service came (back) up: refetch right away instead of waiting for the next poll.
  useEffect(() => {
    if (state.running) refresh();
  }, [state.running, refresh]);

  // Theme / accent / language changed in the main window.
  useEffect(() => {
    const onStorage = (e: StorageEvent) => {
      if (e.key && SYNCED_KEYS.has(e.key)) window.location.reload();
    };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') void bridge?.tray.command('hide');
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [bridge]);

  // Window height follows the content: header + tabs + body content (not its scroll box) + footer + border.
  useLayoutEffect(() => {
    const card = cardRef.current;
    if (!card || !bridge) return;
    const parts = ['header', 'tabs', 'body', 'footer'].map((p) => card.querySelector<HTMLElement>(`[data-tray-part="${p}"]`));
    const report = () => {
      const border = card.offsetHeight - card.clientHeight;
      const height = parts.reduce((sum, el) => sum + (el?.offsetHeight ?? 0), 0) + border;
      bridge.tray.resize(height);
    };
    const observer = new ResizeObserver(report);
    for (const el of parts) if (el) observer.observe(el);
    report();
    return () => observer.disconnect();
  }, [bridge]);

  // Menu-bar title (macOS) and tooltip line, from the user's "menu bar text" choice.
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
  const title = trayTitle(prefs.title, { running: state.running, summary: data.summary, plans: data.plans }, labels);
  useEffect(() => {
    bridge?.tray.setTitle(title.title, title.detail);
  }, [bridge, title.title, title.detail]);

  const command = (c: TrayPanelCommand) => void bridge?.tray.command(c);

  const copyAddress = async () => {
    if (!data.settings) return;
    if (await systemActions.copy(`${data.settings.gatewayBaseUrl}/v1`)) toast(t('common.copied'));
  };

  const switchClient = async (client: ClientInfo) => {
    const providers = data.providers;
    if (providers.length === 0) return;
    const picked = await systemActions.contextMenu(
      providers.map((p) => ({
        id: p.id,
        label: p.enabled ? p.name : t('tray.disabledProvider', { name: p.name }),
        type: 'checkbox' as const,
        checked: p.id === client.providerId,
      })),
    );
    if (!picked || picked === client.providerId) return;
    setBinding.mutate({ kind: client.kind, providerId: picked }, { onError: (e) => toast(errorText(e), 'error') });
  };

  return (
    <div className="h-full">
      <TrayPanelView
        ref={cardRef}
        className="h-full"
        prefs={prefs}
        state={state}
        data={data}
        tab={tab}
        onTabChange={setTab}
        onCommand={command}
        onRefresh={refresh}
        onCopyAddress={() => void copyAddress()}
        onSwitchClient={(c) => void switchClient(c)}
      />
    </div>
  );
}
