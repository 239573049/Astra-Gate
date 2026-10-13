import { Boxes, ChevronRight, CloudDownload, Plus } from 'lucide-react';
import { useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router';

import { useCreateModel, useModels, useSyncApply, useSyncPreview } from '../api/hooks';
import type { Model, SyncChange } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { Checkbox } from '../components/arc/checkbox/checkbox';
import { SortableDataTable, type DataColumn } from '../components/arc/sortable-data-table/sortable-data-table';
import { Page } from '../components/layout/Page';
import { Badge, Button, EmptyState, Field, Input, NumberInput, SearchField, Spinner } from '../components/ui/controls';
import { errorText, Select, Sheet, useFeedback } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';
import { useDebounced } from '../lib/hooks';
import { formatContext, formatUnitPrice, pretty } from '../lib/format';
import { useCommandListener } from '../shell/commands';

type ModelRow = { id: string; name: string; vendor: string; context: number; input: number; providerPrices: number; m: Model };

export function ModelsPage() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [vendor, setVendor] = useState('');
  const [creating, setCreating] = useState(false);
  const [syncOpen, setSyncOpen] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);
  const debounced = useDebounced(search);
  const all = useModels();
  const models = useModels(debounced, vendor);
  const vendors = useMemo(() => [...new Set((all.data ?? []).map((m) => m.vendor))].sort(), [all.data]);

  useCommandListener((cmd) => {
    if (cmd === 'find') searchRef.current?.focus();
  });

  return (
    <Page
      title={t('nav.models')}
      subtitle={models.data ? t('models.count', { count: models.data.length }) : undefined}
      actions={
        <>
          <SearchField inputRef={searchRef} className="w-52" value={search} onChange={setSearch} placeholder={t('models.search')} />
          <Select value={vendor} onChange={setVendor} options={[{ value: '', label: t('models.allVendors') }, ...vendors.map((v) => ({ value: v, label: v }))]} />
          <Button icon={<CloudDownload className="size-3.5" />} onClick={() => setSyncOpen(true)}>
            {t('models.sync')}
          </Button>
          <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setCreating(true)}>
            {t('models.new')}
          </Button>
        </>
      }
    >
      {models.isLoading && <Spinner lines={5} />}
      {models.isError && <Alert tone="danger" title={t('common.error')}>{errorText(models.error)}</Alert>}
      {models.data?.length === 0 && <EmptyState icon={<Boxes className="size-6" />} title={t('models.empty')} />}
      {models.data && models.data.length > 0 && (
        <SortableDataTable<ModelRow>
          caption={t('nav.models')}
          rowKey="id"
          rows={models.data.map((m) => ({
            id: m.id,
            name: m.displayName,
            vendor: m.vendor,
            context: m.contextWindow ?? 0,
            input: m.pricing?.base.input ?? 0,
            providerPrices: m.providerPriceKeys.length,
            m,
          }))}
          columns={modelColumns(t, (id) => navigate(`/models/${encodeURIComponent(id)}`))}
        />
      )}
      <CreateModelSheet open={creating} onOpenChange={setCreating} onCreated={(id) => navigate(`/models/${encodeURIComponent(id)}`)} />
      <SyncSheet open={syncOpen} onOpenChange={setSyncOpen} />
    </Page>
  );
}

