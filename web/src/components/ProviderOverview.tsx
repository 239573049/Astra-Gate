import { useState } from 'react';

import { useUpdateProvider } from '../api/hooks';

import type { Provider, ProviderStat, Range } from '../api/types';
import { SortableDataTable, type DataColumn } from './arc/sortable-data-table/sortable-data-table';
import { ProviderIcon } from './icons';
import { Dot, Switch } from './ui/controls';
import { errorText, useFeedback } from './ui/overlays';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';
import { formatMs, formatPercent, formatTokens, formatUsd } from '../lib/format';

/** How the Providers page lists its providers; remembered per browser. */
export type ProvidersView = 'grid' | 'table';

const VIEW_KEY = 'astra.providers.view';
const CHARTS_KEY = 'astra.providers.charts';

function readKey(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeKey(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    // Storage blocked: the choice lasts this page view only.
  }
}

/** The remembered list view (cards by default). */
export function useProvidersView(): [ProvidersView, (v: ProvidersView) => void] {
  const [view, setView] = useState<ProvidersView>(() => (readKey(VIEW_KEY) === 'table' ? 'table' : 'grid'));
  return [view, (v) => { setView(v); writeKey(VIEW_KEY, v === 'table' ? 'table' : null); }];
}

/** Whether the overview's charts are shown (default on). */
export function useProviderChartsOpen(): [boolean, (open: boolean) => void] {
  const [open, setOpen] = useState(() => readKey(CHARTS_KEY) !== '0');
  return [open, (o) => { setOpen(o); writeKey(CHARTS_KEY, o ? null : '0'); }];
}

/** Below this success rate (with at least {@link LOW_SUCCESS_MIN_REQUESTS} requests) a provider is flagged. */
export const LOW_SUCCESS_RATE = 0.9;
const LOW_SUCCESS_MIN_REQUESTS = 5;

export type ProviderHealth = 'disabled' | 'noKey' | 'lowSuccess' | 'healthy';

export function missingKey(p: Provider): boolean {
  return !p.hasApiKey && p.authScheme !== 'none' && p.authScheme !== 'oauth-subscription' && p.authScheme !== 'plugin';
}

/** One state per provider: switched off, no key, a poor success rate over the window, or fine. */
export function providerHealth(p: Provider, stat?: ProviderStat): ProviderHealth {
  if (!p.enabled) return 'disabled';
  if (missingKey(p)) return 'noKey';
  if (stat && stat.requests >= LOW_SUCCESS_MIN_REQUESTS && stat.successRate < LOW_SUCCESS_RATE) return 'lowSuccess';
  return 'healthy';
}

export const HEALTH_TONE: Record<ProviderHealth, 'green' | 'orange' | 'red' | 'neutral'> = {
  healthy: 'green',
  noKey: 'orange',
  lowSuccess: 'red',
  disabled: 'neutral',
};

export function healthLabel(h: ProviderHealth, t: ReturnType<typeof useI18n>['t']): string {
  return h === 'disabled'
    ? t('providers.card.disabled')
    : h === 'noKey'
      ? t('providers.noKey')
      : h === 'lowSuccess'
        ? t('providers.card.lowSuccess')
        : t('providers.card.active');
}

/** Series colors (TokenPoolEditor's SHARE_COLORS); everything past them is "other". */
const SHARE_COLORS = ['var(--accent)', 'var(--success)', 'var(--warning)', '#af52de', '#5ac8fa', '#ff2d55'];
const OTHER_COLOR = 'var(--text-muted)';

export function successColor(rate: number): string {
  return rate >= 0.97 ? 'var(--success)' : rate >= LOW_SUCCESS_RATE ? 'var(--warning)' : 'var(--danger)';
}

/**
 * Seven days of requests as small bars. `max` is shared by every card and row, so heights compare across providers; the scale is a
 * square root, so one busy provider does not flatten everyone else. A day without requests is a flat grey stub.
 */
