import type { CSSProperties } from 'react';

import { useT, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';
import { formatPercent, formatTokens } from '../lib/format';
import { splitUsage, type UsageCounts } from '../lib/usage';
import { DetailSection } from './ui/controls';

const TOKEN_LABELS: Record<string, MessageKey> = {
  input: 'usage.input',
  cache_read: 'usage.cacheRead',
  cache_write_5m: 'usage.cacheWrite5m',
  cache_write_1h: 'usage.cacheWrite1h',
  output: 'usage.output',
  reasoning: 'usage.reasoning',
  input_audio: 'usage.inputAudio',
  output_audio: 'usage.outputAudio',
  input_image: 'usage.inputImage',
  output_image: 'usage.outputImage',
};

/** Human labels keep the token type visible underneath for exact billing/debugging. */
export function TokenTypeLabel({ type }: { type: string }) {
  const t = useT();
  const label = TOKEN_LABELS[type];
  return (
    <span className="inline-flex flex-col leading-snug">
      <span className={cn(type === 'cache_read' && 'text-[var(--success)]', type.startsWith('cache_write_') && 'text-[var(--warning)]')}>
        {label ? t(label) : type}
      </span>
      {label && <span className="font-mono text-[10.5px] text-[var(--text-muted)]">{type}</span>}
    </span>
  );
}

/** Compact two-line cache cell for the request list; missing usage is NOT a 0% cache hit. */
export function CacheUsageCell({ usage, selected }: { usage: UsageCounts; selected?: boolean }) {
  const t = useT();
  const u = splitUsage(usage);
  if (!u.reported || (u.read === 0 && u.write === 0)) {
    return <span className="text-right opacity-50" title={t(u.reported ? 'usage.noCache' : 'usage.missing')}>—</span>;
  }
  return (
    <span className="num flex flex-col items-end gap-px whitespace-nowrap text-[11px]">
      <span className={cn(!selected && 'text-[var(--success)]')} title={t('usage.hitTooltip', { read: formatTokens(u.read), total: formatTokens(u.input) })}>
        {t('usage.hitShort')} {formatTokens(u.read, true)}
        {u.hitRatio != null && <span className="ml-1 opacity-75">{formatPercent(u.hitRatio)}</span>}
      </span>
      <span className={cn(!selected && 'text-[var(--text-secondary)]')} title={t('usage.writeTooltip', { tokens: formatTokens(u.write) })}>
        {t('usage.writeShort')} {formatTokens(u.write, true)}
      </span>
    </span>
  );
}

export function TokenTotalsCell({ usage, selected }: { usage: UsageCounts; selected?: boolean }) {
  const t = useT();
  const u = splitUsage(usage);
  if (!u.reported) return <span className="text-right opacity-50" title={t('usage.missing')}>—</span>;
  return (
    <span className="num flex flex-col items-end gap-px whitespace-nowrap" title={t('usage.totalsHint')}>
      <span>{formatTokens(u.input, true)} / {formatTokens(u.output, true)}</span>
      {u.reasoning > 0 && <span className={cn('text-[11px]', selected ? 'opacity-75' : 'text-[var(--text-secondary)]')}>{t('usage.reasoningShort')} {formatTokens(u.reasoning, true)}</span>}
    </span>
  );
}

/** Token composition inside the request dialog. The bars divide existing totals, never add to them. */
export function TokenUsageSummary({ usage }: { usage: UsageCounts }) {
  const t = useT();
  const u = splitUsage(usage);
  return (
    <DetailSection title={t('usage.title')}>
      {!u.reported ? (
        <p className="rounded-[var(--radius-control)] bg-[var(--surface-muted)] px-3 py-2 text-[12px] text-[var(--text-secondary)]">{t('usage.missing')}</p>
      ) : (
        <div className="card p-4">
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <Counter label={t('usage.inputTotal')} value={formatTokens(u.input)} />
            <Counter label={t('usage.outputTotal')} value={formatTokens(u.output)} />
            <Counter label={t('usage.hitRate')} value={formatPercent(u.hitRatio)} />
            <Counter label={t('usage.reasoning')} value={formatTokens(u.reasoning)} />
          </div>
          <Composition
            label={t('usage.inputTotal')}
            segments={[
              { label: t('usage.uncachedInput'), count: u.uncachedInput, color: 'var(--accent)' },
              { label: t('usage.cacheRead'), count: u.read, color: 'var(--green)' },
              { label: t('usage.cacheWrite'), count: u.write, color: 'var(--orange)' },
            ]}
          />
          <Composition
            label={t('usage.outputTotal')}
            segments={[
              { label: t('usage.regularOutput'), count: u.regularOutput, color: 'var(--label-2)' },
              { label: t('usage.reasoning'), count: u.reasoning, color: 'var(--accent)' },
            ]}
          />
          <p className="mt-2 text-[11px] text-[var(--text-secondary)]">{t('usage.totalsHint')}</p>
        </div>
      )}
    </DetailSection>
  );
}

function Counter({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="text-[11px] text-[var(--text-secondary)]">{label}</div>
      <div className="num text-[17px] font-medium selectable">{value}</div>
    </div>
  );
}

function Composition({ label, segments }: { label: string; segments: { label: string; count: number; color: string }[] }) {
  const total = segments.reduce((sum, s) => sum + s.count, 0);
  return (
    <div className="mt-3">
      <div aria-label={label} className="flex h-1.5 overflow-hidden rounded-full bg-[var(--surface-muted)]">
        {total > 0 && segments.filter((s) => s.count > 0).map((s) => (
          <span key={s.label} aria-hidden style={{ background: s.color, width: `${(s.count / total * 100).toFixed(3)}%` }} />
        ))}
      </div>
      <div className="num mt-1 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-[var(--text-secondary)]">
        {segments.map((s) => (
          <span key={s.label} className="inline-flex items-center gap-1.5">
            <span aria-hidden className="size-1.5 rounded-full" style={{ background: s.color } as CSSProperties} />
            {s.label} <span className="selectable">{formatTokens(s.count)}</span>
          </span>
        ))}
      </div>
    </div>
  );
}
