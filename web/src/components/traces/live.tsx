import { useSyncExternalStore } from 'react';
import { useNavigate } from 'react-router';

import { useLiveRequest } from '../../api/liveRequests';
import type { RequestSummary } from '../../api/types';
import { useI18n, type TFunction } from '../../i18n';
import { cn } from '../../lib/cn';
import { formatMs } from '../../lib/format';
import { livePhase } from './model';

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

export interface LiveDotProps {
  id: string;
  /** The edge's path, source → target. */
  path: string;
  role: 'client' | 'provider';
  /** The agent's end of a client line (its source) and the provider's end of a provider line (its target). */
  end: { x: number; y: number };
}

/**
 * One in-flight request on a topology line (.plan/observability.md §5), from the live request feed: going out while
 * it is routed and sent, coming back once the upstream answers, a burst where it ended. Hover for what it is waiting
 * on; click to open its trace.
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

  if (phase === 'done' || phase === 'failed') {
    // The request is over: a burst at the agent — or at the provider, when the upstream turned it down.
    const upstreamRefused = phase === 'failed' && (r.httpStatus ?? 0) >= 400;
    if (role === 'provider' ? !upstreamRefused : upstreamRefused) return null;
    return (
      <g className="trace-live" onClick={open}>
        <title>{title}</title>
        <circle cx={end.x} cy={end.y} r={5} className={cn('trace-burst', phase)} />
      </g>
    );
  }
  if (role === 'provider' && phase === 'arriving') return null;
  const back = phase === 'streaming';
  const slow = phase === 'upstream' && elapsed > SLOW_MS;
  const dur = back ? 0.9 : 1.4;
  return (
    <g className="trace-live" onClick={open}>
      <title>{title}</title>
      {/* keyed by phase: a new motion element when the direction changes */}
      <circle key={phase} r={4.5} className={cn('trace-live-dot', back ? 'back' : 'out', slow && 'slow')}>
        <animateMotion
          dur={`${dur}s`}
          repeatCount="indefinite"
          path={path}
          keyPoints={back ? '1;0' : '0;1'}
          keyTimes="0;1"
          calcMode="linear"
          begin={`-${(offsetOf(id) * dur).toFixed(2)}s`}
        />
      </circle>
    </g>
  );
}
