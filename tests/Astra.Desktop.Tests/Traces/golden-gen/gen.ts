// Generator of TraceGolden.g.cs (not part of any build): runs the real web trace model on hand-built inputs and dumps inputs +
// normalised outputs. From the repo root:
//   web/node_modules/.bin/esbuild tests/Astra.Desktop.Tests/Traces/golden-gen/gen.ts --bundle --platform=node --format=esm \
//     --outfile=/tmp/gen.mjs --jsx=automatic --loader:.css=empty \
//     --banner:js="import {createRequire} from 'module';const require=createRequire(import.meta.url);"
//   node /tmp/gen.mjs /tmp/golden.json
//   node tests/Astra.Desktop.Tests/Traces/golden-gen/emit.mjs /tmp/golden.json tests/Astra.Desktop.Tests/Traces/TraceGolden.g.cs
// Throwaway: runs the web traces model on hand-built inputs and dumps inputs + normalized outputs as golden JSON.
import { writeFileSync } from 'node:fs';
(globalThis as any).localStorage = { getItem: () => 'en', setItem: () => {} };
import * as M from '../../../../web/src/components/traces/model';
import { translate } from '../../../../web/src/i18n';
import * as F from '../../../../web/src/lib/format';

const t = (key: any, vars?: any) => translate('en', key, vars);
const label = (kind: string | null | undefined) => (kind === 'claude-code' ? 'Claude Code' : (kind ?? ''));
const T0 = Date.parse('2026-10-14T09:00:00.000Z');
const at = (ms: number) => new Date(T0 + ms).toISOString();

const call = (id: string, startMs: number, totalMs: number | null, extra: any = {}): any => ({
  id, startedAtUtc: at(startMs), clientKind: 'claude-code', providerId: 'copilot', providerName: 'GitHub Copilot',
  inboundProtocol: 'anthropic', upstreamProtocol: 'openai-chat', passthrough: false, stream: true,
  requestedModel: 'claude-sonnet-5-5', upstreamModel: 'claude-sonnet-5.5', status: 'success', httpStatus: 200, totalMs,
  ttfbMs: 300, totalInputTokens: 1000, totalOutputTokens: 50, cacheReadTokens: 0, reasoningTokens: 0, costNanoUsd: 1_000_000,
  sessionKey: 'cc:abc', chainKey: 'fp:main', turnId: 'm1', step: 1, callKind: 'main', linkSource: 'new_turn', toolCalls: 0, ...extra,
});
const span = (requestId: string, name: string, startUs: number, durationUs: number, extra: any = {}): any => ({
  requestId, spanId: `${requestId}:${name}:${startUs}`, parentSpanId: name === 'gateway.request' ? null : `${requestId}:root`, name,
  kind: name === 'gateway.request' ? 'server' : name === 'upstream.attempt' ? 'client' : 'internal', startUs, durationUs, status: 'ok', ...extra,
});
const failoverSpans = (id: string): any[] => [
  span(id, 'gateway.request', 0, 8_420_000),
  span(id, 'gateway.route', 0, 600),
  span(id, 'gateway.privacy', 700, 2_000, { attributes: { 'astra.privacy.hits': 2, 'astra.privacy.restorable': 2 } }),
  span(id, 'gateway.encode', 3_000, 900, { attributes: { 'astra.passthrough': false, 'astra.protocol.upstream': 'openai-chat' } }),
  span(id, 'upstream.attempt', 5_000, 620_000, { status: 'error', attributes: { 'http.response.status_code': 429, 'astra.account.id': 'gh-alice', 'astra.attempt.retry': 'rate_limited_failover' } }),
  span(id, 'upstream.attempt', 625_000, 940_000, { attributes: { 'http.response.status_code': 200, 'astra.account.id': 'gh-bob' } }),
  span(id, 'gateway.response', 1_565_000, 6_850_000, {
    attributes: { 'astra.restore.placeholders': 2, 'astra.response.tool_names': 'Read,Read,Grep', 'astra.response.events': 96 },
    events: [{ name: 'first_token', offset_us: 1_835_000 }],
  }),
];
const routed = (id: string, startUs: number, durationUs: number, attributes: any) => span(id, 'gateway.resolve', startUs, durationUs, { attributes });
const live = (id: string, extra: any = {}): any => ({
  id, startedAtUtc: at(9_000), status: 'pending', providerId: 'copilot', requestedModel: 'claude-sonnet-5-5', sessionKey: 'cc:abc', clientKind: 'claude-code',
  inboundProtocol: 'anthropic', ...extra,
});

