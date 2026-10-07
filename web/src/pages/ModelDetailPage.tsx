import { ArrowLeft, Pencil, Plus, RotateCcw, Trash2 } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router';

import { useDeleteModel, useDeleteModelPrice, useModel, usePriceKeys, useResetModelField, useUpdateModel, useUpsertModelPrice } from '../api/hooks';
import type { ModelCapabilities, ModelDetail, ModelField, ModelInput, ModelPrice, PricingSchedule } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { Checkbox } from '../components/arc/checkbox/checkbox';
import ConfirmMorph from '../components/arc/confirm-morph/confirm-morph';
import { BillingSimulator } from '../components/Billing';
import { Page } from '../components/layout/Page';
import { PricingEditor } from '../components/PricingEditor';
import { Badge, Button, Field, Group, Input, NumberInput, Row, SectionTitle, Spinner, Switch } from '../components/ui/controls';
import { errorText, Select, Sheet, useFeedback } from '../components/ui/overlays';
import { useI18n, type MessageKey } from '../i18n';
import { formatDateTime, priceSummary } from '../lib/format';

const CAPABILITIES: { key: keyof ModelCapabilities; label: MessageKey }[] = [
  { key: 'vision', label: 'cap.vision' },
  { key: 'tools', label: 'cap.tools' },
  { key: 'reasoning', label: 'cap.reasoning' },
  { key: 'promptCache', label: 'cap.promptCache' },
  { key: 'audio', label: 'cap.audio' },
  { key: 'pdf', label: 'cap.pdf' },
  { key: 'structuredOutput', label: 'cap.structuredOutput' },
];

function toInput(m: ModelDetail): ModelInput {
  return {
    displayName: m.displayName,
    vendor: m.vendor,
    family: m.family ?? null,
    aliases: m.aliases,
    contextWindow: m.contextWindow ?? null,
    maxOutputTokens: m.maxOutputTokens ?? null,
    capabilities: m.capabilities,
    pricing: m.pricing ?? null,
    enabled: m.enabled,
  };
}

