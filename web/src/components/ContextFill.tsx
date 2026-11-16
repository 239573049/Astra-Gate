import { useT } from '../i18n';
import type { ContextUsage } from '../api/types';
import { cn } from '../lib/cn';
import { fillTone, formatFill, widthPercent, windowSegments, type FillTone } from '../lib/context';
import { formatPercent, formatTokens } from '../lib/format';
import { DetailSection } from './ui/controls';

const TONE_COLOR: Record<FillTone, string> = { ok: 'var(--accent)', warn: 'var(--orange)', danger: 'var(--red)' };

/** Compact request-list cell: the share of the context window the prompt filled, as a thin bar and a percentage. */
export function ContextFillCell({ context }: { context?: ContextUsage | null }) {
  const t = useT();
  if (!context || context.fill == null || !context.window) {
    return <span className="text-right opacity-50" title={t('context.unknown')}>—</span>;
  }
  const tone = fillTone(context.fill);
  return (
    <span
      className="num flex flex-col items-end gap-1 whitespace-nowrap text-[11px]"
      title={t('context.cellTooltip', { prompt: formatTokens(context.promptTokens), window: formatTokens(context.window) })}
    >
      <span className={cn(tone === 'danger' && 'text-[var(--red)]', tone === 'warn' && 'text-[var(--orange)]')}>{formatFill(context.fill)}</span>
      <span aria-hidden className="h-1 w-14 overflow-hidden rounded-full bg-[var(--surface-muted)]">
        <span className="block h-full rounded-full" style={{ width: `${widthPercent(context.promptTokens, context.window)}%`, background: TONE_COLOR[tone] }} />
      </span>
    </span>
  );
}

/** Request detail: the window bar (cached prompt, fresh prompt, output), the window and output limits, and what the prompt was made of. */
export function ContextSection({ context }: { context?: ContextUsage | null }) {
  const t = useT();
  if (!context) return null;
  const segments = windowSegments(context);
  const colors = { cacheRead: 'var(--green)', fresh: 'var(--accent)', output: 'var(--label-2)' } as const;
  const labels = { cacheRead: t('context.cached'), fresh: t('context.fresh'), output: t('context.output') } as const;
  const parts = context.parts
    ? [
        { key: 'system', label: t('context.part.system'), tokens: context.parts.system, color: 'var(--accent)' },
        { key: 'tools', label: t('context.part.tools'), tokens: context.parts.tools, color: 'var(--orange)' },
        { key: 'history', label: t('context.part.history'), tokens: context.parts.history, color: 'var(--green)' },
        { key: 'latest', label: t('context.part.latest'), tokens: context.parts.latest, color: 'var(--blue, var(--label-2))' },
      ]
    : [];
  const partsTotal = parts.reduce((sum, p) => sum + p.tokens, 0);
  return (
    <DetailSection title={t('context.title')}>
      <div className="card p-4">
        {context.window && context.fill != null ? (
          <>
            <div className="flex items-baseline justify-between gap-3">
              <span className="num text-[22px] font-medium">{formatFill(context.fill)}</span>
              <span className="num text-[12px] text-[var(--text-secondary)]">
                {formatTokens(context.promptTokens)} / {formatTokens(context.window)}
              </span>
            </div>
            <div aria-label={t('context.title')} className="mt-2 flex h-2 overflow-hidden rounded-full bg-[var(--surface-muted)]">
              {segments.filter((s) => s.tokens > 0).map((s) => (
                <span key={s.key} aria-hidden style={{ background: colors[s.key], width: `${widthPercent(s.tokens, context.window!).toFixed(3)}%` }} />
              ))}
            </div>
            <div className="num mt-1 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-[var(--text-secondary)]">
              {segments.map((s) => (
                <span key={s.key} className="inline-flex items-center gap-1.5">
                  <span aria-hidden className="size-1.5 rounded-full" style={{ background: colors[s.key] }} />
                  {labels[s.key]} <span className="selectable">{formatTokens(s.tokens)}</span>
                </span>
              ))}
              {context.promptTokens > 0 && <span>{t('context.cacheShare', { percent: formatPercent(context.cacheReadTokens / context.promptTokens) })}</span>}
            </div>
          </>
        ) : (
          <p className="text-[12px] text-[var(--text-secondary)]">{t('context.unknown')}</p>
        )}
        {context.maxOutput ? (
          <p className="num mt-3 text-[12px] text-[var(--text-secondary)]">
            {t('context.outputLimit', { output: formatTokens(context.outputTokens), max: formatTokens(context.maxOutput), percent: formatFill(context.outputTokens / context.maxOutput) })}
          </p>
        ) : null}
        {partsTotal > 0 && (
          <div className="mt-3">
            <div className="text-[11px] text-[var(--text-secondary)]">{t('context.partsTitle')}</div>
            <div className="mt-1 flex h-1.5 overflow-hidden rounded-full bg-[var(--surface-muted)]">
              {parts.filter((p) => p.tokens > 0).map((p) => (
                <span key={p.key} aria-hidden style={{ background: p.color, width: `${((p.tokens / partsTotal) * 100).toFixed(3)}%` }} />
              ))}
            </div>
            <div className="num mt-1 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-[var(--text-secondary)]">
              {parts.map((p) => (
                <span key={p.key} className="inline-flex items-center gap-1.5">
                  <span aria-hidden className="size-1.5 rounded-full" style={{ background: p.color }} />
                  {p.label} <span className="selectable">≈{formatTokens(p.tokens)}</span>
                </span>
              ))}
            </div>
            <p className="mt-1 text-[10.5px] text-[var(--text-muted)]">{t('context.partsHint')}</p>
          </div>
        )}
      </div>
    </DetailSection>
  );
}