// ---------------------------------------------------------------- projections
const COLOR: Record<string, string> = {
  'color-mix(in oklab, var(--accent) 55%, transparent)': 'flow', 'var(--label-3)': 'request',
  'color-mix(in oklab, var(--accent) 70%, transparent)': 'response', 'color-mix(in oklab, var(--orange) 80%, transparent)': 'warn',
  'var(--red)': 'error', 'var(--green)': 'ok', 'var(--accent-2)': 'inferred',
};
const n = (v: any) => (v === undefined ? null : v);
const num = (v: number) => Math.round(v * 1e6) / 1e6;
const projectGraph = (g: any) => ({
  nodes: g.nodes.map((x: any) => ({
    id: x.id, kind: x.type === 'hub' ? 'hub' : 'card', x: num(x.position.x), y: num(x.position.y), w: n(x.style?.width), h: n(x.style?.height),
    title: n(x.data.title), lines: x.data.lines ?? [], tone: n(x.data.tone), client: n(x.data.client),
    provider: x.data.provider ? { name: x.data.provider.name, icon: n(x.data.provider.icon), templateId: n(x.data.provider.templateId) } : null,
    stacked: !!x.data.stacked, pill: !!x.data.pill, live: n(x.data.live), flag: x.data.flag ? { text: x.data.flag.text, tone: x.data.flag.tone } : null,
    group: !!x.data.group, pending: n(x.data.pending), busy: !!x.data.busy,
  })),
  edges: g.edges.map((e: any) => {
    const d = e.data ?? {};
    return {
      id: e.id, source: e.source, target: e.target, sh: e.sourceHandle, th: e.targetHandle, type: e.type, tone: COLOR[e.style.stroke] ?? `?${e.style.stroke}`,
      dashed: !!e.style.strokeDasharray, sw: e.style.strokeWidth, label: n(e.label), animated: !!e.animated, arrow: e.markerEnd !== undefined,
      flow: e.type === 'flow' ? { role: d.role, requests: d.requests, warn: !!d.warn } : null,
      step: e.type === 'step' ? { label: n(d.label), tools: n(d.tools), gap: n(d.gap), selectId: n(d.selectId), wrap: d.wrap ?? null } : null,
    };
  }),
  ...(g.anomalies ? { anomalies: g.anomalies } : {}),
});
const SW: Record<string, string> = { wait: 'phaseWait', think: 'phaseThink', write: 'phaseWrite', tool: 'phaseTool' };

