import { ArrowLeft, CloudDownload, Copy, Ellipsis, Plug, Plus, RotateCcw, Sparkles, Trash2 } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useNavigate, useParams } from 'react-router';

import { ApiError } from '../api/client';
import {
  useAddProviderModels,
  useApplyTemplateUpdate,
  useDeleteProvider,
  useDeleteProviderModel,
  useDuplicateProvider,
  useModels,
  usePriceKeys,
  useProvider,
  useProviderModels,
  useRemoteModels,
  useTemplateUpdate,
  useTestProvider,
  useUpdateProvider,
  useUpdateProviderModel,
} from '../api/hooks';
import type { ApiProtocol, FieldOrigin, ModelOverrides, Provider, ProviderModel, ProviderUpdate, TemplateEndpoint } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { Checkbox } from '../components/arc/checkbox/checkbox';
import { ChipGroup } from '../components/arc/chip-group/chip-group';
import ConfirmMorph from '../components/arc/confirm-morph/confirm-morph';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../components/arc/tabs/tabs';
import { PricingSourceBadge } from '../components/Billing';
import { CLIENT_META } from '../components/icons';
import { Page } from '../components/layout/Page';
import { PricingEditor } from '../components/PricingEditor';
import { SubscriptionAccounts } from '../components/SubscriptionAccounts';
import { Badge, Button, CodeBlock, DetailSection, EmptyState, Field, Group, Input, KV, NumberInput, Row, SearchField, Spinner, Switch, TextArea } from '../components/ui/controls';
import { errorText, Menu, Select, Sheet, useFeedback } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';
import { formatContext, formatMs, priceSummary, pretty } from '../lib/format';
import { AUTH_OPTIONS, PROTOCOL_OPTIONS } from './ProvidersPage';
import type { ClientKind } from '../api/types';

type Tab = 'models' | 'settings';

export function ProviderDetailPage() {
  const { t } = useI18n();
  const { id = '' } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const provider = useProvider(id);
  const [tab, setTab] = useState<Tab>('models');
  const [selectedPm, setSelectedPm] = useState<number | null>(null);
  const models = useProviderModels(id);
  const pm = models.data?.find((m) => m.id === selectedPm) ?? null;

  if (provider.isError) {
    return (
      <Page title={t('nav.providers')} leading={<BackButton onClick={() => navigate('/providers')} />}>
        <Alert tone="danger" title={t('common.error')}>{errorText(provider.error)}</Alert>
      </Page>
    );
  }
  const p = provider.data;
  return (
    <Page
      title={p ? p.name : ''}
      subtitle={p ? [p.templateId, p.priceKey ? t('providers.priceKeyShort', { key: p.priceKey }) : null].filter(Boolean).join(' · ') : undefined}
      leading={<BackButton onClick={() => navigate('/providers')} />}
      actions={p && <HeaderActions p={p} />}
    >
      {!p ? (
        <Spinner lines={4} className="mx-auto mt-6 max-w-[860px]" />
      ) : (
        <Tabs className="mx-auto max-w-[860px]" value={tab} onValueChange={(v) => setTab(v as Tab)}>
          <TabsList aria-label={p.name}>
            <TabsTrigger value="models">{t('providers.tab.models')}</TabsTrigger>
            <TabsTrigger value="settings">{t('providers.tab.settings')}</TabsTrigger>
          </TabsList>
          <TabsContent value="models" className="pt-4">
            <ModelsTab provider={p} models={models.data} loading={models.isLoading} selected={selectedPm} onSelect={setSelectedPm} />
          </TabsContent>
          <TabsContent value="settings" className="pt-4">
            <SettingsTab provider={p} />
          </TabsContent>
          <ProviderModelSheet provider={p} pm={tab === 'models' ? pm : null} onClose={() => setSelectedPm(null)} />
        </Tabs>
      )}
    </Page>
  );
}

function BackButton({ onClick }: { onClick: () => void }) {
  const { t } = useI18n();
  return <Button variant="glass" icon={<ArrowLeft className="size-4" />} aria-label={t('common.back')} onClick={onClick} />;
}

