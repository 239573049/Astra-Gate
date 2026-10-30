import { ArrowUpRight } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link } from 'react-router';

import { useSettings, useUpdateSettings } from '../../api/hooks';
import type { ClientKind, RequestAttempt, TraceToolCall } from '../../api/types';
import { useI18n, type TFunction } from '../../i18n';
import { cn } from '../../lib/cn';
import { formatMs, formatTokens } from '../../lib/format';
import { ClientIcon } from '../icons';
import { Switch } from '../ui/controls';
import { stageText, useNow } from './live';
import { attemptFailed, livePhase, shortModel, switchReason, toolArguments, toolHeadline, type ActivityItem } from './model';

/** A provider's and an account's names, for the route line. */
export type NameOf = (providerId: string | null | undefined, accountId: string | null | undefined) => { provider: string; account: string | null };

export interface ActivityRailProps {
  items: ActivityItem[];
  names: NameOf;
  /** The card under the pointer: its path is highlighted on the canvas. */
  onHover: (item: ActivityItem | null) => void;
  highlighted: string | null;
  className?: string;
}

/**
 * The activity feed beside the topology (链路追踪, option B of .plan/prototypes/trace-routing-options.html): one card per
 * request — why it was sent, the route it took (each account or provider, failures and why it moved on) and what came back
 * (thinking, text, each tool call with its arguments). Hovering a card highlights its path on the canvas.
 */
export function ActivityRail({ items, names, onHover, highlighted, className }: ActivityRailProps) {
  const { t } = useI18n();
  const settings = useSettings();
  const update = useUpdateSettings();
  const contentOn = settings.data?.traceContent ?? false;
  const now = useNow(items.some((i) => i.request.status === 'pending'));
  const missingArgs = !contentOn && items.some((i) => i.tools.some((c) => !c.arguments));
  return (
    <aside
      aria-label={t('traces.activity.title')}
      className={cn('flex min-h-0 min-w-0 flex-col overflow-hidden rounded-[14px] border border-[var(--separator)] bg-[var(--surface)]', className)}
    >
      <div className="flex items-center gap-2 border-b border-[var(--separator)] px-3 py-2">
        <div className="min-w-0">
          <div className="text-[13px] font-semibold">{t('traces.activity.title')}</div>
          <div className="truncate text-[11px] text-[var(--text-secondary)]">{t('traces.activity.hint')}</div>
        </div>
        <label className="ml-auto flex shrink-0 cursor-pointer items-center gap-2 text-[12px]" title={t('traces.activity.contentHint')}>
          <span aria-hidden>{t('traces.activity.content')}</span>
          <Switch
            checked={contentOn}
            label={t('traces.activity.content')}
            onChange={(traceContent) => update.mutate({ traceContent })}
            disabled={!settings.data || update.isPending}
          />
        </label>
      </div>
      <div className="flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto p-2.5" onMouseLeave={() => onHover(null)}>
        {missingArgs ? <p className="rounded-[9px] bg-[var(--surface-muted)] px-2.5 py-2 text-[11.5px] text-[var(--text-secondary)]">{t('traces.activity.contentOff')}</p> : null}
        {items.length === 0 ? (
          <p className="px-1 py-6 text-center text-[12.5px] text-[var(--text-secondary)]">{t('traces.activity.empty')}</p>
        ) : (
          items.map((item) => <ActivityCard key={item.id} item={item} names={names} now={now} t={t} onHover={onHover} highlighted={highlighted === item.id} />)
        )}
      </div>
    </aside>
  );
}

