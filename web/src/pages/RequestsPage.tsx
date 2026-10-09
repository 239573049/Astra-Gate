import { ArrowRight, Check, LoaderCircle, RefreshCw, ScrollText } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';

import { useClients, useProviders, useRequest, useRequests, useTokens, type RequestQuery } from '../api/hooks';
import { useLiveRequest, useLiveRequestFeed, useLiveRequestList, useLiveRequestsConnected } from '../api/liveRequests';
import type { ClientKind, RequestDetail, RequestSummary } from '../api/types';
import { Accordion } from '../components/arc/accordion/accordion';
import { Alert } from '../components/arc/alert/alert';
import { FilterToolbar, type FilterChip, type FilterField } from '../components/arc/filter-toolbar/filter-toolbar';
import { Pagination } from '../components/arc/pagination/pagination';
import { SortableDataTable, type DataColumn } from '../components/arc/sortable-data-table/sortable-data-table';
import { PricingSourceBadge } from '../components/Billing';
import { CLIENT_META } from '../components/icons';
import { Page } from '../components/layout/Page';
import { CacheUsageCell, TokenTotalsCell, TokenTypeLabel, TokenUsageSummary } from '../components/TokenUsage';
import { Badge, Button, CodeBlock, DetailSection, EmptyState, KV, SearchField, Spinner } from '../components/ui/controls';
import { errorText, Sheet } from '../components/ui/overlays';
import { useI18n, type MessageKey, type TFunction } from '../i18n';
import { cn } from '../lib/cn';
import { useDebounced } from '../lib/hooks';
import { formatDateTime, formatMs, formatNanos, formatTime, formatTokens, formatTps, formatUnitPrice, pretty } from '../lib/format';
import { useCommandListener } from '../shell/commands';

const PAGE_SIZE = 50;
const STATUSES = ['success', 'blocked', 'upstream_error', 'gateway_error', 'client_cancelled'] as const;

const PRIVACY_CATEGORY_KEY: Record<string, MessageKey> = {
  api_key: 'settings.privacy.category.api_key',
  aws_key: 'settings.privacy.category.aws_key',
  jwt: 'settings.privacy.category.jwt',
  private_key: 'settings.privacy.category.private_key',
  email: 'settings.privacy.category.email',
  phone: 'settings.privacy.category.phone',
  intranet: 'settings.privacy.category.intranet',
  credit_card: 'settings.privacy.category.credit_card',
  national_id: 'settings.privacy.category.national_id',
  credential: 'settings.privacy.category.credential',
  custom: 'settings.privacy.category.custom',
};

const PRIVACY_ACTION_KEY: Record<string, MessageKey> = {
  warn: 'settings.privacy.action.warn',
  block: 'settings.privacy.action.block',
  redact: 'settings.privacy.action.redact',
};

type FilterId = 'client' | 'token' | 'provider' | 'status';

/** One table row; sortable columns hold plain values, the full record rides along for rendering. */
type Row = {
  id: string;
  time: number;
  client: string;
  token: string;
  model: string;
  provider: string;
  status: string;
  ttft: number | null;
  tokens: number;
  cache: number;
  cost: number;
  r: RequestSummary;
};