export function MiniBars({ data, labels, max, className }: { data: number[]; labels: string[]; max: number; className?: string }) {
  return (
    <span
      className={cn('flex h-[22px] shrink-0 items-end gap-[2px]', className)}
      title={data.map((v, i) => `${labels[i]}: ${v}`).join('\n')}
    >
      {data.map((v, i) => (
        <i
          key={i}
          className="block w-[6px] rounded-t-[2px]"
          style={{
            height: v > 0 ? `${Math.max(14, Math.sqrt(v / Math.max(1, max)) * 100)}%` : 2,
            background: v > 0 ? 'var(--accent)' : 'var(--border)',
          }}
        />
      ))}
    </span>
  );
}

interface Slice {
  key: string;
  name: string;
  value: number;
  color: string;
}

/** The top providers by `value` with their colors, the rest folded into one "other" slice. */
function slices(stats: ProviderStat[], names: Map<string, string>, value: (s: ProviderStat) => number, other: string): Slice[] {
  const sorted = stats.filter((s) => value(s) > 0).sort((a, b) => value(b) - value(a));
  const top = sorted.slice(0, SHARE_COLORS.length).map((s, i) => ({
    key: s.providerId,
    name: names.get(s.providerId) ?? s.name ?? s.providerId,
    value: value(s),
    color: SHARE_COLORS[i],
  }));
  const rest = sorted.slice(SHARE_COLORS.length).reduce((a, s) => a + value(s), 0);
  return rest > 0 ? [...top, { key: '__other', name: other, value: rest, color: OTHER_COLOR }] : top;
}

/** The ranges the page offers for the overview. */
export const OVERVIEW_RANGES = ['today', '7d', '30d'] as const satisfies readonly Range[];

/**
 * The summary above the provider list: four figures over the chosen range, and (collapsible) the request share, the ranking, the cost
 * share and the health counts. Data: /api/stats/providers for the range, the provider list for names and states.
 */
