import { useLayoutEffect, useRef, useSyncExternalStore } from 'react';
import { useNavigate } from 'react-router';

import { useLiveRequest } from '../../api/liveRequests';
import type { RequestCause, RequestSummary } from '../../api/types';
import { useI18n, type TFunction } from '../../i18n';
import { cn } from '../../lib/cn';
import { formatMs } from '../../lib/format';
import { attemptFailed, livePhase } from './model';

// ------------------------------------------------------------------ a shared 1 s clock (elapsed time on hover)

const clockListeners = new Set<() => void>();
let now = Date.now();
let clock: ReturnType<typeof setInterval> | undefined;

function subscribeClock(cb: () => void) {
  clockListeners.add(cb);
  clock ??= setInterval(() => {
    now = Date.now();
    clockListeners.forEach((l) => l());
  }, 1000);
  return () => {
    clockListeners.delete(cb);
    if (clockListeners.size === 0 && clock) {
      clearInterval(clock);
      clock = undefined;
    }
  };
}

const idle = () => () => {};

/** The time now, ticking once a second while something shows it (and <paramref>enabled</paramref>). */
export const useNow = (enabled = true) => useSyncExternalStore(enabled ? subscribeClock : idle, () => (enabled ? now : 0));

/** Where a live request is, in words: what the model is producing once it answers, else what it waits for. */
export function stageText(r: RequestSummary, t: TFunction): string {
  const phase = livePhase(r);
  if (phase === 'streaming' && r.stage)
    return r.stage.startsWith('tool:') ? t('traces.agent.tool', { name: r.stage.slice('tool:'.length) }) : t(`traces.agent.${r.stage}` as 'traces.agent.thinking');
  return t(`traces.live.${phase}` as 'traces.live.upstream');
}

/** A session card's live line: what its call is doing and for how long (until the stored row replaces the card). */
export function PendingLine({ id }: { id: string }) {
  const r = useLiveRequest(id);
  const { t } = useI18n();
  const clockNow = useNow();
  if (!r) return null;
  const phase = livePhase(r);
  const elapsed = Math.max(0, r.totalMs ?? clockNow - Date.parse(r.startedAtUtc));
  return (
    <div className="flex items-center gap-1.5 truncate text-[11.5px] text-[var(--text-secondary)]">
      <span className={cn('trace-pending-dot', phase === 'failed' ? 'failed' : phase === 'done' ? 'done' : r.stage?.split(':')[0])} />
      <span className="truncate">{stageText(r, t)}</span>
      <span className="num shrink-0">{formatMs(elapsed)}</span>
    </div>
  );
}

/** A request waiting this long for the upstream's first byte is drawn as slow. */
const SLOW_MS = 8000;

/** Spreads the dots of one line so they do not move in lockstep. */
const offsetOf = (id: string) => {
  let h = 0;
  for (const ch of id) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return (h % 1000) / 1000;
};

/** Rough width of a label in the 10.5 px capsule font (CJK characters are square). */
const labelWidth = (text: string) => {
  let w = 0;
  for (const ch of text) w += /[⺀-￿]/.test(ch) ? 10.5 : 6.2;
  return Math.ceil(w) + 14;
};

/** What a request carries to the model, as a capsule label: the user's prompt, or which tool results. */
export function causeLabel(cause: RequestCause | null | undefined, t: TFunction): string | null {
  if (!cause) return null;
  if (cause.kind === 'user') return t('traces.cause.user');
  const results = cause.toolResults;
  if (results.length === 1 && results[0].name) return `${t('traces.cause.tool_result')} · ${results[0].name}`;
  return results.length > 1 ? t('traces.cause.results', { count: results.length }) : t('traces.cause.tool_result');
}

/** What the model produces right now, as a capsule label and a colour. */
function stageOf(stage: string | null | undefined, t: TFunction): { label: string | null; tone: 'think' | 'write' | 'tool' | 'back' } {
  if (stage === 'thinking') return { label: t('traces.output.think'), tone: 'think' };
  if (stage === 'writing') return { label: t('traces.output.text'), tone: 'write' };
  if (stage?.startsWith('tool:')) return { label: stage.slice('tool:'.length) || t('traces.output.tool'), tone: 'tool' };
  return { label: null, tone: 'back' };
}

/** A label riding a line: a rounded capsule centred on the moving point. */
function Capsule({ text, tone }: { text: string; tone: string }) {
  const w = labelWidth(text);
  return (
    <g className={cn('trace-cap', tone)}>
      <rect x={-w / 2} y={-8.5} width={w} height={17} rx={8.5} />
      <text y={3.6} textAnchor="middle">
        {text}
      </text>
    </g>
  );
}

export interface LiveDotProps {
  id: string;
  /** The edge's path, source → target. */
  path: string;
  /** client: agent → Astra; provider / account: Astra → the provider (the account row its current attempt is on). */
  role: 'client' | 'provider' | 'account';
  /** The agent's end of a client line (its source) and the provider's end of a provider line (its target). */
  end: { x: number; y: number };
}

