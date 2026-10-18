import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';

import { ApiError } from '../../api/client';
import { useTraceSession, useTraceSessions } from '../../api/hooks';
import { peekLiveRequest, useLiveRequest, useLiveRequestFeed, useLiveRequestList } from '../../api/liveRequests';
import type { ClientKind, RequestSummary, TraceCall, TraceSpan } from '../../api/types';
import { useI18n, type TFunction } from '../../i18n';
import { formatDateTime, formatMs, formatTokens, formatUsd } from '../../lib/format';
import { ClientIcon, ProviderIcon } from '../icons';
import { EmptyState, Segmented, Spinner } from '../ui/controls';
import { Select } from '../ui/overlays';
import { TraceCanvas } from './canvas';
import { TraceError } from './error';
import { Inspector, TimeBar } from './inspector';
import { useNow } from './live';
import {
  agentStatus,
  attrNum,
  attrStr,
  buildSession,
  buildTimeline,
  callPhases,
  routeDecision,
  routeSummary,
  shortModel,
  spansByRequest,
  PHASE_COLOR as PHASE,
  splitTurnTime,
  SPAN,
  turnPhases,
  STEP_PITCH,
  toolSummary,
  type AgentStatus,
  type ProviderBrand,
  type TimelineRow,
} from './model';
import { labelOfClient, TraceChip, useProviderBrands } from './topology';

const usd = (nanos: number) => formatUsd(nanos / 1e9);

