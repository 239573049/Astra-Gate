import { useQueryClient } from '@tanstack/react-query';
import { ArrowRight, Ban, Brain, Check, CircleCheck, CircleX, Globe, KeyRound, LoaderCircle, RefreshCw, ScrollText } from 'lucide-react';
import { memo, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link, useInRouterContext, useSearchParams } from 'react-router';

import { keys, useProviders, useRequest, useRequestRate, useRequests, useTunnel, type RequestQuery } from '../api/hooks';
import { useLiveRequest, useLiveRequestFeed, useLiveRequestList, useLiveRequestsConnected } from '../api/liveRequests';
import type { ClientKind, RateStats, RequestDetail, RequestSummary } from '../api/types';
import { Accordion } from '../components/arc/accordion/accordion';
import { Alert } from '../components/arc/alert/alert';
import { Pagination } from '../components/arc/pagination/pagination';
import { SortableDataTable, type DataColumn } from '../components/arc/sortable-data-table/sortable-data-table';
import { Tooltip } from '../components/arc/tooltip/tooltip';
import { PricingSourceBadge } from '../components/Billing';
import { CLIENT_META, ClientGlyph, ModelGlyph, ProtocolGlyph, ProviderIcon } from '../components/icons';
import { Page } from '../components/layout/Page';
import { EMPTY_REQUEST_FILTERS, originQuery, RequestFilters, type RequestFilterId, type RequestFilterValues } from '../components/RequestFilters';
import { CacheUsageCell, TokenTotalsCell, TokenTypeLabel, TokenUsageSummary } from '../components/TokenUsage';
import { Badge, Button, CodeBlock, DetailSection, EmptyState, KV, Spinner } from '../components/ui/controls';
import { errorText, Sheet } from '../components/ui/overlays';
import { useI18n, type MessageKey, type TFunction } from '../i18n';
import { cn } from '../lib/cn';
import { useDebounced } from '../lib/hooks';
import { formatDateTime, formatMs, formatNanos, formatPercent, formatTime, formatTokens, formatTps, formatUnitPrice, pretty } from '../lib/format';
import { useCommandListener } from '../shell/commands';

const PAGE_SIZE = 50;

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

/** Filters a link can carry: /requests?token=<id>&provider=…&client=…&status=…&origin=…&model=… (comma lists). */
const URL_FILTERS = ['client', 'token', 'provider', 'status', 'origin'] as const satisfies readonly RequestFilterId[];

function filtersFromParams(params: URLSearchParams): { model: string; filters: RequestFilterValues } {
  const filters = { ...EMPTY_REQUEST_FILTERS };
  for (const id of URL_FILTERS) filters[id] = (params.get(id) ?? '').split(',').filter(Boolean);
  return { model: params.get('model') ?? '', filters };
}

function paramsFromFilters(model: string, filters: RequestFilterValues): URLSearchParams {
  const params = new URLSearchParams();
  if (model) params.set('model', model);
  for (const id of URL_FILTERS) if (filters[id].length > 0) params.set(id, filters[id].join(','));
  return params;
}

/**
 * The request log. Under the router its filters live in the address (a token's "view requests" link opens it on that
 * token, and a filtered view can be shared or reloaded); rendered on its own it keeps them in state only.
 */
export function RequestsPage() {
  return useInRouterContext() ? <RoutedRequests /> : <RequestsView />;
}

function RoutedRequests() {
  const [params, setParams] = useSearchParams();
  // Read once: afterwards the page owns the filters and only writes them back.
  const [initial] = useState(() => filtersFromParams(params));
  const sync = useCallback(
    (model: string, filters: RequestFilterValues) => {
      const next = paramsFromFilters(model, filters);
      setParams((current) => (current.toString() === next.toString() ? current : next), { replace: true });
    },
    [setParams],
  );
  return <RequestsView initial={initial} onFiltersChange={sync} />;
}

