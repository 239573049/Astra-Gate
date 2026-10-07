import { useEffect, useState } from 'react';

import type { ContextTier, PriceSet, ServiceTierRule, TimeWindowRule } from '../api/types';
import { useI18n } from '../i18n';
import { ChipGroup } from './arc/chip-group/chip-group';
import { Button, Field, Input, NumberInput } from './ui/controls';
import { Select, Sheet } from './ui/overlays';

export type PricingRuleKind = 'context' | 'time' | 'service';
export type NewPricingRule =
  | { kind: 'context'; value: ContextTier }
  | { kind: 'time'; value: TimeWindowRule }
  | { kind: 'service'; name: string; value: ServiceTierRule };

/** Adding a rule is always a dialog: cancel never changes the editor's parent value. */
export function PricingRuleDialog({ kind, onClose, onAdd, serviceNames = [], level = 1 }: {
  kind: PricingRuleKind | null;
  onClose: () => void;
  onAdd: (rule: NewPricingRule) => void;
  serviceNames?: string[];
  level?: number;
}) {
  const { t, locale } = useI18n();
  const [name, setName] = useState('');
  const [threshold, setThreshold] = useState<number | null>(200_000);
  const [mode, setMode] = useState<'whole' | 'progressive'>('whole');
  const [outputPolicy, setOutputPolicy] = useState<'highest' | 'base'>('highest');
  const [prices, setPrices] = useState<PriceSet>({});
  const [timezone, setTimezone] = useState('Asia/Shanghai');
  const [start, setStart] = useState('00:30');
  const [end, setEnd] = useState('08:30');
  const [multiplier, setMultiplier] = useState<number | null>(0.5);
  const [days, setDays] = useState<number[]>([1, 2, 3, 4, 5, 6, 7]);
  const [excluded, setExcluded] = useState('');

  useEffect(() => {
    if (!kind) return;
    setName(kind === 'context' ? '>200K' : kind === 'time' ? 'off-peak' : 'flex');
    setThreshold(200_000); setMode('whole'); setOutputPolicy('highest'); setPrices({});
    setTimezone('Asia/Shanghai'); setStart('00:30'); setEnd('08:30'); setMultiplier(0.5);
    setDays([1, 2, 3, 4, 5, 6, 7]); setExcluded('');
  }, [kind]);

  let timezoneValid = true;
  try { new Intl.DateTimeFormat('en', { timeZone: timezone }); }
  catch { timezoneValid = false; }
  const dates = excluded.split(/[,\s]+/).filter(Boolean);
  const pricesValid = Object.values(prices).every((v) => v == null || typeof v === 'number' && Number.isFinite(v) && v >= 0);
  const timeValid = /^([01]\d|2[0-3]):[0-5]\d$/.test(start) && /^([01]\d|2[0-3]):[0-5]\d$/.test(end);
  const nameTaken = kind === 'service' && serviceNames.some((n) => n.toLowerCase() === name.trim().toLowerCase());
  const valid = name.trim().length > 0 && pricesValid && (kind === 'context'
    ? threshold != null && Number.isInteger(threshold) && threshold > 0
    : kind === 'time'
      ? timezoneValid && timeValid && days.length > 0 && multiplier != null && Number.isFinite(multiplier) && multiplier >= 0 && dates.every(validDate)
      : !nameTaken && multiplier != null && Number.isFinite(multiplier) && multiplier >= 0);

  const submit = () => {
    if (!valid || !kind) return;
    if (kind === 'context') onAdd({ kind, value: { name: name.trim(), threshold_input_tokens: threshold!, mode, output_policy: outputPolicy, prices } });
    else if (kind === 'time') onAdd({ kind, value: { name: name.trim(), timezone, start, end, multiplier,
      days: days.length === 7 ? null : [...days].sort((a, b) => a - b), exclude_dates: dates.length ? dates : null } });
    else onAdd({ kind, name: name.trim(), value: { multiplier } });
    onClose();
  };
  const title = kind === 'context' ? t('pricing.addTier') : kind === 'time' ? t('pricing.addWindow') : t('pricing.addServiceTier');
  return (
    <Sheet open={kind !== null} onOpenChange={(open) => !open && onClose()} width={540} title={title} level={level}
      description={kind === 'context' ? t('pricing.contextTiers.hint') : kind === 'time' ? t('pricing.timeWindows.hint') : t('pricing.serviceTiers.hint')}
      footer={<><Button onClick={onClose}>{t('common.cancel')}</Button><Button variant="primary" disabled={!valid} onClick={submit}>{t('common.add')}</Button></>}>
      <div className="grid gap-4">
        <Field label={t('pricing.name')} error={nameTaken ? t('pricing.ruleNameTaken') : undefined}>
          <Input autoFocus value={name} onChange={(e) => setName(e.target.value)} invalid={nameTaken} />
        </Field>
        {kind === 'context' && <>
          <Field label={t('pricing.threshold')}><NumberInput value={threshold} onChange={setThreshold} min={1} step={1} /></Field>
          <div className="grid grid-cols-2 gap-4">
            <Field label={t('pricing.tierMode')}>
              <Select value={mode} onChange={setMode} options={[{ value: 'whole', label: t('pricing.mode.whole') }, { value: 'progressive', label: t('pricing.mode.progressive') }]} />
            </Field>
            <Field label={t('pricing.outputPolicy')}>
              <Select value={outputPolicy} onChange={setOutputPolicy} options={[{ value: 'highest', label: t('pricing.output.highest') }, { value: 'base', label: t('pricing.output.base') }]} />
            </Field>
          </div>
          <div className="grid grid-cols-2 gap-4">
            {(['input', 'cache_read', 'cache_write_5m', 'cache_write_1h', 'output', 'reasoning'] as const).map((token) => (
              <Field key={token} label={token}><NumberInput value={prices[token]} onChange={(v) => setPrices({ ...prices, [token]: v } as PriceSet)} min={0} /></Field>
            ))}
          </div>
        </>}
        {kind === 'time' && <>
          <Field label={t('pricing.timezone')} error={timezoneValid ? undefined : t('pricing.invalidTimezone')}>
            <Input className="font-mono" value={timezone} onChange={(e) => setTimezone(e.target.value)} invalid={!timezoneValid} />
          </Field>
          <div className="grid grid-cols-3 gap-4">
            <Field label={t('pricing.start')}><Input type="time" value={start} onChange={(e) => setStart(e.target.value)} /></Field>
            <Field label={t('pricing.end')}><Input type="time" value={end} onChange={(e) => setEnd(e.target.value)} /></Field>
            <Field label={t('pricing.multiplier')}><NumberInput value={multiplier} onChange={setMultiplier} min={0} /></Field>
          </div>
          <ChipGroup
            label={t('pricing.days')}
            options={(locale === 'zh' ? ['一', '二', '三', '四', '五', '六', '日'] : ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']).map((label, i) => ({ value: String(i + 1), label }))}
            value={days.map(String)}
            onValueChange={(next) => setDays(next.map(Number))}
          />
          <Field label={t('pricing.excludeDates')}>
            <Input className="font-mono" value={excluded} onChange={(e) => setExcluded(e.target.value)} placeholder="2026-10-01, 2026-10-02" />
          </Field>
        </>}
        {kind === 'service' && <Field label={t('pricing.multiplier')}><NumberInput value={multiplier} onChange={setMultiplier} min={0} /></Field>}
      </div>
    </Sheet>
  );
}

function validDate(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const d = new Date(`${value}T00:00:00Z`);
  return Number.isFinite(d.getTime()) && d.toISOString().slice(0, 10) === value;
}