// request.tsx time bar (copied: those lines are inline in the component)
function requestBar(spans: any[]) {
  const split = M.splitRequestTime(spans);
  if (!split) return null;
  const p = M.callPhases(spans);
  let generation: any = null;
  if (p && p.thinkMs + p.writeMs + p.toolMs > 0) {
    let left = split.generationUs;
    const take = (ms: number) => { const us = Math.min(left, Math.max(0, ms * 1000)); left -= us; return us; };
    generation = { think: take(p.thinkMs), write: take(p.writeMs), tool: take(p.toolMs), rest: left };
  }
  const parts: any[] = [
    { label: t('traces.request.time.astra'), value: Math.max(0, split.astraUs - split.routingUs), text: M.formatUs(Math.max(0, split.astraUs - split.routingUs)), swatch: 'accent2' },
    ...(split.routingUs > 0 ? [{ label: t('traces.request.time.routing'), value: split.routingUs, text: M.formatUs(split.routingUs), swatch: 'orange' }] : []),
    { label: t('traces.request.time.retries'), value: split.retryUs, text: M.formatUs(split.retryUs), swatch: 'red' },
    { label: t('traces.request.time.ttfb'), value: split.ttfbUs, text: M.formatUs(split.ttfbUs), swatch: 'muted' },
    { label: t('traces.request.time.firstToken'), value: split.firstTokenUs, text: M.formatUs(split.firstTokenUs), swatch: 'accentSoft45' },
    ...(generation
      ? [
          { label: t('traces.phase.think'), value: generation.think, text: M.formatUs(generation.think), swatch: 'phaseThink' },
          { label: t('traces.phase.write'), value: generation.write, text: M.formatUs(generation.write), swatch: 'phaseWrite' },
          { label: t('traces.phase.tool'), value: generation.tool, text: M.formatUs(generation.tool), swatch: 'phaseTool' },
          ...(generation.rest >= 1000 ? [{ label: t('traces.request.time.generationOther'), value: generation.rest, text: M.formatUs(generation.rest), swatch: 'accentSoft35' }] : []),
        ]
      : [{ label: t('traces.request.time.generation'), value: split.generationUs, text: M.formatUs(split.generationUs), swatch: 'accent' }]),
  ];
  return { title: t('traces.request.time.title'), total: M.formatUs(split.totalUs), parts };
}
function sessionBar(calls: any[], spans: any[]) {
  const time = M.splitTurnTime(calls, spans);
  const sum = M.turnPhases(calls, spans);
  const total = sum.wait + sum.think + sum.write + sum.tool;
  const phaseParts = total <= 0 || time.modelMs <= 0 ? null : (['wait', 'think', 'write', 'tool'] as const).map((k) => ({ kind: k, ms: (sum[k] / total) * time.modelMs }));
  const parts: any[] = [
    ...(phaseParts
      ? phaseParts.map((p) => ({ label: t(`traces.phase.${p.kind}`), value: p.ms, text: F.formatMs(p.ms), swatch: SW[p.kind] }))
      : [{ label: t('traces.session.time.model'), value: time.modelMs, text: F.formatMs(time.modelMs), swatch: 'accent' }]),
    { label: t('traces.session.time.tools'), value: time.toolMs, text: F.formatMs(time.toolMs), swatch: 'muted', note: t('traces.session.time.needsTelemetry') },
    { label: t('traces.session.time.retries'), value: time.retryMs, text: F.formatMs(time.retryMs), swatch: 'red' },
  ];
  return {
    bar: time.totalMs > 0 ? { title: t('traces.session.time.title'), total: F.formatMs(time.totalMs), parts } : null,
    overhead: time.overheadMs > 0 ? t('traces.session.time.overhead', { ms: F.formatMs(time.overheadMs) }) : null,
  };
}
const phases = (p: any) => (p ? { waitMs: p.waitMs, thinkMs: p.thinkMs, writeMs: p.writeMs, toolMs: p.toolMs, segments: p.segments, tools: p.tools } : null);
const asNum = (o: any) => JSON.parse(JSON.stringify(o, (_k, v) => (typeof v === 'number' ? num(v) : v)));

