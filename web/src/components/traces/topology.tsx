import { useMemo, useState, type ReactNode } from 'react';
import { Link } from 'react-router';

import { useProviders, useTraceTopology } from '../../api/hooks';
import { useLiveRequestFeed, useLiveRequestList, useLiveRequestsConnected } from '../../api/liveRequests';
import type { ClientKind, TraceRange, TraceTopology } from '../../api/types';
import { useI18n, type TFunction } from '../../i18n';
import { cn } from '../../lib/cn';
import { formatDateTime, formatMs, formatPercent } from '../../lib/format';
import { ClientIcon, clientName, ProviderIcon } from '../icons';
import { Segmented, Spinner } from '../ui/controls';
import { TraceCanvas } from './canvas';
import { TraceError } from './error';
import { Inspector } from './inspector';
import { buildTopology, formatUs, HUB_ID, type ProviderBrand } from './model';

/** Brand data of every provider (icon, template), by id. */
export function useProviderBrands(): Map<string, ProviderBrand> {
  const providers = useProviders();
  return useMemo(() => new Map((providers.data ?? []).map((p) => [p.id, { name: p.name, icon: p.icon, templateId: p.templateId }])), [providers.data]);
}

export const labelOfClient = (kind: string | null | undefined) => (kind ? clientName(kind) : '');

/** A · the whole traffic picture: clients → Astra → providers, live. */
export function TopologyView() {
  const { t, locale } = useI18n();
  const [range, setRange] = useState<TraceRange>('live');
  const query = useTraceTopology(range);
  const brands = useProviderBrands();
  const [selected, setSelected] = useState<string | null>(null);
  // Requests in flight ride the lines (the live request feed; it only records while someone watches).
  useLiveRequestFeed(true);
  const live = useLiveRequestList();
  const liveConnected = useLiveRequestsConnected();
  const graph = useMemo(
    () => (query.data ? buildTopology(query.data, t, labelOfClient, brands, live) : { nodes: [], edges: [] }),
    [query.data, t, brands, live],
  );
  const data = query.data;
  const inFlight = liveConnected ? live.filter((r) => r.status === 'pending').length : (data?.gateway.inFlight ?? 0);

  return (
    <div className="flex h-full min-h-0 flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        {data ? (
          <>
            <Chip>
              <span className="inline-block size-[7px] rounded-full bg-[var(--green)]" />
              <b>{data.gateway.requests}</b> {t('traces.topology.requestsIn', { window: t(`traces.range.${range}` as 'traces.range.live') })}
            </Chip>
            <Chip>
              {inFlight > 0 ? <span className="trace-live-pulse" /> : null}
              {t('traces.kv.inFlight')} <b>{inFlight}</b>
            </Chip>
            <Chip>
              {t('traces.kv.errorRate')} <b>{formatPercent(data.gateway.requests ? data.gateway.errors / data.gateway.requests : 0)}</b>
            </Chip>
            <Chip>
              {t('traces.kv.overheadP95')} <b>{formatUs(data.gateway.overheadP95Us)}</b>
            </Chip>
          </>
        ) : null}
        <span className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-[var(--text-secondary)] max-lg:hidden">
          <span>{t('traces.topology.legend')}</span>
          <LiveLegend color="var(--accent)">{t('traces.live.legend.out')}</LiveLegend>
          <LiveLegend color="var(--green)">{t('traces.live.legend.back')}</LiveLegend>
          <LiveLegend color="var(--orange)">{t('traces.live.legend.slow')}</LiveLegend>
          <LiveLegend color="var(--red)">{t('traces.live.legend.failed')}</LiveLegend>
        </span>
        <Segmented
          className="ml-auto"
          ariaLabel={t('traces.range.label')}
          value={range}
          onChange={setRange}
          items={(['live', '1h', '24h'] as const).map((r) => ({ value: r, label: t(`traces.range.${r}`) }))}
        />
      </div>
      {query.isLoading && !data ? (
        <Spinner lines={6} />
      ) : query.isError && !data ? (
        <TraceError error={query.error} />
      ) : data && data.gateway.requests === 0 && live.length === 0 ? (
        <div className="card flex flex-1 items-center justify-center p-10 text-[13px] text-[var(--text-secondary)]">{t('traces.topology.empty')}</div>
      ) : (
        <TraceCanvas
          className="min-h-0 flex-1"
          graph={graph}
          layoutKey="topology"
          selected={selected}
          onSelect={setSelected}
          inspector={data && selected ? <TopologyInspector data={data} id={selected} t={t} locale={locale} brands={brands} /> : null}
        />
      )}
    </div>
  );
}

