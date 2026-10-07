import { Calculator } from 'lucide-react';
import { useState } from 'react';

import { useProviders, useSimulate } from '../api/hooks';
import type { BillingResult, PricingSchedule } from '../api/types';
import { Alert } from './arc/alert/alert';
import { SortableDataTable, type DataColumn } from './arc/sortable-data-table/sortable-data-table';
import { useI18n } from '../i18n';
import { formatNanos, formatTokens, formatUnitPrice } from '../lib/format';
import { TOKEN_TYPES } from './PricingEditor';
import { Badge, Button, Input, NumberInput } from './ui/controls';
import { errorText, Select } from './ui/overlays';

export function PricingSourceBadge({ source, priceKey }: { source: BillingResult['pricingSource'] | string | null | undefined; priceKey?: string | null }) {
  const { t } = useI18n();
  switch (source) {
    case 'provider_override':
      return <Badge tone="orange">{t('pricingSource.override')}</Badge>;
    case 'provider_price':
      return <Badge tone="accent">{t('pricingSource.provider', { key: priceKey ?? '' })}</Badge>;
    case 'system_default':
      return <Badge>{t('pricingSource.default')}</Badge>;
    default:
      return <Badge tone="red">{t('pricingSource.none')}</Badge>;
  }
}

type ItemRow = { key: string; type: string; tokens: number; unitPrice: number; tier: string; cost: number; item: BillingResult['items'][number] };

/** Item table + description of a computed bill (simulator and request detail share this). */
export function BillingResultView({ result }: { result: BillingResult }) {
  const { t } = useI18n();
  const columns: DataColumn<ItemRow>[] = [
    {
      key: 'type',
      label: t('billing.type'),
      render: (_, row) => (
        <span className="font-mono text-[12px]">
          {row.item.tokenType}
          {row.item.pricedAs && row.item.pricedAs !== row.item.tokenType && <span className="text-[var(--text-muted)]"> → {row.item.pricedAs}</span>}
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
          {row.item.multiplier !== 1 && <span className="text-[var(--text-muted)]"> (×{row.item.multiplier})</span>}
        </>
      ),
    },
    { key: 'tier', label: t('billing.tier') },
    { key: 'cost', label: t('billing.cost'), numeric: true, render: (_, row) => formatNanos(row.item.costNanos) },
  ];
  const rows: ItemRow[] = result.items.map((it, i) => ({
    key: String(i),
    type: it.tokenType,
    tokens: it.tokens,
    unitPrice: Number(it.unitPrice) || 0,
    tier: it.tier,
    cost: it.costNanos,
    item: it,
  }));
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="num text-[20px] font-medium">{formatNanos(result.totalNanos)}</span>
        <PricingSourceBadge source={result.pricingSource} priceKey={result.priceKey} />
        {!result.priced && <Badge tone="red">{t('billing.unpriced')}</Badge>}
      </div>
      {rows.length > 0 && <SortableDataTable<ItemRow> caption={t('billing.type')} rows={rows} columns={columns} rowKey="key" />}
      {result.description && <p className="text-[12px] leading-relaxed whitespace-pre-line text-[var(--text-secondary)] selectable">{result.description}</p>}
    </div>
  );
}

const DEFAULT_TOKENS: Record<string, number | null> = { input: 1_000_000, output: 100_000 };

/** "What would this cost?" — prices usage against a schedule, a system model, or a model at a provider. */
export function BillingSimulator({ modelId, pricing }: { modelId?: string; pricing?: PricingSchedule | null }) {
  const { t, locale } = useI18n();
  const providers = useProviders();
  const simulate = useSimulate();
  const [tokens, setTokens] = useState<Record<string, number | null>>(DEFAULT_TOKENS);
  const [serviceTier, setServiceTier] = useState('');
  const [time, setTime] = useState('');
  const [providerId, setProviderId] = useState<string>('');

  const run = () => {
    const usageTokens = Object.fromEntries(Object.entries(tokens).filter(([, v]) => v && v > 0)) as Record<string, number>;
    simulate.mutate({
      ...(providerId ? { modelId, providerId } : modelId && !pricing ? { modelId } : { pricing: pricing ?? null }),
      usage: { tokens: usageTokens, serviceTier: serviceTier || null },
      requestTimeUtc: time ? new Date(time).toISOString() : undefined,
      locale,
    });
  };

  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-[repeat(auto-fill,minmax(200px,1fr))] gap-x-5 gap-y-2">
        {TOKEN_TYPES.map((type) => (
          <div key={type} className="flex items-center justify-between gap-2">
            <span className="truncate font-mono text-[11px] text-[var(--text-secondary)]">{type}</span>
            <NumberInput className="w-28" label={type} step={1} min={0} value={tokens[type] ?? null} onChange={(v) => setTokens({ ...tokens, [type]: v })} />
          </div>
        ))}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        {modelId && (
          <Select
            className="w-48"
            value={providerId}
            onChange={setProviderId}
            options={[
              { value: '', label: t('simulator.official') },
              ...(providers.data ?? []).map((p) => ({ value: p.id, label: p.name, detail: p.priceKey ?? p.templateId ?? undefined })),
            ]}
          />
        )}
        <Input className="w-28" placeholder={t('simulator.serviceTier')} value={serviceTier} onChange={(e) => setServiceTier(e.target.value)} />
        <Input className="w-52" aria-label={t('simulator.time')} type="datetime-local" value={time} onChange={(e) => setTime(e.target.value)} />
        <Button variant="primary" icon={<Calculator className="size-3.5" />} loading={simulate.isPending} onClick={run} className="ml-auto">
          {t('simulator.run')}
        </Button>
      </div>
      {simulate.isError && <Alert tone="danger" title={t('common.error')}>{errorText(simulate.error)}</Alert>}
      {simulate.data && <BillingResultView result={simulate.data} />}
    </div>
  );
}
