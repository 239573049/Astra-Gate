import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo } from 'react';

import { api, enc } from '../../api/client';
import { keys } from '../../api/hooks';
import type { ClientInfo, Provider, ProviderAccount, Settings, StatsSummary, TimeseriesPoint, TopModel } from '../../api/types';
import type { TrayPrefs } from '../../shell/bridge';
import { providerQuotaPlans, quotaPlans, type QuotaPlan } from '../../lib/tray';

/** Quota snapshots older than this are refetched from the upstream when the panel needs them. */
const QUOTA_MAX_AGE_MS = 5 * 60_000;
/** Accounts whose quota was requested recently (per window), so a failing probe is not retried in a loop. */
const quotaAttempts = new Map<string, number>();

const tzOffset = () => -new Date().getTimezoneOffset();

export interface TrayData {
  settings: Settings | undefined;
  summary: StatsSummary | undefined;
  /** Today by hour (chart). */
  hourly: TimeseriesPoint[];
  /** Last 7 days by day (yesterday comparison); empty unless that section is on. */
  daily: TimeseriesPoint[];
  top: TopModel[];
  plans: QuotaPlan[];
  /** True once some subscription account exists (the quota block then explains a missing snapshot). */
  hasSubscriptions: boolean;
  clients: ClientInfo[];
  providers: Provider[];
  /** When today's numbers were last fetched (ms epoch; 0 = never). */
  updatedAt: number;
  loading: boolean;
  error: boolean;
}

/**
 * Everything the tray panel shows. Query keys match the ones in api/hooks.ts, so the panel and the pages share a
 * cache. `background` keeps polling while the window is hidden (the panel window computes the menu-bar title
 * even when nobody looks at it); `live` marks the panel as on screen, which is when stale subscription quota
 * snapshots get refreshed from the upstream.
 */
export function useTrayData(prefs: TrayPrefs, opts: { enabled: boolean; background: boolean; live: boolean }): TrayData {
  const qc = useQueryClient();
  const { enabled, background } = opts;
  const poll = { refetchInterval: 15_000, refetchIntervalInBackground: background } as const;
  const needQuota = prefs.sections.quota || prefs.title === 'quota' || prefs.title === 'alerts';

  const settings = useQuery({ queryKey: keys.settings, queryFn: () => api<Settings>('GET', '/api/settings'), enabled });
  const summary = useQuery({
    queryKey: keys.stats('summary', 'today', '', ''),
    queryFn: () => api<StatsSummary>('GET', '/api/stats/summary?range=today'),
    enabled,
    ...poll,
  });
  const hourly = useQuery({
    queryKey: keys.stats('timeseries', 'today', 'hour', 'provider', '', '', tzOffset()),
    queryFn: () => api<TimeseriesPoint[]>('GET', `/api/stats/timeseries?range=today&groupBy=hour&by=provider&tzOffset=${tzOffset()}`),
    enabled: enabled && prefs.sections.chart,
    ...poll,
    refetchInterval: 30_000,
  });
  const daily = useQuery({
    queryKey: keys.stats('timeseries', '7d', 'day', 'provider', '', '', tzOffset()),
    queryFn: () => api<TimeseriesPoint[]>('GET', `/api/stats/timeseries?range=7d&groupBy=day&by=provider&tzOffset=${tzOffset()}`),
    enabled: enabled && prefs.sections.compareYesterday,
    ...poll,
    refetchInterval: 60_000,
  });
  const top = useQuery({
    queryKey: keys.stats('top', 'today', '', 3, ''),
    queryFn: () => api<TopModel[]>('GET', '/api/stats/top-models?range=today&limit=3'),
    enabled: enabled && prefs.sections.topModel,
    ...poll,
    refetchInterval: 30_000,
  });
  const providers = useQuery({
    queryKey: keys.providers,
    queryFn: () => api<Provider[]>('GET', '/api/providers'),
    enabled: enabled && (needQuota || prefs.sections.clients),
    ...poll,
    refetchInterval: 60_000,
  });
  const clients = useQuery({
    queryKey: keys.clients,
    queryFn: () => api<ClientInfo[]>('GET', '/api/clients'),
    enabled: enabled && prefs.sections.clients,
    ...poll,
    refetchInterval: 30_000,
  });

  const subscriptionProviders = useMemo(
    () => (providers.data ?? []).filter((p) => p.enabled && p.authScheme === 'oauth-subscription'),
    [providers.data],
  );
  const accounts = useQueries({
    queries: subscriptionProviders.map((p) => ({
      queryKey: keys.providerAccounts(p.id),
      queryFn: () => api<ProviderAccount[]>('GET', `/api/providers/${enc(p.id)}/accounts`),
      enabled: enabled && needQuota,
      ...poll,
      refetchInterval: 60_000,
    })),
  });
  const accountRows = subscriptionProviders.flatMap((provider, i) => (accounts[i]?.data ?? []).map((account) => ({ provider, account })));
  const accountKey = accountRows.map((r) => `${r.account.id}:${r.account.status}:${r.account.quota?.fetchedAtUtc ?? ''}`).join('|');

  // Stale quota snapshots: ask the upstream again (at most once per window per account).
  const refreshQuota = useMutation({
    mutationFn: (id: string) => api<ProviderAccount>('POST', `/api/provider-accounts/${enc(id)}/quota`),
    onSettled: (account) => {
      if (account) void qc.invalidateQueries({ queryKey: keys.providerAccounts(account.providerId) });
    },
  });
  const quotaActive = enabled && needQuota && (opts.live || prefs.title === 'quota' || prefs.title === 'alerts');
  useEffect(() => {
    if (!quotaActive) return;
    const now = Date.now();
    for (const { account } of accountRows) {
      if (account.status !== 'active') continue;
      const at = account.quota?.fetchedAtUtc ? new Date(account.quota.fetchedAtUtc).getTime() : 0;
      if (Number.isFinite(at) && now - at < QUOTA_MAX_AGE_MS) continue;
      if (now - (quotaAttempts.get(account.id) ?? 0) < QUOTA_MAX_AGE_MS) continue;
      quotaAttempts.set(account.id, now);
      refreshQuota.mutate(account.id, { onError: () => {} });
    }
    // accountKey stands for accountRows; refreshQuota is stable enough (mutate identity does not matter here).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [quotaActive, accountKey]);

  // API-key providers' balances come from the server-side background refresh (ProviderQuotaWorker); the
  // providers query above already polls them, so the panel never queries those upstreams itself.
  const plans = [...quotaPlans(accountRows), ...providerQuotaPlans(providers.data ?? [])];

  return {
    settings: settings.data,
    summary: summary.data,
    hourly: hourly.data ?? [],
    daily: daily.data ?? [],
    top: top.data ?? [],
    plans,
    hasSubscriptions: accountRows.some((r) => r.account.status === 'active'),
    clients: clients.data ?? [],
    providers: providers.data ?? [],
    updatedAt: summary.dataUpdatedAt,
    loading: summary.isLoading,
    error: summary.isError,
  };
}