function LiveLegend({ color, children }: { color: string; children: ReactNode }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className="inline-block size-2 rounded-full" style={{ background: color }} />
      {children}
    </span>
  );
}

function Chip({ children }: { children: ReactNode }) {
  return (
    <span className="inline-flex h-6 items-center gap-1.5 rounded-full bg-[var(--surface-muted)] px-2.5 text-[12px] whitespace-nowrap text-[var(--text-secondary)] [&_b]:font-semibold [&_b]:text-[var(--foreground)]">
      {children}
    </span>
  );
}

export { Chip as TraceChip };

function TopologyInspector({ data, id, t, locale, brands }: { data: TraceTopology; id: string; t: TFunction; locale: string; brands: Map<string, ProviderBrand> }) {
  if (id === HUB_ID) {
    const g = data.gateway;
    return (
      <Inspector
        kind={t('traces.kind.gateway')}
        title={t('traces.topology.gateway')}
        sub={t('traces.topology.inFlightCount', { count: g.inFlight })}
        rows={[
          [t('traces.kv.requests'), g.requests],
          [t('traces.kv.errors'), g.errors],
          [t('traces.kv.overheadP50'), formatUs(g.overheadP50Us)],
          [t('traces.kv.overheadP95'), formatUs(g.overheadP95Us)],
          [t('traces.kv.failovers'), g.failovers],
          ...g.stages
            .filter((s) => s.samples > 0)
            .map((s): [string, ReactNode] => [t(`traces.stage.${s.name}` as 'traces.stage.gateway.route'), `${formatUs(s.p50Us)} / ${formatUs(s.p95Us)}`]),
        ]}
        code={{ title: t('traces.topology.overheadNoteTitle'), lines: [t('traces.topology.overheadNote')] }}
      />
    );
  }
  if (id.startsWith('client:')) {
    const kind = id.slice('client:'.length);
    const c = data.clients.find((x) => (x.clientKind ?? 'direct') === kind);
    if (!c) return null;
    return (
      <Inspector
        kind={t('traces.kind.agent')}
        icon={c.clientKind ? <ClientIcon kind={c.clientKind as ClientKind} size={32} /> : undefined}
        title={c.clientKind ? labelOfClient(c.clientKind) : t('traces.topology.direct')}
        rows={[
          [t('traces.kv.requests'), c.requests],
          [t('traces.kv.errors'), c.errors],
          [t('traces.kv.rate'), c.perMinute.toFixed(1)],
          [t('traces.kv.activeSessions'), c.activeSessions],
        ]}
        actions={
          <Link className="text-[12.5px] text-[var(--accent)]" to={`/traces/sessions${c.clientKind ? `?client=${encodeURIComponent(c.clientKind)}` : ''}`}>
            {t('traces.viewSessions')} →
          </Link>
        }
      />
    );
  }
  const p = data.providers.find((x) => `provider:${x.providerId}` === id);
  if (!p) return null;
  const brand = brands.get(p.providerId);
  return (
    <div className="flex flex-col gap-3">
      <Inspector
        kind={t('traces.kind.provider')}
        icon={<ProviderIcon name={p.name} icon={brand?.icon ?? p.icon} colorKey={brand?.templateId ?? p.templateId} size={32} />}
        title={p.name}
        rows={[
          [t('traces.kv.requests'), p.requests],
          [t('traces.kv.errors'), p.errors],
          [t('traces.kv.attempts'), p.attempts],
          [t('traces.kv.rateLimited'), p.rateLimited],
          [t('traces.kv.failovers'), p.failovers],
          [t('traces.kv.ttfbP95'), formatMs(p.ttfbP95Ms)],
        ]}
      />
      {p.accounts.length > 0 ? (
        <div className="flex flex-col gap-2">
          <span className="text-[11px] text-[var(--text-secondary)]">{t('traces.account.title')}</span>
          {p.accounts.map((a) => (
            <div key={a.id} className="flex items-baseline justify-between gap-3 text-[12px]">
              <span className="shrink-0">{a.name}</span>
              <span className={cn('min-w-0 text-right', a.status === 'ok' ? 'text-[var(--text-secondary)]' : 'text-[var(--orange)]')}>
                {a.status === 'cooldown' && a.cooldownUntilUtc
                  ? t('traces.account.cooldown', { time: formatDateTime(a.cooldownUntilUtc, locale) })
                  : t(`traces.account.${a.status}` as 'traces.account.ok')}
                {a.remainingRequests ? ` · ${t('traces.account.remaining', { count: a.remainingRequests })}` : ''}
              </span>
            </div>
          ))}
        </div>
      ) : null}
    </div>
  );
}