// ---------------------------------------------------------------- scenarios
const out: any[] = [];
const topology = {
  range: 'live', windowMinutes: 5,
  gateway: { requests: 10, errors: 1, inFlight: 2, overheadP50Us: 4_900, overheadP95Us: 38_000, failovers: 1, stages: [{ name: 'gateway.route', p50Us: 400, p95Us: 900, samples: 10 }] },
  clients: [{ clientKind: 'claude-code', requests: 10, errors: 1, perMinute: 2, activeSessions: 1 }],
  providers: [{ providerId: 'copilot', name: 'GitHub Copilot', requests: 10, errors: 1, attempts: 11, rateLimited: 1, failovers: 1, perMinute: 2, ttfbP95Ms: 1400,
    accounts: [{ id: 'gh-alice', name: 'gh-alice', status: 'cooldown', enabled: true, cooldownUntilUtc: at(120_000), remainingRequests: '0' }] }],
};
const topology2 = {
  range: '1h', windowMinutes: 60,
  gateway: { requests: 120, errors: 30, inFlight: 0, overheadP50Us: 2_000, overheadP95Us: 9_000, failovers: 0, stages: [] },
  clients: [
    { clientKind: 'claude-code', requests: 80, errors: 2, perMinute: 1.33, activeSessions: 0 },
    { clientKind: null, requests: 40, errors: 28, perMinute: 12.4, activeSessions: 2 },
    { clientKind: 'codex', requests: 0, errors: 0, perMinute: 0, activeSessions: 0 },
  ],
  providers: [
    { providerId: 'a', name: 'Alpha', icon: 'alpha', templateId: 'tpl', requests: 100, errors: 40, attempts: 100, rateLimited: 0, failovers: 0, perMinute: 1.6, accounts: [] },
    { providerId: 'b', name: 'Beta', requests: 20, errors: 0, attempts: 20, rateLimited: 3, failovers: 0, perMinute: 0.3, accounts: [
      { id: 'b1', name: 'b1', status: 'ok', enabled: true }, { id: 'b2', name: 'b2', status: 'cooldown', enabled: true } ] },
    { providerId: 'c', name: 'Gamma', requests: 0, errors: 0, attempts: 0, rateLimited: 0, failovers: 0, perMinute: 0, accounts: [{ id: 'c1', name: 'c1', status: 'ok', enabled: true }] },
  ],
};
const emptyTopology = { ...topology, range: '24h', gateway: { ...topology.gateway, requests: 0, errors: 0, inFlight: 0, stages: [] }, clients: [], providers: [] };
const liveReqs = [
  live('a', { providerId: 'copilot' }), live('b', { providerId: 'deepseek', clientKind: 'codex' }), live('c', { status: 'success', providerId: 'copilot' }),
  live('d', { providerId: null, clientKind: null }),
];
const brands = { deepseek: { name: 'DeepSeek', icon: 'ds' } };
for (const [name, topo, lv, br] of [
  ['topology/basic', topology, [], {}], ['topology/live', topology, liveReqs, brands], ['topology/busy-1h', topology2, [], { a: { name: 'Alpha Brand', icon: 'x', templateId: 'y' } }],
  ['topology/empty', emptyTopology, [], {}], ['topology/empty-with-live', emptyTopology, [liveReqs[0]], { copilot: { name: 'Copilot' } }],
] as const) {
  const graph = M.buildTopology(topo as any, t, label, new Map(Object.entries(br)) as any, lv as any);
  out.push({ name, kind: 'topology', input: { topology: topo, live: lv, brands: br }, expected: { graph: projectGraph(graph) } });
}

const reqCase = (name: string, trace: any, brand: any = { name: 'GitHub Copilot' }) => {
  const graph = M.buildRequest(trace, t, label, brand);
  const route = M.routeDecision(trace.spans);
  out.push({
    name: `request/${name}`, kind: 'request', input: { trace, brand },
    expected: asNum({
      graph: projectGraph(graph), split: M.splitRequestTime(trace.spans), phases: phases(M.callPhases(trace.spans)), bar: requestBar(trace.spans),
      routeSummary: route ? M.routeSummary(route, t) : null,
    }),
  });
};
const reqOf = (spans: any[], extra: any = {}, c: any = {}) => ({ request: call('r1', 0, 8420, c), inFlight: false, spans, parent: null, children: [], ...extra });
reqCase('failover', reqOf(failoverSpans('r1')));
reqCase('pool-failover', reqOf(failoverSpans('r1').map((sp: any) =>
  sp.name !== 'upstream.attempt' ? sp
    : sp.startUs === 5_000 ? { ...sp, attributes: { ...sp.attributes, 'astra.provider.id': 'relay', 'astra.provider.name': 'Relay', 'astra.attempt.retry': 'provider_failover' } }
    : { ...sp, attributes: { ...sp.attributes, 'astra.provider.id': 'copilot', 'astra.provider.name': 'GitHub Copilot' } }), {}, { providerId: 'copilot' }));