function HeaderActions({ p }: { p: Provider }) {
  const { t } = useI18n();
  const navigate = useNavigate();
  const { toast, confirm } = useFeedback();
  const test = useTestProvider(p.id);
  const duplicate = useDuplicateProvider();
  const del = useDeleteProvider();
  const [updateOpen, setUpdateOpen] = useState(false);

  const runTest = () =>
    test.mutate(undefined, {
      onSuccess: (r) =>
        toast(
          r.ok
            ? t('providers.testOk', { ms: formatMs(r.latencyMs), model: r.model ?? '' })
            : t('providers.testFail', { error: r.error ?? `HTTP ${r.httpStatus ?? '?'}` }),
          r.ok ? 'success' : 'error',
        ),
      onError: (e) => toast(errorText(e), 'error'),
    });

  const remove = async () => {
    if (!(await confirm({ title: t('providers.deleteConfirm', { name: p.name }), detail: t('providers.deleteDetail'), destructive: true, confirmLabel: t('common.delete') }))) return;
    del.mutate(p.id, {
      onSuccess: () => {
        toast(t('providers.deleted'));
        navigate('/providers');
      },
      onError: (e) => {
        const bound = e instanceof ApiError && e.status === 409 ? ((e.details as { boundClients?: string[] })?.boundClients ?? []) : [];
        toast(bound.length ? t('providers.deleteBound', { clients: bound.map((k) => CLIENT_META[k as ClientKind]?.name ?? k).join(', ') }) : errorText(e), 'error');
      },
    });
  };

  return (
    <>
      <Button icon={<Plug className="size-3.5" />} loading={test.isPending} onClick={runTest}>
        {t('providers.test')}
      </Button>
      <Menu
          label={t('common.more')}
          icon={<Ellipsis className="size-4" />}
          items={[
            {
              id: 'dup',
              label: t('providers.duplicate'),
              icon: <Copy className="size-3.5" />,
              onSelect: () => duplicate.mutate(p.id, { onSuccess: (n) => navigate(`/providers/${n.id}`), onError: (e) => toast(errorText(e), 'error') }),
            },
            { id: 'tpl', label: t('providers.templateUpdate'), icon: <Sparkles className="size-3.5" />, disabled: !p.templateId, onSelect: () => setUpdateOpen(true) },
            { id: 'del', label: t('common.delete'), icon: <Trash2 className="size-3.5" />, destructive: true, separatorBefore: true, onSelect: () => void remove() },
          ]}
        />
      <TemplateUpdateSheet open={updateOpen} onOpenChange={setUpdateOpen} provider={p} />
    </>
  );
}

function TemplateUpdateSheet({ open, onOpenChange, provider }: { open: boolean; onOpenChange: (o: boolean) => void; provider: Provider }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const info = useTemplateUpdate(provider.id, open);
  const apply = useApplyTemplateUpdate(provider.id);
  const upToDate = info.data && info.data.latestVersion <= info.data.currentVersion;
  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('providers.templateUpdate')}
      description={info.data ? t('providers.templateVersions', { current: info.data.currentVersion, latest: info.data.latestVersion }) : undefined}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={!info.data || upToDate}
            loading={apply.isPending}
            onClick={() => apply.mutate(undefined, { onSuccess: () => { onOpenChange(false); toast(t('providers.templateApplied'), 'success'); }, onError: (e) => toast(errorText(e), 'error') })}
          >
            {t('common.apply')}
          </Button>
        </>
      }
    >
      {info.isLoading && <Spinner />}
      {info.isError && <Alert tone="danger" title={t('common.error')}>{errorText(info.error)}</Alert>}
      {upToDate && <Alert tone="success" title={t('providers.templateUpToDate')} />}
      {info.data && !upToDate && (
        <ul className="list-disc pl-5 text-[12px]">
          {info.data.changes.map((c, i) => (
            <li key={i}>{c}</li>
          ))}
        </ul>
      )}
    </Sheet>
  );
}

// ---------- models tab ----------