export function ProviderOverview({
  providers,
  stats,
  stats7d,
  range,
  chartsOpen,
}: {
  providers: Provider[];
  stats: ProviderStat[];
  /** The cards' window: decides the health of each provider, like the badges on the cards. */
  stats7d: Map<string, ProviderStat>;
  range: Range;
  chartsOpen: boolean;
}) {
  const { t } = useI18n();
  const names = new Map(providers.map((p) => [p.id, p.name]));
  const requests = stats.reduce((a, s) => a + s.requests, 0);
  const success = stats.reduce((a, s) => a + s.successRequests, 0);
  const cost = stats.reduce((a, s) => a + s.costUsd, 0);
  const failovers = stats.reduce((a, s) => a + s.failoversOut, 0);
  const enabled = providers.filter((p) => p.enabled).length;
  const withTraffic = stats.filter((s) => s.requests > 0 && names.has(s.providerId)).length;
  const share = slices(stats, names, (s) => s.requests, t('providers.overview.others'));
  const costShare = slices(stats, names, (s) => s.costUsd, t('providers.overview.others'));
  const ranking = share.filter((s) => s.key !== '__other');
  const health = { healthy: 0, noKey: 0, lowSuccess: 0, disabled: 0 } satisfies Record<ProviderHealth, number>;
  for (const p of providers) health[providerHealth(p, stats7d.get(p.id))]++;

  const rangeLabel = t(`range.${range}` as 'range.7d');

  return (
    <div className="mb-3 grid gap-3">
      <div className="card px-4 py-3">
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          <Figure label={t('providers.overview.requests')} value={formatTokens(requests, true)} hint={rangeLabel} />
          <Figure label={t('providers.overview.cost')} value={formatUsd(cost)} hint={rangeLabel} />
          <Figure
            label={t('providers.overview.successRate')}
            value={requests > 0 ? formatPercent(success / requests) : '—'}
            color={requests > 0 ? successColor(success / requests) : undefined}
            hint={failovers > 0 ? t('providers.overview.failoverCount', { count: failovers }) : rangeLabel}
          />
          <Figure
            label={t('providers.overview.enabled')}
            value={`${enabled} / ${providers.length}`}
            hint={t('providers.overview.withTraffic', { count: withTraffic })}
          />
        </div>
        {chartsOpen && share.length > 0 && (
          <>
            <div className="mt-3 flex h-2.5 overflow-hidden rounded-full bg-[var(--border)]" aria-label={t('providers.overview.share')}>
              {share.map((s) => (
                <span
                  key={s.key}
                  className="h-full border-r border-[var(--surface)] last:border-r-0"
                  style={{ width: `${(s.value / requests) * 100}%`, background: s.color }}
                  title={`${s.name}: ${s.value} (${formatPercent(s.value / requests)})`}
                />
              ))}
            </div>
            <Legend items={share} total={requests} />
          </>
        )}
      </div>

      {chartsOpen && (
        <div className="grid gap-3 md:grid-cols-[1.3fr_1fr_1fr]">
          <ChartCard title={t('providers.overview.ranking')} hint={rangeLabel}>
            {ranking.length === 0 ? (
              <Empty text={t('providers.overview.noData')} />
            ) : (
              <div className="grid gap-1.5">
                {ranking.map((s) => (
                  <div key={s.key} className="grid grid-cols-[96px_1fr_44px] items-center gap-2 text-[12px]">
                    <span className="truncate" title={s.name}>{s.name}</span>
                    <span className="h-2 overflow-hidden rounded-full bg-[var(--border)]">
                      <i className="block h-full rounded-full" style={{ width: `${(s.value / ranking[0].value) * 100}%`, background: s.color }} />
                    </span>
                    <span className="num text-right text-[var(--text-secondary)]">{formatTokens(s.value, true)}</span>
                  </div>
                ))}
              </div>
            )}
          </ChartCard>
          <ChartCard title={t('providers.overview.costShare')} hint={formatUsd(cost)}>
            {costShare.length === 0 ? (
              <Empty text={t('providers.overview.noCost')} />
            ) : (
              <div className="flex items-center gap-4">
                <Donut items={costShare} total={cost} label={formatUsd(cost, { compact: true })} />
                <Legend items={costShare} total={cost} column />
              </div>
            )}
          </ChartCard>
          <ChartCard title={t('providers.overview.health')} hint={t('range.7d')}>
            <div className="grid grid-cols-2 gap-2">
              <HealthTile tone="green" label={t('providers.overview.healthy')} value={health.healthy} />
              <HealthTile tone="red" label={t('providers.overview.lowSuccess')} value={health.lowSuccess} />
              <HealthTile tone="orange" label={t('providers.noKey')} value={health.noKey} />
              <HealthTile tone="neutral" label={t('providers.card.disabled')} value={health.disabled} />
            </div>
          </ChartCard>
        </div>
      )}
    </div>
  );
}

function Figure({ label, value, hint, color }: { label: string; value: string; hint?: string; color?: string }) {
  return (
    <div className="min-w-0">
      <div className="truncate text-[11px] text-[var(--text-muted)]">{label}</div>
      <div className="num truncate text-[19px] font-semibold leading-tight" style={color ? { color } : undefined}>{value}</div>
      {hint && <div className="truncate text-[11px] text-[var(--text-muted)]">{hint}</div>}
    </div>
  );
}

