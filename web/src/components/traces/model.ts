import { MarkerType, type Edge, type Node } from '@xyflow/react';

import type {
  RequestAttempt,
  RequestCause,
  RequestSummary,
  TraceAccount,
  TraceActivity,
  TraceCall,
  TraceProviderNode,
  TraceRequest,
  TraceSpan,
  TraceToolCall,
  TraceTopology,
} from '../../api/types';
import type { TFunction } from '../../i18n';
import { formatMs, formatTokens } from '../../lib/format';

// Pure builders behind the trace views (.plan/observability.md §5): API payloads → React Flow nodes / edges and
// timeline rows. No React here, so the layouts are testable on their own.

export const SPAN = {
  request: 'gateway.request',
  route: 'gateway.route',
  body: 'gateway.body',
  middleware: 'gateway.middleware',
  privacy: 'gateway.privacy',
  resolve: 'gateway.resolve',
  encode: 'gateway.encode',
  attempt: 'upstream.attempt',
  response: 'gateway.response',
} as const;

export type Tone = 'neutral' | 'ok' | 'warn' | 'error' | 'idle' | 'sub' | 'task';

export interface ProviderBrand {
  name: string;
  icon?: string | null;
  templateId?: string | null;
}

/** A card on a canvas: a brand icon (client or provider), a title and a few secondary lines. */
export interface CardData extends Record<string, unknown> {
  title: string;
  lines: string[];
  tone?: Tone;
  client?: string | null;
  provider?: ProviderBrand | null;
  /** Small icon above the text instead of beside it (tall nodes). */
  stacked?: boolean;
  /** A tool run by the agent: a dashed pill. */
  pill?: boolean;
  /** Requests in flight right now (topology), shown as a badge. */
  live?: number;
  /** A failed / retried / slow call (session), shown as a badge. */
  flag?: { text: string; tone: 'error' | 'warn' };
  /** A folded run of steps: click to expand. */
  group?: boolean;
  /** A call still in flight (its id in the live request feed): the card shows what the model is doing right now. */
  pending?: string;
  /** What the client's agent is doing right now (topology): a coloured line under the title. */
  status?: { text: string; tone: 'think' | 'write' | 'tool' | 'wait' | 'run' | 'warn' } | null;
  /** Another request's path is highlighted: this card fades. */
  dim?: boolean;
}

export interface HubData extends Record<string, unknown> {
  /** Requests are in flight: the logo breathes. */
  busy?: boolean;
  /** A switch a request just made (why, where to) or where one was just sent, written above the logo. */
  note?: { text: string; tone: 'info' | 'warn' } | null;
}

/** One row of a provider's card (topology): an account, or the API key, its requests go through. */
export interface AccountRow {
  /** The row's handle and edge suffix (accountRowId). */
  id: string;
  accountId: string | null;
  name: string;
  /** ok | cooldown | expired | revoked | disabled */
  status: string;
  /** Its right-hand text: cooldown, remaining quota, attempts in the window. */
  detail: string;
  attempts: number;
  errors: number;
  /** Requests in flight on it now. */
  live: number;
  /** A request is being served here ('on'), or one was just refused here ('fail'). */
  hit: 'on' | 'fail' | null;
  dim?: boolean;
}

/** A provider on the topology: its card with one row (and one line from the hub) per account. */
export interface ProviderData extends Record<string, unknown> {
  title: string;
  lines: string[];
  tone: Tone;
  provider: ProviderBrand;
  rows: AccountRow[];
  live: number;
  dim?: boolean;
}

export type CardNode = Node<CardData, 'card'>;
export type HubNode = Node<HubData, 'hub'>;
export type ProviderNode = Node<ProviderData, 'provider'>;
export type TraceNode = CardNode | HubNode | ProviderNode;

/** A topology line and the in-flight requests riding it (ids into the live request feed). */
export interface FlowData extends Record<string, unknown> {
  /** client: agent → Astra; provider / account: Astra → a provider (one of its account rows). The path's direction. */
  role: 'client' | 'provider' | 'account';
  requests: string[];
  /** Requests whose attempt on this line was just refused: a red dot bounces back to the hub. */
  failed?: string[];
  warn?: boolean;
  /** A retry is heading down this line: it runs orange and dashed. */
  retry?: boolean;
}

/** Where an in-flight request is (from the live feed's fields, .plan/observability.md §5). */
export type LivePhase = 'arriving' | 'upstream' | 'streaming' | 'done' | 'failed';

export function livePhase(r: Pick<RequestSummary, 'status' | 'providerId' | 'httpStatus'>): LivePhase {
  if (r.status !== 'pending') return r.status === 'success' ? 'done' : 'failed';
  if (!r.providerId) return 'arriving'; // still being authenticated / routed
  if (r.httpStatus === null || r.httpStatus === undefined) return 'upstream'; // sent, waiting for the first byte
  return 'streaming';
}

const clientNodeId = (kind: string | null | undefined) => `client:${kind ?? 'direct'}`;
const providerNodeId = (id: string) => `provider:${id}`;

export type Graph = { nodes: TraceNode[]; edges: Edge[] };

const COLORS = {
  flow: 'color-mix(in oklab, var(--accent) 55%, transparent)',
  request: 'var(--label-3)',
  response: 'color-mix(in oklab, var(--accent) 70%, transparent)',
  warn: 'color-mix(in oklab, var(--orange) 80%, transparent)',
  error: 'var(--red)',
  ok: 'var(--green)',
  inferred: 'var(--accent-2)',
};

/** Microseconds as a short duration: 0.4 ms, 38 ms, 1.83 s. */
export function formatUs(us: number | null | undefined): string {
  if (us === null || us === undefined) return '—';
  if (us < 10_000) return `${(us / 1000).toFixed(1)} ms`;
  if (us < 1_000_000) return `${Math.round(us / 1000)} ms`;
  return `${(us / 1_000_000).toFixed(2)} s`;
}

export const attr = (span: TraceSpan | undefined, key: string): string | number | boolean | undefined => span?.attributes?.[key];
export const attrNum = (span: TraceSpan | undefined, key: string): number | undefined => {
  const v = attr(span, key);
  return typeof v === 'number' ? v : undefined;
};
export const attrStr = (span: TraceSpan | undefined, key: string): string | undefined => {
  const v = attr(span, key);
  return typeof v === 'string' ? v : undefined;
};

const ROUTE_SOURCES = ['judge', 'pin', 'turn', 'fallback', 'breaker'] as const;
const ROUTE_MODES = ['session', 'turn'] as const;
const ROUTE_REASONS = [
  'timeout', 'network', 'auth', 'rate_limited', 'overloaded', 'bad_request', 'bad_response',
  'low_confidence', 'breaker', 'no_text', 'not_configured', 'error',
] as const;

/** A smart-routing decision recorded on a resolve span (`astra.route.*`; plan §5.9 — no span of its own). */
export interface RouteDecision {
  /** The resolve span that recorded it. A pool failover resolves again per member; only the first one judged. */
  span: TraceSpan;
  mode?: string;
  source?: string;
  grade?: string;
  model?: string;
  confidence?: number;
  fallbackReason?: string;
  adjustedFrom?: string;
  judgeProtocol?: string;
  judgeModel?: string;
  /** Milliseconds the judge call took; 0 when the decision was reused and no judge ran. */
  judgeMs: number;
  judgeInput?: number;
  judgeOutput?: number;
  newConnection: boolean;
}

/** The decision on this request, if it asked for the virtual model. */
export function routeDecision(spans: readonly TraceSpan[]): RouteDecision | null {
  const span = spans.find((s) => s.name === SPAN.resolve && (attrStr(s, 'astra.route.grade') || attrStr(s, 'astra.route.source')));
  if (!span) return null;
  return {
    span,
    mode: attrStr(span, 'astra.route.mode'),
    source: attrStr(span, 'astra.route.source'),
    grade: attrStr(span, 'astra.route.grade'),
    model: attrStr(span, 'astra.route.model'),
    confidence: attrNum(span, 'astra.route.confidence'),
    fallbackReason: attrStr(span, 'astra.route.fallback_reason'),
    adjustedFrom: attrStr(span, 'astra.route.adjusted_from'),
    judgeProtocol: attrStr(span, 'astra.route.judge.protocol'),
    judgeModel: attrStr(span, 'astra.route.judge.model'),
    judgeMs: attrNum(span, 'astra.route.judge.ms') ?? 0,
    judgeInput: attrNum(span, 'astra.route.judge.input_tokens'),
    judgeOutput: attrNum(span, 'astra.route.judge.output_tokens'),
    newConnection: attr(span, 'astra.route.judge.new_connection') === true,
  };
}

const isOneOf = <T extends string>(value: string | undefined, options: readonly T[]): value is T =>
  value !== undefined && (options as readonly string[]).includes(value);

export function routeSourceLabel(source: string | undefined, t: TFunction): string {
  return isOneOf(source, ROUTE_SOURCES) ? t(`traces.route.source.${source}`) : (source ?? '—');
}

export function routeModeLabel(mode: string | undefined, t: TFunction): string {
  return isOneOf(mode, ROUTE_MODES) ? t(`traces.route.mode.${mode}`) : (mode ?? '—');
}

