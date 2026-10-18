import type { ProviderAccountUsage } from '../api/types';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';
import { formatTokens, formatUsd, usdFromNanos } from '../lib/format';

/** The same usage breakdown on account cards, account details and a provider's API-key tab. */
export function AccountUsageStats({ usage, compact = false }: { usage?: ProviderAccountUsage; compact?: boolean }) {
  const { t } = useI18n();
  const rows = [
    { label: t('providers.usage.requests'), values: [usage?.today.requests, usage?.total.requests], format: (n: number, short = compact) => formatTokens(n, short) },
    { label: t('providers.usage.tokens'), values: [usage?.today.tokens, usage?.total.tokens], format: (n: number, short = compact) => formatTokens(n, short) },
    { label: t('providers.usage.cost'), values: [usage?.today.costNanoUsd, usage?.total.costNanoUsd], format: (n: number, short = compact) => formatUsd(usdFromNanos(n), { compact: short }) },
  ];

  return (
    <table className={cn('w-full table-fixed text-right tabular-nums', compact ? 'text-[11px]' : 'text-[12px]')} aria-label={t('providers.usage.title')}>
      <thead className="text-[var(--text-muted)]">
        <tr>
          <th scope="col" className="w-[34%] pb-1 text-left font-normal"><span className="sr-only">{t('providers.usage.title')}</span></th>
          <th scope="col" className="pb-1 font-normal">{t('providers.usage.today')}</th>
          <th scope="col" className="pb-1 font-normal">{t('providers.usage.total')}</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row.label}>
            <th scope="row" className="py-0.5 text-left font-normal text-[var(--text-secondary)]">{row.label}</th>
            {row.values.map((value, i) => (
              <td key={i} className="truncate py-0.5 pl-1 font-mono" title={value == null ? undefined : row.format(value, false)}>
                {value == null ? '—' : row.format(value)}
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function ProviderUsageHint({ timeZone }: { timeZone?: string }) {
  const { t } = useI18n();
  return (
    <p className="text-[11px] leading-relaxed text-[var(--text-muted)]">
      {t('providers.usage.hint')}
      {timeZone && <> {t('providers.usage.dayHint', { zone: timeZone })}</>}
    </p>
  );
}
