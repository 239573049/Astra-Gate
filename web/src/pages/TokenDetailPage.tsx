import { ArrowLeft, ScrollText } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';

import { useProviderStats, useProviders, useRequests, useStatsSummary, useTimeseries, useToken, useTopModels } from '../api/hooks';
import type { ProviderStat, Range, RequestSummary, TopModel } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { BarChart } from '../components/arc/bar-chart/bar-chart';
import { SortableDataTable, type DataColumn } from '../components/arc/sortable-data-table/sortable-data-table';
import { Page } from '../components/layout/Page';
import { healthTone, useHealthLabel } from '../components/TokenPoolEditor';
import { Badge, Button, Dot, EmptyState, Segmented, Spinner } from '../components/ui/controls';
import { errorText } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { formatMs, formatPercent, formatTime, formatTokens, formatUsd } from '../lib/format';
import { bucketAxisLabel, bucketFullLabel, bucketTotals, fillBuckets, rangeBuckets } from '../lib/stats';
import { cacheHitRatio } from '../lib/usage';

type ProviderRow = {
  id: string;
  name: string;
  requests: number;
  share: number;
  success: number;
  ttft: number | null;
  cost: number;
  out: number;
  in: number;
};

type ModelRow = { model: string; requests: number; tokens: number; cost: number };

type LogRow = { id: string; time: number; model: string; provider: string; status: string; cost: number; r: RequestSummary };

/**
 * One token's statistics (plan: token logs and stats): totals for a range, the trend, which models and providers served
 * it — with provider-pool failovers and live member health — and its latest requests, linking to the full request log.
 */
