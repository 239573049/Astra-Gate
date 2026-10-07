import { Plus, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';

import type { ContextTier, PriceSet, PricingSchedule, ServiceTierRule, TimeWindowRule } from '../api/types';
import { useI18n, useT, type MessageKey } from '../i18n';
import { pretty } from '../lib/format';
import { ChipGroup } from './arc/chip-group/chip-group';
import { PricingRuleDialog, type PricingRuleKind } from './PricingRuleDialog';
import { Button, Field, Input, NumberInput, Segmented, TextArea } from './ui/controls';
import { Select, Sheet } from './ui/overlays';

export const TOKEN_TYPES = [
  'input',
  'cache_read',
  'cache_write_5m',
  'cache_write_1h',
  'output',
  'reasoning',
  'input_audio',
  'output_audio',
  'input_image',
  'output_image',
] as const;

export function emptySchedule(): PricingSchedule {
  return { currency: 'USD', unit: 'per_1m_tokens', base: { input: null, output: null } };
}

/** Parses + minimally validates a pricing JSON document. Returns [value, error]. */
export function parsePricing(text: string): [PricingSchedule | null, string | null] {
  if (!text.trim()) return [null, null];
  try {
    const v = JSON.parse(text) as PricingSchedule;
    if (!v || typeof v !== 'object' || Array.isArray(v)) return [null, 'not an object'];
    if (!v.base || typeof v.base !== 'object') return [null, '"base" is required'];
    return [{ ...v, currency: v.currency ?? 'USD', unit: v.unit ?? 'per_1m_tokens' }, null];
  } catch (e) {
    return [null, (e as Error).message];
  }
}

/** Removes empty rule lists so the stored JSON stays minimal. */
export function compactSchedule(p: PricingSchedule): PricingSchedule {
  const out: PricingSchedule = { ...p, base: { ...p.base } };
  for (const k of Object.keys(out.base) as (keyof PriceSet)[]) {
    if (out.base[k] === undefined) delete out.base[k];
  }
  if (!out.context_tiers?.length) delete out.context_tiers;
  if (!out.time_windows?.length) delete out.time_windows;
  if (!out.service_tiers || Object.keys(out.service_tiers).length === 0) delete out.service_tiers;
  if (out.per_request == null) delete out.per_request;
  if (!out.notes) delete out.notes;
  return out;
}

const DAYS: { d: number; zh: string; en: string }[] = [
  { d: 1, zh: '一', en: 'M' },
  { d: 2, zh: '二', en: 'T' },
  { d: 3, zh: '三', en: 'W' },
  { d: 4, zh: '四', en: 'T' },
  { d: 5, zh: '五', en: 'F' },
  { d: 6, zh: '六', en: 'S' },
  { d: 7, zh: '日', en: 'S' },
];

/** Structured form + raw JSON editor for a PricingSchedule (storage format, snake_case). */
export function PricingEditor({
  value,
  onChange,
  allowEmpty,
  dialogLevel = 1,
}: {
  value: PricingSchedule | null;
  onChange: (v: PricingSchedule | null) => void;
  allowEmpty?: boolean;
  dialogLevel?: number;
}) {
  const t = useT();
  const [mode, setMode] = useState<'form' | 'json'>('form');
  const [text, setText] = useState(() => pretty(value));
  const [jsonError, setJsonError] = useState<string | null>(null);
  const [addingRule, setAddingRule] = useState<PricingRuleKind | null>(null);
  const [creatingPrice, setCreatingPrice] = useState(false);
  const [newPrice, setNewPrice] = useState<PricingSchedule>(emptySchedule);

  useEffect(() => {
    if (mode === 'form') setText(pretty(value));
  }, [value, mode]);

  if (value === null && mode === 'form') {
    return (
      <>
        <div className="flex items-center justify-between gap-3 rounded-[var(--radius-panel)] border border-[var(--border)] px-4 py-3 text-[13px] text-[var(--text-secondary)]">
          {t('pricing.none')}
          <Button size="sm" variant="primary" icon={<Plus className="size-3.5" />} onClick={() => { setNewPrice(emptySchedule()); setCreatingPrice(true); }}>
            {t('pricing.add')}
          </Button>
        </div>
        <Sheet open={creatingPrice} onOpenChange={setCreatingPrice} width={660} title={t('pricing.add')} level={dialogLevel}
          footer={<><Button onClick={() => setCreatingPrice(false)}>{t('common.cancel')}</Button><Button variant="primary" onClick={() => { onChange(newPrice); setCreatingPrice(false); }}>{t('common.add')}</Button></>}>
          <PricingEditor value={newPrice} onChange={(p) => p && setNewPrice(p)} dialogLevel={dialogLevel + 1} />
        </Sheet>
      </>
    );
  }

  const p = value ?? emptySchedule();
  const set = (patch: Partial<PricingSchedule>) => onChange(compactSchedule({ ...p, ...patch }));
  const setBase = (type: string, v: number | null) => set({ base: { ...p.base, [type]: v } as PriceSet });

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center gap-2">
        <Segmented
          ariaLabel={t('pricing.editMode')}
          value={mode}
          onChange={(m) => {
            if (m === 'json') setText(pretty(value));
            setMode(m);
          }}
          items={[
            { value: 'form', label: t('pricing.mode.form') },
            { value: 'json', label: 'JSON' },
          ]}
        />
        <span className="text-[12px] text-[var(--text-muted)]">{t('pricing.unit')}</span>
        {allowEmpty && (
          <Button size="sm" variant="destructive" className="ml-auto" onClick={() => onChange(null)}>
            {t('pricing.remove')}
          </Button>
        )}
      </div>

      {mode === 'json' ? (
        <Field label="JSON" error={jsonError ?? undefined}>
          <TextArea
            rows={16}
            spellCheck={false}
            className="font-mono selectable"
            value={text}
            invalid={Boolean(jsonError)}
            onChange={(e) => {
              setText(e.target.value);
              const [v, err] = parsePricing(e.target.value);
              setJsonError(err);
              if (!err && v) onChange(v);
            }}
          />
        </Field>
      ) : (
        <>
          <div>
            <Label>{t('pricing.base')}</Label>
            <div className="grid grid-cols-[repeat(auto-fill,minmax(200px,1fr))] gap-x-5 gap-y-2">
              {TOKEN_TYPES.map((type) => (
                <div key={type} className="flex items-center justify-between gap-2">
                  <span className="truncate font-mono text-[11px] text-[var(--text-secondary)]">{type}</span>
                  <NumberInput className="w-28" label={type} value={p.base[type] ?? null} onChange={(v) => setBase(type, v)} />
                </div>
              ))}
              <div className="flex items-center justify-between gap-2">
                <span className="truncate text-[12px] text-[var(--text-secondary)]">{t('pricing.perRequest')}</span>
                <NumberInput className="w-28" label={t('pricing.perRequest')} value={p.per_request ?? null} onChange={(v) => set({ per_request: v })} />
              </div>
            </div>
          </div>

          <ContextTiersEditor tiers={p.context_tiers ?? []} onChange={(context_tiers) => set({ context_tiers })} onAdd={() => setAddingRule('context')} />
          <TimeWindowsEditor windows={p.time_windows ?? []} onChange={(time_windows) => set({ time_windows })} onAdd={() => setAddingRule('time')} />
          <ServiceTiersEditor tiers={p.service_tiers ?? {}} onChange={(service_tiers) => set({ service_tiers })} onAdd={() => setAddingRule('service')} />

          <TextArea label={t('pricing.notes')} rows={2} className="w-full" value={p.notes ?? ''} onChange={(e) => set({ notes: e.target.value || null })} />
        </>
      )}
      <PricingRuleDialog kind={addingRule} onClose={() => setAddingRule(null)} serviceNames={Object.keys(p.service_tiers ?? {})} level={dialogLevel}
        onAdd={(rule) => {
          if (rule.kind === 'context') set({ context_tiers: [...(p.context_tiers ?? []), rule.value] });
          else if (rule.kind === 'time') set({ time_windows: [...(p.time_windows ?? []), rule.value] });
          else set({ service_tiers: { ...p.service_tiers, [rule.name]: rule.value } });
        }} />
    </div>
  );
}