export function RequestsPage() {
  const { t, locale } = useI18n();
  const [model, setModel] = useState('');
  const [filters, setFilters] = useState<Record<FilterId, string>>({ client: '', token: '', provider: '', status: '' });
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<string | null>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const debouncedModel = useDebounced(model.trim());
  const clients = useClients();
  const providers = useProviders();
  const tokens = useTokens();

  const query: RequestQuery = { model: debouncedModel, ...filters, page, pageSize: PAGE_SIZE };
  // Page 1 follows the live feed: requests show up as they arrive and update in place. Polling is only the
  // fallback for when the live stream is down.
  // The page re-renders only when a live row joins or leaves the list; each row's progress (TTFT, status, cost)
  // re-renders just that row's cells (LiveCell).
  useLiveRequestFeed(page === 1);
  const liveConnected = useLiveRequestsConnected();
  const liveRows = useLiveRequestList();
  const requests = useRequests(query, page === 1 && !liveConnected);
  const data = requests.data;
  const pages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;
  const storedIds = new Set((data?.items ?? []).map((r) => r.id));
  // In-flight rows not in the stored page yet, narrowed by the same filters the server applies.
  const liveOnly = page === 1 ? liveRows.filter((r) => !storedIds.has(r.id) && matchesFilters(r, debouncedModel, filters)) : [];
  const items = [...liveOnly, ...(data?.items ?? [])];

  useCommandListener((cmd) => {
    if (cmd === 'find') searchRef.current?.focus();
  });

  const clientName = (r: RequestSummary) => requestClientName(r, t);

  // Keep query ids separate from displayed labels, including providers with the same name.
  const fieldOptions: Record<FilterId, { value: string; label: string }[]> = {
    client: (clients.data ?? []).map((c) => ({ value: c.kind, label: c.name })),
    token: (tokens.data ?? []).map((tk) => ({ value: tk.id, label: tk.name })),
    provider: (providers.data ?? []).map((p) => ({ value: p.id, label: p.name })),
    status: STATUSES.map((s) => ({ value: s, label: t(`status.${s}`) })),
  };
  const fieldLabels: Record<FilterId, string> = {
    client: t('requests.col.client'),
    token: t('requests.col.token'),
    provider: t('requests.col.provider'),
    status: t('requests.col.status'),
  };
  const fields: FilterField[] = (Object.keys(fieldOptions) as FilterId[]).map((id) => ({
    id,
    label: fieldLabels[id],
    options: fieldOptions[id],
  }));
  const chips: FilterChip[] = (Object.keys(filters) as FilterId[])
    .filter((id) => filters[id])
    .map((id) => ({ id, label: fieldLabels[id], value: filters[id], valueLabel: fieldOptions[id].find((o) => o.value === filters[id])?.label ?? filters[id] }));
  if (model.trim()) chips.unshift({ id: 'model', label: t('requests.col.model'), value: model.trim() });
  const hasFilters = chips.length > 0;
  const filterText = (filter: FilterChip) => `${filter.label}: ${filter.valueLabel ?? filter.value ?? ''}`;

  const clearFilters = () => {
    setModel('');
    setFilters({ client: '', token: '', provider: '', status: '' });
    setPage(1);
  };

  const setFilter = (id: FilterId, value: string) => {
    setFilters((f) => ({ ...f, [id]: value }));
    setPage(1);
  };

  const rows: Row[] = items.map((r) => ({
    id: r.id,
    time: Date.parse(r.startedAtUtc),
    client: clientName(r),
    token: r.tokenName ?? '',
    model: r.requestedModel ?? '',
    provider: r.providerName ?? '',
    status: r.status,
    ttft: r.ttftMs ?? null,
    tokens: r.totalInputTokens + r.totalOutputTokens,
    cache: r.cacheReadTokens,
    cost: r.costNanoUsd ?? 0,
    r,
  }));

  const columns: DataColumn<Row>[] = [
    { key: 'time', label: t('requests.col.time'), width: 92, render: (_, row) => <span className="num">{formatTime(row.r.startedAtUtc, locale)}</span> },
    {
      key: 'model',
      label: t('requests.col.model'),
      render: (_, row) => (
        <LiveCell r={row.r}>
          {(r) => (
            <button type="button" className="block w-full min-w-0 text-left" onClick={() => setSelected(r.id)} aria-label={t('requests.openDetail', { model: r.requestedModel || r.id })}>
              <span className="flex min-w-0 items-center gap-1.5">
                <span className="block min-w-0 truncate font-medium hover:text-[var(--accent)]" title={r.requestedModel ?? ''}>{r.requestedModel || '—'}</span>
                <ReasoningBadge r={r} />
              </span>
              <span className="block text-[11px]">
                <ResponseModelTag r={r} labeled />
              </span>
              <span className="block truncate text-[11px]">
                <ProtocolTag r={r} />
              </span>
            </button>
          )}
        </LiveCell>
      ),
    },
    { key: 'client', label: t('requests.col.client'), width: 110, render: (_, row) => <LiveCell r={row.r}>{(r) => clientName(r)}</LiveCell> },
    {
      key: 'token',
      label: t('requests.col.token'),
      width: 110,
      render: (_, row) => <LiveCell r={row.r}>{(r) => <span className="block truncate">{r.tokenName || '—'}</span>}</LiveCell>,
    },
    {
      key: 'provider',
      label: t('requests.col.provider'),
      width: 130,
      render: (_, row) => <LiveCell r={row.r}>{(r) => <span className="block truncate">{r.providerName || '—'}</span>}</LiveCell>,
    },
    {
      key: 'status',
      label: t('requests.col.status'),
      width: 104,
      render: (_, row) => (
        <LiveCell r={row.r}>
          {(r) => {
            const failed = r.status !== 'success' && r.status !== 'client_cancelled' && r.status !== 'pending';
            if (!failed) return <StatusBadge status={r.status} http={r.httpStatus} />;
            return (
              <button
                type="button"
                className="cursor-pointer rounded-full text-left hover:opacity-80 focus-visible:outline focus-visible:outline-2 focus-visible:outline-[var(--accent)]"
                title={t('requests.viewError')}
                aria-label={`${t(`status.${r.status}` as 'status.success')}: ${t('requests.viewError')}`}
                onClick={() => setSelected(r.id)}
              >
                <StatusBadge status={r.status} http={r.httpStatus} hint={t('requests.viewError')} />
              </button>
            );
          }}
        </LiveCell>
      ),
    },
    {
      key: 'ttft',
      label: 'TTFT / TPS',
      numeric: true,
      width: 112,
      render: (_, row) => (
        <LiveCell r={row.r}>
          {(r) => (
            <div className="num whitespace-nowrap">
              {r.status === 'pending' && r.ttftMs == null ? (
                <div title={t('requests.elapsed')}>
                  <Elapsed since={r.startedAtUtc} />
                </div>
              ) : (
                <div title="TTFT">{formatMs(r.ttftMs)}</div>
              )}
              <div className="text-[11px] text-[var(--text-muted)]" title={t('requests.detail.tps')}>{formatTps(r.outputTps)}</div>
            </div>
          )}
        </LiveCell>
      ),
    },
    { key: 'tokens', label: t('requests.col.tokens'), numeric: true, width: 116, render: (_, row) => <LiveCell r={row.r}>{(r) => <TokenTotalsCell usage={r} />}</LiveCell> },
    { key: 'cache', label: t('requests.col.cache'), numeric: true, width: 136, render: (_, row) => <LiveCell r={row.r}>{(r) => <CacheUsageCell usage={r} />}</LiveCell> },
    {
      key: 'cost',
      label: t('requests.col.cost'),
      numeric: true,
      width: 92,
      render: (_, row) => (
        <LiveCell r={row.r}>
          {(r) => <span className={cn(r.usageSource === 'missing' && 'text-[var(--warning)]')}>{formatNanos(r.costNanoUsd)}</span>}
        </LiveCell>
      ),
    },
  ];

  return (
    <Page
      title={t('nav.requests')}
      subtitle={data ? t('requests.total', { count: formatTokens(data.total + liveOnly.length) }) : undefined}
      actions={
        <Button
          variant="plain"
          icon={<RefreshCw className={cn('size-4', requests.isFetching && 'animate-spin')} />}
          aria-label={t('common.refresh')}
          onClick={() => void requests.refetch()}
        />
      }
    >
      <div className="mb-3">
        <FilterToolbar
          label={t('requests.filters')}
          leading={
            <SearchField
              inputRef={searchRef}
              className="w-full"
              value={model}
              onChange={(v) => {
                setModel(v);
                setPage(1);
              }}
              placeholder={t('requests.searchModel')}
              clearLabel={t('common.clearSearch')}
            />
          }
          filters={chips}
          messages={{
            empty: t('requests.noFilters'),
            clearAll: t('requests.clearFilters'),
            back: t('requests.backToFilters'),
            noOptions: t('common.noResults'),
            cleared: t('requests.filtersCleared'),
            remove: (filter) => t('requests.removeFilter', { filter: filterText(filter) }),
            added: (filter) => t('requests.filterAdded', { filter: filterText(filter) }),
            changed: (filter) => t('requests.filterChanged', { filter: filterText(filter) }),
            removed: (filter) => t('requests.filterRemoved', { filter: filterText(filter) }),
          }}
          onRemove={(id) => {
            if (id === 'model') {
              setModel('');
              setPage(1);
            } else setFilter(id as FilterId, '');
          }}
          onClearAll={clearFilters}
          addFilter={{
            fields,
            label: t('requests.addFilter'),
            onAdd: (chip) => {
              if (chip.value) setFilter(chip.id as FilterId, chip.value);
            },
          }}
        />
      </div>

      {requests.isLoading && <Spinner lines={4} className="py-6" />}
      {requests.isError && <Alert tone="danger" title={t('common.error')}>{errorText(requests.error)}</Alert>}
      {data && items.length === 0 && (
        <EmptyState
          icon={<ScrollText className="size-6" />}
          title={t(hasFilters ? 'requests.filteredEmpty' : 'requests.empty')}
          detail={t(hasFilters ? 'requests.filteredEmpty.detail' : 'requests.empty.detail')}
          action={hasFilters ? <Button onClick={clearFilters}>{t('requests.clearFilters')}</Button> : undefined}
        />
      )}
      {data && items.length > 0 && (
        <SortableDataTable<Row>
          caption={t('nav.requests')}
          rows={rows}
          columns={columns}
          rowKey="id"
          emptyMessage={t('requests.empty')}
        />
      )}
      {data && pages > 1 && (
        <div className="mt-4 flex justify-center">
          <Pagination page={page} pageCount={pages} onPageChange={setPage} label={t('requests.page', { page, pages })} />
        </div>
      )}
      <RequestDetailSheet id={selected} onClose={() => setSelected(null)} />
    </Page>
  );
}

