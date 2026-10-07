import { RefreshCw, ShieldOff } from 'lucide-react';
import { useState } from 'react';

import { useClients, usePrivacy, usePrivacyEvents, type PrivacyEventQuery } from '../api/hooks';
import type { ClientKind, PrivacyEvent } from '../api/types';
import { Alert } from './arc/alert/alert';
import { FilterToolbar, type FilterChip, type FilterField } from './arc/filter-toolbar/filter-toolbar';
import { Pagination } from './arc/pagination/pagination';
import { SortableDataTable, type DataColumn } from './arc/sortable-data-table/sortable-data-table';
import { CLIENT_META } from './icons';
import { Badge, Button, EmptyState, SearchField, Spinner } from './ui/controls';
import { errorText } from './ui/overlays';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';
import { useDebounced } from '../lib/hooks';
import { formatTime, formatTokens } from '../lib/format';
import { RequestDetailSheet } from '../pages/RequestsPage';
import { PrivacyActionBadge, usePrivacyCategoryLabel } from './PrivacyMeta';

const PAGE_SIZE = 50;
const ACTIONS = ['block', 'redact', 'warn'] as const;

type FilterId = 'client' | 'category' | 'action' | 'blocked';

type Row = {
  id: string;
  time: number;
  client: string;
  model: string;
  outcome: string;
  hits: number;
  e: PrivacyEvent;
};

/** 拦截日志：隐私护栏产生过处置结果（拦截/脱敏/警告/只检测）的请求列表。 */
export function PrivacyEventLogs() {
  const { t, locale } = useI18n();
  const categoryLabel = usePrivacyCategoryLabel();
  const [model, setModel] = useState('');
  const [filters, setFilters] = useState<Record<FilterId, string>>({ client: '', category: '', action: '', blocked: '' });
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<string | null>(null);
  const debouncedModel = useDebounced(model);
  const clients = useClients();
  const privacy = usePrivacy();

  const query: PrivacyEventQuery = {
    model: debouncedModel,
    ...filters,
    page,
    pageSize: PAGE_SIZE,
  };
  const events = usePrivacyEvents(query, page === 1);
  const data = events.data;
  const pages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  const clientName = (kind: string | null | undefined) => (kind ? (CLIENT_META[kind as ClientKind]?.name ?? kind) : '—');

  // Categories come from the guard's own rule set (built-ins + any custom rule ids).
  const categories = [...new Set((privacy.data?.rules ?? []).map((r) => r.category))];

  const fieldOptions: Record<FilterId, { value: string; label: string }[]> = {
    client: (clients.data ?? []).map((c) => ({ value: c.kind, label: c.name })),
    category: categories.map((c) => ({ value: c, label: categoryLabel(c) })),
    action: ACTIONS.map((a) => ({ value: a, label: t(`settings.privacy.action.${a}`) })),
    blocked: [
      { value: 'true', label: t('privacy.logs.blockedOnly') },
      { value: 'false', label: t('privacy.logs.passedOnly') },
    ],
  };
  const fieldLabels: Record<FilterId, string> = {
    client: t('requests.col.client'),
    category: t('privacy.stats.col.category'),
    action: t('privacy.logs.col.action'),
    blocked: t('privacy.logs.col.outcome'),
  };
  const fields: FilterField[] = (Object.keys(fieldOptions) as FilterId[]).map((id) => ({
    id,
    label: fieldLabels[id],
    options: fieldOptions[id].map((o) => o.label),
  }));
  const chips: FilterChip[] = (Object.keys(filters) as FilterId[])
    .filter((id) => filters[id])
    .map((id) => ({ id, label: fieldLabels[id], value: fieldOptions[id].find((o) => o.value === filters[id])?.label ?? filters[id] }));

  const setFilter = (id: FilterId, value: string) => {
    setFilters((f) => ({ ...f, [id]: value }));
    setPage(1);
  };

  const rows: Row[] = (data?.items ?? []).map((e) => ({
    id: e.id,
    time: Date.parse(e.startedAtUtc),
    client: clientName(e.clientKind),
    model: e.requestedModel ?? '',
    outcome: outcomeKind(e),
    hits: e.hits.reduce((sum, h) => sum + h.count, 0),
    e,
  }));

  const columns: DataColumn<Row>[] = [
    { key: 'time', label: t('requests.col.time'), width: 92, render: (_, row) => <span className="num">{formatTime(row.e.startedAtUtc, locale)}</span> },
    {
      key: 'model',
      label: t('requests.col.model'),
      render: (_, row) => (
        <button type="button" className="block w-full min-w-0 text-left" onClick={() => setSelected(row.id)} aria-label={t('privacy.logs.viewRequest', { model: row.model || row.id })}>
          <span className="block truncate font-medium hover:text-[var(--accent)]">{row.model || '—'}</span>
          <span className="block truncate text-[11px] text-[var(--text-muted)]">{clientName(row.e.clientKind)}</span>
        </button>
      ),
    },
    {
      key: 'outcome',
      label: t('privacy.logs.col.outcome'),
      width: 130,
      render: (_, row) => <OutcomeBadge event={row.e} />,
    },
    {
      key: 'hits',
      label: t('privacy.logs.col.hits'),
      render: (_, row) => <HitsSummary event={row.e} />,
    },
    { key: 'client', label: t('requests.col.client'), width: 110, render: (v) => <span className="block truncate">{String(v || '—')}</span> },
  ];

  return (
    <>
      <div className="mb-3 flex items-center gap-2">
        <SearchField
          className="w-52"
          value={model}
          onChange={(v) => {
            setModel(v);
            setPage(1);
          }}
          placeholder={t('requests.searchModel')}
        />
        <Button
          variant="plain"
          icon={<RefreshCw className={cn('size-4', events.isFetching && 'animate-spin')} />}
          aria-label={t('common.refresh')}
          onClick={() => void events.refetch()}
        />
      </div>

      <div className="mb-3">
        <FilterToolbar
          label={t('requests.filters')}
          filters={chips}
          onRemove={(id) => setFilter(id as FilterId, '')}
          onClearAll={() => {
            setFilters({ client: '', category: '', action: '', blocked: '' });
            setPage(1);
          }}
          addFilter={{
            fields,
            label: t('requests.addFilter'),
            onAdd: (chip) => {
              const id = chip.id as FilterId;
              const value = fieldOptions[id].find((o) => o.label === chip.value)?.value;
              if (value) setFilter(id, value);
            },
          }}
        />
      </div>

      {events.isLoading && <Spinner lines={4} className="py-6" />}
      {events.isError && <Alert tone="danger" title={t('common.error')}>{errorText(events.error)}</Alert>}
      {data && data.items.length === 0 && (
        <EmptyState icon={<ShieldOff className="size-6" />} title={t('privacy.logs.empty')} detail={t('privacy.logs.empty.detail')} />
      )}
      {data && data.items.length > 0 && (
        <>
          <p className="mb-2 text-[12px] text-[var(--text-secondary)]">{t('privacy.logs.total', { count: formatTokens(data.total) })}</p>
          <SortableDataTable<Row>
            caption={t('privacy.tab.logs')}
            rows={rows}
            columns={columns}
            rowKey="id"
            emptyMessage={t('privacy.logs.empty')}
          />
        </>
      )}
      {data && pages > 1 && (
        <div className="mt-4 flex justify-center">
          <Pagination page={page} pageCount={pages} onPageChange={setPage} label={t('requests.page', { page, pages })} />
        </div>
      )}
      <RequestDetailSheet id={selected} onClose={() => setSelected(null)} />
    </>
  );
}