/** The sessions seen in a window, most recently active first. */
export function SessionsList() {
  const { t, locale } = useI18n();
  const [params, setParams] = useSearchParams();
  const range = params.get('range') ?? '24h';
  const client = params.get('client') ?? undefined;
  const sessions = useTraceSessions(range, client);
  const navigate = useNavigate();
  const set = (key: string, value: string | null) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
  };

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        {client ? (
          <button type="button" onClick={() => set('client', null)} className="inline-flex h-6 items-center gap-1.5 rounded-full bg-[var(--surface-muted)] px-2.5 text-[12px]">
            <ClientIcon kind={client as ClientKind} size={16} />
            {labelOfClient(client)} ×
          </button>
        ) : null}
        <Segmented
          className="ml-auto"
          ariaLabel={t('traces.range.label')}
          value={range}
          onChange={(v) => set('range', v)}
          items={(['1h', '24h', '7d'] as const).map((r) => ({ value: r, label: t(`traces.range.${r}`) }))}
        />
      </div>
      {sessions.isLoading && !sessions.data ? (
        <Spinner lines={6} />
      ) : sessions.isError && !sessions.data ? (
        <TraceError error={sessions.error} />
      ) : !sessions.data || sessions.data.length === 0 ? (
        <EmptyState title={t('traces.sessions.empty')} />
      ) : (
        <div className="card divide-y divide-[var(--separator)]">
          {sessions.data.map((s) => (
            <button
              key={s.sessionKey}
              type="button"
              onClick={() => navigate(`/traces/sessions/${encodeURIComponent(s.sessionKey)}`)}
              className="grid w-full grid-cols-[minmax(0,1.6fr)_repeat(4,minmax(0,1fr))] items-center gap-3 px-3.5 py-2.5 text-left text-[12.5px] hover:bg-[var(--surface-muted)] max-md:grid-cols-2"
            >
              <span className="flex min-w-0 items-center gap-2.5">
                {s.clientKind ? <ClientIcon kind={s.clientKind as ClientKind} size={26} /> : null}
                <span className="min-w-0">
                  <span className="block truncate font-medium">{s.clientKind ? labelOfClient(s.clientKind) : t('traces.topology.direct')}</span>
                  <span className="block truncate font-mono text-[11px] text-[var(--text-secondary)]">{s.sessionKey}</span>
                </span>
              </span>
              <span className="text-[var(--text-secondary)]">
                {s.active ? <span className="text-[var(--green)]">● {t('traces.sessions.active')}</span> : formatDateTime(s.lastAtUtc, locale)}
              </span>
              <span className="num">{t('traces.sessions.turnsCalls', { turns: s.turns, calls: s.requests })}</span>
              <span className={s.errors > 0 ? 'num text-[var(--red)]' : 'num text-[var(--text-secondary)]'}>{t('traces.sessions.errors', { count: s.errors })}</span>
              <span className="num">{usd(s.costNanoUsd)}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** A turn with more calls than this opens on the timeline (unless a view is asked for). */
const LONG_TURN = 15;
const NO_GROUPS: ReadonlySet<string> = new Set();
const NO_LIVE: readonly RequestSummary[] = [];
const NO_CALLS: TraceCall[] = [];
const NO_SPANS: TraceSpan[] = [];

/** C / D · one session: its turns, and the selected turn as a canvas (structure) or a timeline (time). */
export function SessionView({ sessionKey }: { sessionKey: string }) {
  const { t, locale } = useI18n();
  const [params, setParams] = useSearchParams();
  const turnParam = params.get('turn') ?? undefined;
  const query = useTraceSession(sessionKey, turnParam);
  const brands = useProviderBrands();
  const [selected, setSelected] = useState<string | null>(null);
  const [focus, setFocus] = useState<{ id: string; seq: number } | null>(null);
  const [opened, setOpened] = useState<{ turn: string | null | undefined; ids: ReadonlySet<string> }>({ turn: undefined, ids: NO_GROUPS });
  const [width, measure] = useWidth();
  const data = query.data;
  // always on: a call that starts (even after the session went quiet) shows at once, and its stored row is fetched
  // as soon as it is written rather than at the next poll
  useLiveRequestFeed(true);
  const liveList = useLiveRequestList();
  const mine = data ? liveList.filter((r) => r.sessionKey === sessionKey) : NO_LIVE;
  // A call in flight that opened a turn not stored yet is that turn's first step: show that turn (unless one is
  // picked) rather than hang the call off the previous turn.
  const liveTurn = data && !turnParam ? (mine.find((r) => r.turnId && !data.turns.some((x) => x.turnId === r.turnId))?.turnId ?? undefined) : undefined;
  const turnId = liveTurn ?? data?.turnId;
  const calls = data && !liveTurn ? data.calls : NO_CALLS;
  const spans = data && !liveTurn ? data.spans : NO_SPANS;
  const latestTurn = Boolean(liveTurn) || !data || data.turns.length === 0 || data.turns[data.turns.length - 1].turnId === data.turnId;
  // Calls of the shown turn in flight, or finished but not fetched yet (an older server sends no turn: the latest).
  // The same array while they look the same, so traffic elsewhere does not relayout the canvas (or snap a dragged node).
  const [pending, setPending] = useState(NO_LIVE);
  const nextPending = latestTurn ? mine.filter((r) => (!r.turnId || r.turnId === turnId) && !calls.some((c) => c.id === r.id)) : NO_LIVE;
  if (!sameCards(nextPending, pending)) setPending(nextPending);
  const viewParam = params.get('view');
  const view = viewParam === 'timeline' || viewParam === 'canvas' ? viewParam : calls.length > LONG_TURN ? 'timeline' : 'canvas';
  // as many steps per row as fit at a readable zoom
  const columns = width ? Math.min(6, Math.max(2, Math.floor((width / 0.85 + 40) / STEP_PITCH))) : 4;
  const expanded = opened.turn === turnId ? opened.ids : NO_GROUPS;
  const graph = useMemo(
    () => buildSession(calls, spans, t, brands, { columns, collapse: true, expanded, pending }),
    [calls, spans, t, brands, columns, expanded, pending],
  );
  const time = useMemo(() => splitTurnTime(calls, spans), [calls, spans]);
  // the model's time split by phase, scaled to the model part of the bar (calls of sub-agents overlap the main ones)
  const phaseParts = useMemo(() => {
    const sum = turnPhases(calls, spans);
    const total = sum.wait + sum.think + sum.write + sum.tool;
    if (total <= 0 || time.modelMs <= 0) return null;
    return (['wait', 'think', 'write', 'tool'] as const).map((k) => ({ kind: k, ms: (sum[k] / total) * time.modelMs }));
  }, [calls, spans, time]);
  const set = (key: string, value: string | null) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
  };

  if (query.isLoading && !data) return <Spinner lines={6} />;
  if (query.isError && !data && !(query.error instanceof ApiError && query.error.status === 404 && !/\/api\/traces/.test(query.error.message)))
    return <TraceError error={query.error} />;
  if (!data) return <EmptyState title={t('traces.session.notFound')} action={<Link to="/traces/sessions">{t('traces.session.back')}</Link>} />;
  const s = data.session;
  const turnIndex = liveTurn ? data.turns.length : Math.max(0, data.turns.findIndex((x) => x.turnId === data.turnId));
  const inspector = selected ? <CallInspector id={selected} calls={calls} spans={spans} t={t} brands={brands} /> : null;
  const groups = graph.nodes.filter((n) => n.id.startsWith('group:')).map((n) => n.id);
  const expand = (ids: string[]) => setOpened({ turn: turnId, ids: new Set([...expanded, ...ids]) });
  // a folded run opens instead of being selected
  const select = (id: string | null) => (id?.startsWith('group:') ? expand([id]) : setSelected(id));
  const nextAnomaly = () => {
    const ids = graph.anomalies;
    if (ids.length === 0) return;
    const id = ids[(ids.indexOf(focus?.id ?? '') + 1) % ids.length];
    setSelected(id);
    setFocus({ id, seq: (focus?.seq ?? 0) + 1 });
  };

  return (
    <div className="flex h-full min-h-0 flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <TraceChip>
          {s.clientKind ? <ClientIcon kind={s.clientKind as ClientKind} size={16} /> : null}
          <b>{s.clientKind ? labelOfClient(s.clientKind) : t('traces.topology.direct')}</b>
          {s.active ? <span className="text-[var(--green)]">● {t('traces.sessions.active')}</span> : null}
        </TraceChip>
        {s.active || pending.length > 0 ? <AgentStatusChip sessionKey={sessionKey} calls={latestTurn ? calls : NO_CALLS} spans={spans} /> : null}
        <TraceChip>{t('traces.sessions.turnsCalls', { turns: s.turns, calls: s.requests })}</TraceChip>
        {s.providerIds.map((id) => {
          const b = brands.get(id);
          return b ? (
            <TraceChip key={id}>
              <ProviderIcon name={b.name} icon={b.icon} colorKey={b.templateId} size={16} />
              {b.name}
            </TraceChip>
          ) : null;
        })}
        <TraceChip>
          <b>{usd(s.costNanoUsd)}</b>
        </TraceChip>
        <span className="ml-auto flex flex-wrap items-center gap-2">
          {graph.anomalies.length > 0 ? (
            <button
              type="button"
              onClick={nextAnomaly}
              className="inline-flex h-7 items-center gap-1.5 rounded-full border border-[color-mix(in_oklab,var(--red)_45%,var(--separator))] px-3 text-[12px] text-[var(--red)] hover:bg-[color-mix(in_oklab,var(--red)_10%,transparent)]"
            >
              {t('traces.session.nextAnomaly', { count: graph.anomalies.length })}
            </button>
          ) : null}
          {view === 'canvas' && groups.length > 0 ? (
            <button type="button" onClick={() => expand(groups)} className="inline-flex h-7 items-center rounded-full px-3 text-[12px] text-[var(--accent)] hover:bg-[var(--surface-muted)]">
              {t('traces.session.expandAll')}
            </button>
          ) : null}
          {data.turns.length > 0 || liveTurn ? (
            <Select
              ariaLabel={t('traces.session.turnPicker')}
              value={turnId ?? null}
              onChange={(v) => {
                setSelected(null);
                set('turn', v);
              }}
              options={[
                ...data.turns.map((x, i) => ({
                  value: x.turnId,
                  label: `${t('traces.session.turn', { n: i + 1 })} · ${formatDateTime(x.startedAtUtc, locale)}`,
                })),
                ...(liveTurn ? [{ value: liveTurn, label: `${t('traces.session.turn', { n: data.turns.length + 1 })} · ${t('traces.request.pending')}` }] : []),
              ]}
            />
          ) : null}
          <Segmented
            ariaLabel={t('traces.session.viewLabel')}
            value={view}
            onChange={(v) => set('view', v)}
            items={[
              { value: 'canvas', label: t('traces.session.view.canvas') },
              { value: 'timeline', label: t('traces.session.view.timeline') },
            ]}
          />
        </span>
      </div>
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-[11.5px] text-[var(--text-secondary)]">
        <span>{t('traces.session.turn', { n: turnIndex + 1 })}</span>
        {view === 'canvas' ? (
          <>
            <Legend color="var(--label-3)">{t('traces.session.legend.exact')}</Legend>
            <Legend color="var(--accent-2)" dashed>
              {t('traces.session.legend.inferred')}
            </Legend>
            <Legend pill>{t('traces.session.legend.tool')}</Legend>
          </>
        ) : (
          (['wait', 'think', 'write', 'tool'] as const).map((k) => (
            <Legend key={k} swatch={PHASE[k]}>
              {t(`traces.phase.${k}`)}
            </Legend>
          ))
        )}
      </div>
      <div ref={measure} className="flex min-h-0 flex-1 flex-col">
        {calls.length === 0 && pending.length === 0 ? (
          <EmptyState title={t('traces.session.empty')} />
        ) : view === 'canvas' ? (
          // a new turn is a new layout: fit it again
          <TraceCanvas key={turnId ?? ''} className="min-h-0 flex-1" graph={graph} selected={selected} onSelect={select} inspector={inspector} focus={focus} />
        ) : (
          <Timeline calls={calls} spans={spans} pending={pending} selected={selected} onSelect={setSelected} inspector={inspector} focus={focus} />
        )}
      </div>
      {time.totalMs > 0 ? (
        <TimeBar
          title={t('traces.session.time.title')}
          total={formatMs(time.totalMs)}
          parts={[
            ...(phaseParts
              ? phaseParts.map((p) => ({ label: t(`traces.phase.${p.kind}`), value: p.ms, text: formatMs(p.ms), color: PHASE[p.kind] }))
              : [{ label: t('traces.session.time.model'), value: time.modelMs, text: formatMs(time.modelMs), color: 'var(--accent)' }]),
            {
              label: t('traces.session.time.tools'),
              value: time.toolMs,
              text: formatMs(time.toolMs),
              color: 'var(--label-3)',
              note: <span className="rounded-full bg-[color-mix(in_oklab,var(--accent-2)_20%,transparent)] px-1.5 text-[10.5px]">{t('traces.session.time.needsTelemetry')}</span>,
            },
            { label: t('traces.session.time.retries'), value: time.retryMs, text: formatMs(time.retryMs), color: 'var(--red)' },
          ]}
        />
      ) : null}
      {time.overheadMs > 0 ? <span className="-mt-1 text-[11.5px] text-[var(--text-secondary)]">{t('traces.session.time.overhead', { ms: formatMs(time.overheadMs) })}</span> : null}
    </div>
  );
}

/** Whether two lists of live calls draw the same cards and bars. */
const sameCards = (a: readonly RequestSummary[], b: readonly RequestSummary[]) =>
  a.length === b.length && a.every((r, i) => r.id === b[i].id && r.status === b[i].status && r.providerId === b[i].providerId && r.upstreamModel === b[i].upstreamModel && r.totalMs === b[i].totalMs);

/** The element's content width, once measured. */
function useWidth(): [number | null, (el: HTMLElement | null) => void] {
  const [width, setWidth] = useState<number | null>(null);
  const observer = useRef<ResizeObserver | null>(null);
  const ref = useCallback((el: HTMLElement | null) => {
    observer.current?.disconnect();
    observer.current = null;
    if (!el || typeof ResizeObserver === 'undefined') return;
    observer.current = new ResizeObserver(([entry]) => setWidth(Math.round(entry.contentRect.width)));
    observer.current.observe(el);
  }, []);
  return [width, ref];
}

const STATUS_COLOR: Record<AgentStatus['kind'], string> = {
  upstream: PHASE.wait,
  waiting: PHASE.wait,
  thinking: PHASE.think,
  writing: PHASE.write,
  tool: PHASE.tool,
  tools: PHASE.tool,
  idle: 'var(--text-muted)',
  failed: 'var(--red)',
};

/** "12 s", "4 min": how long the agent has been at it. */
const sinceText = (ms: number) => (ms < 60_000 ? `${Math.max(0, Math.round(ms / 1000))} s` : ms < 3_600_000 ? `${Math.floor(ms / 60_000)} min` : `${Math.floor(ms / 3_600_000)} h`);

/** What the agent of an active session is doing right now: the call in flight, or what the last one left it doing. */
function AgentStatusChip({ sessionKey, calls, spans }: { sessionKey: string; calls: TraceCall[]; spans: TraceSpan[] }) {
  const { t } = useI18n();
  const now = useNow();
  const list = useLiveRequestList();
  // the newest call of this session in flight (the list is newest first); its own row for every stage change
  const liveId = list.find((r) => r.status === 'pending' && r.sessionKey === sessionKey)?.id;
  const live = useLiveRequest(liveId ?? '');
  const status = agentStatus(live?.status === 'pending' ? live : undefined, calls, spans);
  if (!status) return null;
  const text =
    status.kind === 'tool'
      ? t('traces.agent.tool', { name: status.tool })
      : status.kind === 'tools'
        ? t('traces.agent.tools', { names: status.tools })
        : t(`traces.agent.${status.kind}`);
  const busy = status.kind !== 'idle' && status.kind !== 'failed';
  const model = 'model' in status ? status.model : null;
  return (
    <TraceChip>
      <span className={busy ? 'size-2 animate-pulse rounded-full' : 'size-2 rounded-full'} style={{ background: STATUS_COLOR[status.kind] }} aria-hidden />
      <span>{t('traces.agent.label')}</span>
      <b>{text}</b>
      {model ? <span>{shortModel(model)}</span> : null}
      <span className="num">{sinceText(now - status.since)}</span>
    </TraceChip>
  );
}

function Legend({ color, dashed, pill, swatch, children }: { color?: string; dashed?: boolean; pill?: boolean; swatch?: string; children: ReactNode }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      {swatch ? (
        <span className="inline-block size-2.5 rounded-[3px]" style={{ background: swatch }} />
      ) : pill ? (
        <span className="inline-block h-2.5 w-4 rounded-full border border-dashed border-[var(--label-3)] bg-[var(--surface-muted)]" />
      ) : (
        <span className="inline-block h-0.5 w-[18px]" style={{ background: dashed ? `repeating-linear-gradient(90deg, ${color} 0 5px, transparent 5px 9px)` : color }} />
      )}
      {children}
    </span>
  );
}

const BAR: Record<TimelineRow['kind'], string> = {
  turn: 'var(--fill)',
  llm: 'var(--accent)',
  sub: 'var(--accent-2)',
  tool: 'var(--label-3)',
  'attempt-ok': 'color-mix(in oklab, var(--accent) 70%, transparent)',
  'attempt-fail': 'repeating-linear-gradient(135deg, var(--red) 0 4px, color-mix(in oklab, var(--red) 45%, transparent) 4px 8px)',
  live: 'var(--accent)',
};

/** D · the turn on one time axis: each call split into the model's phases, retries and the tools between calls. */
function Timeline({
  calls,
  spans,
  pending,
  selected,
  onSelect,
  inspector,
  focus,
}: {
  calls: TraceCall[];
  spans: TraceSpan[];
  /** Calls in flight: their bars grow until the stored rows replace them. */
  pending: readonly RequestSummary[];
  selected: string | null;
  onSelect: (id: string) => void;
  inspector: ReactNode | null;
  focus: { id: string; seq: number } | null;
}) {
  const { t } = useI18n();
  const nowMs = useNow(pending.length > 0);
  // the live rows as they are now (stage changes do not change the list), once a second with the clock
  const { rows, totalMs } = useMemo(
    () => buildTimeline(calls, spans, t, pending.map((r) => peekLiveRequest(r.id) ?? r), nowMs),
    [calls, spans, t, pending, nowMs],
  );
  const rowEls = useRef(new Map<string, HTMLElement>());
  useEffect(() => {
    if (focus) rowEls.current.get(focus.id)?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' });
  }, [focus]);
  const ticks = useMemo(() => {
    if (totalMs <= 0) return [];
    const step = niceStep(totalMs / 5);
    return Array.from({ length: Math.floor(totalMs / step) + 1 }, (_, i) => i * step);
  }, [totalMs]);
  const pct = (ms: number) => `${totalMs > 0 ? (ms / totalMs) * 100 : 0}%`;
  return (
    <div className="flex min-h-0 flex-1 flex-wrap items-start gap-3 overflow-auto">
      <section className="card min-w-0 flex-[999_1_640px] p-3" aria-label={t('traces.session.view.timeline')}>
        <div className="overflow-x-auto">
          <div className="flex min-w-[720px] flex-col gap-0.5">
            <div className="grid h-6 grid-cols-[240px_minmax(0,1fr)_64px] items-end gap-3 px-2 text-[11px] text-[var(--text-muted)]">
              <span>{t('traces.timeline.node')}</span>
              <div className="relative h-4">
                {ticks.map((ms) => (
                  <span key={ms} className="absolute top-0 -translate-x-1/2 whitespace-nowrap" style={{ left: pct(ms) }}>
                    {formatMs(ms)}
                  </span>
                ))}
              </div>
              <span className="text-right">{t('traces.timeline.duration')}</span>
            </div>
            {rows.map((row) => (
              <button
                key={row.id}
                ref={(el) => {
                  if (el) rowEls.current.set(row.id, el);
                  else rowEls.current.delete(row.id);
                }}
                type="button"
                title={row.detail ?? undefined}
                aria-pressed={selected === row.selectId}
                onClick={() => onSelect(row.selectId)}
                className="grid h-[30px] grid-cols-[240px_minmax(0,1fr)_64px] items-center gap-3 rounded-lg px-2 text-left text-[12px] hover:bg-[var(--surface-muted)] aria-pressed:bg-[color-mix(in_oklab,var(--accent)_14%,transparent)]"
              >
                <span className="flex min-w-0 items-center gap-1.5" style={{ paddingLeft: row.depth * 18, fontWeight: row.depth === 0 ? 600 : 400 }}>
                  <span className="truncate">{row.label}</span>
                  {row.flag ? (
                    <span className="shrink-0 rounded-full px-1.5 text-[10px] leading-4 font-semibold text-white" style={{ background: row.flag === 'error' ? 'var(--red)' : 'var(--orange)' }}>
                      {t(`traces.flag.${row.flag}`)}
                    </span>
                  ) : null}
                </span>
                <span className="relative block h-3.5 rounded bg-[var(--surface-muted)]">
                  {row.segments && row.segments.length > 0 ? (
                    <>
                      {/* the call, faint, under the phases the model went through */}
                      <span
                        className="absolute inset-y-0 min-w-[3px] rounded"
                        style={{ left: pct(row.startMs), width: pct(row.endMs - row.startMs), background: `color-mix(in oklab, ${BAR[row.kind]} 22%, transparent)` }}
                      />
                      {row.segments.map((seg, i, all) => (
                        <span
                          key={i}
                          // a call in flight: its current phase pulses
                          className={row.kind === 'live' && i === all.length - 1 ? 'absolute inset-y-0 min-w-[2px] animate-pulse rounded-[3px]' : 'absolute inset-y-0 min-w-[2px] rounded-[3px]'}
                          style={{ left: pct(seg.from), width: pct(Math.max(0, seg.to - seg.from)), background: PHASE[seg.kind] }}
                        />
                      ))}
                    </>
                  ) : (
                    <span
                      className={row.kind === 'live' ? 'absolute inset-y-0 min-w-[3px] animate-pulse rounded' : 'absolute inset-y-0 min-w-[3px] rounded'}
                      style={{ left: pct(row.startMs), width: pct(row.endMs - row.startMs), background: row.flag === 'error' ? 'var(--red)' : BAR[row.kind] }}
                    />
                  )}
                </span>
                <span className="num text-right text-[var(--text-secondary)]">{formatMs(row.endMs - row.startMs)}</span>
              </button>
            ))}
          </div>
        </div>
      </section>
      {inspector ? <aside className="card min-w-0 flex-[1_1_300px] p-3.5">{inspector}</aside> : null}
    </div>
  );
}

function niceStep(raw: number): number {
  const pow = 10 ** Math.floor(Math.log10(Math.max(raw, 1)));
  const n = raw / pow;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * pow;
}

/** Details of a call (or the tool gap before it) in a session. */
function CallInspector({ id, calls, spans, t, brands }: { id: string; calls: TraceCall[]; spans: TraceSpan[]; t: TFunction; brands: Map<string, ProviderBrand> }) {
  const by = spansByRequest(spans);
  if (id === 'turn') {
    const time = splitTurnTime(calls, spans);
    return (
      <Inspector
        title={t('traces.session.turnRow')}
        rows={[
          [t('traces.kv.calls'), calls.length],
          [t('traces.session.time.model'), formatMs(time.modelMs)],
          [t('traces.session.time.tools'), formatMs(time.toolMs)],
          [t('traces.session.time.retries'), formatMs(time.retryMs)],
          [t('traces.kv.cost'), usd(calls.reduce((sum, c) => sum + c.costNanoUsd, 0))],
        ]}
      />
    );
  }
  if (id.startsWith('tool:')) {
    const child = calls.find((c) => c.id === id.slice('tool:'.length));
    // the call that asked for the tools: the linked parent, else the main call just before
    const parent = child
      ? ((child.parentRequestId ? calls.find((c) => c.id === child.parentRequestId) : undefined) ??
        calls
          .filter((c) => c.callKind !== 'sidechain' && c.callKind !== 'aux' && Date.parse(c.startedAtUtc) < Date.parse(child.startedAtUtc))
          .sort((a, b) => Date.parse(b.startedAtUtc) - Date.parse(a.startedAtUtc))[0])
      : undefined;
    if (!child || !parent) return null;
    const names = callPhases(by.get(parent.id) ?? [])?.tools ?? [];
    const gap = Date.parse(child.startedAtUtc) - (Date.parse(parent.startedAtUtc) + (parent.totalMs ?? 0));
    return (
      <Inspector
        kind={t('traces.kind.tool')}
        title={names.length > 0 ? toolSummary(names) : t('traces.session.toolCount', { count: parent.toolCalls })}
        rows={[
          [t('traces.kv.toolCalls'), parent.toolCalls],
          [t('traces.kv.gap'), formatMs(Math.max(0, gap))],
          [t('traces.kv.link'), t(`traces.link.${child.linkSource ?? 'new_turn'}` as 'traces.link.tool_call')],
        ]}
        code={{ title: t('traces.session.toolNoteTitle'), lines: [t('traces.session.toolNote')] }}
      />
    );
  }
  const call = calls.find((c) => c.id === id);
  if (!call) return null;
  const own = by.get(call.id) ?? [];
  const attempts = own.filter((s) => s.name === SPAN.attempt);
  const routed = routeDecision(own);
  const brand = call.providerId ? brands.get(call.providerId) : undefined;
  const phases = callPhases(own);
  const response = own.find((s) => s.name === SPAN.response);
  const reasoningChars = attrNum(response, 'astra.response.reasoning_chars');
  const textChars = attrNum(response, 'astra.response.text_chars');
  const phaseText = phases
    ? (
        [
          ['wait', phases.waitMs],
          ['think', phases.thinkMs],
          ['write', phases.writeMs],
          ['tool', phases.toolMs],
        ] as const
      )
        .filter(([k, ms]) => k === 'wait' || ms > 0)
        .map(([k, ms]) => `${t(`traces.phase.${k}`)} ${formatMs(ms)}`)
        .join(' · ')
    : null;
  const optional: [string, ReactNode][] = [
    ...(phaseText ? [[t('traces.kv.phases'), phaseText] as [string, ReactNode]] : []),
    ...(call.reasoningEffort || call.reasoningMode
      ? [[t('traces.kv.reasoning'), [call.reasoningEffort, call.reasoningMode].filter(Boolean).join(' · ')] as [string, ReactNode]]
      : []),
    ...(reasoningChars !== undefined || textChars !== undefined
      ? [[t('traces.kv.chars'), `${reasoningChars ?? 0} / ${textChars ?? 0}`] as [string, ReactNode]]
      : []),
    ...(phases && phases.tools.length > 0 ? [[t('traces.kv.tools'), toolSummary(phases.tools)] as [string, ReactNode]] : []),
  ];
  return (
    <Inspector
      kind={t(`traces.kind.${call.callKind ?? 'main'}` as 'traces.kind.main')}
      icon={brand ? <ProviderIcon name={brand.name} icon={brand.icon} colorKey={brand.templateId} size={28} /> : undefined}
      title={`LLM #${call.step ?? 1} · ${shortModel(call.upstreamModel ?? call.requestedModel)}`}
      sub={[call.providerName, call.accountName ?? call.accountId].filter(Boolean).join(' · ')}
      rows={[
        [t('traces.kv.status'), call.httpStatus ? `${call.status} · ${call.httpStatus}` : call.status],
        ...(routed ? [[t('traces.node.routing'), routeSummary(routed, t)] as [string, ReactNode]] : []),
        [t('traces.kv.duration'), `${formatMs(call.totalMs)}${call.ttftMs ? ` · ${t('traces.kv.firstToken')} ${formatMs(call.ttftMs)}` : ''}`],
        [t('traces.kv.tokens'), `${formatTokens(call.totalInputTokens, true)} / ${formatTokens(call.totalOutputTokens, true)}`],
        ...optional,
        [t('traces.kv.finish'), call.finishReason ?? '—'],
        [t('traces.kv.attempts'), attempts.map((a) => `${attrStr(a, 'astra.account.id') ?? '—'} ${a.attributes?.['http.response.status_code'] ?? ''}`).join(' → ') || '—'],
        [t('traces.kv.link'), t(`traces.link.${call.linkSource ?? 'new_turn'}` as 'traces.link.tool_call')],
        [t('traces.kv.cost'), usd(call.costNanoUsd)],
      ]}
      actions={
        <Link className="text-[12.5px] text-[var(--accent)]" to={`/traces/requests/${encodeURIComponent(call.id)}`}>
          {t('traces.openRequest')} →
        </Link>
      }
    />
  );
}