function modelColumns(t: ReturnType<typeof useI18n>['t'], open: (id: string) => void): DataColumn<ModelRow>[] {
  return [
    {
      key: 'name',
      label: t('models.col.model'),
      render: (_, row) => (
        <button type="button" className="block w-full min-w-0 text-left" onClick={() => open(row.id)}>
          <span className="flex items-center gap-2">
            <span className={cn('truncate font-medium hover:text-[var(--accent)]', !row.m.enabled && 'text-[var(--text-muted)]')}>{row.name}</span>
            {row.m.source === 'user' && <Badge tone="accent">{t('models.source.user')}</Badge>}
            {row.m.userModifiedFields.length > 0 && <Badge tone="orange">{t('models.modified')}</Badge>}
          </span>
          <span className="block truncate font-mono text-[11px] text-[var(--text-muted)]">{row.id}</span>
        </button>
      ),
    },
    { key: 'vendor', label: t('models.col.vendor'), width: 120 },
    { key: 'context', label: t('models.col.context'), numeric: true, width: 96, render: (_, row) => formatContext(row.m.contextWindow) },
    {
      key: 'input',
      label: t('models.col.price'),
      numeric: true,
      width: 150,
      render: (_, row) => {
        const base = row.m.pricing?.base;
        return base ? `${formatUnitPrice(base.input)} / ${formatUnitPrice(base.output)}` : '—';
      },
    },
    {
      key: 'providerPrices',
      label: t('models.col.providerPrices'),
      numeric: true,
      width: 130,
      render: (_, row) => (row.providerPrices ? t('models.providerPriceCount', { count: row.providerPrices }) : '—'),
    },
  ];
}

function CreateModelSheet({ open, onOpenChange, onCreated }: { open: boolean; onOpenChange: (o: boolean) => void; onCreated: (id: string) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const create = useCreateModel();
  const [id, setId] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [vendor, setVendor] = useState('');
  const [context, setContext] = useState<number | null>(null);
  const [input, setInput] = useState<number | null>(null);
  const [output, setOutput] = useState<number | null>(null);

  const submit = () =>
    create.mutate(
      {
        id: id.trim(),
        displayName: displayName.trim() || id.trim(),
        vendor: vendor.trim(),
        contextWindow: context,
        pricing: input != null || output != null ? { currency: 'USD', unit: 'per_1m_tokens', base: { input, output } } : null,
      },
      {
        onSuccess: (m) => {
          onOpenChange(false);
          setId('');
          setDisplayName('');
          onCreated(m.id);
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('models.new')}
      description={t('models.newDetail')}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!id.trim() || !vendor.trim()} loading={create.isPending} onClick={submit}>
            {t('common.create')}
          </Button>
        </>
      }
    >
      <div className="grid gap-4">
        <Field label={t('models.field.id')} hint={t('models.field.idHint')}>
          <Input className="font-mono" value={id} onChange={(e) => setId(e.target.value)} placeholder="my-model-1" />
        </Field>
        <div className="grid grid-cols-2 gap-4">
          <Field label={t('models.field.displayName')}>
            <Input value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          </Field>
          <Field label={t('models.field.vendor')}>
            <Input value={vendor} onChange={(e) => setVendor(e.target.value)} placeholder="openai" />
          </Field>
        </div>
        <div className="grid grid-cols-3 gap-4">
          <Field label={t('models.field.contextWindow')}>
            <NumberInput value={context} onChange={setContext} />
          </Field>
          <Field label={t('models.field.inputPrice')}>
            <NumberInput value={input} onChange={setInput} />
          </Field>
          <Field label={t('models.field.outputPrice')}>
            <NumberInput value={output} onChange={setOutput} />
          </Field>
        </div>
      </div>
    </Sheet>
  );
}

type DiffLine = { path: string; before?: unknown; after?: unknown };

const isPlainObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

/** Leaf paths of a JSON value ("" for a scalar); arrays and empty objects count as leaves. */
function flatten(value: unknown, prefix = '', out = new Map<string, unknown>()): Map<string, unknown> {
  if (isPlainObject(value) && Object.keys(value).length > 0) {
    for (const [k, v] of Object.entries(value)) flatten(v, prefix ? `${prefix}.${k}` : k, out);
  } else if (value !== undefined && value !== null) {
    out.set(prefix, value);
  }
  return out;
}

/** Only the leaves that differ, so a price row shows "base.input 0.5 → 0.6" instead of two near-identical JSON blobs. */
function diffLines(before: unknown, after: unknown): DiffLine[] {
  const a = flatten(before);
  const b = flatten(after);
  const paths = [...b.keys(), ...[...a.keys()].filter((k) => !b.has(k))];
  return paths
    .filter((p) => JSON.stringify(a.get(p)) !== JSON.stringify(b.get(p)))
    .map((p) => ({ path: p.replace(/^pricing\./, ''), before: a.get(p), after: b.get(p) }));
}