/** Client name; a request authenticated by a bare token (no client suffix) is a direct call. */
function requestClientName(r: Pick<RequestSummary, 'clientKind' | 'tokenId'>, t: TFunction): string {
  if (r.clientKind) return CLIENT_META[r.clientKind as ClientKind]?.name ?? r.clientKind;
  return r.tokenId ? t('requests.direct') : '—';
}

/** Client-side mirror of the server's list filters (RequestRepository), for live rows not stored yet. */
function matchesFilters(r: RequestSummary, model: string, filters: Record<FilterId, string>): boolean {
  if (filters.client && r.clientKind !== filters.client) return false;
  if (filters.token && r.tokenId !== filters.token) return false;
  if (filters.provider && r.providerId !== filters.provider) return false;
  if (filters.status && r.status !== filters.status) return false;
  if (model) {
    const needle = model.toLowerCase();
    const names = [r.requestedModel, r.upstreamModel, r.systemModelId, r.responseModel];
    if (!names.some((m) => m?.toLowerCase().includes(needle))) return false;
  }
  return true;
}

/**
 * Renders one cell from the freshest copy of its request: the live feed's while the request is in flight (or just
 * finished and awaiting the list refetch), else the stored row. Subscribes to that request alone, so a progress
 * event re-renders only the cells of its own row.
 */