/** A fallback reason in the words the smart-routing page already uses; the raw reason when it is a new one. */
export function routeReasonLabel(reason: string | undefined, t: TFunction): string | undefined {
  if (!reason) return undefined;
  return isOneOf(reason, ROUTE_REASONS) ? t(`smartRouting.failure.${reason}`) : reason;
}

/** Judge time inside a resolve span, in microseconds; 0 when this span is not the one that recorded the decision. */
export function judgeUsOn(span: TraceSpan | undefined, decision: RouteDecision | null): number {
  if (!span || !decision || span.spanId !== decision.span.spanId || decision.judgeMs <= 0) return 0;
  return Math.min(span.durationUs, Math.round(decision.judgeMs * 1000));
}

/** "standard · Judged · 2.01 s" — the decision in one line. */
export function routeSummary(d: RouteDecision, t: TFunction): string {
  const parts = [d.grade ?? '—', routeSourceLabel(d.source, t)];
  if (d.judgeMs > 0) parts.push(formatUs(Math.round(d.judgeMs * 1000)));
  return parts.join(' · ');
}

const edgeBase = (id: string, source: string, target: string, color: string, extra: Partial<Edge> = {}): Edge => ({
  id,
  source,
  target,
  sourceHandle: 'right',
  targetHandle: 'left',
  type: 'default',
  markerEnd: { type: MarkerType.ArrowClosed, color, width: 16, height: 16 },
  style: { stroke: color, strokeWidth: 1.75 },
  ...extra,
});

// ------------------------------------------------------------------ A · topology

export const HUB_ID = 'astra';
const ROW = 100;
/** Provider card: header, then one row per account (or the API key). */
export const PROVIDER_HEAD = 50;
export const ACCOUNT_ROW = 34;
const PROVIDER_GAP = 22;

/** The handle (and edge suffix) of one account row; `key` for a provider called with its API key. */
export const accountRowId = (accountId: string | null | undefined) => (accountId ? `a:${accountId}` : 'a:key');

/** How long a failed attempt keeps bouncing on its row, and a switch stays written above the hub. */
export const FAIL_SHOW_MS = 1800;
export const NOTE_SHOW_MS = 2600;

/** The attempt a live request is on now (its last one); null for a feed without attempts (an older server). */
export const currentAttempt = (r: Pick<RequestSummary, 'attempts'>): RequestAttempt | null => r.attempts?.at(-1) ?? null;

/** Retry reasons (astra.attempt.retry) with a short traces.switch.* label; another one shows no label rather than a raw key. */
const SWITCH_REASONS = new Set(['rate_limited_failover', 'auth_failover', 'auth_refresh', 'provider_failover', 'compression_rejected']);

/** "限流，换账号" for a retry reason; null for none or one without a label. */
export const switchReason = (retry: string | null | undefined, t: TFunction): string | null =>
  retry && SWITCH_REASONS.has(retry) ? t(`traces.switch.${retry}` as 'traces.switch.rate_limited_failover') : null;

/** An attempt that did not answer: refused by the upstream, or failed before it could. */
export const attemptFailed = (a: RequestAttempt) => a.endMs !== null && a.endMs !== undefined && ((a.status ?? 0) >= 400 || (!a.status && !!a.error) || !!a.retry);

/** The path one request took, for highlighting it on the canvas: its client and every account row an attempt went to. */
export interface HighlightPath {
  clientKind?: string | null;
  attempts: Pick<RequestAttempt, 'providerId' | 'accountId'>[];
  providerId?: string | null;
  accountId?: string | null;
}

export interface TopologyOptions {
  /** The time now (ms since the epoch): how long ago an attempt failed, how long a switch note stays. */
  nowMs?: number;
  /** Dim everything this request did not touch. */
  highlight?: HighlightPath | null;
  /** The latest stored requests (activity feed): what a client's agent does between calls. */
  activity?: readonly TraceActivity[];
}

/**
 * Client and provider columns around the Astra hub. A provider's card lists its accounts (or its API key) as rows, and
 * each row has its own line from the hub, so a request is drawn on the account that serves it: going out while it is
 * sent, coming back coloured by what the model produces, bouncing back red when the upstream refused it and leaving for
 * the next row (or provider) on an orange line when the gateway retries. Line width follows volume; orange marks rate
 * limits / failovers. <paramref>live</paramref> (the live request feed) puts every request in flight on its lines, and adds
 * the client or provider it uses when the window has no traffic of theirs yet.
 */
export function buildTopology(
  source: TraceTopology,
  t: TFunction,
  clientLabel: (kind: string | null | undefined) => string,
  brands: Map<string, ProviderBrand>,
  live: readonly RequestSummary[] = [],
  options: TopologyOptions = {},
): Graph {
  const data = withLive(source, live, brands);
  const now = options.nowMs ?? Date.now();
  const pending = live.filter((r) => r.status === 'pending');
  const max = Math.max(1, ...data.clients.map((c) => c.requests), ...data.providers.map((p) => p.requests));
  const width = (requests: number) => 1.5 + 3.5 * Math.sqrt(Math.min(1, requests / max));
  const liveRange = data.range === 'live';
  const label = (perMinute: number, requests: number) =>
    liveRange ? t('traces.topology.perMinute', { count: perMinute < 10 ? perMinute.toFixed(1) : Math.round(perMinute) }) : t('traces.topology.count', { count: requests });

  // Which row each live request is on, and the rows where an attempt of it just failed.
  const startOf = (r: RequestSummary) => Date.parse(r.startedAtUtc);
  const rowsOf = (p: TraceProviderNode) => (p.accounts.length > 0 ? p.accounts.map((a) => accountRowId(a.id)) : [accountRowId(null)]);
  const providerById = new Map(data.providers.map((p) => [p.providerId, p]));
  const rowFor = (providerId: string | null | undefined, accountId: string | null | undefined): string | null => {
    const p = providerId ? providerById.get(providerId) : undefined;
    if (!p) return null;
    const rows = rowsOf(p);
    const wanted = accountRowId(accountId);
    return rows.includes(wanted) ? wanted : rows[0];
  };
  const on = new Map<string, string[]>(); // `${providerId}|${row}` → requests riding that row now
  const failed = new Map<string, string[]>(); // `${providerId}|${row}` → requests that just failed there
  const retrying = new Set<string>(); // rows a retry is heading to
  const push = (map: Map<string, string[]>, key: string, id: string) => map.set(key, [...(map.get(key) ?? []), id]);
  for (const r of live) {
    const attempts = r.attempts ?? [];
    const current = attempts.at(-1);
    const providerId = current?.providerId ?? r.providerId;
    const row = rowFor(providerId, current ? current.accountId : r.accountId);
    // A finished request still rides its line (its burst); a pending one only once it has a provider.
    if (row && providerId && (r.status !== 'pending' || !current || !attemptFailed(current))) push(on, `${providerId}|${row}`, r.id);
    attempts.forEach((a, i) => {
      const at = a.providerId ?? r.providerId;
      const failedRow = rowFor(at, a.accountId);
      if (!at || !failedRow) return;
      if (attemptFailed(a) && now - (startOf(r) + (a.endMs ?? 0)) < FAIL_SHOW_MS) push(failed, `${at}|${failedRow}`, r.id);
      if (i > 0 && r.status === 'pending' && (a.endMs === null || a.endMs === undefined) && !a.status) retrying.add(`${at}|${failedRow}`);
    });
  }

  // Dimming: a highlighted request lights its client and the rows its attempts went to.
  const hl = options.highlight;
  const hlRows = hl
    ? new Set(
        (hl.attempts.length > 0 ? hl.attempts : [{ providerId: hl.providerId, accountId: hl.accountId }])
          .map((a) => (a.providerId ? `${a.providerId}|${rowFor(a.providerId, a.accountId) ?? ''}` : ''))
          .filter(Boolean),
      )
    : null;
  const hlClient = hl ? clientNodeId(hl.clientKind) : null;

  const providerHeight = (p: TraceProviderNode) => PROVIDER_HEAD + rowsOf(p).length * ACCOUNT_ROW + 10;
  const clientsHeight = data.clients.length * ROW - (ROW - 60);
  const providersHeight = data.providers.reduce((sum, p) => sum + providerHeight(p), 0) + Math.max(0, data.providers.length - 1) * PROVIDER_GAP;
  const height = Math.max(clientsHeight, providersHeight, 60);

  const nodes: TraceNode[] = [{ id: HUB_ID, type: 'hub', position: { x: 404, y: height / 2 - 48 }, data: { busy: pending.length > 0, note: hubNote(live, now, data, t) } }];
  const edges: Edge[] = [];
  const clientTop = (height - clientsHeight) / 2;
  data.clients.forEach((c, i) => {
    const id = clientNodeId(c.clientKind);
    const mine = pending.filter((r) => (r.clientKind ?? null) === (c.clientKind ?? null));
    nodes.push({
      id,
      type: 'card',
      position: { x: 0, y: clientTop + i * ROW },
      style: { width: 184 },
      data: {
        title: c.clientKind ? clientLabel(c.clientKind) : t('traces.topology.direct'),
        lines: [c.activeSessions > 0 ? t('traces.topology.activeSessions', { count: c.activeSessions }) : t('traces.topology.idle')],
        tone: c.activeSessions > 0 || mine.length > 0 ? 'ok' : 'idle',
        client: c.clientKind ?? null,
        live: mine.length,
        status: clientStatus(c.clientKind ?? null, live, options.activity ?? [], now, t),
        dim: hlClient !== null && hlClient !== id,
      },
    });
    edges.push({
      ...edgeBase(`e:${id}`, id, HUB_ID, COLORS.flow),
      type: 'flow',
      markerEnd: undefined,
      label: label(c.perMinute, c.requests),
      style: { stroke: COLORS.flow, strokeWidth: width(c.requests) + (hlClient === id ? 1.5 : 0), opacity: hlClient !== null && hlClient !== id ? 0.25 : undefined },
      data: { role: 'client', requests: live.filter((r) => (r.clientKind ?? null) === (c.clientKind ?? null)).map((r) => r.id) } satisfies FlowData,
    });
  });

  let y = (height - providersHeight) / 2;
  for (const p of data.providers) {
    const id = providerNodeId(p.providerId);
    const cooling = p.accounts.filter((a) => a.status === 'cooldown').length;
    const warn = p.rateLimited > 0 || p.failovers > 0 || cooling > 0;
    const failing = p.requests > 0 && p.errors / p.requests > 0.2;
    const brand = brands.get(p.providerId) ?? { name: p.name, icon: p.icon, templateId: p.templateId };
    const rowIds = rowsOf(p);
    const multi = p.accounts.length > 1;
    const rows: AccountRow[] = rowIds.map((rowId, k) => {
      const account = p.accounts[k];
      const key = `${p.providerId}|${rowId}`;
      return {
        id: rowId,
        accountId: account?.id ?? null,
        name: account ? account.name : t('traces.topology.apiKey'),
        status: account ? account.status : 'ok',
        detail: account ? accountDetail(account, now, t) : p.attempts > 0 ? t('traces.account.attempts', { count: p.attempts }) : t('traces.account.ready'),
        attempts: account ? (account.attempts ?? 0) : p.attempts,
        errors: account ? (account.errors ?? 0) : p.errors,
        live: (on.get(key) ?? []).filter((r) => pending.some((x) => x.id === r)).length,
        hit: (failed.get(key)?.length ?? 0) > 0 ? 'fail' : (on.get(key) ?? []).some((r) => pending.some((x) => x.id === r)) ? 'on' : null,
        dim: hlRows !== null && !hlRows.has(key),
      };
    });
    const lines = [
      [
        p.accounts.length > 0 ? t('traces.topology.accounts', { count: p.accounts.length }) : t('traces.topology.apiKey'),
        cooling > 0 ? t('traces.topology.cooling', { count: cooling }) : null,
        multi ? label(p.perMinute, p.requests) : null,
      ]
        .filter(Boolean)
        .join(' · '),
    ];
    nodes.push({
      id,
      type: 'provider',
      position: { x: 760, y },
      style: { width: 248 },
      data: {
        title: p.name,
        lines,
        tone: failing ? 'error' : warn ? 'warn' : 'ok',
        provider: brand,
        rows,
        live: pending.filter((r) => (currentAttempt(r)?.providerId ?? r.providerId) === p.providerId).length,
        dim: hlRows !== null && !rows.some((r) => !r.dim),
      },
    });
    y += providerHeight(p) + PROVIDER_GAP;
    rows.forEach((row) => {
      const key = `${p.providerId}|${row.id}`;
      const volume = p.accounts.length > 0 ? row.attempts : p.requests;
      const rowWarn = row.status === 'cooldown' || row.hit === 'fail';
      const color = failing ? COLORS.error : warn && (rowWarn || !multi) ? COLORS.warn : COLORS.flow;
      const isHl = hlRows?.has(key) ?? false;
      edges.push({
        ...edgeBase(`e:${id}:${row.id}`, HUB_ID, id, color, { targetHandle: row.id }),
        type: 'flow',
        markerEnd: undefined,
        label: multi ? undefined : label(p.perMinute, p.requests),
        style: { stroke: color, strokeWidth: width(volume) + (isHl ? 1.5 : 0), opacity: hlRows !== null && !isHl ? 0.25 : undefined },
        data: {
          role: 'account',
          requests: on.get(key) ?? [],
          failed: failed.get(key) ?? [],
          warn,
          retry: retrying.has(key),
        } satisfies FlowData,
      });
    });
  }
  return { nodes, edges };
}