function ModelsTab({ provider, models, loading, selected, onSelect }: { provider: Provider; models?: ProviderModel[]; loading: boolean; selected: number | null; onSelect: (id: number | null) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const [search, setSearch] = useState('');
  const [remoteOpen, setRemoteOpen] = useState(false);
  const [addOpen, setAddOpen] = useState(false);
  const update = useUpdateProviderModel(provider.id);
  const list = (models ?? []).filter((m) => !search || m.modelId.toLowerCase().includes(search.toLowerCase()) || m.effective.displayName.toLowerCase().includes(search.toLowerCase()));

  return (
    <>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <SearchField className="w-56" value={search} onChange={setSearch} placeholder={t('providers.searchModels')} />
        <div className="ml-auto flex items-center gap-2">
          <Button icon={<Plus className="size-3.5" />} onClick={() => setAddOpen(true)}>
            {t('providers.addModels')}
          </Button>
          <Button variant="primary" icon={<CloudDownload className="size-3.5" />} onClick={() => setRemoteOpen(true)}>
            {t('providers.fetchRemote')}
          </Button>
        </div>
      </div>
      {loading && <Spinner lines={4} />}
      {models?.length === 0 && (
        <div className="card">
          <EmptyState title={t('providers.noModels')} detail={t('providers.noModelsDetail')} />
        </div>
      )}
      {list.length > 0 && (
        <Group footer={t('providers.modelsFooter')}>
          {list.map((m) => (
            <Row
              key={m.id}
              selected={m.id === selected}
              onClick={() => onSelect(m.id)}
              label={
                <span className="flex items-center gap-2">
                  <span className={cn('font-mono text-[12.5px]', !m.enabled && 'text-[var(--text-muted)]')}>{m.modelId}</span>
                  {!m.systemModelId && <Badge tone="orange">{t('providers.unlinked')}</Badge>}
                </span>
              }
              detail={[
                m.effective.displayName !== m.modelId ? m.effective.displayName : null,
                m.effective.contextWindow ? formatContext(m.effective.contextWindow) : null,
                priceSummary(m.effectivePricing),
              ]
                .filter(Boolean)
                .join(' · ')}
            >
              <PricingSourceBadge source={m.pricingSource} priceKey={m.priceKey} />
              <Switch checked={m.enabled} label={t('common.enabled')} onChange={(enabled) => update.mutate({ pmId: m.id, enabled }, { onError: (e) => toast(errorText(e), 'error') })} />
            </Row>
          ))}
        </Group>
      )}
      <AddModelsSheet open={addOpen} onOpenChange={setAddOpen} provider={provider} />
      <RemoteModelsSheet open={remoteOpen} onOpenChange={setRemoteOpen} provider={provider} />
    </>
  );
}

/** Manually add model IDs (one per line or comma separated); they auto-link to system models. */
function AddModelsSheet({ open, onOpenChange, provider }: { open: boolean; onOpenChange: (o: boolean) => void; provider: Provider }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const add = useAddProviderModels(provider.id);
  const [text, setText] = useState('');
  useEffect(() => {
    if (open) setText('');
  }, [open]);
  const ids = [...new Set(text.split(/[\s,]+/).map((s) => s.trim()).filter(Boolean))];
  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('providers.addModels')}
      description={t('providers.addModelsDetail')}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={ids.length === 0}
            loading={add.isPending}
            onClick={() =>
              add.mutate(ids, {
                onSuccess: (r) => {
                  onOpenChange(false);
                  toast(t('providers.modelsAdded', { count: r.length }), 'success');
                },
                onError: (e) => toast(errorText(e), 'error'),
              })
            }
          >
            {t('providers.addSelected', { count: ids.length })}
          </Button>
        </>
      }
    >
      <TextArea
        autoFocus
        label={t('providers.modelIds')}
        rows={6}
        spellCheck={false}
        className="w-full font-mono text-[12px]"
        placeholder={'deepseek-v4-pro\nclaude-sonnet-4-6'}
        value={text}
        onChange={(e) => setText(e.target.value)}
      />
    </Sheet>
  );
}