export function ModelDetailPage() {
  const { t, locale } = useI18n();
  const navigate = useNavigate();
  const { id: rawId = '' } = useParams<{ id: string }>();
  const id = decodeURIComponent(rawId);
  const model = useModel(id);
  const update = useUpdateModel(id);
  const reset = useResetModelField(id);
  const del = useDeleteModel();
  const { toast } = useFeedback();
  const base = useMemo(() => (model.data ? toInput(model.data) : null), [model.data]);
  const [form, setForm] = useState<ModelInput | null>(base);
  const [aliasText, setAliasText] = useState('');

  useEffect(() => {
    setForm(base);
    setAliasText(base?.aliases?.join(', ') ?? '');
  }, [base]);

  const back = <Button variant="glass" icon={<ArrowLeft className="size-4" />} aria-label={t('common.back')} onClick={() => navigate('/models')} />;
  if (model.isError) {
    return (
      <Page title={t('nav.models')} leading={back}>
        <Alert tone="danger" title={t('common.error')}>{errorText(model.error)}</Alert>
      </Page>
    );
  }
  const m = model.data;
  if (!m || !form) {
    return (
      <Page title="" leading={back}>
        <Spinner lines={5} className="mx-auto max-w-[1100px]" />
      </Page>
    );
  }

  const set = (patch: Partial<ModelInput>) => setForm((f) => ({ ...f!, ...patch }));
  const current: ModelInput = { ...form, aliases: aliasText.split(/[,\n]/).map((s) => s.trim()).filter(Boolean) };
  const dirty = JSON.stringify(current) !== JSON.stringify(base);
  const modified = new Set(m.userModifiedFields);
  const canReset = m.source === 'seed';

  const save = () => update.mutate(current, { onSuccess: () => toast(t('common.saved'), 'success'), onError: (e) => toast(errorText(e), 'error') });

  const marker = (field: ModelField) =>
    modified.has(field) ? (
      <span className="inline-flex items-center gap-1">
        <Badge tone="orange">{t('models.modified')}</Badge>
        {canReset && (
          <Button
            size="sm"
            variant="plain"
            icon={<RotateCcw className="size-3" />}
            title={t('models.resetField')}
            aria-label={t('models.resetField')}
            onClick={() => reset.mutate(field, { onSuccess: () => toast(t('models.fieldReset')), onError: (e) => toast(errorText(e), 'error') })}
          />
        )}
      </span>
    ) : null;

  return (
    <Page
      title={m.displayName}
      subtitle={`${m.id} · ${t(`models.source.${m.source}`)} · ${t('models.updated', { time: formatDateTime(m.updatedAt, locale) })}`}
      leading={back}
      actions={
        <>
          {m.source === 'user' && (
            <DeleteMorph
              label={t('common.delete')}
              prompt={t('models.deleteConfirm', { id: m.id })}
              onConfirm={async () => {
                await del.mutateAsync(m.id);
                navigate('/models');
              }}
            />
          )}
          {dirty && (
            <Button onClick={() => { setForm(base); setAliasText(base?.aliases?.join(', ') ?? ''); }}>
              {t('common.revert')}
            </Button>
          )}
          <Button variant="primary" disabled={!dirty} loading={update.isPending} onClick={save}>
            {t('common.save')}
          </Button>
        </>
      }
    >
      <div className="mx-auto grid max-w-[1100px] gap-x-6 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <div>
          <Group title={t('models.section.basic')} footer={canReset ? t('models.modifiedFooter') : undefined}>
            <FieldRow label={t('models.field.displayName')} marker={marker('displayName')}>
              <Input className="w-60" aria-label={t('models.field.displayName')} value={form.displayName} onChange={(e) => set({ displayName: e.target.value })} />
            </FieldRow>
            <FieldRow label={t('models.field.vendor')}>
              <Input className="w-60" aria-label={t('models.field.vendor')} value={form.vendor} onChange={(e) => set({ vendor: e.target.value })} />
            </FieldRow>
            <FieldRow label={t('models.field.family')} marker={marker('family')}>
              <Input className="w-60" aria-label={t('models.field.family')} value={form.family ?? ''} onChange={(e) => set({ family: e.target.value || null })} />
            </FieldRow>
            <FieldRow label={t('models.field.aliases')} marker={marker('aliases')}>
              <Input className="w-60 font-mono" aria-label={t('models.field.aliases')} value={aliasText} onChange={(e) => setAliasText(e.target.value)} placeholder="a, b, c" />
            </FieldRow>
            <FieldRow label={t('models.field.contextWindow')} marker={marker('contextWindow')}>
              <NumberInput className="w-36" label={t('models.field.contextWindow')} value={form.contextWindow} onChange={(v) => set({ contextWindow: v })} />
            </FieldRow>
            <FieldRow label={t('models.field.maxOutputTokens')} marker={marker('maxOutputTokens')}>
              <NumberInput className="w-36" label={t('models.field.maxOutputTokens')} value={form.maxOutputTokens} onChange={(v) => set({ maxOutputTokens: v })} />
            </FieldRow>
            <FieldRow label={t('common.enabled')}>
              <Switch checked={form.enabled ?? true} label={t('common.enabled')} onChange={(enabled) => set({ enabled })} />
            </FieldRow>
          </Group>

          <Group title={<span className="inline-flex items-center gap-2">{t('models.section.capabilities')} {marker('capabilities')}</span>}>
            <div className="grid grid-cols-2 gap-x-4 gap-y-3 px-4 py-4">
              {CAPABILITIES.map((c) => (
                <Checkbox
                  key={c.key}
                  label={t(c.label)}
                  checked={Boolean(form.capabilities?.[c.key])}
                  onCheckedChange={(v) => set({ capabilities: { ...form.capabilities, [c.key]: v === true } })}
                />
              ))}
            </div>
          </Group>

          <ProviderPrices model={m} />

          {m.providerOverrides.length > 0 && (
            <Group title={t('models.section.overrides')} footer={t('models.overridesFooter')}>
              {m.providerOverrides.map((o) => (
                <Row key={o.providerModelId} label={<Link className="text-[var(--accent)]" to={`/providers/${o.providerId}`}>{o.providerName}</Link>} detail={`${o.modelId} · ${priceSummary(o.pricing)}`} />
              ))}
            </Group>
          )}
        </div>

        <div>
          <section className="mb-5">
            <SectionTitle action={marker('pricing')}>{t('models.section.pricing')}</SectionTitle>
            <p className="mb-3 px-1 text-[12px] text-[var(--text-secondary)]">{t('models.pricingHint')}</p>
            <div className="card p-4">
              <PricingEditor value={form.pricing ?? null} onChange={(pricing) => set({ pricing })} allowEmpty />
            </div>
          </section>
          <section className="mb-5">
            <SectionTitle>{t('models.section.simulator')}</SectionTitle>
            <div className="card p-4">
              <BillingSimulator modelId={dirty ? undefined : m.id} pricing={dirty ? current.pricing : null} />
              {dirty && <p className="mt-3 text-[12px] text-[var(--text-muted)]">{t('models.simulatorUnsaved')}</p>}
            </div>
          </section>
        </div>
      </div>
    </Page>
  );
}

function FieldRow({ label, marker, children }: { label: string; marker?: ReactNode; children: ReactNode }) {
  return (
    <Row label={<span className="inline-flex items-center gap-2">{label}{marker}</span>}>
      {children}
    </Row>
  );
}