const leafText = (v: unknown) => (typeof v === 'string' ? v : JSON.stringify(v));

const SYNC_DIFF_LINES = 4;

function SyncDiff({ before, after }: { before: unknown; after: unknown }) {
  const { t } = useI18n();
  const lines = diffLines(before, after);
  const hidden = lines.length - SYNC_DIFF_LINES;
  return (
    <span
      className="flex w-full min-w-0 flex-col gap-0.5 font-mono text-[11px] leading-snug"
      title={`${pretty(before) || '∅'}\n→\n${pretty(after) || '∅'}`}
    >
      {lines.slice(0, SYNC_DIFF_LINES).map((l) => (
        <span key={l.path} className="flex min-w-0 items-baseline gap-1.5 whitespace-nowrap">
          {l.path && <span className="shrink-0 text-[var(--text-secondary)]">{l.path}</span>}
          {l.before !== undefined && <span className="min-w-0 truncate text-[var(--danger)] line-through opacity-80">{leafText(l.before)}</span>}
          {l.before !== undefined && l.after !== undefined && <span className="shrink-0 text-[var(--text-secondary)]">→</span>}
          {l.after !== undefined && <span className="min-w-0 truncate text-[var(--success)]">{leafText(l.after)}</span>}
        </span>
      ))}
      {hidden > 0 && <span className="text-[var(--text-secondary)]">{t('models.sync.more', { n: hidden })}</span>}
    </span>
  );
}