function Label({ children, action }: { children: React.ReactNode; action?: React.ReactNode }) {
  return (
    <div className="mb-2 flex items-center justify-between">
      <span className="text-[13px] font-medium">{children}</span>
      {action}
    </div>
  );
}

function AddButton({ onClick, label }: { onClick: () => void; label: MessageKey }) {
  const t = useT();
  return (
    <Button size="sm" variant="plain" icon={<Plus className="size-3.5" />} onClick={onClick}>
      {t(label)}
    </Button>
  );
}

function RemoveButton({ onClick }: { onClick: () => void }) {
  const t = useT();
  return <Button size="sm" variant="plain" aria-label={t('common.delete')} icon={<Trash2 className="size-3.5" />} onClick={onClick} />;
}

function ContextTiersEditor({ tiers, onChange, onAdd }: { tiers: ContextTier[]; onChange: (v: ContextTier[]) => void; onAdd: () => void }) {
  const t = useT();
  const update = (i: number, patch: Partial<ContextTier>) => onChange(tiers.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const setPrice = (i: number, type: string, v: number | null) =>
    update(i, { prices: { ...tiers[i]!.prices, [type]: v } as PriceSet });
  return (
    <div>
      <Label
        action={
          <AddButton label="pricing.addTier" onClick={onAdd} />
        }
      >
        {t('pricing.contextTiers')}
      </Label>
      {tiers.length === 0 && <Hint>{t('pricing.contextTiers.hint')}</Hint>}
      <div className="flex flex-col gap-2">
        {tiers.map((tier, i) => (
          <div key={i} className="rounded-[var(--radius-panel)] border border-[var(--border)] p-3">
            <div className="flex flex-wrap items-center gap-2">
              <Input className="w-28" value={tier.name} onChange={(e) => update(i, { name: e.target.value })} placeholder={t('pricing.name')} />
              <span className="text-[12px] text-[var(--text-secondary)]">{t('pricing.threshold')}</span>
              <NumberInput className="w-28" label={t('pricing.threshold')} value={tier.threshold_input_tokens} onChange={(v) => update(i, { threshold_input_tokens: v ?? 0 })} />
              <Select
                className="w-36"
                value={tier.mode}
                onChange={(mode) => update(i, { mode })}
                options={[
                  { value: 'whole', label: t('pricing.mode.whole') },
                  { value: 'progressive', label: t('pricing.mode.progressive') },
                ]}
              />
              <Select
                className="w-40"
                value={tier.output_policy}
                onChange={(output_policy) => update(i, { output_policy })}
                options={[
                  { value: 'highest', label: t('pricing.output.highest') },
                  { value: 'base', label: t('pricing.output.base') },
                ]}
              />
              <span className="ml-auto">
                <RemoveButton onClick={() => onChange(tiers.filter((_, j) => j !== i))} />
              </span>
            </div>
            <PriceRow prices={tier.prices} onChange={(type, v) => setPrice(i, type, v)} />
          </div>
        ))}
      </div>
    </div>
  );
}

function PriceRow({ prices, onChange }: { prices: PriceSet | null | undefined; onChange: (type: string, v: number | null) => void }) {
  return (
    <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1.5">
      {(['input', 'cache_read', 'output'] as const).map((type) => (
        <div key={type} className="flex items-center gap-2">
          <span className="font-mono text-[11px] text-[var(--text-secondary)]">{type}</span>
          <NumberInput className="w-24" label={type} value={prices?.[type] ?? null} onChange={(v) => onChange(type, v)} />
        </div>
      ))}
    </div>
  );
}

function TimeWindowsEditor({ windows, onChange, onAdd }: { windows: TimeWindowRule[]; onChange: (v: TimeWindowRule[]) => void; onAdd: () => void }) {
  const { t, locale } = useI18n();
  const update = (i: number, patch: Partial<TimeWindowRule>) => onChange(windows.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  return (
    <div>
      <Label
        action={
          <AddButton label="pricing.addWindow" onClick={onAdd} />
        }
      >
        {t('pricing.timeWindows')}
      </Label>
      {windows.length === 0 && <Hint>{t('pricing.timeWindows.hint')}</Hint>}
      <div className="flex flex-col gap-2">
        {windows.map((w, i) => (
          <div key={i} className="rounded-[var(--radius-panel)] border border-[var(--border)] p-3">
            <div className="flex flex-wrap items-center gap-2">
              <Input className="w-24" value={w.name} onChange={(e) => update(i, { name: e.target.value })} placeholder={t('pricing.name')} />
              <Input className="w-40 font-mono" aria-label={t('pricing.timezone')} value={w.timezone} onChange={(e) => update(i, { timezone: e.target.value })} placeholder="Asia/Shanghai" />
              <Input className="w-[110px] font-mono" aria-label={t('pricing.start')} type="time" value={w.start} onChange={(e) => update(i, { start: e.target.value })} />
              <span className="text-[var(--text-muted)]">–</span>
              <Input className="w-[110px] font-mono" aria-label={t('pricing.end')} type="time" value={w.end} onChange={(e) => update(i, { end: e.target.value })} />
              <span className="text-[12px] text-[var(--text-secondary)]">×</span>
              <NumberInput className="w-20" label={t('pricing.multiplier')} value={w.multiplier ?? null} onChange={(v) => update(i, { multiplier: v })} />
              <span className="ml-auto">
                <RemoveButton onClick={() => onChange(windows.filter((_, j) => j !== i))} />
              </span>
            </div>
            <div className="mt-2 flex flex-wrap items-center gap-1">
              <ChipGroup
                label={t('pricing.days')}
                options={DAYS.map(({ d, zh, en }) => ({ value: String(d), label: locale === 'zh' ? zh : en }))}
                value={(w.days ?? [1, 2, 3, 4, 5, 6, 7]).map(String)}
                onValueChange={(next) => {
                  const days = next.map(Number).sort();
                  update(i, { days: days.length === 7 ? null : days });
                }}
              />
              <Input
                className="ml-2 min-w-40 flex-1 font-mono"
                placeholder={t('pricing.excludeDates')}
                value={(w.exclude_dates ?? []).join(', ')}
                onChange={(e) => {
                  const list = e.target.value.split(/[,\s]+/).filter(Boolean);
                  update(i, { exclude_dates: list.length ? list : null });
                }}
              />
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function ServiceTiersEditor({ tiers, onChange, onAdd }: { tiers: Record<string, ServiceTierRule>; onChange: (v: Record<string, ServiceTierRule>) => void; onAdd: () => void }) {
  const t = useT();
  const entries = Object.entries(tiers);
  const rebuild = (list: [string, ServiceTierRule][]) => onChange(Object.fromEntries(list));
  return (
    <div>
      <Label
        action={
          <AddButton label="pricing.addServiceTier" onClick={onAdd} />
        }
      >
        {t('pricing.serviceTiers')}
      </Label>
      {entries.length === 0 && <Hint>{t('pricing.serviceTiers.hint')}</Hint>}
      <div className="flex flex-col gap-1.5">
        {entries.map(([name, rule], i) => (
          <div key={i} className="flex items-center gap-2">
            <Input
              className="w-36 font-mono"
              aria-label={t('pricing.name')}
              value={name}
              onChange={(e) => rebuild(entries.map((x, j) => (j === i ? [e.target.value, x[1]] : x)))}
            />
            <span className="text-[12px] text-[var(--text-secondary)]">×</span>
            <NumberInput
              className="w-24"
              label={t('pricing.multiplier')}
              value={rule.multiplier ?? null}
              onChange={(v) => rebuild(entries.map((x, j) => (j === i ? [x[0], { ...x[1], multiplier: v }] : x)))}
            />
            {rule.prices && <span className="text-[12px] text-[var(--text-muted)]">{t('pricing.hasOwnPrices')}</span>}
            <RemoveButton onClick={() => rebuild(entries.filter((_, j) => j !== i))} />
          </div>
        ))}
      </div>
    </div>
  );
}

function Hint({ children }: { children: React.ReactNode }) {
  return <div className="mb-2 text-[12px] text-[var(--text-muted)]">{children}</div>;
}

