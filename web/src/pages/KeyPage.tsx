import { useCallback, useEffect, useState, type FormEvent } from 'react';

import { fetchKeyInfo, fetchKeyRequests, fetchKeyStats, KeyInfoError } from '../api/hooks';
import type { KeyInfo, KeyRequest, KeyStats, KeyStatsRange, KeyUsage, KeyUsageBy } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { PasswordField } from '../components/arc/password-field/password-field';
import { AstraLogo } from '../components/AstraLogo';
import { CLIENT_META } from '../components/icons';
import { Badge, Button, Segmented } from '../components/ui/controls';
import { useI18n } from '../i18n';
import { formatDateTime, formatMs, formatPercent, formatTime, formatTokens, formatUsd } from '../lib/format';

const STORAGE_KEY = 'astra.keyLookup';

function remembered(): string {
  try {
    return sessionStorage.getItem(STORAGE_KEY) ?? '';
  } catch {
    return '';
  }
}

function remember(key: string | null) {
  try {
    if (key) sessionStorage.setItem(STORAGE_KEY, key);
    else sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // storage unavailable (private mode): the page still works, it just forgets
  }
}

/**
 * /key — token self-service (plan: alerts-webhooks-token-limits §2.2): whoever holds a gateway token looks up its own
 * limits, usage, statistics and request log with the token itself. Outside the admin login on purpose; it only calls
 * /v1/astra/key[/stats|/requests], which show nothing upstream. The token stays in this tab's sessionStorage; a ?key= link fills it in and is cleared from the
 * address bar right away.
 */
export function KeyPage() {
  const { t } = useI18n();
  const [key, setKey] = useState('');
  const [info, setInfo] = useState<KeyInfo | null>(null);
  const [activeKey, setActiveKey] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const lookUp = useCallback(
    async (value: string) => {
      const trimmed = value.trim();
      if (!trimmed) return;
      setLoading(true);
      setError(null);
      try {
        setInfo(await fetchKeyInfo(trimmed));
        setActiveKey(trimmed);
        remember(trimmed);
      } catch (e) {
        setInfo(null);
        remember(null);
        setError(e instanceof KeyInfoError && e.status === 401 ? t('key.invalid') : t('key.error', { error: (e as Error).message }));
      } finally {
        setLoading(false);
      }
    },
    [t],
  );

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const fromLink = params.get('key');
    if (fromLink) window.history.replaceState(null, '', window.location.pathname);
    const initial = fromLink ?? remembered();
    if (initial) {
      setKey(initial);
      void lookUp(initial);
    }
    // Once, on arrival.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    void lookUp(key);
  };

  return (
    <div className="wallpaper flex min-h-full justify-center overflow-y-auto p-4 sm:items-start sm:py-12">
      <div className="flex w-full max-w-[720px] flex-col gap-4">
        <div className="flex items-center gap-3">
          <AstraLogo className="size-10 rounded-[12px]" />
          <div>
            <h1 className="text-[17px] font-medium">{t('key.title')}</h1>
            <p className="text-[13px] text-[var(--text-secondary)]">{t('key.subtitle')}</p>
          </div>
        </div>

        {!info && (
          <form onSubmit={submit} className="card flex flex-col gap-4 p-5">
            <PasswordField label={t('key.title')} placeholder={t('key.placeholder')} autoFocus autoComplete="off" value={key}
              aria-invalid={error ? true : undefined} onChange={(e) => setKey(e.target.value)} />
            {error && <Alert tone="danger" title={error} />}
            <Button type="submit" variant="primary" loading={loading} disabled={!key.trim()}>
              {t('key.submit')}
            </Button>
          </form>
        )}

        {info && (
          <KeyReport
            info={info}
            apiKey={activeKey}
            onForget={() => {
              remember(null);
              setInfo(null);
              setKey('');
            }}
          />
        )}
      </div>
    </div>
  );
}

