import { useIsFetching, useQueryClient } from '@tanstack/react-query';
import { Activity, ArrowRight, RefreshCw } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Link } from 'react-router';

import { useRequests, useSettings, useStatsSummary, useTimeseries, useTopModels } from '../api/hooks';
import type { ClientKind, Range, RequestSummary, TimeseriesPoint } from '../api/types';
import { BarChart } from '../components/arc/bar-chart/bar-chart';
import { SortableDataTable, type DataColumn } from '../components/arc/sortable-data-table/sortable-data-table';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../components/arc/tabs/tabs';
import { CLIENT_META, CLIENT_ORDER, ClientGlyph, ClientIcon } from '../components/icons';
import { Page } from '../components/layout/Page';
import { Badge, Button, CodeBlock, EmptyState, Segmented, Spinner, type SegmentItem } from '../components/ui/controls';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';
import { formatMs, formatNanos, formatPercent, formatTime, formatTokens, formatTps, formatUsd, formatUsdAxis } from '../lib/format';
import {
  breakdownRows,
  bucketAxisLabel,
  bucketFullLabel,
  bucketTotals,
  fillBuckets,
  rangeBuckets,
  rangeStart,
  type BreakdownRow,
  type Measure,
} from '../lib/stats';
import { cacheHitRatio } from '../lib/usage';
import { RequestDetailSheet } from './RequestsPage';

type ClientFilterValue = ClientKind | 'all';
type Section = 'requests' | 'providers' | 'models';
type ModelBreakdownRow = BreakdownRow & { cacheHit: number };

/**
 * The chart rounds its average to whole units, so small measures are plotted scaled up: cost in micro-USD
 * to keep cents visible, requests in hundredths so a sub-1 hourly average reads "0.46" instead of "0".
 */
const CHART_SCALE: Record<Measure, number> = { costUsd: 1_000_000, requests: 100, tokens: 1 };

type LogRow = {
  id: string;
  time: number;
  client: string;
  provider: string;
  model: string;
  input: number;
  output: number;
  cache: number;
  cost: number;
  speed: number;
  r: RequestSummary;
};