function ChartCard({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return (
    <div className="card min-h-[164px] px-4 py-3">
      <div className="mb-2.5 flex items-center justify-between gap-2 text-[12px]">
        <span className="font-medium text-[var(--text-secondary)]">{title}</span>
        {hint && <span className="num text-[var(--text-muted)]">{hint}</span>}
      </div>
      {children}
    </div>
  );
}

function Empty({ text }: { text: string }) {
  return <div className="grid h-[110px] place-items-center text-[12px] text-[var(--text-muted)]">{text}</div>;
}

function Legend({ items, total, column }: { items: Slice[]; total: number; column?: boolean }) {
  return (
    <div className={cn('mt-2 flex min-w-0 gap-x-3.5 gap-y-1 text-[11px] text-[var(--text-secondary)]', column ? 'mt-0 flex-col' : 'flex-wrap')}>
      {items.map((s) => (
        <span key={s.key} className="inline-flex min-w-0 items-center gap-1.5" title={s.name}>
          <i className="size-2 shrink-0 rounded-[2px]" style={{ background: s.color }} />
          <span className="truncate">{s.name}</span>
          <span className="num shrink-0 text-[var(--text-muted)]">{formatPercent(s.value / total)}</span>
        </span>
      ))}
    </div>
  );
}

function Donut({ items, total, label }: { items: Slice[]; total: number; label: string }) {
  let at = 0;
  const stops = items
    .map((s) => {
      const from = at;
      at += (s.value / total) * 360;
      return `${s.color} ${from}deg ${at}deg`;
    })
    .join(', ');
  return (
    <div className="grid size-[104px] shrink-0 place-items-center rounded-full" style={{ background: `conic-gradient(${stops})` }}>
      <div className="num grid size-[70px] place-items-center rounded-full bg-[var(--surface)] text-[12px] font-semibold">{label}</div>
    </div>
  );
}

function HealthTile({ tone, label, value }: { tone: 'green' | 'orange' | 'red' | 'neutral'; label: string; value: number }) {
  return (
    <div className="rounded-lg bg-[color-mix(in_oklab,var(--text)_5%,transparent)] px-2.5 py-2">
      <div className="num text-[18px] font-semibold leading-tight">{value}</div>
      <div className="flex items-center gap-1.5 truncate text-[11px] text-[var(--text-muted)]">
        <Dot tone={tone} />
        {label}
      </div>
    </div>
  );
}

type ProviderRow = {
  id: string;
  name: string;
  health: number;
  models: number;
  requests: number;
  successRate: number;
  ttft: number;
  tokens: number;
  cost: number;
  enabled: number;
  p: Provider;
  stat?: ProviderStat;
  days: number[];
};

const HEALTH_ORDER: Record<ProviderHealth, number> = { lowSuccess: 0, noKey: 1, healthy: 2, disabled: 3 };

/** The table view: one sortable row per provider over the cards' 7-day window. */
export function ProvidersTable({
  providers,
  stats7d,
  usage,
  labels,
  max,
  onOpen,
}: {
  providers: Provider[];
  stats7d: Map<string, ProviderStat>;
  usage: (p: Provider) => number[];
  labels: string[];
  max: number;
  onOpen: (p: Provider) => void;
}) {
  const { t } = useI18n();
  const rows: ProviderRow[] = providers.map((p) => {
    const s = stats7d.get(p.id);
    const days = usage(p);
    return {
      id: p.id,
      name: p.name,
      health: HEALTH_ORDER[providerHealth(p, s)],
      models: p.modelCount,
      requests: days.reduce((a, b) => a + b, 0),
      successRate: s && s.requests > 0 ? s.successRate : -1,
      ttft: s?.avgTtftMs ?? -1,
      tokens: s ? s.inputTokens + s.outputTokens : 0,
      cost: s?.costUsd ?? 0,
      enabled: p.enabled ? 1 : 0,
      p,
      stat: s,
      days,
    };
  });
  const columns: DataColumn<ProviderRow>[] = [
    {
      key: 'name',
      label: t('providers.col.provider'),
      render: (_, r) => (
        <button type="button" className="flex w-full min-w-0 items-center gap-2 text-left" onClick={() => onOpen(r.p)}>
          <span className={cn(!r.p.enabled && 'opacity-50 grayscale')}>
            <ProviderIcon name={r.p.name} icon={r.p.icon} colorKey={r.p.templateId} size={22} />
          </span>
          <span className="min-w-0">
            <span className={cn('block truncate font-medium hover:text-[var(--accent)]', !r.p.enabled && 'text-[var(--text-muted)]')}>{r.name}</span>
            <span className="block truncate font-mono text-[11px] text-[var(--text-muted)]">{hostOf(r.p.endpoints[0]?.baseUrl) ?? '—'}</span>
          </span>
        </button>
      ),
    },
    {
      key: 'health',
      label: t('providers.col.status'),
      width: 112,
      render: (_, r) => {
        const h = providerHealth(r.p, r.stat);
        return (
          <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-[12px] text-[var(--text-secondary)]">
            <Dot tone={HEALTH_TONE[h]} />
            {healthLabel(h, t)}
          </span>
        );
      },
    },
    { key: 'models', label: t('providers.col.models'), numeric: true, width: 56 },
    { key: 'days', label: t('providers.col.trend'), sortable: false, width: 84, render: (_, r) => <MiniBars data={r.days} labels={labels} max={max} /> },
    { key: 'requests', label: t('providers.col.requests'), numeric: true, width: 80, render: (_, r) => (r.requests ? formatTokens(r.requests, true) : '—') },
    {
      key: 'successRate',
      label: t('providers.col.successRate'),
      width: 124,
      render: (_, r) =>
        r.successRate < 0 ? (
          <span className="text-[var(--text-muted)]">—</span>
        ) : (
          <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
            <span className="h-[5px] w-10 overflow-hidden rounded-full bg-[var(--border)]">
              <i className="block h-full" style={{ width: `${r.successRate * 100}%`, background: successColor(r.successRate) }} />
            </span>
            <span className="num text-[12px]">{formatPercent(r.successRate)}</span>
          </span>
        ),
    },
    { key: 'ttft', label: t('providers.col.ttft'), numeric: true, width: 92, render: (_, r) => <Nowrap>{r.ttft < 0 ? '—' : formatMs(r.ttft)}</Nowrap> },
    {
      key: 'tokens',
      label: t('providers.col.tokens'),
      numeric: true,
      width: 128,
      render: (_, r) => (
        <Nowrap>{r.stat && r.tokens > 0 ? `${formatTokens(r.stat.inputTokens, true)} / ${formatTokens(r.stat.outputTokens, true)}` : '—'}</Nowrap>
      ),
    },
    { key: 'cost', label: t('providers.col.cost'), numeric: true, width: 96, render: (_, r) => <Nowrap>{r.cost > 0 ? formatUsd(r.cost) : '—'}</Nowrap> },
    {
      key: 'enabled',
      label: t('providers.col.enabled'),
      width: 64,
      render: (_, r) => <ProviderEnabledSwitch provider={r.p} />,
    },
  ];
  return (
    <SortableDataTable<ProviderRow>
      caption={t('nav.providers')}
      rowKey="id"
      rows={rows}
      columns={columns}
      defaultSort={{ key: 'requests', direction: 'desc' }}
    />
  );
}

const Nowrap = ({ children }: { children: React.ReactNode }) => <span className="whitespace-nowrap">{children}</span>;

export function hostOf(url: string | undefined): string | null {
  if (!url) return null;
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

/** A provider's enabled switch; its row or card keeps the click to itself. */
export function ProviderEnabledSwitch({ provider }: { provider: Provider }) {
  const { t } = useI18n();
  const update = useUpdateProvider(provider.id);
  const { toast } = useFeedback();
  return (
    <span className="ml-1 inline-flex" onClick={(e) => e.stopPropagation()}>
      <Switch
        checked={provider.enabled}
        label={t('common.enabled')}
        onChange={(enabled) => update.mutate({ enabled }, { onError: (e) => toast(errorText(e), 'error') })}
      />
    </span>
  );
}