const judgedSpans = [
  span('r1', 'gateway.request', 0, 5_000_000), span('r1', 'gateway.privacy', 700, 2_000),
  routed('r1', 3_000, 2_030_000, { 'astra.provider.name': 'Grok', 'astra.route.mode': 'session', 'astra.route.source': 'judge', 'astra.route.grade': 'standard',
    'astra.route.model': 'grok-4.7', 'astra.route.confidence': 0.82, 'astra.route.judge.protocol': 'typesafe', 'astra.route.judge.model': 'jev-1',
    'astra.route.judge.ms': 2010, 'astra.route.judge.new_connection': true }),
  span('r1', 'upstream.attempt', 2_050_000, 2_000_000, { attributes: { 'http.response.status_code': 200 } }),
];
reqCase('judged', reqOf(judgedSpans, {}, { requestedModel: 'astra-auto', totalMs: 5000 }), { name: 'Grok' });
const fallbackSpans = [
  routed('r1', 0, 320_000, { 'astra.provider.name': 'Grok', 'astra.route.source': 'fallback', 'astra.route.grade': 'standard', 'astra.route.fallback_reason': 'low_confidence', 'astra.route.judge.ms': 300 }),
  span('r1', 'upstream.attempt', 320_000, 10_000, { attributes: { 'http.response.status_code': 200 } }),
];
reqCase('fallback', reqOf(fallbackSpans, {}, { totalMs: 1000 }), null);
reqCase('adjusted', reqOf([routed('r1', 0, 15_000, { 'astra.provider.name': 'Grok', 'astra.route.source': 'judge', 'astra.route.grade': 'strong', 'astra.route.adjusted_from': 'light' })]), null);
reqCase('pinned', reqOf([span('r1', 'gateway.request', 0, 100_000), routed('r1', 0, 15_000, { 'astra.provider.name': 'Grok', 'astra.route.source': 'pin', 'astra.route.grade': 'light' }),
  span('r1', 'upstream.attempt', 20_000, 10_000, { attributes: { 'http.response.status_code': 200 } })]), null);
reqCase('pool-resolves-again', reqOf([
  routed('r1', 1_000, 2_000_000, { 'astra.provider.name': 'Relay', 'astra.route.source': 'judge', 'astra.route.grade': 'fast', 'astra.route.judge.ms': 1900 }),
  routed('r1', 2_100_000, 4_000, { 'astra.provider.name': 'GitHub Copilot' }),
  span('r1', 'upstream.attempt', 2_200_000, 100_000, { attributes: { 'http.response.status_code': 200, 'astra.provider.id': 'copilot' } })], {}, { providerId: 'copilot', totalMs: 5000 }));
const streamSpans = [
  span('r1', 'gateway.request', 0, 9_000_000, { attributes: { 'astra.request.messages': 42 } }),
  span('r1', 'gateway.route', 0, 800), span('r1', 'gateway.body', 800, 300), span('r1', 'gateway.middleware', 1_200, 500, { attributes: { 'astra.middleware.rewritten': true } }),
  span('r1', 'gateway.privacy', 1_800, 1_500, { attributes: { 'astra.privacy.hits': 0 } }),
  span('r1', 'gateway.resolve', 3_400, 2_000, { attributes: { 'astra.provider.name': 'Copilot' } }),
  span('r1', 'gateway.encode', 5_500, 700, { attributes: { 'astra.passthrough': true } }),
  span('r1', 'upstream.attempt', 7_000, 1_200_000, { attributes: { 'http.response.status_code': 200, 'astra.account.id': 'gh-bob' } }),
  span('r1', 'gateway.response', 1_207_000, 7_700_000, {
    attributes: { 'astra.response.max_gap_ms': 12_500, 'astra.response.events': 300, 'astra.middleware.response': true, 'astra.response.tool_names': 'Bash' },
    events: [{ name: 'first_token', offset_us: 2_000_000 }, { name: 'reasoning_start', offset_us: 2_000_000 }, { name: 'reasoning_end', offset_us: 4_000_000 },
      { name: 'text_start', offset_us: 4_100_000 }, { name: 'text_end', offset_us: 6_100_000 }, { name: 'tool_call', offset_us: 6_300_000, attributes: { name: 'Bash' } }],
  }),
];
reqCase('streaming', reqOf(streamSpans, {}, { totalMs: 9000, passthrough: true, finishReason: 'tool_calls', upstreamRequestId: 'req_abcdef123456' }));
reqCase('errored', reqOf([
  span('r1', 'gateway.request', 0, 400_000, { status: 'error' }), span('r1', 'gateway.route', 0, 600, { status: 'error', statusMessage: 'bad token' }),
  span('r1', 'upstream.attempt', 5_000, 300_000, { status: 'error', attributes: { 'http.response.status_code': 500 }, statusMessage: 'boom' }),
  span('r1', 'gateway.response', 305_000, 90_000, { status: 'error', statusMessage: 'stream cut' })], {}, { status: 'upstream_error', httpStatus: 500, totalMs: 400, errorMessage: 'upstream 500' }));