export function TokenDetailPage() {
  const { id = '' } = useParams();
  const { t, locale } = useI18n();
  const navigate = useNavigate();
  const [range, setRange] = useState<Range>('7d');
  const token = useToken(id);
  const providers = useProviders();
  const summary = useStatsSummary(range, undefined, id);
  const series = useTimeseries(range, range === 'today' ? 'hour' : 'day', 'token', undefined, id);
  const models = useTopModels(range, undefined, 20, id);
  const byProvider = useProviderStats(range, id);
  const recent = useRequests({ token: id, page: 1, pageSize: 10 }, false);
  const healthLabel = useHealthLabel();
  const back = <Button variant="glass" icon={<ArrowLeft className="size-4" />} aria-label={t('common.back')} onClick={() => navigate('/tokens')} />;

  const rangeLabel = t(`range.${range}`);
  const chart = useMemo(
    () =>
      fillBuckets(bucketTotals(series.data ?? [], 'requests'), rangeBuckets(range)).map((p) => ({
        key: p.bucket,
        label: bucketFullLabel(p.bucket),
        axisLabel: bucketAxisLabel(p.bucket),
        value: Math.round(p.value),
      })),
    [series.data, range],
  );
  const names = new Map((providers.data ?? []).map((p) => [p.id, p.name]));
  const totalRequests = (byProvider.data ?? []).reduce((sum, p) => sum + p.requests, 0);
  const providerRows: ProviderRow[] = (byProvider.data ?? []).map((p: ProviderStat) => ({
    id: p.providerId,
    name: names.get(p.providerId) ?? p.name ?? p.providerId,
    requests: p.requests,
    share: totalRequests ? p.requests / totalRequests : 0,
    success: p.successRate,
    ttft: p.avgTtftMs ?? null,
    cost: p.costUsd,
    out: p.failoversOut,
    in: p.failoversIn,
  }));
  const providerColumns: DataColumn<ProviderRow>[] = [
    { key: 'name', label: t('tokens.detail.provider'), sortable: true },
    { key: 'requests', label: t('overview.requests'), sortable: true, numeric: true, render: (v) => formatTokens(v as number) },
    { key: 'share', label: t('tokens.detail.share'), sortable: true, numeric: true, render: (v) => formatPercent(v as number) },
    { key: 'success', label: t('overview.successRate'), sortable: true, numeric: true, render: (v, r) => (r.requests ? formatPercent(v as number) : '—') },
    { key: 'ttft', label: t('overview.avgTtft'), sortable: true, numeric: true, render: (v) => formatMs(v as number | null) },
    { key: 'cost', label: t('overview.cost'), sortable: true, numeric: true, render: (v) => formatUsd(v as number) },
    { key: 'out', label: t('tokens.detail.failoversOut'), sortable: true, numeric: true },
    { key: 'in', label: t('tokens.detail.failoversIn'), sortable: true, numeric: true },
  ];
  const modelRows: ModelRow[] = (models.data ?? []).map((m: TopModel) => ({ model: m.model, requests: m.requests, tokens: m.tokens, cost: m.costUsd }));
  const modelColumns: DataColumn<ModelRow>[] = [
    { key: 'model', label: t('tokens.detail.model'), sortable: true },
    { key: 'requests', label: t('overview.requests'), sortable: true, numeric: true, render: (v) => formatTokens(v as number) },
    { key: 'tokens', label: t('overview.tokens'), sortable: true, numeric: true, render: (v) => formatTokens(v as number, true) },
    { key: 'cost', label: t('overview.cost'), sortable: true, numeric: true, render: (v) => formatUsd(v as number) },
  ];
  const logRows: LogRow[] = (recent.data?.items ?? []).map((r) => ({
    id: r.id,
    time: new Date(r.startedAtUtc).getTime(),
    model: r.requestedModel ?? '—',
    provider: r.providerName ?? r.providerId ?? '—',
    status: r.status,
    cost: r.costNanoUsd / 1e9,
    r,
  }));
  const logColumns: DataColumn<LogRow>[] = [
    { key: 'time', label: t('tokens.detail.time'), render: (_, row) => formatTime(row.r.startedAtUtc, locale) },
    { key: 'model', label: t('tokens.detail.model') },
    { key: 'provider', label: t('tokens.detail.provider') },
    { key: 'status', label: t('tokens.detail.status'), render: (v) => <Badge tone={v === 'success' ? 'green' : 'red'}>{t(`status.${v as Exclude<RequestSummary['status'], 'pending'>}`)}</Badge> },
    { key: 'cost', label: t('overview.cost'), numeric: true, render: (v) => formatUsd(v as number) },
  ];

  if (token.isError)
    return (
      <Page title={t('nav.tokens')} leading={back}>
        <Alert tone="danger" title={t('common.error')}>{errorText(token.error)}</Alert>
      </Page>
    );
  const tk = token.data;
  const s = summary.data;
  return (
    <Page
      title={tk?.name ?? ''}
      subtitle={tk ? <code className="font-mono">{tk.keyMasked}</code> : undefined}
      leading={back}
      actions={
        <Link to={`/requests?token=${encodeURIComponent(id)}`}>
          <Button icon={<ScrollText className="size-3.5" />}>{t('tokens.detail.viewLogs')}</Button>
        </Link>
      }
    >
      <div className="mx-auto flex max-w-[1100px] flex-col gap-4">
        <div className="flex justify-end">
          <Segmented
            ariaLabel={t('overview.range')}
            value={range}
            onChange={setRange}
            items={(['today', '7d', '30d', '90d'] as const).map((r) => ({ value: r, label: t(`range.${r}`) }))}
          />
        </div>

        {!s ? (
          <Spinner lines={3} />
        ) : (
          <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5">
            <Stat label={t('overview.totalCost')} value={formatUsd(s.costUsd)} context={rangeLabel} />
            <Stat label={t('overview.totalRequests')} value={formatTokens(s.requests)} context={rangeLabel} />
            <Stat
              label={t('overview.tokens')}
              value={formatTokens(s.inputTokens + s.outputTokens, true)}
              context={t('overview.tokensContext', { input: formatTokens(s.inputTokens, true), output: formatTokens(s.outputTokens, true) })}
            />
            <Stat label={t('usage.hitRate')} value={formatPercent(cacheHitRatio(s.cacheReadTokens, s.inputTokens) ?? 0)} context={rangeLabel} />
            <Stat label={t('overview.successRate')} value={s.requests ? formatPercent(s.successRate) : '—'} context={formatMs(s.avgTtftMs)} />
          </div>
        )}

        {tk?.pool && (
          <section className="card p-5">
            <h2 className="mb-3 text-[13px] font-medium">
              {t('tokens.pool.title')} · {t(`tokens.strategy.${tk.pool.strategy}`)}
            </h2>
            <div className="flex flex-wrap gap-x-5 gap-y-2 text-[13px]">
              {tk.pool.members.map((m) => (
                <span key={m.providerId} className="inline-flex items-center gap-2">
                  <Dot tone={healthTone(m.health.state)} />
                  {names.get(m.providerId) ?? m.providerId}
                  <span className="text-[12px] text-[var(--text-muted)]">{healthLabel(m.health)}</span>
                </span>
              ))}
            </div>
          </section>
        )}

        {s && s.requests === 0 ? (
          <div className="card">
            <EmptyState title={t('overview.tokenEmpty.title', { token: tk?.name ?? '' })} detail={t('overview.tokenEmpty.detail', { range: rangeLabel })} />
          </div>
        ) : (
          <>
            <section className="card p-5">
              <h2 className="mb-3 text-[13px] font-medium">
                {t('overview.usageTrend')} · {rangeLabel}
              </h2>
              {series.isLoading ? (
                <Spinner lines={4} />
              ) : (
                <BarChart
                  data={chart}
                  label={t('overview.requests')}
                  period={rangeLabel}
                  height={160}
                  formatValue={(v) => formatTokens(v)}
                  averageLabel={range === 'today' ? t('overview.hourlyAverage') : t('overview.dailyAverage')}
                  valueLabel={t('overview.total')}
                  categoryLabel={range === 'today' ? t('overview.hour') : t('overview.day')}
                />
              )}
            </section>
            <section className="card p-5">
              <h2 className="mb-1 text-[13px] font-medium">{t('tokens.detail.byProvider')}</h2>
              <p className="mb-3 text-[12px] text-[var(--text-muted)]">{t('tokens.detail.byProviderHint')}</p>
              <SortableDataTable<ProviderRow> caption={t('tokens.detail.byProvider')} rows={providerRows} columns={providerColumns} rowKey="id" emptyMessage={t('common.noData')} />
            </section>
            <section className="card p-5">
              <h2 className="mb-3 text-[13px] font-medium">{t('tokens.detail.byModel')}</h2>
              <SortableDataTable<ModelRow> caption={t('tokens.detail.byModel')} rows={modelRows} columns={modelColumns} rowKey="model" emptyMessage={t('common.noData')} />
            </section>
          </>
        )}

        <section className="card p-5">
          <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
            <h2 className="text-[13px] font-medium">{t('tokens.detail.recent')}</h2>
            <Link className="text-[12px] text-[var(--accent)]" to={`/requests?token=${encodeURIComponent(id)}`}>
              {t('tokens.detail.viewLogs')}
            </Link>
          </div>
          <SortableDataTable<LogRow> caption={t('tokens.detail.recent')} rows={logRows} columns={logColumns} rowKey="id" emptyMessage={t('common.noData')} />
        </section>
      </div>
    </Page>
  );
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