/** What an account row says on its right: why it is out, or what the upstream last reported of its quota. */
function accountDetail(a: TraceAccount, now: number, t: TFunction): string {
  if (a.status === 'cooldown' && a.cooldownUntilUtc) {
    const left = Date.parse(a.cooldownUntilUtc) - now;
    return left > 0 && left < 3_600_000 ? t('traces.account.cooldownIn', { time: formatMs(Math.ceil(left / 1000) * 1000) }) : t('traces.account.cooldown', { time: formatClock(a.cooldownUntilUtc) });
  }
  if (a.status !== 'ok') return t(`traces.account.${a.status}` as 'traces.account.ok');
  if (a.remainingRequests) return t('traces.account.remaining', { count: a.remainingRequests });
  return (a.attempts ?? 0) > 0 ? t('traces.account.attempts', { count: a.attempts ?? 0 }) : t('traces.account.ready');
}

const formatClock = (iso: string) => {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
};

/** The words above the hub: the switch a request just made (why, and where to), else where a request was just sent. */
export function hubNote(live: readonly RequestSummary[], now: number, data: TraceTopology, t: TFunction): HubData['note'] {
  const accountName = (providerId: string | null | undefined, accountId: string | null | undefined) => {
    const p = data.providers.find((x) => x.providerId === providerId);
    const a = accountId ? p?.accounts.find((x) => x.id === accountId) : undefined;
    return { provider: p?.name ?? providerId ?? '', account: a?.name ?? null };
  };
  let best: { at: number; note: NonNullable<HubData['note']> } | null = null;
  for (const r of live) {
    const start = Date.parse(r.startedAtUtc);
    const attempts = r.attempts ?? [];
    attempts.forEach((a, i) => {
      const next = attempts[i + 1];
      if (next && attemptFailed(a)) {
        const at = start + (a.endMs ?? next.startMs);
        if (now - at > NOTE_SHOW_MS || (best && best.at > at)) return;
        const to = accountName(next.providerId, next.accountId);
        const same = (next.providerId ?? r.providerId) === (a.providerId ?? r.providerId);
        const reason = switchReason(a.retry, t) ?? (a.status ? String(a.status) : t('traces.live.failed'));
        best = { at, note: { text: t('traces.hub.switch', { reason, target: same && to.account ? to.account : to.provider }), tone: 'warn' } };
      } else if (i === 0) {
        const at = start + a.startMs;
        if (now - at > NOTE_SHOW_MS / 2 || (best && best.at >= at)) return;
        const to = accountName(a.providerId ?? r.providerId, a.accountId);
        best = { at, note: { text: t('traces.hub.route', { target: to.account ? `${to.provider} · ${to.account}` : to.provider }), tone: 'info' } };
      }
    });
  }
  return (best as { note: NonNullable<HubData['note']> } | null)?.note ?? null;
}

/**
 * What a client's agent is doing, on its card: the call in flight (what the model produces), else — for a moment after a
 * call that asked for tools — running those tools.
 */
function clientStatus(kind: string | null, live: readonly RequestSummary[], activity: readonly TraceActivity[], now: number, t: TFunction): CardData['status'] {
  const mine = live.filter((r) => (r.clientKind ?? null) === kind);
  const running = mine.filter((r) => r.status === 'pending').sort((a, b) => (a.startedAtUtc < b.startedAtUtc ? 1 : -1))[0];
  if (running) {
    const stage = running.stage ?? '';
    if (stage.startsWith('tool:')) return { text: t('traces.agent.tool', { name: stage.slice('tool:'.length) }), tone: 'tool' };
    if (stage === 'thinking') return { text: t('traces.agent.thinking'), tone: 'think' };
    if (stage === 'writing') return { text: t('traces.agent.writing'), tone: 'write' };
    const current = currentAttempt(running);
    if (current && attemptFailed(current)) return { text: t('traces.agent.retrying'), tone: 'warn' };
    return { text: running.httpStatus ? t('traces.agent.waiting') : t('traces.agent.upstream'), tone: 'wait' };
  }
  // The latest finished call of this client, from the live feed (not yet stored) or the activity feed.
  const finished = [
    ...mine.filter((r) => r.status !== 'pending').map((r) => ({ end: Date.parse(r.startedAtUtc) + (r.totalMs ?? 0), ok: r.status === 'success', tools: liveTools(r) })),
    ...activity
      .filter((a) => (a.request.clientKind ?? null) === kind && a.request.callKind !== 'aux')
      .map((a) => ({ end: Date.parse(a.request.startedAtUtc) + (a.request.totalMs ?? 0), ok: a.request.status === 'success', tools: a.finishReason === 'tool_calls' || a.toolCallCount > 0 ? a.toolCalls.map((c) => c.name) : [] })),
  ].sort((a, b) => b.end - a.end)[0];
  if (!finished || now - finished.end > 120_000 || !finished.ok || finished.tools.length === 0) return null;
  return { text: t('traces.agent.tools', { names: toolSummary(finished.tools) }), tone: 'run' };
}