export function OverviewPage() {
  const { t } = useI18n();
  const qc = useQueryClient();
  const [range, setRange] = useState<Range>('today');
  const [client, setClient] = useState<ClientFilterValue>('all');
  const [measure, setMeasure] = useState<Measure>('requests');
  const clientKind = client === 'all' ? undefined : client;
  const summary = useStatsSummary(range, clientKind);
  // by=provider: the chart sums every series per bucket, and the provider tab reuses the same rows.
  const series = useTimeseries(range, range === 'today' ? 'hour' : 'day', 'provider', clientKind);
  const settings = useSettings();
  const fetching = useIsFetching({ queryKey: ['stats'] }) > 0;

  const s = summary.data;
  const empty = s && s.requests === 0;
  const rangeLabel = t(`range.${range}`);

  const chart = useMemo(() => {
    const totals = bucketTotals(series.data ?? [], measure);
    return fillBuckets(totals, rangeBuckets(range)).map((p) => ({
      key: p.bucket,
      label: bucketFullLabel(p.bucket),
      axisLabel: bucketAxisLabel(p.bucket),
      value: Math.round(p.value * CHART_SCALE[measure]),
    }));
  }, [series.data, measure, range]);
  const measureLabel = { costUsd: t('overview.cost'), requests: t('overview.requests'), tokens: t('overview.tokens') }[measure];
  const formatMeasure = (v: number) => {
    const n = v / CHART_SCALE[measure];
    return measure === 'costUsd' ? formatUsdAxis(n) : formatTokens(Number(n.toFixed(2)), measure === 'tokens');
  };

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['stats'] });
    void qc.invalidateQueries({ queryKey: ['requests'] });
  };

  return (
    <Page
      title={t('nav.overview')}
      actions={
        <Button
          variant="plain"
          icon={<RefreshCw className={cn('size-4', fetching && 'animate-spin')} />}
          aria-label={t('common.refresh')}
          onClick={refresh}
        />
      }
    >
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <ClientFilter value={client} onChange={setClient} />
        <Segmented
          ariaLabel={t('overview.range')}
          value={range}
          onChange={setRange}
          items={[
            { value: 'today', label: t('range.today') },
            { value: '7d', label: t('range.7d') },
            { value: '30d', label: t('range.30d') },
            { value: '90d', label: t('range.90d') },
          ]}
        />
      </div>

      {!s ? (
        <Spinner lines={4} />
      ) : (
        <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
          <Stat label={t('overview.totalCost')} value={formatUsd(s.costUsd)} context={rangeLabel} />
          <Stat label={t('overview.totalRequests')} value={formatTokens(s.requests)} context={rangeLabel} />
          <Stat
            label={t('overview.tokens')}
            value={formatTokens(s.inputTokens + s.outputTokens, true)}
            context={t('overview.tokensContext', { input: formatTokens(s.inputTokens, true), output: formatTokens(s.outputTokens, true) })}
          />
          <Stat
            label={t('usage.hitRate')}
            value={formatPercent(cacheHitRatio(s.cacheReadTokens, s.inputTokens) ?? 0)}
            context={t('usage.cacheAmounts', { read: formatTokens(s.cacheReadTokens, true), write: formatTokens(s.cacheWriteTokens, true) })}
          />
          <Stat label={t('overview.successRate')} value={s.requests ? formatPercent(s.successRate) : '—'} context={rangeLabel} />
          <Stat label={t('overview.avgTtft')} value={formatMs(s.avgTtftMs)} context={s.avgTtftMs == null ? t('common.noData') : rangeLabel} />
        </div>
      )}

      {empty ? (
        <div className="card mt-4">
          {clientKind ? (
            <EmptyState
              icon={<ClientIcon kind={clientKind} size={32} />}
              title={t('overview.clientEmpty.title', { client: CLIENT_META[clientKind].name })}
              detail={t('overview.clientEmpty.detail', { range: rangeLabel })}
            />
          ) : (
            <EmptyState
              icon={<Activity className="size-6" />}
              title={t('overview.empty.title')}
              detail={t('overview.empty.detail')}
              action={settings.data && <CodeBlock language="text" filename={t('settings.gatewayUrl')}>{`${settings.data.gatewayBaseUrl}/v1`}</CodeBlock>}
            />
          )}
        </div>
      ) : (
        <div className="mt-4 flex flex-col gap-4">
          <section className="card p-5">
            <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
              <div className="flex min-w-0 items-center gap-3">
                <h2 className="text-[13px] font-medium">
                  {t('overview.usageTrend')} · {rangeLabel}
                </h2>
                <span className="inline-flex items-center gap-1.5 text-[12px] text-[var(--text-secondary)]">
                  <span aria-hidden className="size-2.5 rounded-[3px] bg-[var(--accent)]" />
                  {measureLabel}
                </span>
              </div>
              <Segmented
                ariaLabel={t('overview.measure')}
                value={measure}
                onChange={setMeasure}
                items={[
                  { value: 'requests', label: t('overview.requests') },
                  { value: 'tokens', label: t('overview.tokens') },
                  { value: 'costUsd', label: t('overview.cost') },
                ]}
              />
            </div>
            {series.isLoading ? (
              <Spinner lines={4} />
            ) : (
              <BarChart
                data={chart}
                label={measureLabel}
                period={rangeLabel}
                height={180}
                formatValue={formatMeasure}
                averageLabel={range === 'today' ? t('overview.hourlyAverage') : t('overview.dailyAverage')}
                valueLabel={t('overview.total')}
                categoryLabel={range === 'today' ? t('overview.hour') : t('overview.day')}
              />
            )}
          </section>

          <Breakdown range={range} clientKind={clientKind} series={series.data ?? []} seriesLoading={series.isLoading} />
        </div>
      )}
    </Page>
  );
}

/**
 * cc-switch style client switcher on Arc's segmented control: "All" plus one official logo per supported
 * client. Logo segments carry no visible text, so the client name rides along visually hidden (accessible
 * name) and as a hover title.
 */
function ClientFilter({ value, onChange }: { value: ClientFilterValue; onChange: (v: ClientFilterValue) => void }) {
  const { t } = useI18n();
  const items: SegmentItem<ClientFilterValue>[] = [
    { value: 'all', label: t('overview.allClients') },
    ...CLIENT_ORDER.map((k) => ({
      value: k,
      label: '',
      accessory: (
        <span className="inline-flex items-center" title={CLIENT_META[k].name}>
          <ClientGlyph kind={k} size={18} />
          <span className="sr-only">{CLIENT_META[k].name}</span>
        </span>
      ),
    })),
  ];
  return <Segmented ariaLabel={t('overview.clientFilter')} value={value} onChange={onChange} items={items} />;
}