function ActivityCard({
  item,
  names,
  now,
  t,
  onHover,
  highlighted,
}: {
  item: ActivityItem;
  names: NameOf;
  now: number;
  t: TFunction;
  onHover: (item: ActivityItem | null) => void;
  highlighted: boolean;
}) {
  const r = item.request;
  const pending = r.status === 'pending';
  const failed = !pending && r.status !== 'success';
  const elapsed = pending ? Math.max(0, now - Date.parse(r.startedAtUtc)) : r.totalMs;
  return (
    <article
      onMouseEnter={() => onHover(item)}
      className={cn(
        'flex flex-col gap-2 rounded-[11px] border bg-[var(--surface-solid)] p-2.5 transition-[border-color,box-shadow]',
        highlighted ? 'border-[var(--accent)] shadow-[0_0_0_1px_var(--accent)]' : pending ? 'border-[color-mix(in_oklab,var(--accent)_45%,transparent)]' : 'border-[var(--separator)]',
      )}
    >
      <header className="flex min-w-0 items-center gap-2 text-[12px]">
        {r.clientKind ? <ClientIcon kind={r.clientKind as ClientKind} size={16} /> : null}
        <span className="min-w-0 truncate font-semibold">{shortModel(r.requestedModel)}</span>
        {item.step ? <span className="shrink-0 text-[var(--text-secondary)]">#{item.step}</span> : null}
        {pending ? (
          <span className="flex min-w-0 items-center gap-1 truncate text-[11px] text-[var(--accent)]">
            <span className="trace-live-pulse shrink-0" />
            <span className="truncate">{stageText(r, t)}</span>
          </span>
        ) : failed ? (
          <span className="shrink-0 rounded-full px-1.5 text-[11px] text-[var(--red)] ring-1 ring-[var(--red)]">{r.httpStatus ?? t('traces.live.failed')}</span>
        ) : null}
        <span className="num ml-auto shrink-0 text-[11.5px] text-[var(--text-secondary)]">{formatMs(elapsed)}</span>
        {item.live && pending ? null : (
          <Link
            to={`/traces/requests/${encodeURIComponent(item.id)}`}
            title={t('traces.activity.open')}
            aria-label={t('traces.activity.open')}
            className="shrink-0 rounded-md p-0.5 text-[var(--text-secondary)] hover:bg-[var(--surface-muted)] hover:text-[var(--foreground)]"
          >
            <ArrowUpRight className="size-3.5" />
          </Link>
        )}
      </header>

      {item.cause ? (
        <Line label={t('traces.activity.cause')}>
          {item.cause.kind === 'user' ? (
            <>
              <Chip tone="user">{t('traces.cause.user')}</Chip>
              {item.cause.prompt ? <p className="line-clamp-3 break-words whitespace-pre-wrap">{item.cause.prompt}</p> : null}
            </>
          ) : (
            <div className="flex flex-wrap items-center gap-1.5">
              <Chip tone="result">{t('traces.cause.tool_result')}</Chip>
              {item.cause.toolResults.map((x, i) => (
                <span key={i} className="inline-flex items-center gap-1 text-[11.5px] whitespace-nowrap">
                  {x.name ?? '?'}
                  <span className={x.error ? 'font-bold text-[var(--red)]' : 'font-bold text-[var(--green)]'}>{x.error ? '✕' : '✓'}</span>
                  <span className="text-[var(--text-secondary)]">{t('traces.cause.chars', { count: formatTokens(x.chars, true) })}</span>
                </span>
              ))}
            </div>
          )}
        </Line>
      ) : null}

      <Route item={item} names={names} t={t} />

      <Output item={item} t={t} pending={pending} />

      {!pending ? (
        <footer className="flex flex-col gap-0.5 border-t border-dashed border-[var(--separator)] pt-1.5 text-[11px] text-[var(--text-secondary)]">
          <span>
            {[
              item.finishReason ? t(`traces.finish.${item.finishReason}` as 'traces.finish.stop') : null,
              t('traces.activity.tokens', { input: formatTokens(r.totalInputTokens, true), output: formatTokens(r.totalOutputTokens, true) }),
            ]
              .filter(Boolean)
              .join(' · ')}
          </span>
          {failed && item.errorMessage ? <span className="line-clamp-2 break-words text-[var(--red)]">{item.errorMessage}</span> : null}
        </footer>
      ) : null}
    </article>
  );
}

/** The accounts / providers a request went to: a failed one with its status, the switch to the next. */
function Route({ item, names, t }: { item: ActivityItem; names: NameOf; t: TFunction }) {
  const r = item.request;
  const attempts: RequestAttempt[] =
    item.attempts.length > 0 ? item.attempts : r.providerId ? [{ providerId: r.providerId, accountId: r.accountId, startMs: 0, status: r.httpStatus, endMs: r.totalMs }] : [];
  if (attempts.length === 0) return null;
  const pending = r.status === 'pending';
  return (
    <Line label={t('traces.activity.route')}>
      <div className="flex flex-wrap items-center gap-1.5">
        {attempts.map((a, i) => {
          const prev = attempts[i - 1];
          const name = names(a.providerId ?? r.providerId, a.accountId);
          const providerChanged = !prev || (prev.providerId ?? r.providerId) !== (a.providerId ?? r.providerId);
          const text = providerChanged || !name.account ? [name.provider, name.account].filter(Boolean).join(' · ') : name.account;
          const bad = attemptFailed(a) && (i < attempts.length - 1 || !pending);
          const open = !a.status && (a.endMs === null || a.endMs === undefined);
          return (
            <span key={i} className="contents">
              {prev ? (
                <span className="text-[11px] whitespace-nowrap text-[var(--orange)]">
                  {switchReason(prev.retry, t) ?? ''} →
                </span>
              ) : null}
              <span
                className={cn(
                  'inline-flex max-w-full items-center gap-1 truncate rounded-[7px] px-1.5 py-px text-[11.5px] ring-1',
                  bad ? 'ring-[var(--red)]' : open && pending ? 'ring-[var(--accent)]' : 'ring-[var(--separator)]',
                )}
              >
                <span className={cn('size-1.5 shrink-0 rounded-full', bad ? 'bg-[var(--red)]' : open && pending ? 'trace-live-pulse' : 'bg-[var(--green)]')} />
                <span className="truncate">{text}</span>
                {bad ? <b className="font-semibold text-[var(--red)]">{a.status ?? t('traces.cause.failed')}</b> : null}
              </span>
            </span>
          );
        })}
      </div>
    </Line>
  );
}

/** What came back: thinking (time and size), the start of the reply, each tool call with its arguments. */
function Output({ item, t, pending }: { item: ActivityItem; t: TFunction; pending: boolean }) {
  const r = item.request;
  const parts: ReactNode[] = [];
  if (item.thinkMs || item.thinkChars)
    parts.push(
      <div key="think" className="flex items-center gap-1.5">
        <Chip tone="think">{t('traces.output.think')}</Chip>
        <span className="text-[11.5px] text-[var(--text-secondary)]">
          {item.thinkChars ? t('traces.output.thinkLine', { time: formatMs(item.thinkMs ?? 0), chars: formatTokens(item.thinkChars, true) }) : formatMs(item.thinkMs ?? 0)}
        </span>
      </div>,
    );
  if (item.reply || item.textChars || item.textMs)
    parts.push(
      <div key="text" className="flex flex-col gap-1">
        <div className="flex items-center gap-1.5">
          <Chip tone="text">{t('traces.output.text')}</Chip>
          {item.textChars ? (
            <span className="text-[11.5px] text-[var(--text-secondary)]">{t('traces.cause.chars', { count: formatTokens(item.textChars, true) })}</span>
          ) : item.textMs ? (
            <span className="text-[11.5px] text-[var(--text-secondary)]">{formatMs(item.textMs)}</span>
          ) : null}
        </div>
        {item.reply ? <p className="line-clamp-4 break-words whitespace-pre-wrap">{item.reply}</p> : null}
      </div>,
    );
  item.tools.forEach((call, i) => parts.push(<ToolCall key={`tool${i}`} call={call} t={t} live={item.live} />));
  if (parts.length === 0) {
    if (!pending || livePhase(r) === 'arriving') return null;
    parts.push(
      <span key="wait" className="text-[11.5px] text-[var(--text-secondary)]">
        {t('traces.output.waiting')}
      </span>,
    );
  }
  return <Line label={t('traces.activity.output')}>{parts}</Line>;
}

/** One tool call; a live item's arguments arrive with its stored row, so it says nothing about them yet. */
function ToolCall({ call, t, live }: { call: TraceToolCall; t: TFunction; live: boolean }) {
  const args = toolArguments(call.arguments);
  const headline = toolHeadline(call.arguments);
  if (!args)
    return (
      <div className="flex min-w-0 items-center gap-1.5">
        <Chip tone="tool">{t('traces.output.tool')}</Chip>
        <b className="font-semibold">{call.name}</b>
        {live ? null : <span className="truncate text-[11px] text-[var(--text-secondary)]">{t('traces.output.noArgs')}</span>}
      </div>
    );
  return (
    <details open className="group rounded-[8px] bg-[var(--surface-muted)]">
      <summary className="flex min-w-0 cursor-pointer list-none items-center gap-1.5 px-1.5 py-1 [&::-webkit-details-marker]:hidden">
        <Chip tone="tool">{t('traces.output.tool')}</Chip>
        <b className="shrink-0 font-semibold">{call.name}</b>
        {headline ? <span className="min-w-0 truncate font-mono text-[11px] text-[var(--text-secondary)]">{headline}</span> : null}
        <span className="ml-auto shrink-0 text-[10px] text-[var(--text-secondary)] group-open:rotate-90">▸</span>
      </summary>
      {'pairs' in args ? (
        <dl className="mx-1.5 mb-1.5 grid grid-cols-[max-content_minmax(0,1fr)] gap-x-2.5 gap-y-0.5 rounded-[6px] bg-[var(--surface-solid)] px-2 py-1.5 font-mono text-[11px]">
          {args.pairs.map(([k, v]) => (
            <div key={k} className="contents">
              <dt className="text-[var(--text-secondary)]">{k}</dt>
              <dd className="max-h-40 overflow-auto break-all whitespace-pre-wrap">{v}</dd>
            </div>
          ))}
        </dl>
      ) : (
        <pre className="mx-1.5 mb-1.5 max-h-40 overflow-auto rounded-[6px] bg-[var(--surface-solid)] px-2 py-1.5 font-mono text-[11px] break-all whitespace-pre-wrap">{args.raw}</pre>
      )}
    </details>
  );
}

function Line({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="grid grid-cols-[30px_minmax(0,1fr)] gap-2 text-[12px]">
      <span className="pt-0.5 text-[11px] text-[var(--text-secondary)]">{label}</span>
      <div className="flex min-w-0 flex-col gap-1.5">{children}</div>
    </div>
  );
}

const CHIP_TONE: Record<string, string> = {
  user: 'bg-[#64d2ff] text-[#0b0b0d]',
  result: 'text-[var(--green)] ring-1 ring-[var(--green)]',
  think: 'bg-[var(--accent-2)] text-white',
  text: 'bg-[var(--accent)] text-white',
  tool: 'bg-[var(--green)] text-[#0b0b0d]',
};

function Chip({ tone, children }: { tone: keyof typeof CHIP_TONE; children: ReactNode }) {
  return <span className={cn('inline-flex h-[18px] w-fit shrink-0 items-center rounded-[5px] px-1.5 text-[11px] font-semibold whitespace-nowrap', CHIP_TONE[tone])}>{children}</span>;
}