/** Tool names a live call produced (its `tool:<name>` phases). */
export function liveTools(r: Pick<RequestSummary, 'phases'>): string[] {
  return (r.phases ?? '')
    .split(',')
    .map((p) => p.slice(0, p.lastIndexOf('@')))
    .filter((s) => s.startsWith('tool:'))
    .map((s) => s.slice('tool:'.length))
    .filter(Boolean);
}

/** The aggregates plus a zero row for every client / provider an in-flight request uses that the window has not seen. */
function withLive(data: TraceTopology, live: readonly RequestSummary[], brands: Map<string, ProviderBrand>): TraceTopology {
  const clients = [...data.clients];
  const providers = [...data.providers];
  for (const r of live) {
    if (!clients.some((c) => (c.clientKind ?? null) === (r.clientKind ?? null)))
      clients.push({ clientKind: r.clientKind ?? null, requests: 0, errors: 0, perMinute: 0, activeSessions: 0 });
    const used = new Set([r.providerId, ...(r.attempts ?? []).map((a) => a.providerId)].filter((p): p is string => !!p));
    for (const providerId of used) {
      if (providers.some((p) => p.providerId === providerId)) continue;
      const brand = brands.get(providerId);
      const named = (r.attempts ?? []).find((a) => a.providerId === providerId)?.providerName;
      providers.push({
        providerId,
        name: brand?.name ?? named ?? (providerId === r.providerId ? r.providerName : null) ?? providerId,
        icon: brand?.icon,
        templateId: brand?.templateId,
        requests: 0,
        errors: 0,
        attempts: 0,
        rateLimited: 0,
        failovers: 0,
        perMinute: 0,
        accounts: [],
      });
    }
  }
  return clients.length === data.clients.length && providers.length === data.providers.length ? data : { ...data, clients, providers };
}

// ------------------------------------------------------------------ B · one request

export interface TimeSplit {
  /** Everything before the first upstream attempt, including a smart-routing judge. */
  astraUs: number;
  /** The judge call, already included in `astraUs`. 0 when no judge ran (a pin, a turn, or no routing). */
  routingUs: number;
  retryUs: number;
  ttfbUs: number;
  firstTokenUs: number;
  generationUs: number;
  tailUs: number;
  totalUs: number;
}

/** Where one request's time went: Astra itself, the routing judge, wasted attempts, upstream first byte, first token, generation. */
export function splitRequestTime(spans: TraceSpan[]): TimeSplit | null {
  const root = spans.find((s) => s.name === SPAN.request);
  const attempts = spans.filter((s) => s.name === SPAN.attempt).sort((a, b) => a.startUs - b.startUs);
  if (!root || attempts.length === 0) return null;
  const first = attempts[0];
  const last = attempts[attempts.length - 1];
  const headersAt = last.startUs + last.durationUs;
  const response = spans.find((s) => s.name === SPAN.response);
  const firstToken = response?.events?.find((e) => e.name === 'first_token')?.offset_us;
  const responseEnd = response ? response.startUs + response.durationUs : headersAt;
  const tokenAt = firstToken !== undefined && firstToken >= headersAt ? firstToken : headersAt;
  const routing = routeDecision(spans);
  return {
    astraUs: first.startUs,
    routingUs: Math.min(first.startUs, judgeUsOn(routing?.span, routing)),
    retryUs: Math.max(0, last.startUs - first.startUs),
    ttfbUs: last.durationUs,
    firstTokenUs: Math.max(0, tokenAt - headersAt),
    generationUs: Math.max(0, responseEnd - tokenAt),
    tailUs: Math.max(0, root.durationUs - responseEnd),
    totalUs: root.durationUs,
  };
}

const PITCH = 170;

/** The request leg left to right (entry … attempts → provider) and the response leg back underneath. */
export function buildRequest(
  trace: TraceRequest,
  t: TFunction,
  clientLabel: (kind: string | null | undefined) => string,
  brand: ProviderBrand | null,
): Graph {
  const r = trace.request;
  const byName = (name: string) => trace.spans.find((s) => s.name === name);
  // A provider-pool failover resolves and encodes once per member tried: the last ones describe what answered.
  const lastByName = (name: string) => trace.spans.filter((s) => s.name === name).sort((a, b) => a.startUs - b.startUs).at(-1);
  const route = byName(SPAN.route);
  const body = byName(SPAN.body);
  const middleware = byName(SPAN.middleware);
  const privacy = byName(SPAN.privacy);
  const resolve = lastByName(SPAN.resolve);
  const encode = lastByName(SPAN.encode);
  const response = byName(SPAN.response);
  const attempts = trace.spans.filter((s) => s.name === SPAN.attempt).sort((a, b) => a.startUs - b.startUs);
  const tone = (s: TraceSpan | undefined): Tone => (s?.status === 'error' ? 'error' : 'neutral');
  const nodes: TraceNode[] = [];
  const edges: Edge[] = [];
  const rowY = 100;

  nodes.push({
    id: 'agent',
    type: 'card',
    position: { x: -56, y: rowY },
    style: { width: 168 },
    data: { title: r.clientKind ? clientLabel(r.clientKind) : t('traces.topology.direct'), lines: [`${r.inboundProtocol}${r.stream ? ' · stream' : ''}`], client: r.clientKind },
  });
  const chain: { id: string; x: number }[] = [];
  let x = 170;
  const stage = (id: string, span: TraceSpan | undefined, title: string, lines: string[], toneOverride?: Tone) => {
    if (!span) return;
    nodes.push({ id, type: 'card', position: { x, y: rowY }, style: { width: 140 }, data: { title, lines, tone: toneOverride ?? tone(span) } });
    chain.push({ id, x });
    x += PITCH;
  };
  if (route) {
    const entry = { ...route, durationUs: route.durationUs + (body?.durationUs ?? 0) };
    stage('route', entry, t('traces.node.entry'), [t('traces.node.entryDetail'), formatUs(entry.durationUs)]);
  }
  stage('middleware', middleware, t('traces.node.middleware'), [
    attr(middleware, 'astra.middleware.rewritten') ? t('traces.node.rewritten') : t('traces.node.unchanged'),
    formatUs(middleware?.durationUs),
  ]);
  stage('privacy', privacy, t('traces.node.privacy'), [
    t('traces.node.hits', { count: attrNum(privacy, 'astra.privacy.hits') ?? 0 }),
    formatUs(privacy?.durationUs),
  ]);
  // Smart routing is attributes on the resolve span, not a span of its own (plan §5.9). Drawn between the guard and
  // the resolve, and its judge time is taken back out of the resolve node so the two don't both claim it.
  const decision = routeDecision(trace.spans);
  if (decision) {
    const sourceLabel = routeSourceLabel(decision.source, t);
    const warn = decision.source === 'fallback' || decision.source === 'breaker' || !!decision.adjustedFrom;
    stage(
      'routing',
      decision.span,
      t('traces.node.routing'),
      [decision.grade ?? '—', decision.judgeMs > 0 ? `${sourceLabel} · ${formatUs(Math.round(decision.judgeMs * 1000))}` : sourceLabel],
      warn ? 'warn' : 'ok',
    );
  }
  const judged = judgeUsOn(resolve, decision);
  stage('resolve', resolve, t('traces.node.resolve'), [
    attrStr(resolve, 'astra.provider.name') ?? r.providerName ?? '—',
    formatUs(resolve ? Math.max(0, resolve.durationUs - judged) : undefined),
  ]);
  const passthrough = attr(encode, 'astra.passthrough') === true || r.passthrough;
  stage('encode', encode, passthrough ? t('traces.node.passthrough') : t('traces.node.encode'), [
    passthrough ? (r.upstreamProtocol ?? r.inboundProtocol) : `→ ${r.upstreamProtocol ?? '—'}`,
    formatUs(encode?.durationUs),
  ]);

  const center = rowY + 32;
  const attemptX = x + 20;
  const providerX = attemptX + 200;
  const providerHeight = Math.max(150, attempts.length * 80);
  // Attempts of a provider pool can go to several providers (issue #2): the one that answered is the main node, the
  // others failed over and sit above it.
  const attemptProvider = (a: TraceSpan) => attrStr(a, 'astra.provider.id') ?? r.providerId ?? null;
  const others = [...new Set(attempts.map(attemptProvider).filter((p): p is string => !!p && p !== r.providerId))];
  const providerNode = (a: TraceSpan) => {
    const p = attemptProvider(a);
    return p && p !== r.providerId ? `provider:${p}` : 'provider';
  };
  others.forEach((p, k) => {
    const named = attempts.find((a) => attemptProvider(a) === p);
    nodes.push({
      id: `provider:${p}`,
      type: 'card',
      position: { x: providerX, y: center - providerHeight / 2 - 96 * (k + 1) },
      style: { width: 168 },
      data: { title: attrStr(named, 'astra.provider.name') ?? p, lines: [t('traces.node.failedOver')], tone: 'error' },
    });
  });
  attempts.forEach((a, i) => {
    const y = center - 30 + (i - (attempts.length - 1) / 2) * 80;
    const status = attrNum(a, 'http.response.status_code');
    const failed = a.status === 'error';
    nodes.push({
      id: `attempt:${i}`,
      type: 'card',
      position: { x: attemptX, y },
      style: { width: 136 },
      data: {
        title: t('traces.node.attempt', { n: i + 1 }),
        lines: [
          (others.length > 0 ? attrStr(a, 'astra.provider.name') : null) ?? attrStr(a, 'astra.account.id') ?? '—',
          `${status ?? '—'} · ${formatUs(a.durationUs)}`,
        ],
        tone: failed ? 'error' : 'ok',
      },
    });
    const last = chain[chain.length - 1];
    edges.push(edgeBase(`e:fork:${i}`, last ? last.id : 'agent', `attempt:${i}`, COLORS.request));
    const color = failed ? COLORS.error : COLORS.ok;
    edges.push(
      edgeBase(`e:attempt:${i}`, `attempt:${i}`, providerNode(a), color, {
        label: status ? String(status) : undefined,
        style: { stroke: color, strokeWidth: 1.75, strokeDasharray: failed ? '5 4' : undefined },
      }),
    );
  });
  nodes.push({
    id: 'provider',
    type: 'card',
    position: { x: providerX, y: center - providerHeight / 2 },
    style: { width: 168, height: providerHeight },
    data: {
      title: brand?.name ?? r.providerName ?? '—',
      lines: [r.upstreamModel ?? r.requestedModel ?? '—', r.finishReason ? `finish: ${r.finishReason}` : '', r.upstreamRequestId ? `req-id …${r.upstreamRequestId.slice(-6)}` : ''].filter(Boolean),
      provider: brand,
      stacked: true,
      tone: r.status === 'success' ? 'neutral' : 'error',
    },
  });
  // request leg: agent → stages
  [{ id: 'agent', x: -56 }, ...chain].forEach((n, i, all) => {
    const next = all[i + 1];
    if (next) edges.push(edgeBase(`e:req:${n.id}`, n.id, next.id, COLORS.request));
  });
  if (attempts.length === 0) {
    const last = chain[chain.length - 1];
    if (last) edges.push(edgeBase('e:req:provider', last.id, 'provider', COLORS.request));
  }

  // response leg, right to left under the stages it mirrors
  const backY = 340;
  const back: { id: string }[] = [];
  const responseNode = (id: string, atX: number, title: string, lines: string[]) => {
    nodes.push({ id, type: 'card', position: { x: atX, y: backY }, style: { width: 136 }, data: { title, lines, tone: 'neutral' } });
    back.push({ id });
  };
  if (response) {
    responseNode('decode', attemptX, t('traces.node.decode'), [
      t('traces.node.maxGap', { ms: formatMs(attrNum(response, 'astra.response.max_gap_ms') ?? 0) }),
      formatUs(response.durationUs),
    ]);
    const at = (id: string) => chain.find((c) => c.id === id)?.x;
    if (!passthrough) responseNode('reencode', at('encode') ?? attemptX - PITCH, t('traces.node.reencode'), [`→ ${r.inboundProtocol}`]);
    const restored = attrNum(response, 'astra.restore.placeholders') ?? 0;
    if (restored > 0) responseNode('restore', at('privacy') ?? attemptX - 2 * PITCH, t('traces.node.restore'), [t('traces.node.restored', { count: restored })]);
    if (attr(response, 'astra.middleware.response') === true)
      responseNode('middlewareResponse', at('middleware') ?? 170, t('traces.node.middlewareResponse'), [t('traces.node.unchanged')]);
    edges.push(edgeBase('e:res:provider', 'provider', 'decode', COLORS.response, {
      sourceHandle: 'bottom',
      targetHandle: 'top',
      label: attrNum(response, 'astra.response.events') ? t('traces.node.events', { count: attrNum(response, 'astra.response.events') ?? 0 }) : undefined,
    }));
    back.forEach((n, i) => {
      const next = back[i + 1];
      edges.push(edgeBase(`e:res:${n.id}`, n.id, next ? next.id : 'agent', COLORS.response, {
        sourceHandle: 'left',
        targetHandle: next ? 'right' : 'bottom',
      }));
    });
  }
  return { nodes, edges };
}