reqCase('empty', reqOf([]), null);
reqCase('in-flight-no-attempt', reqOf([span('r1', 'gateway.request', 0, 50_000), span('r1', 'gateway.route', 0, 600), span('r1', 'gateway.privacy', 700, 2_000)], { inFlight: true }, { status: 'pending' }));
reqCase('clock-skew', reqOf([
  span('r1', 'gateway.request', 0, 1_000_000), span('r1', 'upstream.attempt', 900_000, 300_000, { attributes: { 'http.response.status_code': 200 } }),
  span('r1', 'upstream.attempt', 100_000, -50_000, { status: 'error', attributes: { 'http.response.status_code': 502 } }),
  span('r1', 'gateway.response', 400_000, 200_000, { events: [{ name: 'first_token', offset_us: 50_000 }] })]));

// ---------------------------------------------------------------- sessions
const sessCase = (name: string, calls: any[], spans: any[], options: any = {}, pending: any[] = [], nowMs = T0 + 20_000) => {
  const opts = { columns: 4, collapse: true, expanded: [] as string[], ...options };
  const g = M.buildSession(calls, spans, t, new Map([['anthropic', { name: 'Anthropic', icon: 'a' }]]) as any, { columns: opts.columns, collapse: opts.collapse, expanded: new Set(opts.expanded), pending });
  const tl = M.buildTimeline(calls, spans, t, pending, nowMs);
  out.push({
    name: `session/${name}`, kind: 'session', input: { calls, spans, options: opts, pending, nowMs, brands: { anthropic: { name: 'Anthropic', icon: 'a' } } },
    expected: asNum({
      graph: projectGraph(g), turnTime: M.splitTurnTime(calls, spans), turnPhases: M.turnPhases(calls, spans), bar: sessionBar(calls, spans),
      timeline: { totalMs: tl.totalMs, rows: tl.rows.map((r: any) => ({ id: r.id, selectId: r.selectId, label: r.label, depth: r.depth, startMs: r.startMs, endMs: r.endMs, kind: r.kind,
        segments: r.segments ?? null, flag: r.flag ?? null, detail: r.detail ?? null })) },
      anomalies: [...M.findAnomalies(calls, spans)].map(([k, v]) => [k, v]),
    }),
  });
};
const mains = (k: number, extra: (i: number) => any = () => ({})) => Array.from({ length: k }, (_, i) => call(`m${i}`, i * 4_000, 3_000, { step: i + 1, toolCalls: 1, ...extra(i) }));
{
  const calls = [call('m1', 0, 3_000, { toolCalls: 1 }), call('s1', 3_200, 2_000, { callKind: 'sidechain', chainKey: 'fp:sub', parentRequestId: 'm1', linkSource: 'inferred', providerId: 'anthropic' }),
    call('m2', 6_000, 2_000, { parentRequestId: 'm1', step: 2, linkSource: 'tool_call' })];
  sessCase('tools-and-subagent', calls, [...failoverSpans('m2'), span('m1', 'gateway.response', 0, 10, { attributes: { 'astra.response.tool_names': 'Task' } })]);
}
sessCase('wrap', mains(6), []);
sessCase('fold', mains(12), []);
sessCase('fold-failing', mains(12, (i) => (i === 5 ? { status: 'upstream_error', httpStatus: 500 } : {})), []);
sessCase('fold-expanded', mains(12), [], { expanded: ['group:m1'] });
sessCase('short-8', mains(8), []);
sessCase('pending-live', mains(4), [span('m3', 'gateway.response', 0, 10, { attributes: { 'astra.response.tool_names': 'Bash' } })], {}, [live('p1', { stage: 'thinking', phases: 'thinking@1500' }), live('p2', { upstreamModel: 'claude-opus-4' }), live('m0')], T0 + 20_000);
sessCase('pending-only', [], [], {}, [live('p1', { phases: 'thinking@1500,writing@6000,tool:Bash@9000' })], T0 + 12_000);
sessCase('pending-after-parent', [call('m1', 0, 3_000, { toolCalls: 2 }), call('m2', 4_000, 2_000, { parentRequestId: 'm1', step: 2 })],
  [span('m1', 'gateway.response', 0, 10, { attributes: { 'astra.response.tool_names': 'Read,Read' } })], {}, [live('p9', { parentRequestId: 'm1', status: 'pending', httpStatus: 200, stage: 'writing' })]);
{
  // an agent session: main loop, a sub-agent chain of two calls hanging off m2 (links through each other), an aux call, a session-linked call
  const calls = [
    call('m1', 0, 2_000, { toolCalls: 1 }),
    call('m2', 2_500, 3_000, { step: 2, parentRequestId: 'm1', linkSource: 'tool_call', toolCalls: 1 }),
    call('s1', 3_000, 1_500, { callKind: 'sidechain', chainKey: 'fp:sub', parentRequestId: 'm2', linkSource: 'inferred', step: 1, providerId: 'anthropic' }),
    call('s2', 4_700, 1_000, { callKind: 'sidechain', chainKey: 'fp:sub', parentRequestId: 's1', linkSource: 'inferred', step: 2, providerId: 'anthropic' }),
    call('x1', 1_000, 400, { callKind: 'aux', chainKey: 'fp:aux', parentRequestId: 'm1', linkSource: 'inferred', step: 1, providerName: 'GitHub Copilot' }),
    call('m3', 7_000, 4_000, { step: 3, parentRequestId: 'm2', linkSource: 'session', toolCalls: 0, turnId: 'm1', totalInputTokens: 123_456, totalOutputTokens: 15_300 }),
    call('m4', 12_000, null, { step: 4, parentRequestId: 'm3', linkSource: 'tool_call', status: 'upstream_error', httpStatus: 500 }),
  ];
  sessCase('agent-session-links', calls, [
    span('m2', 'gateway.response', 0, 3_000_000, { events: [{ name: 'tool_call', offset_us: 2_000_000, attributes: { name: 'Task' } }, { name: 'reasoning_start', offset_us: 100_000 }, { name: 'reasoning_end', offset_us: 900_000 }] }),
    ...failoverSpans('m3'),
  ]);
}
sessCase('slow-and-retry-flags', [call('a', 0, 1_000), call('b', 1_500, 1_000), call('c', 3_000, 1_000), call('d', 5_000, 9_000, { parentRequestId: 'c', step: 2 })], []);
sessCase('clock-skew', [call('a', 5_000, 2_000, { toolCalls: 1 }), call('b', 1_000, 1_000, { parentRequestId: 'a', step: 2 }), call('c', 4_000, -500, { step: 3 }), call('d', 4_500, null, { step: 4 })],
  [span('a', 'gateway.request', 0, 2_000_000), span('a', 'upstream.attempt', 500_000, -100_000, { attributes: { 'http.response.status_code': 200 } }),
    span('a', 'gateway.response', 400_000, 100_000, { events: [{ name: 'first_token', offset_us: 10_000 }] })]);