function RemoteModelsSheet({ open, onOpenChange, provider }: { open: boolean; onOpenChange: (o: boolean) => void; provider: Provider }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const remote = useRemoteModels(provider.id, open);
  const add = useAddProviderModels(provider.id);
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState('');

  useEffect(() => {
    if (open) setChecked(new Set());
  }, [open]);

  const list = (remote.data ?? []).filter((m) => !search || m.id.toLowerCase().includes(search.toLowerCase()));
  const toggle = (id: string) => setChecked((s) => {
    const n = new Set(s);
    if (n.has(id)) n.delete(id);
    else n.add(id);
    return n;
  });

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      width={560}
      title={t('providers.fetchRemote')}
      description={t('providers.fetchRemoteDetail')}
      footer={
        <>
          <Button
            variant="plain"
            className="mr-auto"
            onClick={() => setChecked(new Set(list.filter((m) => !m.alreadyAdded).map((m) => m.id)))}
            disabled={list.length === 0}
          >
            {t('common.selectAll')}
          </Button>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={checked.size === 0}
            loading={add.isPending}
            onClick={() =>
              add.mutate([...checked], {
                onSuccess: (r) => {
                  onOpenChange(false);
                  toast(t('providers.modelsAdded', { count: r.length }), 'success');
                },
                onError: (e) => toast(errorText(e), 'error'),
              })
            }
          >
            {t('providers.addSelected', { count: checked.size })}
          </Button>
        </>
      }
    >
      <SearchField className="mb-3" value={search} onChange={setSearch} placeholder={t('providers.searchModels')} />
      {remote.isLoading && <Spinner lines={4} />}
      {remote.isError && <Alert tone="danger" title={t('common.error')}>{errorText(remote.error)}</Alert>}
      <div className="card divide-y divide-[var(--border)]">
        {list.map((m) => (
          <Row
            key={m.id}
            onClick={m.alreadyAdded ? undefined : () => toggle(m.id)}
            label={<span className="font-mono text-[12px]">{m.id}</span>}
            detail={m.linkedSystemModelId ? t('providers.linksTo', { id: m.linkedSystemModelId }) : t('providers.noLink')}
          >
            {m.alreadyAdded ? (
              <Badge>{t('providers.added')}</Badge>
            ) : (
              <span onClick={(e) => e.stopPropagation()}>
                <Checkbox aria-label={m.id} checked={checked.has(m.id)} onCheckedChange={() => toggle(m.id)} />
              </span>
            )}
          </Row>
        ))}
      </div>
    </Sheet>
  );
}

// ---------- provider model edit dialog (inherit / override) ----------

function OriginBadge({ origin }: { origin: FieldOrigin }) {
  const { t } = useI18n();
  return origin === 'overridden' ? <Badge tone="orange">{t('origin.overridden')}</Badge> : origin === 'inherited' ? <Badge>{t('origin.inherited')}</Badge> : <Badge tone="red">{t('origin.unset')}</Badge>;
}