// ------------------------------------------------------------------ C · one turn of a session

export interface TurnTime {
  modelMs: number;
  toolMs: number;
  retryMs: number;
  overheadMs: number;
  totalMs: number;
}

const startMs = (c: TraceCall) => Date.parse(c.startedAtUtc);
const endMs = (c: TraceCall) => startMs(c) + (c.totalMs ?? 0);

/** The spans of one call, keyed by request id. */
export function spansByRequest(spans: TraceSpan[]): Map<string, TraceSpan[]> {
  const map = new Map<string, TraceSpan[]>();
  for (const s of spans) {
    const list = map.get(s.requestId);
    if (list) list.push(s);
    else map.set(s.requestId, [s]);
  }
  return map;
}

/** Where a turn's time went: model calls, the agent's own time between them (tools / waiting), wasted retries, Astra. */
export function splitTurnTime(calls: TraceCall[], spans: TraceSpan[]): TurnTime {
  const by = spansByRequest(spans);
  const ordered = [...calls].sort((a, b) => startMs(a) - startMs(b));
  if (ordered.length === 0) return { modelMs: 0, toolMs: 0, retryMs: 0, overheadMs: 0, totalMs: 0 };
  const begin = startMs(ordered[0]);
  const end = Math.max(...ordered.map(endMs));
  let retryUs = 0;
  let overheadUs = 0;
  for (const c of ordered) {
    const split = splitRequestTime(by.get(c.id) ?? []);
    if (!split) continue;
    retryUs += split.retryUs;
    overheadUs += split.astraUs;
  }
  // Time covered by at least one call (main and sub-agent calls overlap); the rest is the agent's own.
  let busy = 0;
  let cursor = begin;
  for (const c of ordered) {
    const s = Math.max(startMs(c), cursor);
    const e = endMs(c);
    if (e > s) busy += e - s;
    cursor = Math.max(cursor, e);
  }
  const retryMs = retryUs / 1000;
  return { modelMs: Math.max(0, busy - retryMs), toolMs: Math.max(0, end - begin - busy), retryMs, overheadMs: overheadUs / 1000, totalMs: end - begin };
}


// ------------------------------------------------------------------ the model's own phases inside a call

export type PhaseKind = 'wait' | 'think' | 'write' | 'tool';

/** The model's phases: timeline bars, time bars, the agent's status. */
export const PHASE_COLOR: Record<PhaseKind, string> = {
  wait: 'color-mix(in oklab, var(--label-3) 70%, transparent)',
  think: 'var(--accent-2)',
  write: 'var(--accent)',
  tool: 'var(--green)',
};

export interface CallPhases {
  /** From arrival to the first output: Astra, the upstream's queue and first byte. */
  waitMs: number;
  thinkMs: number;
  writeMs: number;
  /** From the first tool call starting to the end of the response (its arguments streaming). */
  toolMs: number;
  /** Bars on the call's own time axis (ms from its start). */
  segments: { kind: PhaseKind; from: number; to: number }[];
  tools: string[];
}

/** When the model waited, thought, wrote and called tools in one call (the response span's events). */
export function callPhases(spans: TraceSpan[]): CallPhases | null {
  const response = spans.find((s) => s.name === SPAN.response);
  if (!response) return null;
  const ev = (name: string) => {
    const e = response.events?.find((x) => x.name === name);
    return e ? e.offset_us / 1000 : undefined;
  };
  const toolEvents = (response.events ?? []).filter((e) => e.name === 'tool_call');
  const end = (response.startUs + response.durationUs) / 1000;
  const rs = ev('reasoning_start');
  const re = ev('reasoning_end');
  const ts = ev('text_start');
  const te = ev('text_end');
  const firstTool = toolEvents.length > 0 ? toolEvents[0].offset_us / 1000 : undefined;
  const outputs = [rs, ts, firstTool, ev('first_token')].filter((v): v is number => v !== undefined);
  const firstOutput = outputs.length > 0 ? Math.min(...outputs) : response.startUs / 1000;
  const segments: CallPhases['segments'] = [{ kind: 'wait', from: 0, to: firstOutput }];
  if (rs !== undefined && re !== undefined) segments.push({ kind: 'think', from: rs, to: Math.max(re, rs) });
  if (ts !== undefined && te !== undefined) segments.push({ kind: 'write', from: ts, to: Math.max(te, ts) });
  if (firstTool !== undefined) segments.push({ kind: 'tool', from: firstTool, to: Math.max(end, firstTool) });
  const names = toolEvents.map((e) => String(e.attributes?.name ?? '')).filter(Boolean);
  const fromAttr = attrStr(response, 'astra.response.tool_names');
  return {
    waitMs: firstOutput,
    thinkMs: rs !== undefined && re !== undefined ? Math.max(0, re - rs) : 0,
    writeMs: ts !== undefined && te !== undefined ? Math.max(0, te - ts) : 0,
    toolMs: firstTool !== undefined ? Math.max(0, end - firstTool) : 0,
    segments,
    tools: names.length > 0 ? names : fromAttr ? fromAttr.split(',') : [],
  };
}