function LiveCell({ r, children }: { r: RequestSummary; children: (r: RequestSummary) => ReactNode }) {
  return <>{children(useLiveRequest(r.id) ?? r)}</>;
}

/** Live "time since the request arrived" for a request still waiting on its first token. */
function Elapsed({ since }: { since: string }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 200);
    return () => clearInterval(timer);
  }, []);
  return <>{formatMs(Math.max(0, now - Date.parse(since)))}</>;
}

function StatusBadge({ status, http, hint }: { status: string; http?: number | null; hint?: string }) {
  const { t } = useI18n();
  if (status === 'pending') {
    return (
      <Badge tone="accent" title={hint}>
        <span className="inline-flex items-center gap-1">
          <LoaderCircle className="size-3 animate-spin" aria-hidden="true" />
          {t('status.pending')}
        </span>
      </Badge>
    );
  }
  const tone = status === 'success' ? 'green' : status === 'client_cancelled' ? 'orange' : 'red';
  const key = `status.${status}` as 'status.success';
  return (
    <Badge tone={tone} title={[http ? `HTTP ${http}` : '', hint].filter(Boolean).join(' · ') || undefined}>
      {t(key)}
    </Badge>
  );
}

function ProtocolTag({ r }: { r: Pick<RequestSummary, 'inboundProtocol' | 'upstreamProtocol' | 'passthrough'> }) {
  if (r.passthrough || !r.upstreamProtocol || r.upstreamProtocol === r.inboundProtocol) {
    return <span className="text-[var(--text-muted)]">{r.inboundProtocol}</span>;
  }
  return (
    <span className="inline-flex items-center gap-0.5 text-[var(--warning)]">
      {r.inboundProtocol}
      <ArrowRight className="size-3" />
      {r.upstreamProtocol}
    </span>
  );
}