function Stat({ label, value, context }: { label: string; value: string; context: string }) {
  return (
    <article className="card min-w-0 px-4 py-3">
      <div className="truncate text-[12px] text-[var(--text-secondary)]">{label}</div>
      <div className="num mt-1 truncate text-[22px] leading-tight font-semibold selectable" title={value}>
        {value}
      </div>
      <div className="mt-1 truncate text-[11px] text-[var(--text-muted)]" title={context}>
        {context}
      </div>
    </article>
  );
}

/** Recent requests, per-provider and per-model breakdowns for the selected client and range. */
function Breakdown({ range, clientKind, series, seriesLoading }: { range: Range; clientKind?: ClientKind; series: TimeseriesPoint[]; seriesLoading: boolean }) {
  const { t, locale } = useI18n();
  const [section, setSection] = useState<Section>('requests');
  const [selected, setSelected] = useState<string | null>(null);
  const from = useMemo(() => rangeStart(range)?.toISOString(), [range]);
  const requests = useRequests({ client: clientKind, from, page: 1, pageSize: 20 }, section === 'requests');
  const top = useTopModels(range, clientKind, 20);

  const logRows: LogRow[] = (requests.data?.items ?? []).map((r) => ({
    id: r.id,
    time: Date.parse(r.startedAtUtc),
    client: r.clientKind ? (CLIENT_META[r.clientKind as ClientKind]?.name ?? r.clientKind) : '—',
    provider: r.providerName ?? '',
    model: r.requestedModel ?? '',
    input: r.totalInputTokens,
    output: r.totalOutputTokens,
    cache: r.cacheReadTokens,
    cost: r.costNanoUsd,
    speed: r.outputTps ?? -1,
    r,
  }));
  const logColumns: DataColumn<LogRow>[] = [
    { key: 'time', label: t('requests.col.time'), width: 92, render: (_, row) => <span className="num">{formatTime(row.r.startedAtUtc, locale)}</span> },
    {
      key: 'client',
      label: t('requests.col.client'),
      width: 150,
      render: (v, row) => (
        <span className="flex min-w-0 items-center gap-1.5">
          {row.r.clientKind && row.r.clientKind in CLIENT_META && <ClientGlyph kind={row.r.clientKind as ClientKind} size={16} />}
          <span className="truncate">{String(v)}</span>
        </span>
      ),
    },
    { key: 'provider', label: t('requests.col.provider'), width: 140, render: (v) => <span className="block truncate">{String(v || '—')}</span> },
    {
      key: 'model',
      label: t('requests.col.model'),
      render: (_, row) => (
        <button type="button" className="flex w-full min-w-0 items-center gap-2 text-left" onClick={() => setSelected(row.id)} aria-label={t('requests.openDetail', { model: row.model || row.id })}>
          <span className="truncate font-mono text-[12px] hover:text-[var(--accent)]" title={row.model}>{row.model || '—'}</span>
          {row.r.status !== 'success' && <Badge tone={row.r.status === 'client_cancelled' ? 'orange' : 'red'}>{t(`status.${row.r.status}`)}</Badge>}
        </button>
      ),
    },
    { key: 'input', label: t('overview.col.input'), numeric: true, width: 84, render: (_, row) => usageCell(row.r, formatTokens(row.input, true)) },
    { key: 'output', label: t('overview.col.output'), numeric: true, width: 84, render: (_, row) => usageCell(row.r, formatTokens(row.output, true)) },
    {
      key: 'cache',
      label: t('overview.col.cacheHit'),
      numeric: true,
      width: 96,
      render: (_, row) => usageCell(row.r, row.cache > 0 ? formatTokens(row.cache, true) : '—'),
    },
    {
      key: 'cost',
      label: t('requests.col.cost'),
      numeric: true,
      width: 104,
      render: (_, row) => <span className={cn('num whitespace-nowrap', row.r.usageSource === 'missing' && 'text-[var(--warning)]')}>{formatNanos(row.r.costNanoUsd)}</span>,
    },
    { key: 'speed', label: t('overview.col.speed'), numeric: true, width: 100, render: (_, row) => <span className="num text-[var(--text-secondary)]">{formatTps(row.r.outputTps)}</span> },
  ];

  const providerRows = useMemo(() => breakdownRows(series.map((p) => ({ key: p.key, requests: p.requests, tokens: p.tokens, costUsd: p.costUsd }))), [series]);
  const modelRows = useMemo<ModelBreakdownRow[]>(() => {
    // -1 (shown as "—") when a model had no input tokens to hit, so it sorts below a real 0%.
    const hit = new Map((top.data ?? []).map((m) => [m.model, cacheHitRatio(m.cacheReadTokens, m.inputTokens) ?? -1]));
    return breakdownRows((top.data ?? []).map((m) => ({ key: m.model, requests: m.requests, tokens: m.tokens, costUsd: m.costUsd }))).map((r) => ({
      ...r,
      cacheHit: hit.get(r.key) ?? -1,
    }));
  }, [top.data]);
  const breakdownColumns = <T extends BreakdownRow>(nameLabel: string, extra: DataColumn<T>[] = []): DataColumn<T>[] => [
    { key: 'key', label: nameLabel, render: (v) => <span className="block truncate font-mono text-[12px]">{String(v)}</span> },
    { key: 'requests', label: t('overview.requests'), numeric: true, width: 110, render: (v) => formatTokens(Number(v)) },
    { key: 'tokens', label: t('overview.tokens'), numeric: true, width: 110, render: (v) => formatTokens(Number(v), true) },
    ...extra,
    { key: 'costUsd', label: t('overview.cost'), numeric: true, width: 110, render: (v) => formatUsd(Number(v)) },
    { key: 'share', label: t('overview.col.share'), numeric: true, width: 150, render: (v) => <ShareBar ratio={Number(v)} /> },
  ];
  const cacheHitColumn: DataColumn<ModelBreakdownRow> = {
    key: 'cacheHit',
    label: t('usage.hitRate'),
    numeric: true,
    width: 120,
    render: (v) => <span className={cn('num', Number(v) < 0 && 'text-[var(--text-muted)]')}>{Number(v) < 0 ? '—' : formatPercent(Number(v))}</span>,
  };

  return (
    <section>
      <Tabs value={section} onValueChange={(v) => setSection(v as Section)}>
        <div className="flex items-center justify-between gap-2">
          <TabsList aria-label={t('overview.breakdown')}>
            <TabsTrigger value="requests">{t('nav.requests')}</TabsTrigger>
            <TabsTrigger value="providers">{t('nav.providers')}</TabsTrigger>
            <TabsTrigger value="models">{t('nav.models')}</TabsTrigger>
          </TabsList>
          {section === 'requests' && (
            <Link to="/requests" className="inline-flex items-center gap-1 text-[12px] text-[var(--text-secondary)] hover:text-[var(--accent)]">
              {t('overview.viewAll')}
              <ArrowRight className="size-3.5" />
            </Link>
          )}
        </div>
        <TabsContent value="requests">
          {requests.isLoading ? (
            <Spinner lines={4} />
          ) : (
            <SortableDataTable<LogRow> caption={t('nav.requests')} rows={logRows} columns={logColumns} rowKey="id" emptyMessage={t('common.noData')} />
          )}
        </TabsContent>
        <TabsContent value="providers">
          {seriesLoading ? (
            <Spinner lines={3} />
          ) : (
            <SortableDataTable<BreakdownRow>
              caption={t('nav.providers')}
              rows={providerRows}
              columns={breakdownColumns(t('requests.col.provider'))}
              rowKey="key"
              defaultSort={{ key: 'costUsd', direction: 'desc' }}
              emptyMessage={t('common.noData')}
            />
          )}
        </TabsContent>
        <TabsContent value="models">
          {top.isLoading ? (
            <Spinner lines={3} />
          ) : (
            <SortableDataTable<ModelBreakdownRow>
              caption={t('overview.topModels')}
              rows={modelRows}
              columns={breakdownColumns(t('requests.col.model'), [cacheHitColumn])}
              rowKey="key"
              defaultSort={{ key: 'costUsd', direction: 'desc' }}
              emptyMessage={t('common.noData')}
            />
          )}
        </TabsContent>
      </Tabs>
      <RequestDetailSheet id={selected} onClose={() => setSelected(null)} />
    </section>
  );
}

/** Token counts are meaningless when the upstream reported no usage. */
function usageCell(r: RequestSummary, text: string) {
  return <span className={cn('num', r.usageSource === 'missing' && 'opacity-50')}>{r.usageSource === 'missing' ? '—' : text}</span>;
}

function ShareBar({ ratio }: { ratio: number }) {
  return (
    <span className="inline-flex w-full items-center justify-end gap-2">
      <span aria-hidden className="h-1.5 w-16 overflow-hidden rounded-full bg-[var(--surface-muted)]">
        <span className="block h-full rounded-full bg-[var(--accent)]" style={{ width: `${Math.min(100, ratio * 100).toFixed(1)}%` }} />
      </span>
      <span className="num w-12 text-right">{formatPercent(ratio)}</span>
    </span>
  );
}