function ProviderModelSheet({ provider, pm, onClose }: { provider: Provider; pm: ProviderModel | null; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateProviderModel(provider.id);
  const del = useDeleteProviderModel(provider.id);
  const allModels = useModels();
  const [o, setO] = useState<ModelOverrides>({});
  const [link, setLink] = useState<string>('');
  const [customPrice, setCustomPrice] = useState(false);

  useEffect(() => {
    if (!pm) return;
    setO(pm.overrides);
    setLink(pm.systemModelId ?? '');
    setCustomPrice(pm.overrides.pricing != null);
  }, [pm]);

  if (!pm) return <Sheet open={false} onOpenChange={() => onClose()} title={t('providers.tab.models')} />;

  const overrides: ModelOverrides = { ...o, pricing: customPrice ? (o.pricing ?? null) : null };
  const dirty = JSON.stringify(overrides) !== JSON.stringify({ ...pm.overrides, pricing: pm.overrides.pricing ?? null }) || link !== (pm.systemModelId ?? '');

  const save = () =>
    update.mutate(
      { pmId: pm.id, overrides, systemModelId: link || null },
      {
        onSuccess: () => {
          toast(t('common.saved'), 'success');
          onClose();
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );


  const field = (key: 'displayName' | 'contextWindow' | 'maxOutputTokens', label: string, input: ReactNode) => (
    <div>
      <div className="mb-1 flex items-center justify-between gap-2">
        <span className="text-[13px] font-medium">{label}</span>
        <span className="flex items-center gap-1">
          <OriginBadge origin={o[key] != null ? 'overridden' : pm.origins[key] === 'overridden' ? 'inherited' : pm.origins[key]} />
          {o[key] != null && (
            <Button size="sm" variant="plain" icon={<RotateCcw className="size-3" />} title={t('origin.reset')} aria-label={t('origin.reset')} onClick={() => setO({ ...o, [key]: null })} />
          )}
        </span>
      </div>
      {input}
    </div>
  );

  return (
    <Sheet
      open
      onOpenChange={(open) => !open && onClose()}
      width={720}
      title={pm.modelId}
      description={provider.name}
      footer={
        <>
          <div className="mr-auto">
            <ConfirmMorph
              label={t('providers.removeModelShort')}
              icon={<Trash2 className="size-4" />}
              prompt={t('providers.removeModel', { id: pm.modelId })}
              confirmLabel={t('common.delete')}
              cancelLabel={t('common.cancel')}
              pendingLabel={t('common.deleting')}
              doneLabel={t('common.deleted')}
              errorLabel={t('common.error')}
              retryLabel={t('common.retry')}
              onConfirm={async () => {
                await del.mutateAsync(pm.id);
                onClose();
              }}
            />
          </div>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!dirty} loading={update.isPending} onClick={save}>
            {t('common.save')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-6">
        <div className="flex flex-wrap items-center gap-2">
          <PricingSourceBadge source={pm.pricingSource} priceKey={pm.priceKey} />
          {pm.multiplier !== 1 && pm.pricingSource !== 'provider_override' && <Badge>×{pm.multiplier}</Badge>}
        </div>
        <DetailSection title={t('providers.linkedModel')}>
          <Select
            className="w-full"
            ariaLabel={t('providers.linkedModel')}
            value={link}
            onChange={setLink}
            options={[{ value: '', label: t('providers.noLink') }, ...(allModels.data ?? []).map((m) => ({ value: m.id, label: m.id, detail: m.displayName }))]}
          />
          <p className="mt-2 text-[11px] text-[var(--text-muted)]">{t('providers.linkedModelHint')}</p>
        </DetailSection>

        <DetailSection title={t('providers.overrides')}>
          <div className="grid gap-3 sm:grid-cols-3">
            {field(
              'displayName',
              t('models.field.displayName'),
              <Input className="w-full" aria-label={t('models.field.displayName')} placeholder={pm.effective.displayName} value={o.displayName ?? ''} onChange={(e) => setO({ ...o, displayName: e.target.value || null })} />,
            )}
            {field(
              'contextWindow',
              t('models.field.contextWindow'),
              <NumberInput className="w-full" label={t('models.field.contextWindow')} placeholder={String(pm.effective.contextWindow ?? '')} value={o.contextWindow} onChange={(v) => setO({ ...o, contextWindow: v })} />,
            )}
            {field(
              'maxOutputTokens',
              t('models.field.maxOutputTokens'),
              <NumberInput className="w-full" label={t('models.field.maxOutputTokens')} placeholder={String(pm.effective.maxOutputTokens ?? '')} value={o.maxOutputTokens} onChange={(v) => setO({ ...o, maxOutputTokens: v })} />,
            )}
          </div>
        </DetailSection>

        <DetailSection
          title={t('providers.price')}
          action={
            <label className="flex items-center gap-2 text-[12px] text-[var(--text-secondary)]">
              {t('providers.customPrice')}
              <Switch
                checked={customPrice}
                label={t('providers.customPrice')}
                onChange={(on) => {
                  setCustomPrice(on);
                  if (on && o.pricing == null) setO({ ...o, pricing: pm.effectivePricing ?? { currency: 'USD', unit: 'per_1m_tokens', base: { input: null, output: null } } });
                }}
              />
            </label>
          }
        >
          <p className="mb-3 text-[11px] text-[var(--text-muted)]">{customPrice ? t('providers.customPriceHint') : t('providers.inheritedPriceHint')}</p>
          {customPrice ? (
            <div className="card p-4">
              <PricingEditor value={o.pricing ?? null} onChange={(pricing) => setO({ ...o, pricing })} />
            </div>
          ) : pm.effectivePricing ? (
            <CodeBlock maxHeight={220}>{pretty(pm.effectivePricing)}</CodeBlock>
          ) : (
            <Alert tone="warning" title={t('pricingSource.none')} />
          )}
        </DetailSection>
      </div>
    </Sheet>
  );
}


// ---------- settings tab ----------

function toUpdate(p: Provider): ProviderUpdate {
  return {
    name: p.name,
    apiKey: '',
    endpoints: p.endpoints,
    preferredUpstreamProtocols: p.preferredUpstreamProtocols,
    authScheme: p.authScheme,
    extraHeaders: p.extraHeaders,
    httpProxy: p.httpProxy ?? null,
    priceMultiplier: p.priceMultiplier,
    priceKey: p.priceKey ?? null,
    settings: p.settings,
    enabled: p.enabled,
    notes: p.notes ?? null,
  };
}

function SettingsTab({ provider }: { provider: Provider }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateProvider(provider.id);
  const priceKeys = usePriceKeys();
  const base = useMemo(() => toUpdate(provider), [provider]);
  const [form, setForm] = useState<ProviderUpdate>(base);
  const [headers, setHeaders] = useState<[string, string][]>(Object.entries(provider.extraHeaders));
  const [endpointOpen, setEndpointOpen] = useState(false);
  const [headerOpen, setHeaderOpen] = useState(false);

  useEffect(() => {
    setForm(base);
    setHeaders(Object.entries(base.extraHeaders));
  }, [base]);

  const set = (patch: Partial<ProviderUpdate>) => setForm((f) => ({ ...f, ...patch }));
  const formWithHeaders = { ...form, extraHeaders: Object.fromEntries(headers.filter(([k]) => k.trim())) };
  const dirty = JSON.stringify(formWithHeaders) !== JSON.stringify(base);

  const save = () => {
    const patch: Partial<ProviderUpdate> = {};
    for (const k of Object.keys(formWithHeaders) as (keyof ProviderUpdate)[]) {
      if (k === 'apiKey') continue;
      if (JSON.stringify(formWithHeaders[k]) !== JSON.stringify(base[k])) (patch as Record<string, unknown>)[k] = formWithHeaders[k];
    }
    if (form.apiKey) patch.apiKey = form.apiKey;
    update.mutate(patch, { onSuccess: () => toast(t('common.saved'), 'success'), onError: (e) => toast(errorText(e), 'error') });
  };

  const setEndpoint = (i: number, patch: Partial<TemplateEndpoint>) => set({ endpoints: form.endpoints.map((e, j) => (j === i ? { ...e, ...patch } : e)) });

  return (
    <>
      <Group title={t('providers.section.basic')}>
        <Row label={t('providers.name')}>
          <Input className="w-64" aria-label={t('providers.name')} value={form.name} onChange={(e) => set({ name: e.target.value })} />
        </Row>
        <Row label={t('common.enabled')}>
          <Switch checked={form.enabled} label={t('common.enabled')} onChange={(enabled) => set({ enabled })} />
        </Row>
        <Row label={t('providers.notes')}>
          <Input className="w-64" aria-label={t('providers.notes')} value={form.notes ?? ''} onChange={(e) => set({ notes: e.target.value || null })} />
        </Row>
      </Group>

      <Group title={t('providers.section.auth')} footer={t('providers.apiKeyStored')}>
        <Row label={t('providers.apiKey')} detail={provider.hasApiKey ? provider.apiKeyMasked ?? '••••' : t('providers.noKey')}>
          <Input type="password" autoComplete="off" className="w-64 font-mono" aria-label={t('providers.apiKey')} placeholder={t('providers.replaceKey')} value={form.apiKey} onChange={(e) => set({ apiKey: e.target.value })} />
        </Row>
        <Row label={t('providers.authScheme')}>
          <Select className="w-48" value={form.authScheme} onChange={(authScheme) => set({ authScheme })} options={AUTH_OPTIONS} />
        </Row>
      </Group>

      <SubscriptionAccounts provider={provider} />

      <Group
        title={t('providers.section.endpoints')}
        footer={t('providers.endpointsFooter')}
      >
        {form.endpoints.map((e, i) => (
          <div key={i} className="flex flex-wrap items-center gap-2 px-4 py-3">
            <Select className="w-48" value={e.protocol} onChange={(protocol) => setEndpoint(i, { protocol })} options={PROTOCOL_OPTIONS} />
            <Input className="min-w-60 flex-1 font-mono" aria-label={t('providers.baseUrl')} value={e.baseUrl} onChange={(ev) => setEndpoint(i, { baseUrl: ev.target.value })} />
            <label className="flex items-center gap-2 text-[12px] text-[var(--text-secondary)]">
              <Switch checked={Boolean(e.fullUrl)} label={t('providers.fullUrl')} onChange={(fullUrl) => setEndpoint(i, { fullUrl })} />
              {t('providers.fullUrl')}
            </label>
            <Button size="sm" variant="plain" icon={<Trash2 className="size-3.5" />} aria-label={t('common.delete')} onClick={() => set({ endpoints: form.endpoints.filter((_, j) => j !== i) })} />
          </div>
        ))}
        <div className="px-4 py-2">
          <Button size="sm" variant="plain" icon={<Plus className="size-3.5" />} onClick={() => setEndpointOpen(true)}>
            {t('providers.addEndpoint')}
          </Button>
        </div>
        <Row
          label={t('providers.preferred')}
          detail={
            <>
              {t('providers.preferredHint')}
              {/* A protocol with no endpoint address is skipped when the upstream is picked, so say which ones are dead weight. */}
              {form.preferredUpstreamProtocols.filter((p) => !form.endpoints.some((e) => e.protocol === p)).map((p) => (
                <span key={p} className="mt-1 block text-[var(--warning)]">
                  {t('providers.preferredMissing', { protocol: PROTOCOL_OPTIONS.find((o) => o.value === p)?.label ?? p })}
                </span>
              ))}
            </>
          }
        >
          <ChipGroup
            label={t('providers.preferred')}
            options={PROTOCOL_OPTIONS.map((o) => {
              const idx = form.preferredUpstreamProtocols.indexOf(o.value);
              return { value: o.value, label: idx >= 0 ? `${idx + 1}. ${o.label}` : o.label };
            })}
            value={form.preferredUpstreamProtocols}
            onValueChange={(next) => {
              // The chips report options order; keep the priority order in which protocols were picked.
              const kept = form.preferredUpstreamProtocols.filter((x) => next.includes(x));
              const added = next.filter((x) => !kept.includes(x as ApiProtocol)) as ApiProtocol[];
              set({ preferredUpstreamProtocols: [...kept, ...added] });
            }}
          />
        </Row>
      </Group>

      <Group title={t('providers.section.billing')} footer={t('providers.billingFooter')}>
        <Row label={t('providers.priceKey')} detail={t('providers.priceKeyHint')}>
          <Select
            className="w-56"
            value={form.priceKey == null ? 'inherit' : `price:${form.priceKey}`}
            onChange={(v) => set({ priceKey: v === 'inherit' ? null : v.slice('price:'.length) })}
            options={[
              { value: 'inherit', label: t('providers.priceKeyDefault', { id: provider.templateId ?? '—' }) },
              { value: 'price:', label: t('providers.priceKeyNone') },
              ...(priceKeys.data ?? []).map((k) => ({ value: `price:${k.priceKey}`, label: k.label, detail: k.priceKey })),
            ]}
          />
        </Row>
        <Row label={t('providers.multiplier')} detail={t('providers.multiplierHint')}>
          <NumberInput className="w-28" label={t('providers.multiplier')} step={0.01} min={0} value={form.priceMultiplier} onChange={(v) => set({ priceMultiplier: v ?? 1 })} />
        </Row>
      </Group>

      <Group title={t('providers.section.advanced')}>
        <Row label={t('providers.proxy')} detail={t('providers.proxyHint')}>
          <Input className="w-64 font-mono" aria-label={t('providers.proxy')} placeholder="http://127.0.0.1:7890" value={form.httpProxy ?? ''} onChange={(e) => set({ httpProxy: e.target.value || null })} />
        </Row>
        <div className="px-4 py-3">
          <div className="mb-2 text-[13px]">{t('providers.headers')}</div>
          {headers.map(([k, v], i) => (
            <div key={i} className="mb-2 flex gap-2">
              <Input className="w-48 font-mono" aria-label={t('providers.headerName')} placeholder="Header" value={k} onChange={(e) => setHeaders(headers.map((h, j) => (j === i ? [e.target.value, h[1]] : h)))} />
              <Input className="flex-1 font-mono" aria-label={t('providers.headerValue')} placeholder="value" value={v} onChange={(e) => setHeaders(headers.map((h, j) => (j === i ? [h[0], e.target.value] : h)))} />
              <Button size="sm" variant="plain" icon={<Trash2 className="size-3.5" />} aria-label={t('common.delete')} onClick={() => setHeaders(headers.filter((_, j) => j !== i))} />
            </div>
          ))}
          <Button size="sm" variant="plain" icon={<Plus className="size-3.5" />} onClick={() => setHeaderOpen(true)}>
            {t('providers.addHeader')}
          </Button>
        </div>
        {Object.keys(provider.settings).length > 0 && (
          <div className="px-4 py-3">
            <TextArea
              label={t('providers.adapterSettings')}
              rows={4}
              className="w-full font-mono"
              defaultValue={pretty(form.settings)}
              onBlur={(e) => {
                try {
                  set({ settings: JSON.parse(e.target.value) as Record<string, unknown> });
                } catch {
                  toast(t('common.invalidJson'), 'error');
                }
              }}
            />
          </div>
        )}
      </Group>

      <Group title={t('providers.section.info')}>
        <div className="px-4 py-3">
          <KV label="ID">{provider.id}</KV>
          {provider.adapterId && <KV label={t('providers.adapter')}>{provider.adapterId}</KV>}
          <KV label={t('providers.boundClients')}>{provider.boundClients.map((k) => CLIENT_META[k as ClientKind]?.name ?? k).join(', ') || '—'}</KV>
        </div>
      </Group>

      <AddEndpointSheet open={endpointOpen} onOpenChange={setEndpointOpen} onAdd={(e) => set({ endpoints: [...form.endpoints, e] })} />
      <AddHeaderSheet open={headerOpen} onOpenChange={setHeaderOpen} onAdd={(h) => setHeaders([...headers.filter(([k]) => k.toLowerCase() !== h[0].toLowerCase()), h])} />

      <div className="sticky bottom-2 flex justify-end">
        {dirty && (
          <div className="glass flex items-center gap-2 rounded-full p-1 pl-4 text-[12px]">
            <span className="text-[var(--text-secondary)]">{t('common.unsaved')}</span>
            <Button size="sm" onClick={() => { setForm(base); setHeaders(Object.entries(base.extraHeaders)); }}>
              {t('common.revert')}
            </Button>
            <Button size="sm" variant="primary" loading={update.isPending} onClick={save}>
              {t('common.save')}
            </Button>
          </div>
        )}
      </div>
    </>
  );
}


function AddEndpointSheet({ open, onOpenChange, onAdd }: { open: boolean; onOpenChange: (o: boolean) => void; onAdd: (e: TemplateEndpoint) => void }) {
  const { t } = useI18n();
  const [protocol, setProtocol] = useState<ApiProtocol>('openai-chat');
  const [baseUrl, setBaseUrl] = useState('');
  const [fullUrl, setFullUrl] = useState(false);
  useEffect(() => {
    if (open) {
      setBaseUrl('');
      setFullUrl(false);
    }
  }, [open]);
  const valid = /^https?:\/\/\S+$/.test(baseUrl.trim());
  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('providers.addEndpoint')}
      description={t('providers.addEndpointDetail')}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={!valid}
            onClick={() => {
              onAdd({ protocol, baseUrl: baseUrl.trim(), ...(fullUrl ? { fullUrl: true } : {}) });
              onOpenChange(false);
            }}
          >
            {t('common.add')}
          </Button>
        </>
      }
    >
      <div className="grid gap-3">
        <Field label={t('providers.protocol')}>
          <Select value={protocol} onChange={setProtocol} options={PROTOCOL_OPTIONS} />
        </Field>
        <Field label={t('providers.baseUrl')} hint={fullUrl ? t('providers.fullUrlHint') : t('providers.baseUrlHint')}>
          <Input autoFocus className="font-mono" placeholder="https://api.example.com/v1" value={baseUrl} invalid={baseUrl !== '' && !valid} onChange={(e) => setBaseUrl(e.target.value)} />
        </Field>
        <label className="flex items-center gap-2 text-[12px] text-[var(--text-secondary)]">
          <Switch checked={fullUrl} onChange={setFullUrl} label={t('providers.fullUrl')} />
          {t('providers.fullUrl')}
        </label>
      </div>
    </Sheet>
  );
}

function AddHeaderSheet({ open, onOpenChange, onAdd }: { open: boolean; onOpenChange: (o: boolean) => void; onAdd: (h: [string, string]) => void }) {
  const { t } = useI18n();
  const [name, setName] = useState('');
  const [value, setValue] = useState('');
  useEffect(() => {
    if (open) {
      setName('');
      setValue('');
    }
  }, [open]);
  const valid = /^[A-Za-z0-9-]+$/.test(name.trim());
  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('providers.addHeader')}
      description={t('providers.addHeaderDetail')}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={!valid}
            onClick={() => {
              onAdd([name.trim(), value]);
              onOpenChange(false);
            }}
          >
            {t('common.add')}
          </Button>
        </>
      }
    >
      <div className="grid gap-3">
        <Field label={t('providers.headerName')}>
          <Input autoFocus className="font-mono" placeholder="X-Custom-Header" value={name} invalid={name !== '' && !valid} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('providers.headerValue')}>
          <Input className="font-mono" value={value} onChange={(e) => setValue(e.target.value)} />
        </Field>
      </div>
    </Sheet>
  );
}