/** System prices of this model at specific AI providers (price key = provider type). */
function ProviderPrices({ model }: { model: ModelDetail }) {
  const { t } = useI18n();
  const del = useDeleteModelPrice(model.id);
  const [editing, setEditing] = useState<ModelPrice | 'new' | null>(null);
  return (
    <>
      <Group
        title={
          <span className="flex items-center justify-between">
            {t('models.section.providerPrices')}
            <Button size="sm" variant="plain" icon={<Plus className="size-3.5" />} onClick={() => setEditing('new')}>
              {t('common.add')}
            </Button>
          </span>
        }
        footer={t('models.providerPricesFooter')}
      >
        {model.providerPrices.length === 0 && <Row label={<span className="text-[12px] text-[var(--text-muted)]">{t('models.noProviderPrices')}</span>} />}
        {model.providerPrices.map((p) => (
          <Row
            key={p.priceKey}
            label={
              <span className="flex items-center gap-2">
                <span className="font-mono text-[12.5px]">{p.priceKey}</span>
                {p.userModified && <Badge tone="orange">{t('models.modified')}</Badge>}
              </span>
            }
            detail={[priceSummary(p.pricing), p.upstreamModelId, p.pricing.time_windows?.length ? t('models.hasTimeWindows') : null, p.pricing.context_tiers?.length ? t('models.hasTiers') : null].filter(Boolean).join(' · ')}
          >
            <Button size="sm" variant="plain" icon={<Pencil className="size-3.5" />} aria-label={t('common.edit')} onClick={() => setEditing(p)} />
            <DeleteMorph label={t('common.delete')} prompt={t('models.deletePrice', { key: p.priceKey })} onConfirm={() => del.mutateAsync(p.priceKey)} />
          </Row>
        ))}
      </Group>
      <PriceSheet model={model} editing={editing} onClose={() => setEditing(null)} />
    </>
  );
}

function PriceSheet({ model, editing, onClose }: { model: ModelDetail; editing: ModelPrice | 'new' | null; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const priceKeys = usePriceKeys();
  const upsert = useUpsertModelPrice(model.id);
  const isNew = editing === 'new';
  const [priceKey, setPriceKey] = useState('');
  const [upstream, setUpstream] = useState('');
  const [pricing, setPricing] = useState<PricingSchedule | null>(null);

  useEffect(() => {
    if (!editing) return;
    if (editing === 'new') {
      setPriceKey('');
      setUpstream('');
      setPricing(model.pricing ? structuredClone(model.pricing) : { currency: 'USD', unit: 'per_1m_tokens', base: { input: null, output: null } });
    } else {
      setPriceKey(editing.priceKey);
      setUpstream(editing.upstreamModelId ?? '');
      setPricing(structuredClone(editing.pricing));
    }
  }, [editing, model.pricing]);

  const taken = new Set(model.providerPrices.map((p) => p.priceKey));
  return (
    <Sheet
      open={editing !== null}
      onOpenChange={(o) => !o && onClose()}
      width={680}
      title={isNew ? t('models.addPrice') : t('models.editPrice', { key: priceKey })}
      description={t('models.priceSheetDetail')}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button
            variant="primary"
            disabled={!priceKey || !pricing}
            loading={upsert.isPending}
            onClick={() =>
              upsert.mutate(
                { priceKey, upstreamModelId: upstream || null, pricing: pricing! },
                { onSuccess: () => { toast(t('common.saved'), 'success'); onClose(); }, onError: (e) => toast(errorText(e), 'error') },
              )
            }
          >
            {t('common.save')}
          </Button>
        </>
      }
    >
      <div className="grid gap-4">
        <div className="grid grid-cols-2 gap-4">
          <Field label={t('providers.priceKey')}>
            {isNew ? (
              <Select
                value={priceKey}
                onChange={setPriceKey}
                placeholder={t('models.pickPriceKey')}
                options={(priceKeys.data ?? []).map((k) => ({ value: k.priceKey, label: k.label, detail: k.priceKey, disabled: taken.has(k.priceKey) }))}
              />
            ) : (
              <Input value={priceKey} disabled className="font-mono" />
            )}
          </Field>
          <Field label={t('models.upstreamId')} hint={t('models.upstreamIdHint')}>
            <Input className="font-mono" value={upstream} onChange={(e) => setUpstream(e.target.value)} placeholder={model.id} />
          </Field>
        </div>
        {isNew && <Input className="font-mono" aria-label={t('models.customPriceKey')} placeholder={t('models.customPriceKey')} value={priceKey} onChange={(e) => setPriceKey(e.target.value.trim())} />}
        <PricingEditor value={pricing} onChange={setPricing} />
      </div>
    </Sheet>
  );
}

/** Destructive action that confirms in place (Arc confirm morph) instead of a separate dialog. */
function DeleteMorph({ label, prompt, onConfirm }: { label: string; prompt: string; onConfirm: () => Promise<unknown> }) {
  const { t } = useI18n();
  return (
    <ConfirmMorph
      label={label}
      icon={<Trash2 className="size-4" />}
      prompt={prompt}
      confirmLabel={t('common.delete')}
      cancelLabel={t('common.cancel')}
      pendingLabel={t('common.deleting')}
      doneLabel={t('common.deleted')}
      errorLabel={t('common.error')}
      retryLabel={t('common.retry')}
      onConfirm={onConfirm}
    />
  );
}