sessCase('empty', [], []);

// ---------------------------------------------------------------- calculations
const evSpans = [span('c', 'gateway.response', 50_000, 3_300_000, { events: [
  { name: 'reasoning_start', offset_us: 100_000 }, { name: 'reasoning_end', offset_us: 2_100_000 }, { name: 'text_start', offset_us: 2_200_000 }, { name: 'text_end', offset_us: 3_000_000 },
  { name: 'tool_call', offset_us: 3_100_000, attributes: { name: 'Read' } }, { name: 'tool_call', offset_us: 3_200_000, attributes: { name: 'Grep' } }] })];
const phaseCases: [string, any[]][] = [
  ['events', evSpans], ['none', []], ['only-attr-tools', [span('c', 'gateway.response', 0, 10, { attributes: { 'astra.response.tool_names': 'Read,Read,Grep' } })]],
  ['unfinished-reasoning', [span('c', 'gateway.response', 0, 2_000_000, { events: [{ name: 'reasoning_start', offset_us: 500_000 }] })]],
  ['reversed-events', [span('c', 'gateway.response', 0, 2_000_000, { events: [{ name: 'text_start', offset_us: 900_000 }, { name: 'text_end', offset_us: 500_000 }] })]],
];
const liveRows: [string, any, number][] = [
  ['staged', { startedAtUtc: at(0), totalMs: null, phases: 'thinking@1500,writing@6000,tool:Bash@9000' }, T0 + 10_000],
  ['no-phases', { startedAtUtc: at(0), totalMs: null, phases: null }, T0 + 2_000],
  ['done', { startedAtUtc: at(0), totalMs: 4_000, phases: 'thinking@1000,writing@9000' }, T0 + 30_000],
  ['junk', { startedAtUtc: at(0), totalMs: null, phases: 'oops,@5,x@y,thinking@' }, T0 + 3_000],
];
const status = (stage: string | null, httpStatus: number | null = 200) => ({ id: 'x', startedAtUtc: at(10_000), status: 'pending', providerId: 'copilot', httpStatus, stage, requestedModel: 'claude-sonnet-5-5' });
const agentCases: [string, any, any[], any[]][] = [
  ['thinking', status('thinking'), [], []], ['tool', status('tool:Bash'), [], []], ['upstream', status(null, null), [], []], ['waiting', status(null), [], []],
  ['tools', undefined, [call('m1', 0, 3_000, { toolCalls: 2 })], [span('m1', 'gateway.response', 0, 10, { attributes: { 'astra.response.tool_names': 'Read,Read' } })]],
  ['tools-count', undefined, [call('m1', 0, 3_000, { finishReason: 'tool_calls' })], []],
  ['idle', undefined, [call('m1', 0, 3_000)], []], ['failed', undefined, [call('m1', 0, 3_000, { status: 'upstream_error' })], []], ['none', undefined, [], []],
  ['aux-ignored', undefined, [call('x', 0, 3_000, { callKind: 'aux' })], []],
];
out.push({
  name: 'calc/all', kind: 'calc', input: { phaseCases: phaseCases.map(([n, s]) => ({ name: n, spans: s })), liveRows: liveRows.map(([n, r, now]) => ({ name: n, row: r, nowMs: now })),
    agentCases: agentCases.map(([n, l, c, s]) => ({ name: n, live: l ?? null, calls: c, spans: s })) },
  expected: asNum({
    phases: phaseCases.map(([n, s]) => ({ name: n, phases: phases(M.callPhases(s)), line: (() => { const p = M.callPhases(s); return p ? M.phaseLine(p, t) : null; })() })),
    livePhases: liveRows.map(([n, r, now]) => ({ name: n, segments: M.livePhases(r, now) })),
    agent: agentCases.map(([n, l, c, s]) => ({ name: n, status: (() => { const a = M.agentStatus(l, c, s); return a ? { kind: a.kind, since: a.since, model: (a as any).model ?? null, tool: (a as any).tool ?? null, tools: (a as any).tools ?? null } : null; })() })),
    formats: {
      us: [null, 0, 400, 9_999, 10_000, 38_400, 999_999, 1_000_000, 1_834_567, -5].map((v) => [v, M.formatUs(v as any)]),
      ms: [null, 0, 0.4, 0.5, 1.5, 2.5, 420.49, 9_999.4, 10_000, 12_345, -3].map((v) => [v, F.formatMs(v as any)]),
      tokens: [null, 0, 999, 1000, 9_999, 10_000, 12_345, 99_999, 999_950, 1_500_000, 8_100_000, 2_345_678_901].map((v) => [v, F.formatTokens(v as any, true), F.formatTokens(v as any)]),
      usd: [null, 0, 0.000_000_5, 0.009, 0.0213, 0.0012345, 0.5, 0.999, 1, 12.345, 1234.5, 1_000_000].map((v) => [v, F.formatUsd(v as any)]),
      percent: [null, 0, 0.001, 0.12345, 0.5, 0.9995, 1].map((v) => [v, F.formatPercent(v as any)]),
      model: [null, '', 'claude-sonnet-5-5', 'models/gemini-3-pro', 'a-very-long-model-name-that-keeps-going'].map((v) => [v, M.shortModel(v as any)]),
      tools: [[], ['Read'], ['Read', 'Read', 'Grep'], ['A', 'B', 'A', 'B', 'B']].map((v) => [v, M.toolSummary(v as any)]),
    },
    livePhase: [['pending', null, null], ['pending', 'p', null], ['pending', 'p', 200], ['success', 'p', 200], ['upstream_error', 'p', 429]].map(([s, p, h]) => [s, p, h, M.livePhase({ status: s, providerId: p, httpStatus: h } as any)]),
  }),
});

writeFileSync(process.argv[2], JSON.stringify(out));
console.log('scenarios', out.length);