function RequestsView({
  initial,
  onFiltersChange,
}: {
  initial?: { model: string; filters: RequestFilterValues };
  onFiltersChange?: (model: string, filters: RequestFilterValues) => void;
}) {
  const { t } = useI18n();
  const [model, setModel] = useState(initial?.model ?? '');
  const [filters, setFilters] = useState<RequestFilterValues>(initial?.filters ?? EMPTY_REQUEST_FILTERS);
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<string | null>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const debouncedModel = useDebounced(model.trim());
  // Checking several values in one open menu settles into a single query instead of one per click.
  const debouncedFilters = useDebounced(filters);
  useEffect(() => onFiltersChange?.(debouncedModel, debouncedFilters), [debouncedModel, debouncedFilters, onFiltersChange]);
  // Comma-joined lists: the server matches any listed value (RequestRepository).
  const listFilters = {
    client: debouncedFilters.client.join(','),
    token: debouncedFilters.token.join(','),
    provider: debouncedFilters.provider.join(','),
    status: debouncedFilters.status.join(','),
    origin: originQuery(debouncedFilters.origin),
  };
  // Header rate readout: the trailing window under the same filters as the list (paging excluded), every 5 s.
  const qc = useQueryClient();
  const rate = useRequestRate({ ...listFilters, model: debouncedModel });

  const query: RequestQuery = { model: debouncedModel, ...listFilters, page, pageSize: PAGE_SIZE };
  // Sets, so matching live rows stays O(1) per filter however many values are picked.
  const liveFilters = useMemo(
    () => ({
      client: new Set(debouncedFilters.client),
      token: new Set(debouncedFilters.token),
      provider: new Set(debouncedFilters.provider),
      status: new Set(debouncedFilters.status),
      origin: new Set(debouncedFilters.origin.length === 1 ? debouncedFilters.origin : []),
    }),
    [debouncedFilters],
  );
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
  // In-flight rows not in the stored page yet, narrowed by the same filters the server applies.
  const liveOnly = useMemo(() => {
    if (page !== 1) return [];
    const storedIds = new Set((data?.items ?? []).map((r) => r.id));
    return liveRows.filter((r) => !storedIds.has(r.id) && matchesFilters(r, debouncedModel, liveFilters));
  }, [page, data, liveRows, debouncedModel, liveFilters]);
  // Stable between data changes, so typing, filter clicks and rate polls leave the memoized table alone.
  const items = useMemo(() => [...liveOnly, ...(data?.items ?? [])], [liveOnly, data]);
  // Remote access in use (now, or in the rows at hand): offer the origin filter.
  const tunnel = useTunnel();
  const showOrigin = tunnel.data?.enabled === true || items.some((r) => r.remote);

  useCommandListener((cmd) => {
    if (cmd === 'find') searchRef.current?.focus();
  });

  const hasFilters = model.trim() !== '' || Object.values(filters).some((v) => v.length > 0);

  const clearFilters = useCallback(() => {
    setModel('');
    setFilters(EMPTY_REQUEST_FILTERS);
    setPage(1);
  }, []);

  const setFilter = useCallback((id: RequestFilterId, values: string[]) => {
    setFilters((f) => ({ ...f, [id]: values }));
    setPage(1);
  }, []);

  return (
    <Page
      title={t('nav.requests')}
      subtitle={data ? t('requests.total', { count: formatTokens(data.total + liveOnly.length) }) : undefined}
      actions={
        <>
          {data && <TotalCost nanos={data.totalCostNanoUsd} />}
          <LiveRate rate={rate.data} />
          <Button
            variant="plain"
            icon={<RefreshCw className={cn('size-4', requests.isFetching && 'animate-spin')} />}
            aria-label={t('common.refresh')}
            onClick={() => {
              void requests.refetch();
              void qc.invalidateQueries({ queryKey: keys.stats('rate') });
            }}
          />
        </>
      }
    >
      <div className="mb-3">
        <RequestFilters
          model={model}
          onModelChange={(v) => {
            setModel(v);
            setPage(1);
          }}
          searchRef={searchRef}
          filters={filters}
          onFilterChange={setFilter}
          onClear={clearFilters}
          showOrigin={showOrigin}
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
        <RequestTable items={items} onOpen={setSelected} />
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

/**
 * The request list. Memoized on `items`: the page re-renders on every keystroke, filter click and rate poll,
 * and re-rendering 50 rows of live cells each time made filtering feel sluggish.
 */
const RequestTable = memo(function RequestTable({ items, onOpen }: { items: RequestSummary[]; onOpen: (id: string) => void }) {
  const { t, locale } = useI18n();
  const clientName = useCallback((r: RequestSummary) => requestClientName(r, t), [t]);
  const providers = useProviders();
  // A request row names its provider but not its icon: look the icon up in the provider list.
  const providerById = useMemo(() => new Map((providers.data ?? []).map((p) => [p.id, p])), [providers.data]);

  const rows = useMemo<Row[]>(() => items.map((r) => ({
    id: r.id,
    time: Date.parse(r.startedAtUtc),
    client: clientName(r),
    token: r.tokenName ?? '',
    model: r.requestedModel ?? '',
    provider: r.providerName ?? '',
    status: r.status,
    ttft: r.ttftMs ?? null,
    tps: r.outputTps ?? null,
    tokens: r.totalInputTokens + r.totalOutputTokens,
    cache: r.cacheReadTokens,
    cost: r.costNanoUsd ?? 0,
    r,
  })), [items, clientName]);

  const columns = useMemo<DataColumn<Row>[]>(() => [
    { key: 'time', label: t('requests.col.time'), width: 92, render: (_, row) => <span className="num">{formatTime(row.r.startedAtUtc, locale)}</span> },
    {
      key: 'model',
      label: t('requests.col.model'),
      // Wide enough for a full model ID next to the reasoning badge; the badge wraps below the name
      // rather than squeezing it (see the cell below).
      width: 320,
      render: (_, row) => (
        <LiveCell r={row.r}>
          {(r) => (
            <button type="button" className="block w-full min-w-0 text-left" onClick={() => onOpen(r.id)} aria-label={t('requests.openDetail', { model: r.requestedModel || r.id })}>
              {/* Wrapping, not shrinking: the model name keeps its full width and the badge moves to its own line. */}
              <span className="flex min-w-0 flex-wrap items-center gap-x-1.5">
                <span className="flex min-w-0 items-center gap-1.5">
                  <ModelGlyph model={r.upstreamModel || r.requestedModel} size={15} />
                  <span className="block min-w-0 truncate font-medium hover:text-[var(--accent)]" title={r.requestedModel ?? ''}>{r.requestedModel || '—'}</span>
                  <MappedModel r={r} />
                </span>
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
    {
      key: 'client',
      label: t('requests.col.client'),
      width: 72,
      render: (_, row) => (
        <LiveCell r={row.r}>
          {(r) => (
            <span className="inline-flex items-center gap-1">
              {r.clientKind ? (
                <Tooltip content={clientName(r)}>
                  <span className="inline-flex" role="img" aria-label={clientName(r)}>
                    <ClientGlyph kind={r.clientKind as ClientKind} size={20} />
                  </span>
                </Tooltip>
              ) : clientName(r)}
              {r.remote && <RemoteMark r={r} />}
            </span>
          )}
        </LiveCell>
      ),
    },
    {
      key: 'token',
      label: t('requests.col.token'),
      width: 110,
      render: (_, row) => (
        <LiveCell r={row.r}>
          {(r) => (
            <span className="flex min-w-0 items-center gap-1.5">
              {r.tokenName && <KeyRound className="size-3.5 shrink-0 text-[var(--text-muted)]" aria-hidden="true" />}
              <span className="block min-w-0 truncate">{r.tokenName || '—'}</span>
            </span>
          )}
        </LiveCell>
      ),
    },
    {
      key: 'provider',
      label: t('requests.col.provider'),
      width: 130,
      render: (_, row) => (
        <LiveCell r={row.r}>
          {(r) => {
            const p = r.providerId ? providerById.get(r.providerId) : undefined;
            return (
              <span className="flex min-w-0 items-center gap-1.5">
                {r.providerName && <ProviderIcon name={r.providerName} icon={p?.icon} colorKey={p?.templateId} size={18} />}
                <span className="block min-w-0 truncate">{r.providerName || '—'}</span>
              </span>
            );
          }}
        </LiveCell>
      ),
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
                onClick={() => onOpen(r.id)}
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
      label: t('requests.col.ttft'),
      numeric: true,
      width: 88,
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
            </div>
          )}
        </LiveCell>
      ),
    },
    {
      key: 'tps',
      label: t('requests.col.tps'),
      numeric: true,
      width: 92,
      render: (_, row) => (
        <LiveCell r={row.r}>
          {(r) => <span className="num whitespace-nowrap" title={t('requests.detail.tps')}>{formatTps(r.outputTps)}</span>}
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
  ], [t, locale, clientName, providerById, onOpen]);

  return <SortableDataTable<Row> caption={t('nav.requests')} rows={rows} columns={columns} rowKey="id" emptyMessage={t('requests.empty')} />;
});

/** Client name; a request authenticated by a bare token (no client suffix) is a direct call. */
function requestClientName(r: Pick<RequestSummary, 'clientKind' | 'tokenId'>, t: TFunction): string {
  if (r.clientKind) return CLIENT_META[r.clientKind as ClientKind]?.name ?? r.clientKind;
  return r.tokenId ? t('requests.direct') : '—';
}

/** Marks a request that came through remote access; the tooltip names the visitor's address. */
function RemoteMark({ r }: { r: RequestSummary }) {
  const { t } = useI18n();
  const label = r.clientIp ? t('requests.origin.remoteFrom', { ip: r.clientIp }) : t('requests.origin.remote');
  return (
    <Tooltip content={label}>
      <span className="inline-flex text-[var(--accent)]" role="img" aria-label={label}>
        <Globe className="size-3.5" />
      </span>
    </Tooltip>
  );
}

/** Client-side mirror of the server's list filters (RequestRepository), for live rows not stored yet. */
function matchesFilters(r: RequestSummary, model: string, filters: Record<RequestFilterId, Set<string>>): boolean {
  if (filters.client.size && !filters.client.has(r.clientKind ?? '')) return false;
  if (filters.token.size && !filters.token.has(r.tokenId ?? '')) return false;
  if (filters.provider.size && !filters.provider.has(r.providerId ?? '')) return false;
  if (filters.status.size && !filters.status.has(r.status)) return false;
  if (filters.origin.size && !filters.origin.has(r.remote ? 'remote' : 'local')) return false;
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

/** Header pill with the summed cost of every request matching the filters (all pages, from the list query itself). */
function TotalCost({ nanos }: { nanos: number }) {
  const { t } = useI18n();
  return (
    <Tooltip side="bottom" content={t('requests.totalCost.hint')}>
      <div className="flex items-baseline gap-1 rounded-full border border-[var(--border)] px-3.5 py-1.5">
        <span className="text-[10.5px] text-[var(--text-muted)]">{t('requests.totalCost')}</span>
        <span className="num text-[12px]">{formatNanos(nanos)}</span>
      </div>
    </Tooltip>
  );
}

/** Header pill with the live RPM / TPM / cache-hit ratio of the trailing window; hidden until the first answer arrives. */
function LiveRate({ rate }: { rate: RateStats | undefined }) {
  const { t } = useI18n();
  if (!rate) return null;
  const hint = t('requests.rate.hint', { window: rate.windowSeconds });
  const entry = (label: string, value: string) => (
    <span key={label} className="flex items-baseline gap-1">
      <span className="text-[10.5px] text-[var(--text-muted)]">{label}</span>
      <span className="num text-[12px]">{value}</span>
    </span>
  );
  const rateText = (n: number) => (n >= 100 ? formatTokens(Math.round(n)) : n.toFixed(1));
  return (
    <Tooltip side="bottom" content={hint}>
      <div className="flex items-center gap-3 rounded-full border border-[var(--border)] px-3.5 py-1.5">
        {entry(t('requests.rate.rpm'), rateText(rate.rpm))}
        {entry(t('requests.rate.tpm'), formatTokens(Math.round(rate.tpm), true))}
        {entry(t('usage.hitRate'), formatPercent(rate.cacheHitRate))}
      </div>
    </Tooltip>
  );
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
  const StatusIcon = status === 'success' ? CircleCheck : status === 'client_cancelled' ? Ban : CircleX;
  const key = `status.${status}` as 'status.success';
  return (
    <Badge tone={tone} title={[http ? `HTTP ${http}` : '', hint].filter(Boolean).join(' · ') || undefined}>
      <span className="inline-flex items-center gap-1">
        <StatusIcon className="size-3" aria-hidden="true" />
        {t(key)}
      </span>
    </Badge>
  );
}

function ProtocolTag({ r }: { r: Pick<RequestSummary, 'inboundProtocol' | 'upstreamProtocol' | 'passthrough'> }) {
  if (r.passthrough || !r.upstreamProtocol || r.upstreamProtocol === r.inboundProtocol) {
    return (
      <span className="inline-flex items-center gap-1 text-[var(--text-muted)]">
        <ProtocolGlyph protocol={r.inboundProtocol} />
        {r.inboundProtocol}
      </span>
    );
  }
  return (
    <span className="inline-flex items-center gap-0.5 text-[var(--warning)]">
      <ProtocolGlyph protocol={r.inboundProtocol} />
      <span className="ml-0.5">{r.inboundProtocol}</span>
      <ArrowRight className="size-3" />
      <ProtocolGlyph protocol={r.upstreamProtocol} />
      <span className="ml-0.5">{r.upstreamProtocol}</span>
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
      <span className="inline-flex min-w-0 items-center gap-1">
        <Brain className="size-3 shrink-0" aria-hidden="true" />
        <span className="block max-w-40 min-w-0 truncate">{t('requests.reasoning')}: {parts.join(' · ')}</span>
      </span>
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

/** The model the gateway sent in place of the requested one (a Claude Desktop role or a provider model mapping). */
function MappedModel({ r }: { r: Pick<RequestSummary, 'requestedModel' | 'upstreamModel'> }) {
  const { t } = useI18n();
  if (!r.requestedModel || !r.upstreamModel || r.upstreamModel === r.requestedModel) return null;
  return (
    <span className="flex min-w-0 items-center gap-1 text-[var(--text-muted)]" title={t('requests.mappedModel.hint', { model: r.upstreamModel })}>
      <ArrowRight className="size-3 shrink-0" aria-hidden="true" />
      <span className="truncate">{r.upstreamModel}</span>
    </span>
  );
}

function ResponseModelTag({ r, labeled = false }: { r: Pick<RequestSummary, 'requestedModel' | 'upstreamModel' | 'responseModel' | 'status'>; labeled?: boolean }) {
  const { t } = useI18n();
  // Compared with what the gateway sent: a mapping is the user's own setting (shown beside the requested model), a
  // response naming another model is the upstream's doing.
  const sent = r.upstreamModel || r.requestedModel;
  const comparable = !!r.responseModel && !!sent;
  const matches = comparable && r.responseModel === sent;
  // The returned model is recorded when the request finishes, so an in-flight one has none yet.
  const pending = !r.responseModel && r.status === 'pending';
  const value = r.responseModel || t(pending ? 'requests.modelPending' : 'requests.modelNotRecorded');
  const title = r.responseModel
    ? t('requests.modelComparison', { requested: r.requestedModel ?? '—', upstream: r.upstreamModel ?? '—', returned: r.responseModel })
    : t(pending ? 'requests.modelPending.hint' : 'requests.modelNotRecorded.hint');
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
/** The request's span tree in 链路追踪 (.plan/observability.md §5); only where a router is mounted. */
function TraceLink({ id, label }: { id: string; label: string }) {
  if (!useInRouterContext()) return null;
  return (
    <Link
      to={`/traces/requests/${encodeURIComponent(id)}`}
      className="inline-flex h-8 items-center rounded-[10px] px-3 text-[13px] text-[var(--accent)] hover:bg-[var(--surface-muted)]"
    >
      {label}
    </Link>
  );
}

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
      footer={
        <div className="flex items-center gap-2">
          {id ? <TraceLink id={id} label={t('traces.openTrace')} /> : null}
          <Button onClick={onClose}>{t('common.close')}</Button>
        </div>
      }
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
              <KV label={t('requests.col.origin')}>
                {r.remote
                  ? r.clientIp ? t('requests.origin.remoteFrom', { ip: r.clientIp }) : t('requests.origin.remote')
                  : r.clientIp ?? t('requests.origin.local')}
              </KV>
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