/** A call in flight as phase bars (ms from its start), from the live feed's stage start times. */
export function livePhases(r: Pick<RequestSummary, 'phases' | 'startedAtUtc' | 'totalMs'>, nowMs: number): CallPhases['segments'] {
  const end = r.totalMs ?? Math.max(0, nowMs - Date.parse(r.startedAtUtc));
  const marks = (r.phases ?? '')
    .split(',')
    .map((p) => ({ stage: p.slice(0, p.lastIndexOf('@')), at: Number(p.slice(p.lastIndexOf('@') + 1)) }))
    .filter((m) => m.stage && Number.isFinite(m.at));
  const kind = (stage: string): PhaseKind => (stage === 'thinking' ? 'think' : stage === 'writing' ? 'write' : stage.startsWith('tool:') ? 'tool' : 'wait');
  const segments: CallPhases['segments'] = [{ kind: 'wait', from: 0, to: Math.min(marks[0]?.at ?? end, end) }];
  marks.forEach((m, i) => segments.push({ kind: kind(m.stage), from: m.at, to: Math.max(m.at, marks[i + 1]?.at ?? end) }));
  return segments;
}

/** The model's phases summed over a turn's calls (retries taken out of the wait), for the turn's time bar. */
export function turnPhases(calls: TraceCall[], spans: TraceSpan[]): Record<PhaseKind, number> {
  const by = spansByRequest(spans);
  const sum: Record<PhaseKind, number> = { wait: 0, think: 0, write: 0, tool: 0 };
  for (const c of calls) {
    const own = by.get(c.id) ?? [];
    const p = callPhases(own);
    if (!p) continue;
    sum.wait += Math.max(0, p.waitMs - (splitRequestTime(own)?.retryUs ?? 0) / 1000);
    sum.think += p.thinkMs;
    sum.write += p.writeMs;
    sum.tool += p.toolMs;
  }
  return sum;
}

/** "Read×2 · Grep" from a list of tool names. */
export function toolSummary(names: string[]): string {
  const counts = new Map<string, number>();
  for (const n of names) counts.set(n, (counts.get(n) ?? 0) + 1);
  return [...counts].map(([n, k]) => (k > 1 ? `${n}×${k}` : n)).join(' · ');
}

/** "思考 2.1 s · 输出 1.3 s" — only the phases that took time. */
export function phaseLine(p: CallPhases | null, t: TFunction): string | null {
  if (!p) return null;
  const parts = [
    p.thinkMs >= 50 ? `${t('traces.phase.think')} ${formatMs(p.thinkMs)}` : null,
    p.writeMs >= 50 ? `${t('traces.phase.write')} ${formatMs(p.writeMs)}` : null,
    p.toolMs >= 50 ? `${t('traces.phase.tool')} ${formatMs(p.toolMs)}` : null,
  ].filter(Boolean);
  return parts.length > 0 ? parts.join(' · ') : null;
}

// ------------------------------------------------------------------ anomalies

export type Anomaly = 'error' | 'retry' | 'slow';

/** Calls worth a look: failed, retried, or more than twice the turn's average (and over 3 s). */
export function findAnomalies(calls: TraceCall[], spans: TraceSpan[]): Map<string, Anomaly> {
  const by = spansByRequest(spans);
  const timed = calls.filter((c) => c.totalMs !== null && c.totalMs !== undefined);
  const mean = timed.length > 0 ? timed.reduce((s, c) => s + (c.totalMs ?? 0), 0) / timed.length : 0;
  const out = new Map<string, Anomaly>();
  for (const c of calls) {
    if (c.status !== 'success') out.set(c.id, 'error');
    else if ((by.get(c.id) ?? []).filter((s) => s.name === SPAN.attempt).length > 1) out.set(c.id, 'retry');
    else if (timed.length >= 3 && (c.totalMs ?? 0) > Math.max(3000, 2 * mean)) out.set(c.id, 'slow');
  }
  return out;
}

// ------------------------------------------------------------------ C · one turn as a canvas

export interface SessionOptions {
  /** Steps per row before the main chain wraps. */
  columns: number;
  /** Fold runs of unremarkable steps into groups once the turn is long. */
  collapse: boolean;
  /** Groups the user opened. */
  expanded: ReadonlySet<string>;
  /** Calls of this session from the live feed: those not among <c>calls</c> yet are drawn after the last step. */
  pending?: readonly RequestSummary[];
}

export interface StepData extends Record<string, unknown> {
  /** Tools the agent ran between the two calls, and the gap (the full text: tooltip, accessible name). */
  label?: string;
  /** "Read×2 · Grep" — the label's first line. */
  tools?: string | null;
  /** The gap between the calls — the label's second line, never cut off. */
  gap?: string | null;
  /** What clicking the label selects (the tool gap's inspector). */
  selectId?: string;
  /** The line from the end of a row back to the start of the next one. */
  wrap?: { rightX: number; gapY: number; leftX: number };
}

export interface SessionGraph extends Graph {
  /** Node ids of the anomalies, in order. */
  anomalies: string[];
}

const STEP_W = 210;
export const STEP_PITCH = 340;
const LANE_H = 118;
const SIDE_PITCH = STEP_PITCH;
/** Calls in flight at once (parallel sub-agents) stack this far apart. */
const PENDING_PITCH = 84;
/** A turn with more main steps than this folds its unremarkable runs. */
export const COLLAPSE_AFTER = 8;

type Item = { id: string; calls: TraceCall[]; group: boolean };

/**
 * The main agent wraps into rows like text; each sub-agent / side-call chain gets a lane under the row of the step that
 * started it. Tools run between two calls are written on the line joining them; long unremarkable runs fold into groups.
 */