function KeyReport({ info, apiKey, onForget }: { info: KeyInfo; apiKey: string; onForget: () => void }) {
  const { t, locale } = useI18n();
  const [tab, setTab] = useState<'overview' | 'stats' | 'requests'>('overview');
  const clients =
    info.allowedClients == null
      ? t('key.clientsAny')
      : info.allowedClients.length === 0
        ? t('key.directOnly')
        : info.allowedClients.map((k) => CLIENT_META[k as keyof typeof CLIENT_META]?.name ?? k).join(', ');
  const usageRows: [string, KeyUsage][] = [
    [t('key.today'), info.usage.today],
    [t('key.week'), info.usage.week],
    [t('key.month'), info.usage.month],
    [t('key.total'), info.usage.total],
  ];
  const maxDaily = Math.max(...info.daily.map((d) => d.costUsd), 0);

  return (
    <>
      <section className="card flex flex-col gap-3 p-5">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="text-[15px] font-medium">{info.name}</h2>
          <code className="font-mono text-[12px] text-[var(--text-secondary)]">{info.keyPrefix}</code>
          {!info.enabled && <Badge tone="orange">{t('key.disabled')}</Badge>}
          <Button size="sm" variant="plain" className="ml-auto" onClick={onForget}>
            {t('key.forget')}
          </Button>
        </div>
        <div className="text-[12px] text-[var(--text-secondary)]">
          {t('key.clients')}: {clients} · {t('key.timeZone', { zone: info.timeZone })}
        </div>
        <Segmented
          ariaLabel={t('key.title')}
          value={tab}
          onChange={setTab}
          items={[
            { value: 'overview', label: t('key.tab.overview') },
            { value: 'stats', label: t('key.tab.stats') },
            { value: 'requests', label: t('key.tab.requests') },
          ]}
        />
      </section>

      {tab === 'stats' && <KeyStatsPanel apiKey={apiKey} />}
      {tab === 'requests' && <KeyRequestsPanel apiKey={apiKey} />}
      {tab === 'overview' && (
        <>
          <section className="card flex flex-col gap-3 p-5">
            <h3 className="text-[13px] font-medium">{t('key.limits')}</h3>
            {info.limits.length === 0 && info.rpm == null && <p className="text-[12px] text-[var(--text-muted)]">{t('key.unlimited')}</p>}
            <div className="grid gap-4 sm:grid-cols-2">
              {info.limits.map((l) => {
                const amount = (v: number) => (l.metric === 'cost' ? formatUsd(v) : formatTokens(v, true));
                const percent = Math.max(0, Math.min(100, Math.round(l.usedPercent)));
                const tone = percent >= 90 ? 'var(--danger)' : percent >= 70 ? 'var(--warning)' : 'var(--accent)';
                const label = `${t(`tokens.period.${l.period}`)} · ${t(`tokens.metric.${l.metric}`)}`;
                return (
                  <div key={`${l.metric}:${l.period}`}>
                    <div className="mb-1 flex items-baseline justify-between gap-2 text-[12px]">
                      <span className="text-[var(--text-secondary)]">{label}</span>
                      <span className="num">{amount(l.used)} / {amount(l.limit)}</span>
                    </div>
                    <div role="progressbar" aria-label={label} aria-valuenow={percent} aria-valuemin={0} aria-valuemax={100}
                      className="h-1.5 w-full overflow-hidden rounded-full bg-[var(--surface-muted)]">
                      <div className="h-full rounded-full" style={{ width: `${percent}%`, background: tone }} />
                    </div>
                    <div className="mt-1 text-[11px] text-[var(--text-muted)]">
                      {l.exhausted ? t('key.exhausted') : ''}
                      {l.exhausted && l.resetsAt ? ' · ' : ''}
                      {l.resetsAt ? t('key.resets', { time: formatDateTime(l.resetsAt, locale) }) : ''}
                    </div>
                  </div>
                );
              })}
            </div>
            {info.rpm && <p className="num text-[12px] text-[var(--text-secondary)]">{t('key.rpm', { used: info.rpm.used, limit: info.rpm.limit })}</p>}
          </section>

          <section className="card flex flex-col gap-3 p-5">
            <h3 className="text-[13px] font-medium">{t('key.usage')}</h3>
            <div className="overflow-x-auto">
              <table className="w-full text-[12px]">
                <thead>
                  <tr className="text-[var(--text-muted)]">
                    <th className="py-1 text-left font-normal" />
                    <th className="py-1 text-right font-normal">{t('key.cost')}</th>
                    <th className="py-1 text-right font-normal">{t('key.requests')}</th>
                    <th className="py-1 text-right font-normal">{t('key.tokens')}</th>
                  </tr>
                </thead>
                <tbody>
                  {usageRows.map(([label, u]) => (
                    <tr key={label}>
                      <th scope="row" className="py-1 text-left font-medium text-[var(--text-secondary)]">{label}</th>
                      <td className="num py-1 text-right font-semibold">{formatUsd(u.costUsd)}</td>
                      <td className="num py-1 text-right">{formatTokens(u.requests)}</td>
                      <td className="num py-1 text-right">{formatTokens(u.inputTokens + u.outputTokens, true)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <h4 className="mt-2 text-[12px] text-[var(--text-muted)]">{t('key.daily')}</h4>
            <div className="flex h-24 items-end gap-[2px]" role="list" aria-label={t('key.daily')}>
              {info.daily.map((d) => (
                <div key={d.day} role="listitem" title={`${d.day} · ${formatUsd(d.costUsd)} · ${formatTokens(d.requests)} ${t('key.requests')}`}
                  className="flex-1 rounded-t-[2px] bg-[var(--accent)]"
                  style={{ height: `${maxDaily > 0 ? Math.max(2, (d.costUsd / maxDaily) * 100) : 2}%`, opacity: d.costUsd > 0 ? 1 : 0.25 }} />
              ))}
            </div>
          </section>
        </>
      )}
    </>
  );
}
const RANGES: KeyStatsRange[] = ['today', '7d', '30d', '90d'];

/** The holder's statistics: totals, the request trend, models and clients used, and why requests failed. */
function KeyStatsPanel({ apiKey }: { apiKey: string }) {
  const { t } = useI18n();
  const [range, setRange] = useState<KeyStatsRange>('7d');
  const [stats, setStats] = useState<KeyStats | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    setError(null);
    fetchKeyStats(apiKey, range).then(
      (s) => live && setStats(s),
      (e: Error) => live && setError(t('key.error', { error: e.message })),
    );
    return () => {
      live = false;
    };
  }, [apiKey, range, t]);

  const s = stats?.summary;
  const maxBucket = Math.max(...(stats?.series ?? []).map((p) => p.requests), 0);
  return (
    <section className="card flex flex-col gap-4 p-5">
      <Segmented ariaLabel={t('overview.range')} value={range} onChange={setRange} items={RANGES.map((r) => ({ value: r, label: t(`range.${r}`) }))} />
      {error && <Alert tone="danger" title={error} />}
      {!stats || !s ? (
        !error && <p className="text-[12px] text-[var(--text-muted)]">…</p>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <Figure label={t('key.stats.requests')} value={formatTokens(s.requests)} />
            <Figure label={t('key.stats.successRate')} value={s.requests ? formatPercent(s.successRate) : '—'} />
            <Figure label={t('key.cost')} value={formatUsd(s.costUsd)} />
            <Figure label={t('key.stats.avgTtft')} value={formatMs(s.avgTtftMs)} />
          </div>
          <div>
            <h4 className="mb-2 text-[12px] text-[var(--text-muted)]">{t('key.stats.trend')}</h4>
            <div className="flex h-20 items-end gap-[2px]" role="list" aria-label={t('key.stats.trend')}>
              {stats.series.map((p) => (
                <div key={p.bucket} role="listitem" title={`${p.bucket} · ${formatTokens(p.requests)} ${t('key.requests')} · ${formatUsd(p.costUsd)}`}
                  className="flex-1 rounded-t-[2px] bg-[var(--accent)]"
                  style={{ height: `${maxBucket > 0 ? Math.max(2, (p.requests / maxBucket) * 100) : 2}%` }} />
              ))}
            </div>
          </div>
          <UsageTable title={t('key.stats.byModel')} rows={stats.models.map((m) => ({ name: m.model ?? '—', ...m }))} />
          <UsageTable title={t('key.stats.byClient')} rows={stats.clients.map((c) => ({ name: c.client ? CLIENT_META[c.client as keyof typeof CLIENT_META]?.name ?? c.client : t('key.stats.direct'), ...c }))} />
          <div>
            <h4 className="mb-2 text-[12px] text-[var(--text-muted)]">{t('key.stats.errors')}</h4>
            {stats.errors.length === 0 ? (
              <p className="text-[12px] text-[var(--text-secondary)]">{t('key.stats.noErrors')}</p>
            ) : (
              <ul className="flex flex-wrap gap-2">
                {stats.errors.map((e) => (
                  <li key={e.category}>
                    <Badge tone="orange">{t(`key.category.${e.category}`)} · {formatTokens(e.count)}</Badge>
                  </li>
                ))}
              </ul>
            )}
          </div>
          {stats.retentionDays != null && <p className="text-[11px] text-[var(--text-muted)]">{t('key.stats.retention', { days: stats.retentionDays })}</p>}
        </>
      )}
    </section>
  );
}

function Figure({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <div className="truncate text-[11px] text-[var(--text-muted)]">{label}</div>
      <div className="num truncate text-[17px] font-semibold">{value}</div>
    </div>
  );
}

function UsageTable({ title, rows }: { title: string; rows: (KeyUsageBy & { name: string })[] }) {
  const { t } = useI18n();
  if (rows.length === 0) return null;
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-[12px]">
        <caption className="mb-1 text-left text-[12px] text-[var(--text-muted)]">{title}</caption>
        <thead>
          <tr className="text-[var(--text-muted)]">
            <th className="py-1 text-left font-normal" />
            <th className="py-1 text-right font-normal">{t('key.requests')}</th>
            <th className="py-1 text-right font-normal">{t('key.stats.successRate')}</th>
            <th className="py-1 text-right font-normal">{t('key.tokens')}</th>
            <th className="py-1 text-right font-normal">{t('key.cost')}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.name}>
              <th scope="row" className="max-w-[240px] truncate py-1 text-left font-medium text-[var(--text-secondary)]">{r.name}</th>
              <td className="num py-1 text-right">{formatTokens(r.requests)}</td>
              <td className="num py-1 text-right">{r.requests ? formatPercent(r.successRequests / r.requests) : '—'}</td>
              <td className="num py-1 text-right">{formatTokens(r.inputTokens + r.outputTokens, true)}</td>
              <td className="num py-1 text-right font-semibold">{formatUsd(r.costUsd)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** The holder's own request log, newest first, a page at a time. */
function KeyRequestsPanel({ apiKey }: { apiKey: string }) {
  const { t, locale } = useI18n();
  const [filter, setFilter] = useState<'all' | 'failed'>('all');
  const [items, setItems] = useState<KeyRequest[]>([]);
  const [next, setNext] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(
    async (before: string | null) => {
      setLoading(true);
      setError(null);
      try {
        const page = await fetchKeyRequests(apiKey, { before, status: filter === 'failed' ? FAILED_STATUSES : undefined });
        setItems((current) => (before ? [...current, ...page.items] : page.items));
        setNext(page.nextBefore ?? null);
      } catch (e) {
        setError(t('key.error', { error: (e as Error).message }));
      } finally {
        setLoading(false);
      }
    },
    [apiKey, filter, t],
  );
  useEffect(() => {
    void load(null);
  }, [load]);

  return (
    <section className="card flex flex-col gap-3 p-5">
      <Segmented
        ariaLabel={t('key.requests.status')}
        value={filter}
        onChange={setFilter}
        items={[
          { value: 'all', label: t('key.requests.all') },
          { value: 'failed', label: t('key.requests.failed') },
        ]}
      />
      {error && <Alert tone="danger" title={error} />}
      {items.length === 0 && !loading && !error && <p className="text-[12px] text-[var(--text-muted)]">{t('key.requests.empty')}</p>}
      {items.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-[12px]">
            <thead>
              <tr className="text-[var(--text-muted)]">
                <th className="py-1 text-left font-normal">{t('key.requests.time')}</th>
                <th className="py-1 text-left font-normal">{t('key.requests.model')}</th>
                <th className="py-1 text-left font-normal">{t('key.requests.client')}</th>
                <th className="py-1 text-left font-normal">{t('key.requests.status')}</th>
                <th className="py-1 text-right font-normal">{t('key.requests.tokens')}</th>
                <th className="py-1 text-right font-normal">{t('key.cost')}</th>
                <th className="py-1 text-right font-normal">{t('key.requests.latency')}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((r) => (
                <tr key={r.id} className="border-t border-[var(--border-subtle)]">
                  <td className="py-1.5 whitespace-nowrap text-[var(--text-secondary)]" title={formatDateTime(r.time, locale)}>{formatTime(r.time, locale)}</td>
                  <td className="max-w-[200px] truncate py-1.5 font-mono" title={r.model ?? ''}>{r.model ?? '—'}</td>
                  <td className="py-1.5 text-[var(--text-secondary)]">
                    {r.client ? CLIENT_META[r.client as keyof typeof CLIENT_META]?.name ?? r.client : t('key.stats.direct')}
                  </td>
                  <td className="py-1.5">
                    {r.errorCategory ? (
                      <Badge tone="orange" title={r.httpStatus ? `HTTP ${r.httpStatus}` : undefined}>{t(`key.category.${r.errorCategory}`)}</Badge>
                    ) : (
                      <Badge tone="green">{t('key.requests.success')}</Badge>
                    )}
                  </td>
                  <td className="num py-1.5 text-right">
                    {formatTokens(r.inputTokens, true)} / {formatTokens(r.outputTokens, true)}
                  </td>
                  <td className="num py-1.5 text-right font-semibold">{formatUsd(r.costUsd)}</td>
                  <td className="num py-1.5 text-right text-[var(--text-secondary)]">
                    {formatMs(r.ttftMs)} / {formatMs(r.totalMs)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {next && (
        <div>
          <Button size="sm" loading={loading} onClick={() => void load(next)}>
            {t('key.requests.more')}
          </Button>
        </div>
      )}
    </section>
  );
}

/** Every logged status but success (the server matches any listed one). */
const FAILED_STATUSES = 'upstream_error,gateway_error,client_cancelled,blocked';