/** Compact "what the client explicitly set" reasoning label; null parts mean nothing was recorded. */
function reasoningParts(
  r: Pick<RequestSummary, 'reasoningEffort' | 'reasoningMode' | 'reasoningBudgetTokens'>,
  t: TFunction,
): string[] {
  const parts: string[] = [];
  if (r.reasoningEffort) parts.push(r.reasoningEffort);
  if (r.reasoningMode) parts.push(r.reasoningMode);
  if (r.reasoningBudgetTokens != null) parts.push(t('requests.reasoning.budget', { tokens: String(r.reasoningBudgetTokens) }));
  return parts;
}

/** Badge next to the model name; hidden when the request recorded no reasoning config at all. */
function ReasoningBadge({ r }: { r: Pick<RequestSummary, 'reasoningEffort' | 'reasoningMode' | 'reasoningBudgetTokens'> }) {
  const { t } = useI18n();
  const parts = reasoningParts(r, t);
  if (parts.length === 0) return null;
  return (
    <Badge tone="neutral" className="max-w-44 text-[10px] font-normal" title={`${t('requests.reasoning.hint')} ${parts.join(' · ')}`}>
      <span className="block max-w-40 truncate">{t('requests.reasoning')}: {parts.join(' · ')}</span>
    </Badge>
  );
}

/** Detail-sheet value: the recorded config, or an explicit muted "not recorded" (never a guess). */
function ReasoningValue({ r }: { r: Pick<RequestSummary, 'reasoningEffort' | 'reasoningMode' | 'reasoningBudgetTokens'> }) {
  const { t } = useI18n();
  const parts = reasoningParts(r, t);
  if (parts.length === 0) {
    return (
      <span className="text-[var(--text-muted)]" title={t('requests.reasoning.hint')}>
        {t('requests.reasoning.notRecorded')}
      </span>
    );
  }
  return <span title={t('requests.reasoning.hint')}>{parts.join(' · ')}</span>;
}

function ResponseModelTag({ r, labeled = false }: { r: Pick<RequestSummary, 'requestedModel' | 'upstreamModel' | 'responseModel'>; labeled?: boolean }) {
  const { t } = useI18n();
  const comparable = !!r.responseModel && !!r.requestedModel;
  const matches = comparable && r.responseModel === r.requestedModel;
  const value = r.responseModel || t('requests.modelNotRecorded');
  const title = r.responseModel
    ? t('requests.modelComparison', { requested: r.requestedModel ?? '—', upstream: r.upstreamModel ?? '—', returned: r.responseModel })
    : t('requests.modelNotRecorded.hint');
  return (
    <span className={cn('flex min-w-0 items-center gap-1', !comparable ? 'text-[var(--text-muted)]' : matches ? 'text-[var(--success)]' : 'text-[var(--warning)]')} title={title}>
      {comparable && (matches ? <Check className="size-3 shrink-0" aria-hidden="true" /> : <ArrowRight className="size-3 shrink-0" aria-hidden="true" />)}
      <span className="truncate">{labeled ? t('requests.returnedModel', { model: value }) : value}</span>
      {comparable && <span className="shrink-0 text-[10px]">{t(matches ? 'requests.modelMatch' : 'requests.modelMismatch')}</span>}
    </span>
  );
}

type UsageRow = { key: string; type: string; tokens: number; unitPrice: number; tier: string; cost: number; item: RequestDetail['usageItems'][number] };