export function buildSession(
  calls: TraceCall[],
  spans: TraceSpan[],
  t: TFunction,
  brands: Map<string, ProviderBrand>,
  options: SessionOptions = { columns: 4, collapse: true, expanded: new Set() },
): SessionGraph {
  const by = spansByRequest(spans);
  const ordered = [...calls].sort((a, b) => startMs(a) - startMs(b));
  const byId = new Map(ordered.map((c) => [c.id, c]));
  const isSide = (c: TraceCall) => c.callKind === 'sidechain' || c.callKind === 'aux';
  const main = ordered.filter((c) => !isSide(c));
  const side = ordered.filter(isSide);
  const anomalies = findAnomalies(ordered, spans);
  const tries = (c: TraceCall) => (by.get(c.id) ?? []).filter((s) => s.name === SPAN.attempt).length;
  const columns = Math.max(1, options.columns);

  // the main call a side chain hangs off (sub-agents link through one another)
  const anchorOf = (c: TraceCall): string | undefined => {
    let cur: TraceCall | undefined = c;
    for (let i = 0; cur && i < 64; i++) {
      const parent: TraceCall | undefined = cur.parentRequestId ? byId.get(cur.parentRequestId) : undefined;
      if (!parent) return undefined;
      if (!isSide(parent)) return parent.id;
      cur = parent;
    }
    return undefined;
  };
  const anchors = new Set(side.map(anchorOf).filter((x): x is string => Boolean(x)));

  // items: single steps, or folded runs of unremarkable ones
  const items: Item[] = [];
  const keep = (c: TraceCall, i: number) =>
    !options.collapse || main.length <= COLLAPSE_AFTER || i === 0 || i >= main.length - 2 || anomalies.has(c.id) || anchors.has(c.id);
  let run: TraceCall[] = [];
  const flush = () => {
    const id = run.length > 0 ? `group:${run[0].id}` : '';
    if (run.length >= 3 && !options.expanded.has(id)) items.push({ id, calls: run, group: true });
    else for (const c of run) items.push({ id: c.id, calls: [c], group: false });
    run = [];
  };
  main.forEach((c, i) => {
    if (keep(c, i)) {
      flush();
      items.push({ id: c.id, calls: [c], group: false });
    } else run.push(c);
  });
  flush();
  const itemOf = new Map<string, number>();
  items.forEach((item, i) => item.calls.forEach((c) => itemOf.set(c.id, i)));
  // in flight (or finished but not stored yet): stacked in the slot after the last step
  const pending = (options.pending ?? []).filter((r) => !byId.has(r.id));
  const slots = items.length + (pending.length > 0 ? 1 : 0);
  const pendingRow = Math.floor(items.length / columns);

  // side chains: one lane each, under the row of their anchor
  const chains = new Map<string, TraceCall[]>();
  for (const c of side) {
    const key = `${c.callKind}:${c.chainKey ?? c.id}:${anchorOf(c) ?? ''}`;
    const list = chains.get(key);
    if (list) list.push(c);
    else chains.set(key, [c]);
  }
  const rows = Math.max(1, Math.ceil(slots / columns));
  const lanesOfRow: { key: string; anchorItem: number }[][] = Array.from({ length: rows }, () => []);
  for (const [key, list] of chains) {
    const anchor = anchorOf(list[0]);
    const index = anchor !== undefined ? (itemOf.get(anchor) ?? 0) : 0;
    lanesOfRow[Math.floor(index / columns)].push({ key, anchorItem: index });
  }
  const rowTop: number[] = [];
  let y = 0;
  const stackExtra = (r: number) => (pending.length > 1 && r === pendingRow ? (pending.length - 1) * PENDING_PITCH : 0);
  for (let r = 0; r < rows; r++) {
    rowTop.push(y);
    y += 96 + stackExtra(r) + lanesOfRow[r].length * LANE_H + 70;
  }
  const rowRight = (r: number) => (Math.min(columns, items.length - r * columns) - 1) * STEP_PITCH + STEP_W;

  const nodes: TraceNode[] = [];
  const edges: Edge[] = [];
  const flag = (id: string): CardData['flag'] => {
    const a = anomalies.get(id);
    return a ? { text: t(`traces.flag.${a}`), tone: a === 'error' ? 'error' : 'warn' } : undefined;
  };
  const card = (c: TraceCall, x: number, cy: number, sub: boolean) => {
    const brand = c.providerId ? (brands.get(c.providerId) ?? { name: c.providerName ?? '' }) : null;
    const n = tries(c);
    const phases = phaseLine(callPhases(by.get(c.id) ?? []), t);
    const routed = routeDecision(by.get(c.id) ?? []);
    nodes.push({
      id: c.id,
      type: 'card',
      position: { x, y: cy },
      style: { width: STEP_W },
      data: {
        title: `${sub ? (c.callKind === 'sidechain' ? t('traces.kind.sidechain') : t('traces.kind.aux')) : 'LLM'} #${c.step ?? 1} · ${shortModel(c.upstreamModel ?? c.requestedModel)}`,
        lines: [
          `${formatMs(c.totalMs)} · ${formatTokens(c.totalInputTokens, true)}→${formatTokens(c.totalOutputTokens, true)}${n > 1 ? ` · ${t('traces.session.tries', { count: n })}` : ''}`,
          ...(routed ? [t('traces.session.routing', { grade: routed.grade ?? '—' })] : []),
          ...(phases ? [phases] : []),
        ],
        tone: c.status !== 'success' ? 'error' : n > 1 ? 'warn' : sub ? 'sub' : 'neutral',
        provider: brand,
        flag: flag(c.id),
      },
    });
  };
  const toolLabel = (from: TraceCall, to: TraceCall): Pick<StepData, 'label' | 'tools' | 'gap'> => {
    const ms = Math.max(0, startMs(to) - endMs(from));
    const gap = ms > 0 ? formatMs(ms) : null;
    if (from.toolCalls === 0) return { label: gap ?? undefined, tools: null, gap };
    const names = callPhases(by.get(from.id) ?? [])?.tools ?? [];
    const tools = names.length > 0 ? toolSummary(names) : t('traces.session.toolCount', { count: from.toolCalls });
    return { label: gap ? `${tools} · ${gap}` : tools, tools, gap };
  };
  const stepEdge = (id: string, source: string, target: string, from: TraceCall, to: TraceCall, extra: Partial<Edge> = {}, wrap?: StepData['wrap']): Edge => {
    const dashed = to.linkSource === 'session';
    return {
      id,
      source,
      target,
      sourceHandle: 'right',
      targetHandle: 'left',
      type: 'step',
      markerEnd: { type: MarkerType.ArrowClosed, color: COLORS.request, width: 16, height: 16 },
      style: { stroke: COLORS.request, strokeWidth: 1.75, strokeDasharray: dashed ? '5 4' : undefined },
      data: { ...toolLabel(from, to), selectId: from.toolCalls > 0 ? `tool:${to.id}` : undefined, wrap } satisfies StepData,
      ...extra,
    };
  };

  items.forEach((item, i) => {
    const row = Math.floor(i / columns);
    const x = (i % columns) * STEP_PITCH;
    const top = rowTop[row];
    if (item.group) {
      const first = item.calls[0];
      const last = item.calls[item.calls.length - 1];
      const total = item.calls.reduce((s, c) => s + (c.totalMs ?? 0), 0);
      const toolCount = item.calls.reduce((s, c) => s + c.toolCalls, 0);
      nodes.push({
        id: item.id,
        type: 'card',
        position: { x, y: top },
        style: { width: STEP_W },
        data: {
          title: `LLM #${first.step ?? 1}–#${last.step ?? 1}`,
          lines: [t('traces.session.groupCalls', { count: item.calls.length, ms: formatMs(total) }), t('traces.session.groupTools', { count: toolCount })],
          group: true,
          provider: first.providerId ? (brands.get(first.providerId) ?? { name: first.providerName ?? '' }) : null,
        },
      });
    } else card(item.calls[0], x, top, false);
    const next = items[i + 1];
    if (!next) return;
    const from = item.calls[item.calls.length - 1];
    const to = next.calls[0];
    const nextRow = Math.floor((i + 1) / columns);
    edges.push(
      stepEdge(`e:${item.id}->${next.id}`, item.id, next.id, from, to, {},
        nextRow !== row ? { rightX: rowRight(row) + 44, gapY: rowTop[nextRow] - 36, leftX: -44 } : undefined),
    );
  });

  // side lanes
  lanesOfRow.forEach((lanes, row) =>
    lanes.forEach(({ key, anchorItem }, lane) => {
      const list = chains.get(key)!;
      const laneY = rowTop[row] + 96 + stackExtra(row) + 22 + lane * LANE_H;
      let x = (anchorItem % columns) * STEP_PITCH;
      list.forEach((c, k) => {
        card(c, x, laneY, true);
        if (k === 0) {
          edges.push({
            ...edgeBase(`e:side:${c.id}`, items[anchorItem].id, c.id, COLORS.inferred),
            sourceHandle: 'bottom',
            targetHandle: 'top',
            label: t(c.callKind === 'sidechain' ? 'traces.session.inferredSub' : 'traces.session.inferredAux'),
            style: { stroke: COLORS.inferred, strokeWidth: 1.75, strokeDasharray: '5 4' },
            markerEnd: { type: MarkerType.ArrowClosed, color: COLORS.inferred, width: 16, height: 16 },
          });
        } else edges.push(stepEdge(`e:${list[k - 1].id}->${c.id}`, list[k - 1].id, c.id, list[k - 1], c));
        x += SIDE_PITCH;
      });
    }),
  );

  // the calls in flight, joined by a moving line to the call before them (else the last step)
  const last = items[items.length - 1];
  const lastRow = Math.floor((items.length - 1) / columns);
  const sourceOf = (r: RequestSummary): { id: string; call: TraceCall; wraps: boolean } | undefined => {
    const parent = r.parentRequestId ? byId.get(r.parentRequestId) : undefined;
    if (parent) {
      const at = itemOf.get(parent.id);
      return { id: at !== undefined ? items[at].id : parent.id, call: parent, wraps: at === items.length - 1 };
    }
    return last ? { id: last.id, call: last.calls[last.calls.length - 1], wraps: true } : undefined;
  };
  pending.forEach((r, k) => {
    const brand = r.providerId ? (brands.get(r.providerId) ?? { name: r.providerName ?? '' }) : null;
    nodes.push({
      id: r.id,
      type: 'card',
      position: { x: (items.length % columns) * STEP_PITCH, y: rowTop[pendingRow] + k * PENDING_PITCH },
      style: { width: STEP_W },
      data: { title: `LLM · ${shortModel(r.upstreamModel ?? r.requestedModel)}`, lines: [], provider: brand, pending: r.id },
    });
    const source = sourceOf(r);
    if (!source) return;
    const from = source.call;
    const tools = from.toolCalls > 0 ? (callPhases(by.get(from.id) ?? [])?.tools ?? []) : [];
    const label = tools.length > 0 ? toolSummary(tools) : from.toolCalls > 0 ? t('traces.session.toolCount', { count: from.toolCalls }) : null;
    edges.push({
      id: `e:${source.id}->${r.id}`,
      source: source.id,
      target: r.id,
      sourceHandle: 'right',
      targetHandle: 'left',
      type: 'step',
      animated: true,
      markerEnd: { type: MarkerType.ArrowClosed, color: COLORS.response, width: 16, height: 16 },
      style: { stroke: COLORS.response, strokeWidth: 1.75 },
      data: {
        label: label ?? undefined,
        tools: label,
        gap: null,
        wrap: source.wraps && pendingRow !== lastRow ? { rightX: rowRight(lastRow) + 44, gapY: rowTop[pendingRow] - 36, leftX: -44 } : undefined,
      } satisfies StepData,
    });
  });

  const visible = new Set(nodes.map((n) => n.id));
  return { nodes, edges, anomalies: ordered.filter((c) => anomalies.has(c.id) && visible.has(c.id)).map((c) => c.id) };
}

// ------------------------------------------------------------------ the agent right now

export type AgentStatus =
  | { kind: 'upstream' | 'waiting' | 'thinking' | 'writing'; since: number; model?: string | null }
  | { kind: 'tool'; tool: string; since: number; model?: string | null }
  | { kind: 'tools'; tools: string; since: number }
  | { kind: 'idle' | 'failed'; since: number };

/**
 * What the agent of an active session is doing: the call in flight (from the live feed, with what the model is
 * producing), else what the last call left it doing — running the tools it asked for, or waiting for the user.
 */
export function agentStatus(live: RequestSummary | undefined, calls: TraceCall[], spans: TraceSpan[]): AgentStatus | null {
  if (live) {
    const since = Date.parse(live.startedAtUtc);
    const stage = live.stage ?? '';
    if (stage.startsWith('tool:')) return { kind: 'tool', tool: stage.slice('tool:'.length), since, model: live.requestedModel };
    if (stage === 'thinking' || stage === 'writing') return { kind: stage, since, model: live.requestedModel };
    if (live.httpStatus) return { kind: 'waiting', since, model: live.requestedModel };
    return { kind: 'upstream', since, model: live.requestedModel };
  }
  const last = [...calls].filter((c) => c.callKind !== 'aux').sort((a, b) => endMs(b) - endMs(a))[0];
  if (!last) return null;
  const since = endMs(last);
  if (last.status !== 'success') return { kind: 'failed', since };
  if (last.toolCalls > 0 || last.finishReason === 'tool_calls') {
    const names = callPhases(spansByRequest(spans).get(last.id) ?? [])?.tools ?? [];
    return { kind: 'tools', tools: names.length > 0 ? toolSummary(names) : String(last.toolCalls), since };
  }
  return { kind: 'idle', since };
}