/** One model's pending changes; a group checkbox toggles them all, single-change groups stay one row. */
function SyncGroups({
  changes,
  label,
  checked,
  onChange,
}: {
  changes: SyncChange[];
  label: (c: SyncChange) => string;
  checked: Set<string>;
  onChange: (next: Set<string>) => void;
}) {
  const { t } = useI18n();
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const groups = useMemo(() => {
    const map = new Map<string, SyncChange[]>();
    for (const c of changes) map.set(c.modelId, [...(map.get(c.modelId) ?? []), c]);
    return [...map];
  }, [changes]);

  const stateOf = (ids: string[]) => {
    const n = ids.filter((id) => checked.has(id)).length;
    return n === 0 ? false : n === ids.length ? true : ('indeterminate' as const);
  };
  const setMany = (ids: string[], on: boolean) => {
    const next = new Set(checked);
    for (const id of ids) if (on) next.add(id);
    else next.delete(id);
    onChange(next);
  };
  const toggleCollapsed = (model: string) =>
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(model)) next.delete(model);
      else next.add(model);
      return next;
    });

  const body = (c: SyncChange) => (
    <span className="flex min-w-0 flex-1 flex-col items-start gap-1">
      <span className="flex flex-wrap gap-1">
        <Badge tone={c.kind === 'new_model' ? 'green' : 'neutral'}>{label(c)}</Badge>
        {c.skippedBecauseUserModified && <Badge tone="orange">{t('models.sync.userModified')}</Badge>}
      </span>
      {c.kind !== 'new_model' && <SyncDiff before={c.before} after={c.after} />}
    </span>
  );

  const allIds = changes.map((c) => c.id);
  return (
    <div className="card max-h-[520px] overflow-auto">
      <ul className="divide-y divide-[var(--border)]">
        <li className="sticky top-0 z-10 flex items-center gap-3 bg-[var(--surface)] px-4 py-2">
          <Checkbox checked={stateOf(allIds)} onCheckedChange={(s) => setMany(allIds, s !== false)} aria-label={t('models.sync.selectAll')} />
          <span className="text-[11px] text-[var(--text-muted)]">
            {t('models.sync.selectedOf', { n: checked.size, total: allIds.length, models: groups.length })}
          </span>
        </li>
        {groups.map(([model, items]) => {
          const ids = items.map((c) => c.id);
          const single = items.length === 1 ? items[0] : undefined;
          const open = !collapsed.has(model);
          return (
            <li key={model} className="px-4 py-2.5">
              <div className="flex items-start gap-3">
                <Checkbox checked={stateOf(ids)} onCheckedChange={(s) => setMany(ids, s !== false)} aria-label={model} className="mt-0.5" />
                <div className="flex min-w-0 flex-1 flex-col gap-1.5">
                  {single ? (
                    <span className="truncate font-mono text-[12px] font-medium" title={model}>
                      {model}
                    </span>
                  ) : (
                    <button
                      type="button"
                      className="flex min-w-0 items-center gap-1 text-left"
                      aria-expanded={open}
                      onClick={() => toggleCollapsed(model)}
                    >
                      <ChevronRight className={cn('size-3.5 shrink-0 text-[var(--text-muted)] transition-transform', open && 'rotate-90')} />
                      <span className="truncate font-mono text-[12px] font-medium" title={model}>
                        {model}
                      </span>
                      <span className="ml-auto shrink-0 pl-2 text-[11px] text-[var(--text-muted)]">
                        {t('models.sync.groupCount', { n: items.length })}
                      </span>
                    </button>
                  )}
                  {single && body(single)}
                </div>
              </div>
              {!single && open && (
                <ul className="mt-2 ml-7 flex flex-col gap-2 border-l border-[var(--border)] pl-3">
                  {items.map((c) => (
                    <li key={c.id} className="flex items-start gap-3">
                      <Checkbox
                        checked={checked.has(c.id)}
                        onCheckedChange={(s) => setMany([c.id], s !== false)}
                        aria-label={`${model} ${label(c)}`}
                        className="mt-0.5"
                      />
                      {body(c)}
                    </li>
                  ))}
                </ul>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}

function SyncSheet({ open, onOpenChange }: { open: boolean; onOpenChange: (o: boolean) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const preview = useSyncPreview();
  const apply = useSyncApply();
  const [checked, setChecked] = useState<Set<string>>(new Set());

  const load = () =>
    preview.mutate(undefined, {
      onSuccess: (p) => setChecked(new Set(p.changes.filter((c) => !c.skippedBecauseUserModified).map((c) => c.id))),
    });

  const changes = preview.data?.changes ?? [];
  const label = (c: SyncChange) =>
    c.kind === 'new_model' ? t('models.sync.newModel') : c.kind === 'price' ? t('models.sync.price', { key: c.priceKey ?? '' }) : t('models.sync.field', { field: c.field ?? '' });

  return (
    <Sheet
      open={open}
      onOpenChange={(o) => {
        onOpenChange(o);
        if (o && !preview.data) load();
      }}
      width={720}
      title={t('models.sync')}
      description={t('models.syncDetail')}
      footer={
        <>
          <Button className="mr-auto" variant="plain" onClick={load} loading={preview.isPending}>
            {t('common.refresh')}
          </Button>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={checked.size === 0}
            loading={apply.isPending}
            onClick={() =>
              apply.mutate([...checked], {
                onSuccess: (r) => {
                  toast(t('models.sync.applied', { count: r.applied }), 'success');
                  onOpenChange(false);
                  preview.reset();
                },
                onError: (e) => toast(errorText(e), 'error'),
              })
            }
          >
            {t('models.sync.apply', { count: checked.size })}
          </Button>
        </>
      }
    >
      {open && !preview.data && !preview.isPending && !preview.isError && (
        <div className="py-6 text-center">
          <Button variant="primary" onClick={load}>
            {t('models.sync.check')}
          </Button>
        </div>
      )}
      {preview.isPending && <Spinner lines={4} />}
      {preview.isError && <Alert tone="danger" title={t('common.error')}>{errorText(preview.error)}</Alert>}
      {preview.data && changes.length === 0 && <Alert tone="success" title={t('models.sync.none')} />}
      {changes.length > 0 && <SyncGroups changes={changes} label={label} checked={checked} onChange={setChecked} />}
    </Sheet>
  );
}