function outcomeKind(e: PrivacyEvent) {
  if (e.blocked) return 'blocked';
  if (e.redactions > 0) return 'redact';
  if (e.dryRun) return 'dryRun';
  return 'warn';
}

function OutcomeBadge({ event }: { event: PrivacyEvent }) {
  const { t } = useI18n();
  if (event.blocked) return <Badge tone="red">{t('privacy.logs.outcome.blocked')}</Badge>;
  if (event.redactions > 0) return <Badge tone="orange">{t('privacy.logs.outcome.redacted', { count: event.redactions })}</Badge>;
  if (event.dryRun) return <Badge tone="neutral">{t('privacy.logs.outcome.dryRun')}</Badge>;
  return <Badge tone="neutral">{t('settings.privacy.action.warn')}</Badge>;
}

function HitsSummary({ event }: { event: PrivacyEvent }) {
  const categoryLabel = usePrivacyCategoryLabel();
  const { t } = useI18n();
  if (event.hits.length === 0) return <span className="text-[var(--text-muted)]">—</span>;
  const shown = event.hits.slice(0, 2);
  const rest = event.hits.length - shown.length;
  return (
    <span className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[12px]">
      {shown.map((h) => (
        <span key={`${h.ruleId}:${h.action}`} className="inline-flex items-center gap-1">
          <PrivacyActionBadge action={h.action} />
          <span>{categoryLabel(h.category)} × {h.count}</span>
          {h.samples && h.samples.length > 0 && (
            <span className="font-mono text-[11px] text-[var(--text-muted)]">{h.samples.slice(0, 2).join('  ')}</span>
          )}
        </span>
      ))}
      {rest > 0 && <span className="text-[var(--text-muted)]">{t('privacy.logs.moreHits', { count: rest })}</span>}
    </span>
  );
}