/** "claude-sonnet-5-5" → "sonnet-5-5": the family is what tells calls apart at a glance. */
export function shortModel(model: string | null | undefined): string {
  if (!model) return '—';
  const m = model.replace(/^claude-/, '').replace(/^models\//, '');
  return m.length > 22 ? `${m.slice(0, 21)}…` : m;
}


// ------------------------------------------------------------------ D · timeline

export interface TimelineRow {
  id: string;
  /** The node id this row selects (the call, or an attempt of it). */
  selectId: string;
  label: string;
  depth: number;
  startMs: number;
  endMs: number;
  kind: 'turn' | 'llm' | 'sub' | 'tool' | 'attempt-ok' | 'attempt-fail' | 'live';
  /** The model's phases inside a call, on the turn's axis. */
  segments?: { kind: PhaseKind; from: number; to: number }[];
  flag?: Anomaly;
  /** "思考 2.1 s · 输出 1.3 s". */
  detail?: string | null;
}

/**
 * The turn as rows on one time axis (ms from its first call): calls (with their phases), retried attempts, tool gaps —
 * and the calls still in flight (<paramref>pending</paramref>), growing up to <paramref>nowMs</paramref>.
 */
export function buildTimeline(
  calls: TraceCall[],
  spans: TraceSpan[],
  t: TFunction,
  pending: readonly RequestSummary[] = [],
  nowMs = Date.now(),
): { rows: TimelineRow[]; totalMs: number } {
  const by = spansByRequest(spans);
  const ordered = [...calls].sort((a, b) => startMs(a) - startMs(b));
  const live = pending.filter((r) => !ordered.some((c) => c.id === r.id));
  if (ordered.length === 0 && live.length === 0) return { rows: [], totalMs: 0 };
  const anomalies = findAnomalies(ordered, spans);
  const t0 = Math.min(...ordered.map(startMs), ...live.map((r) => Date.parse(r.startedAtUtc)));
  const totalMs = Math.max(...ordered.map(endMs), ...(live.length > 0 ? [nowMs] : [])) - t0;
  const rows: TimelineRow[] = [{ id: 'turn', selectId: 'turn', label: t('traces.session.turnRow'), depth: 0, startMs: 0, endMs: totalMs, kind: 'turn' }];
  for (const c of ordered) {
    const sub = c.callKind === 'sidechain' || c.callKind === 'aux';
    const parent = c.parentRequestId ? ordered.find((p) => p.id === c.parentRequestId) : undefined;
    if (parent && !sub && (parent.callKind ?? 'main') === 'main' && parent.toolCalls > 0 && startMs(c) > endMs(parent)) {
      const names = callPhases(by.get(parent.id) ?? [])?.tools ?? [];
      rows.push({
        id: `gap:${c.id}`,
        selectId: `tool:${c.id}`,
        label: names.length > 0 ? t('traces.session.toolRow', { names: toolSummary(names) }) : t('traces.session.toolGapRow'),
        depth: 1,
        startMs: endMs(parent) - t0,
        endMs: startMs(c) - t0,
        kind: 'tool',
      });
    }
    const start = startMs(c) - t0;
    const phases = callPhases(by.get(c.id) ?? []);
    rows.push({
      id: c.id,
      selectId: c.id,
      label: `${sub ? (c.callKind === 'sidechain' ? t('traces.kind.sidechain') : t('traces.kind.aux')) : 'LLM'} #${c.step ?? 1} · ${shortModel(c.upstreamModel ?? c.requestedModel)}`,
      depth: sub ? 2 : 1,
      startMs: start,
      endMs: endMs(c) - t0,
      kind: sub ? 'sub' : 'llm',
      segments: phases?.segments.map((s) => ({ kind: s.kind, from: start + s.from, to: start + s.to })),
      flag: anomalies.get(c.id),
      detail: phaseLine(phases, t),
    });
    const attempts = (by.get(c.id) ?? []).filter((s) => s.name === SPAN.attempt).sort((a, b) => a.startUs - b.startUs);
    if (attempts.length > 1)
      attempts.forEach((a, i) =>
        rows.push({
          id: `${c.id}:attempt:${i}`,
          selectId: c.id,
          label: `${t('traces.node.attempt', { n: i + 1 })} · ${attrStr(a, 'astra.account.id') ?? '—'} · ${attrNum(a, 'http.response.status_code') ?? '—'}`,
          depth: (sub ? 2 : 1) + 1,
          startMs: start + a.startUs / 1000,
          endMs: start + (a.startUs + a.durationUs) / 1000,
          kind: a.status === 'error' ? 'attempt-fail' : 'attempt-ok',
        }),
      );
  }
  for (const r of live) {
    const start = Math.max(0, Date.parse(r.startedAtUtc) - t0);
    rows.push({
      id: r.id,
      selectId: r.id,
      label: `LLM · ${shortModel(r.upstreamModel ?? r.requestedModel)}`,
      depth: 1,
      startMs: start,
      endMs: Math.max(start, r.totalMs ? start + r.totalMs : nowMs - t0),
      kind: 'live',
      segments: livePhases(r, nowMs).map((s) => ({ kind: s.kind, from: start + s.from, to: start + s.to })),
    });
  }
  return { rows, totalMs };
}

// ------------------------------------------------------------------ the activity feed beside the topology

/** One request of the activity feed: from the stored row (GET /api/traces/activity) or, while it runs, the live feed. */
export interface ActivityItem {
  id: string;
  /** Still from the live feed: in flight, or finished but not stored yet. */
  live: boolean;
  request: RequestSummary;
  step?: number | null;
  cause?: RequestCause | null;
  attempts: RequestAttempt[];
  thinkMs?: number | null;
  thinkChars?: number | null;
  /** Live items: how long the model wrote text (from the phases); stored ones carry textChars / reply instead. */
  textMs?: number | null;
  textChars?: number | null;
  reply?: string | null;
  tools: TraceToolCall[];
  finishReason?: string | null;
  errorMessage?: string | null;
}

/**
 * The feed's items, newest first: the stored requests, with the live feed's version of every request still running
 * (or finished and not stored yet) in their place. A live item has its tool names from its phases; their arguments,
 * the reply and the thinking size arrive with the stored row.
 */
export function activityItems(stored: readonly TraceActivity[], live: readonly RequestSummary[], nowMs: number, limit = 30): ActivityItem[] {
  const byId = new Map<string, ActivityItem>();
  for (const a of stored)
    byId.set(a.request.id, {
      id: a.request.id,
      live: false,
      request: a.request,
      step: a.step,
      cause: a.cause,
      attempts: a.attempts,
      thinkMs: a.reasoningMs,
      thinkChars: a.reasoningChars,
      textChars: a.textChars,
      reply: a.reply,
      tools: a.toolCalls,
      finishReason: a.finishReason,
      errorMessage: a.errorMessage,
    });
  for (const r of live) {
    if (r.status !== 'pending' && byId.has(r.id)) continue;
    const phases = livePhases(r, nowMs);
    const spent = (kind: PhaseKind) => phases.filter((s) => s.kind === kind).reduce((sum, s) => sum + (s.to - s.from), 0);
    const think = spent('think');
    const write = spent('write');
    byId.set(r.id, {
      id: r.id,
      live: true,
      request: r,
      cause: r.cause,
      attempts: r.attempts ?? [],
      thinkMs: think > 0 ? think : null,
      textMs: write > 0 ? write : null,
      tools: liveTools(r).map((name) => ({ name })),
    });
  }
  return [...byId.values()]
    .sort((a, b) => (a.request.startedAtUtc === b.request.startedAtUtc ? (a.id < b.id ? 1 : -1) : a.request.startedAtUtc < b.request.startedAtUtc ? 1 : -1))
    .slice(0, limit);
}

/** A tool call's arguments for display: an object's fields as key / value pairs (strings as they are), else the raw text. */
export function toolArguments(text: string | null | undefined): { pairs: [string, string][] } | { raw: string } | null {
  if (!text) return null;
  try {
    const value: unknown = JSON.parse(text);
    if (value && typeof value === 'object' && !Array.isArray(value))
      return { pairs: Object.entries(value as Record<string, unknown>).map(([k, v]) => [k, typeof v === 'string' ? v : JSON.stringify(v, null, 2)]) };
  } catch {
    // cut at 4 KB, or not JSON: shown as it is
  }
  return { raw: text };
}

/** The first argument worth showing next to a tool's name: a path, a command, a pattern… */
export function toolHeadline(text: string | null | undefined): string | null {
  const args = toolArguments(text);
  if (!args) return null;
  if ('raw' in args) return args.raw.slice(0, 80);
  const preferred = ['file_path', 'path', 'command', 'cmd', 'pattern', 'query', 'url', 'description'];
  const pair = args.pairs.find(([k]) => preferred.includes(k)) ?? args.pairs[0];
  return pair ? pair[1].split('\n')[0].slice(0, 80) : null;
}