/**
 * One in-flight request on a topology line (.plan/observability.md §5), from the live request feed: going out as a capsule
 * that says what it carries (the user's prompt, tool results, a retry), coming back once the upstream answers as a stream
 * coloured by what the model produces (thinking, text, a tool call and its name), a burst where it ended. Hover for what it
 * is waiting on; click to open its trace.
 */
export function LiveDot({ id, path, role, end }: LiveDotProps) {
  const r = useLiveRequest(id);
  const { t } = useI18n();
  const navigate = useNavigate();
  const clockNow = useNow();
  if (!r) return null;
  const phase = livePhase(r);
  const open = () => navigate(`/traces/requests/${encodeURIComponent(id)}`);
  const elapsed = Math.max(0, r.totalMs ?? clockNow - Date.parse(r.startedAtUtc));
  const title = [r.requestedModel, stageText(r, t), r.httpStatus ? String(r.httpStatus) : null, formatMs(elapsed)]
    .filter(Boolean)
    .join(' · ');
  const upstreamSide = role !== 'client';

  if (phase === 'done' || phase === 'failed') {
    // The request is over: a burst at the agent — or at the provider, when the upstream turned it down.
    const upstreamRefused = phase === 'failed' && (r.httpStatus ?? 0) >= 400;
    if (upstreamSide ? !upstreamRefused : upstreamRefused) return null;
    return (
      <g className="trace-live" onClick={open}>
        <title>{title}</title>
        <circle cx={end.x} cy={end.y} r={5} className={cn('trace-burst', phase)} />
      </g>
    );
  }
  if (upstreamSide && phase === 'arriving') return null;
  const back = phase === 'streaming';
  const slow = phase === 'upstream' && elapsed > SLOW_MS;
  const attempts = r.attempts ?? [];
  const retry = !back && attempts.length > 1;
  const dur = back ? 0.9 : 1.4;
  // Already under way (a negative begin) so the dots of one line do not move in lockstep; a trailing dot starts `lag` later.
  const motion = (lag = 0) => (
    <animateMotion
      dur={`${dur}s`}
      repeatCount="indefinite"
      path={path}
      keyPoints={back ? '1;0' : '0;1'}
      keyTimes="0;1"
      calcMode="linear"
      begin={`${(lag - offsetOf(id) * dur).toFixed(2)}s`}
    />
  );
  if (back) {
    // The answer streams back: a capsule naming what the model produces, two dots trailing it.
    const stage = stageOf(r.stage, t);
    return (
      <g className="trace-live" onClick={open}>
        <title>{title}</title>
        {[0.16, 0.32].map((k) => (
          <circle key={`${phase}${k}`} r={3} className={cn('trace-live-dot', 'stage-' + stage.tone)}>
            {motion(k)}
          </circle>
        ))}
        <g key={`${phase}${stage.tone}`}>
          {stage.label ? <Capsule text={stage.label} tone={'stage-' + stage.tone} /> : <circle r={4.5} className={cn('trace-live-dot', 'stage-' + stage.tone)} />}
          {motion()}
        </g>
      </g>
    );
  }
  const cause = causeLabel(r.cause, t);
  const text = retry ? `${t('traces.cause.retry')}${cause ? ` · ${cause}` : ''}` : cause;
  const tone = retry ? 'retry' : slow ? 'slow' : r.cause?.kind === 'tool_result' ? 'result' : 'out';
  return (
    <g className="trace-live" onClick={open}>
      <title>{title}</title>
      {/* keyed by phase: a new motion element when the direction changes */}
      <g key={`${phase}${attempts.length}`}>
        {text ? <Capsule text={text} tone={tone} /> : <circle r={4.5} className={cn('trace-live-dot', tone)} />}
        {motion()}
      </g>
    </g>
  );
}

/**
 * An attempt the upstream just refused on this line: a red capsule with its status runs back from the account to the hub,
 * once, and the row bursts — the next attempt then leaves on its own line.
 */
export function FailedDot({ id, path, end }: { id: string; path: string; end: { x: number; y: number } }) {
  const r = useLiveRequest(id);
  const { t } = useI18n();
  const ref = useRef<SVGAnimateMotionElement>(null);
  // begin="indefinite" + beginElement: the run starts when the failure shows up, not at the document's start
  useLayoutEffect(() => {
    try {
      ref.current?.beginElement?.();
    } catch {
      // an environment without SMIL: the capsule just sits at the hub end
    }
  }, []);
  const failed = [...(r?.attempts ?? [])].reverse().find(attemptFailed);
  if (!r || !failed) return null;
  const text = failed.status ? String(failed.status) : t('traces.cause.failed');
  return (
    <g className="trace-live">
      <circle cx={end.x} cy={end.y} r={5} className="trace-burst failed" />
      <g className="trace-fail-run">
        <Capsule text={text} tone="fail" />
        <animateMotion ref={ref} begin="indefinite" dur="0.9s" fill="freeze" path={path} keyPoints="1;0" keyTimes="0;1" calcMode="linear" />
      </g>
    </g>
  );
}
