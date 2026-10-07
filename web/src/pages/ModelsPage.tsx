import { Boxes, CloudDownload, Plus } from 'lucide-react';
import { useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router';

import { useCreateModel, useModels, useSyncApply, useSyncPreview } from '../api/hooks';
import type { Model, SyncChange } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
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

type SyncRow = { id: string; model: string; kind: string; c: SyncChange };

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
      width={640}
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
      {changes.length > 0 && (
        <SortableDataTable<SyncRow>
          caption={t('models.sync')}
          selectable
          rowKey="id"
          itemName={{ one: t('models.sync.changeOne'), other: t('models.sync.changeOther') }}
          selectedKeys={[...checked]}
          onSelectionChange={(keys) => setChecked(new Set(keys))}
          rows={changes.map((c) => ({ id: c.id, model: c.modelId, kind: label(c), c }))}
          columns={[
            { key: 'model', label: t('models.col.model'), render: (v) => <span className="font-mono text-[12px]">{String(v)}</span> },
            {
              key: 'kind',
              label: t('models.sync.change'),
              render: (_, row) => (
                <span className="flex flex-col items-start gap-1">
                  <span className="flex flex-wrap gap-1">
                    <Badge tone={row.c.kind === 'new_model' ? 'green' : 'neutral'}>{row.kind}</Badge>
                    {row.c.skippedBecauseUserModified && <Badge tone="orange">{t('models.sync.userModified')}</Badge>}
                  </span>
                  {row.c.kind !== 'new_model' && (
                    <span className="grid w-full grid-cols-2 gap-2 font-mono text-[10.5px]">
                      <span className="truncate text-[var(--danger)]" title={pretty(row.c.before)}>
                        {JSON.stringify(row.c.before) ?? '∅'}
                      </span>
                      <span className="truncate text-[var(--success)]" title={pretty(row.c.after)}>
                        {JSON.stringify(row.c.after) ?? '∅'}
                      </span>
                    </span>
                  )}
                </span>
              ),
            },
          ]}
        />
      )}
    </Sheet>
  );
}