/** Request detail dialog: summary, timing, per-token-type bill, raw usage/pricing and captured bodies. */
export function RequestDetailSheet({ id, onClose }: { id: string | null; onClose: () => void }) {
  const { t, locale } = useI18n();
  const q = useRequest(id);
  const r: RequestDetail | undefined = q.data;

  const usageRows: UsageRow[] = (r?.usageItems ?? []).map((it, i) => ({
    key: String(i),
    type: it.tokenType,
    tokens: it.tokens,
    unitPrice: Number(it.unitPrice) || 0,
    tier: it.tierApplied,
    cost: it.costNanoUsd,
    item: it,
  }));
  const usageColumns: DataColumn<UsageRow>[] = [
    {
      key: 'type',
      label: t('billing.type'),
      render: (_, row) => (
        <span>
          <TokenTypeLabel type={row.item.tokenType} />
          {row.item.pricedAs && row.item.pricedAs !== row.item.tokenType && <span className="ml-1 font-mono text-[10.5px] text-[var(--text-muted)]"> → {row.item.pricedAs}</span>}
        </span>
      ),
    },
    { key: 'tokens', label: t('billing.tokens'), numeric: true, render: (_, row) => (row.item.isPerCall ? `${row.item.tokens}×` : formatTokens(row.item.tokens)) },
    {
      key: 'unitPrice',
      label: t('billing.unitPrice'),
      numeric: true,
      render: (_, row) => (
        <>
          {formatUnitPrice(row.item.unitPrice)}
          {row.item.multipliers?.length ? <span className="text-[var(--text-muted)]"> ({row.item.multipliers.join(', ')})</span> : null}
        </>
      ),
    },
    { key: 'tier', label: t('billing.tier') },
    { key: 'cost', label: t('billing.cost'), numeric: true, render: (_, row) => formatNanos(row.item.costNanoUsd) },
  ];

  const raw: { title: string; content: string }[] = [];
  if (r?.usageRaw != null) raw.push({ title: t('requests.detail.usageRaw'), content: pretty(r.usageRaw) });
  if (r?.pricingSnapshot) raw.push({ title: t('requests.detail.pricingSnapshot'), content: pretty(r.pricingSnapshot) });
  for (const k of ['clientRequest', 'upstreamRequest', 'upstreamRequestHeaders', 'upstreamResponse', 'clientResponse'] as const) {
    const body = r?.bodies?.[k];
    if (body) raw.push({ title: t(`requests.body.${k}`), content: pretty(body) });
  }

  return (
    <Sheet
      open={id !== null}
      onOpenChange={(o) => !o && onClose()}
      width={780}
      title={r?.requestedModel ?? t('requests.detail.title')}
      description={r?.id}
      footer={<Button onClick={onClose}>{t('common.close')}</Button>}
    >
      {q.isLoading && <Spinner lines={5} />}
      {q.isError && <Alert tone="danger" title={t('common.error')}>{errorText(q.error)}</Alert>}
      {r && (
        <div className="flex flex-col gap-6 selectable">
          <div className="grid gap-x-8 gap-y-5 sm:grid-cols-2">
            <DetailSection title={t('requests.detail.summary')} action={<StatusBadge status={r.status} http={r.httpStatus} />}>
              <KV label={t('requests.col.time')}>{formatDateTime(r.startedAtUtc, locale)}</KV>
              <KV label={t('requests.col.client')}>{requestClientName(r, t)}</KV>
              <KV label={t('requests.col.token')}>{r.tokenName ?? '—'}</KV>
              <KV label={t('requests.col.provider')}>{r.providerName ?? '—'}</KV>
              {r.accountId && <KV label={t('requests.detail.account')}>{r.accountName ?? r.accountId}</KV>}
              <KV label={t('requests.detail.protocol')}>
                <ProtocolTag r={r} />
              </KV>
              <KV label={t('requests.detail.requestedModel')}>{r.requestedModel ?? '—'}</KV>
              <KV label={t('requests.detail.upstreamModel')}>{r.upstreamModel ?? '—'}</KV>
              <KV label={t('requests.detail.responseModel')}><ResponseModelTag r={r} /></KV>
              <KV label={t('requests.detail.systemModel')}>{r.systemModelId ?? '—'}</KV>
              <KV label={t('requests.detail.reasoning')}>
                <ReasoningValue r={r} />
              </KV>
              <KV label={t('requests.detail.stream')}>{r.stream ? t('common.yes') : t('common.no')}</KV>
              {r.serviceTier && <KV label="service_tier">{r.serviceTier}</KV>}
              {r.upstreamRequestId && (
                <KV label={t('requests.detail.upstreamId')} mono>
                  {r.upstreamRequestId}
                </KV>
              )}
            </DetailSection>
            <DetailSection title={t('requests.detail.timing')}>
              <KV label="TTFB">{formatMs(r.ttfbMs)}</KV>
              <KV label="TTFT">{formatMs(r.ttftMs)}</KV>
              <KV label={t('requests.detail.generation')}>{formatMs(r.generationMs)}</KV>
              <KV label={t('requests.detail.total')}>{formatMs(r.totalMs)}</KV>
              <KV label={t('requests.detail.tps')}>{formatTps(r.outputTps)}</KV>
              <KV label={t('requests.col.tokens')}>
                {formatTokens(r.totalInputTokens)} / {formatTokens(r.totalOutputTokens)}
              </KV>
            </DetailSection>
          </div>

          <TokenUsageSummary usage={r} />

          {(r.errorType || r.errorMessage) && (
            <Alert tone="danger" title={r.errorType ?? t('requests.detail.error')}>
              {r.errorMessage ?? undefined}
            </Alert>
          )}

          {r.privacy && (
            <DetailSection
              title={t('requests.detail.privacy')}
              action={
                <Badge tone={r.privacy.blocked ? 'red' : r.privacy.dryRun ? 'neutral' : 'orange'}>
                  {r.privacy.blocked
                    ? t('requests.detail.privacyBlocked')
                    : r.privacy.dryRun
                      ? t('requests.detail.privacyDryRun')
                      : t('requests.detail.privacyRedacted', { count: r.privacy.redactions })}
                </Badge>
              }
            >
              {r.privacy.hits.length > 0 ? (
                <ul className="space-y-1 text-[12px]">
                  {r.privacy.hits.map((h) => {
                    const actionKey = PRIVACY_ACTION_KEY[h.action];
                    const categoryKey = PRIVACY_CATEGORY_KEY[h.category];
                    return (
                      <li key={`${h.ruleId}:${h.action}`} className="flex flex-wrap items-center gap-2">
                        <Badge tone={h.action === 'block' ? 'red' : h.action === 'redact' ? 'orange' : 'neutral'}>
                          {actionKey ? t(actionKey) : h.action}
                        </Badge>
                        <span>
                          {categoryKey ? t(categoryKey) : h.category} · {h.ruleId} × {h.count}
                        </span>
                        {h.samples && h.samples.length > 0 && (
                          <span className="font-mono text-[11px] text-[var(--text-muted)]">{h.samples.join('  ')}</span>
                        )}
                      </li>
                    );
                  })}
                </ul>
              ) : (
                <p className="text-[12px] text-[var(--text-secondary)]">{t('settings.privacy.noHits')}</p>
              )}
            </DetailSection>
          )}

          <DetailSection title={t('requests.detail.billing')} action={<PricingSourceBadge source={r.pricingSource ?? 'none'} priceKey={r.priceKey} />}>
            <div className="num mb-3 text-[22px] font-medium">{formatNanos(r.costNanoUsd)}</div>
            {r.usageSource === 'missing' && (
              <div className="mb-3">
                <Alert tone="warning" title={t('requests.detail.usageMissing')} />
              </div>
            )}
            {usageRows.length > 0 && <SortableDataTable<UsageRow> caption={t('requests.detail.billing')} rows={usageRows} columns={usageColumns} rowKey="key" />}
            {r.billingDescription && <p className="mt-3 text-[12px] leading-relaxed whitespace-pre-line text-[var(--text-secondary)]">{r.billingDescription}</p>}
          </DetailSection>

          {raw.length > 0 && <Accordion defaultOpen={-1} items={raw.map((x) => ({ title: x.title, content: <CodeBlock maxHeight={420}>{x.content}</CodeBlock> }))} />}
        </div>
      )}
    </Sheet>
  );
}

