import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { ComponentType, ReactNode } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { RequestSummary, TraceCall, TraceRequest, TraceSessionDetail, TraceSpan, TraceTopology } from '../api/types';
import {
  agentStatus,
  buildRequest,
  buildSession,
  buildTimeline,
  buildTopology,
  callPhases,
  HUB_ID,
  livePhase,
  livePhases,
  phaseLine,
  splitRequestTime,
  splitTurnTime,
  turnPhases,
  type StepData,
} from '../components/traces/model';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';
import { TracesPage } from '../pages/TracesPage';

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);
const label = (kind: string | null | undefined) => (kind === 'claude-code' ? 'Claude Code' : (kind ?? ''));

const T0 = Date.parse('2026-10-14T09:00:00.000Z');
const at = (ms: number) => new Date(T0 + ms).toISOString();

const call = (id: string, startMs: number, totalMs: number, extra: Partial<TraceCall> = {}): TraceCall => ({
  id,
  startedAtUtc: at(startMs),
  clientKind: 'claude-code',
  providerId: 'copilot',
  providerName: 'GitHub Copilot',
  inboundProtocol: 'anthropic',
  upstreamProtocol: 'openai-chat',
  passthrough: false,
  stream: true,
  requestedModel: 'claude-sonnet-5-5',
  upstreamModel: 'claude-sonnet-5.5',
  status: 'success',
  httpStatus: 200,
  totalMs,
  ttfbMs: 300,
  totalInputTokens: 1000,
  totalOutputTokens: 50,
  cacheReadTokens: 0,
  reasoningTokens: 0,
  costNanoUsd: 1_000_000,
  sessionKey: 'cc:abc',
  chainKey: 'fp:main',
  turnId: 'm1',
  step: 1,
  callKind: 'main',
  linkSource: 'new_turn',
  toolCalls: 0,
  ...extra,
});

const span = (requestId: string, name: string, startUs: number, durationUs: number, extra: Partial<TraceSpan> = {}): TraceSpan => ({
  requestId,
  spanId: `${requestId}:${name}:${startUs}`,
  parentSpanId: name === 'gateway.request' ? null : `${requestId}:root`,
  name,
  kind: name === 'gateway.request' ? 'server' : name === 'upstream.attempt' ? 'client' : 'internal',
  startUs,
  durationUs,
  status: 'ok',
  ...extra,
});

// A failover request: two attempts (429 then 200), a converted response with a first token.
const failoverSpans = (id: string): TraceSpan[] => [
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

const topology: TraceTopology = {
  range: 'live',
  windowMinutes: 5,
  gateway: {
    requests: 10,
    errors: 1,
    inFlight: 2,
    overheadP50Us: 4_900,
    overheadP95Us: 38_000,
    failovers: 1,
    stages: [{ name: 'gateway.route', p50Us: 400, p95Us: 900, samples: 10 }],
  },
  clients: [{ clientKind: 'claude-code', requests: 10, errors: 1, perMinute: 2, activeSessions: 1 }],
  providers: [
    {
      providerId: 'copilot',
      name: 'GitHub Copilot',
      requests: 10,
      errors: 1,
      attempts: 11,
      rateLimited: 1,
      failovers: 1,
      perMinute: 2,
      ttfbP95Ms: 1400,
      accounts: [{ id: 'gh-alice', name: 'gh-alice', status: 'cooldown', enabled: true, cooldownUntilUtc: at(120_000), remainingRequests: '0' }],
    },
  ],
};

describe('trace layouts', () => {
  it('puts clients and providers around the Astra hub, with warn colouring', () => {
    const graph = buildTopology(topology, t, label, new Map());
    expect(graph.nodes.map((n) => n.id)).toEqual([HUB_ID, 'client:claude-code', 'provider:copilot']);
    expect(graph.nodes[0].type).toBe('hub');
    const client = graph.nodes.find((n) => n.id === 'client:claude-code')!;
    expect(client.data).toMatchObject({ title: 'Claude Code', client: 'claude-code', tone: 'ok' });
    const provider = graph.nodes.find((n) => n.id === 'provider:copilot')!;
    expect(provider.data).toMatchObject({ title: 'GitHub Copilot', tone: 'warn' });
    expect(graph.edges.map((e) => [e.source, e.target])).toEqual([
      ['client:claude-code', HUB_ID],
      [HUB_ID, 'provider:copilot'],
    ]);
    expect(graph.edges.map((e) => (e.data as { role: string }).role)).toEqual(['client', 'provider']);
    expect(graph.edges.every((e) => (e.data as { requests: string[] }).requests.length === 0)).toBe(true);
  });

  it('puts every request in flight on its lines and adds the nodes the window has not seen', () => {
    const r = (id: string, extra: Partial<RequestSummary>): RequestSummary =>
      ({ id, startedAtUtc: at(0), clientKind: 'claude-code', status: 'pending', inboundProtocol: 'anthropic', passthrough: false, stream: true,
         totalInputTokens: 0, totalOutputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, reasoningTokens: 0, costNanoUsd: 0, usageSource: 'missing', ...extra }) as RequestSummary;
    const live = [r('a', { providerId: 'copilot' }), r('b', { providerId: 'deepseek', clientKind: 'codex' }), r('c', { status: 'success', providerId: 'copilot' })];
    const graph = buildTopology(topology, t, label, new Map([['deepseek', { name: 'DeepSeek' }]]), live);
    expect(graph.nodes.map((n) => n.id)).toEqual(expect.arrayContaining(['client:codex', 'provider:deepseek']));
    expect(graph.nodes.find((n) => n.id === 'provider:deepseek')!.data).toMatchObject({ title: 'DeepSeek', live: 1 });
    // a finished request still rides its lines (its burst), but only pending ones count as in flight
    expect(graph.nodes.find((n) => n.id === 'provider:copilot')!.data).toMatchObject({ live: 1 });
    expect((graph.edges.find((e) => e.target === 'provider:copilot')!.data as { requests: string[] }).requests).toEqual(['a', 'c']);
    expect(graph.nodes.find((n) => n.id === HUB_ID)!.data).toMatchObject({ busy: true });
  });

  it('reads the phase of a live request from its fields', () => {
    expect(livePhase({ status: 'pending', providerId: null })).toBe('arriving');
    expect(livePhase({ status: 'pending', providerId: 'p' })).toBe('upstream');
    expect(livePhase({ status: 'pending', providerId: 'p', httpStatus: 200 })).toBe('streaming');
    expect(livePhase({ status: 'success', providerId: 'p', httpStatus: 200 })).toBe('done');
    expect(livePhase({ status: 'upstream_error', providerId: 'p', httpStatus: 429 })).toBe('failed');
  });

  it('draws a failover as a fork of attempts and mirrors the response leg', () => {
    const trace: TraceRequest = { request: call('r1', 0, 8420), inFlight: false, spans: failoverSpans('r1'), parent: null, children: [] };
    const graph = buildRequest(trace, t, label, { name: 'GitHub Copilot' });
    const ids = graph.nodes.map((n) => n.id);
    expect(ids).toEqual(expect.arrayContaining(['agent', 'route', 'privacy', 'encode', 'attempt:0', 'attempt:1', 'provider', 'decode', 'reencode', 'restore']));
    expect(ids).not.toContain('middleware');
    expect(ids).not.toContain('routing');
    const failed = graph.edges.find((e) => e.id === 'e:attempt:0')!;
    expect(failed.style).toMatchObject({ strokeDasharray: '5 4' });
    expect(failed.label).toBe('429');
    expect(graph.nodes.find((n) => n.id === 'attempt:0')!.data).toMatchObject({ tone: 'error' });
    // the response leg ends back at the agent
    expect(graph.edges.some((e) => e.target === 'agent' && e.targetHandle === 'bottom')).toBe(true);
  });

  it('gives each provider of a pool failover its own node, the answering one last', () => {
    const spans = failoverSpans('r1').map((sp) =>
      sp.name !== 'upstream.attempt'
        ? sp
        : sp.startUs === Math.min(...failoverSpans('r1').filter((x) => x.name === 'upstream.attempt').map((x) => x.startUs))
          ? { ...sp, attributes: { ...sp.attributes, 'astra.provider.id': 'relay', 'astra.provider.name': 'Relay', 'astra.attempt.retry': 'provider_failover' } }
          : { ...sp, attributes: { ...sp.attributes, 'astra.provider.id': 'copilot', 'astra.provider.name': 'GitHub Copilot' } },
    );
    const trace: TraceRequest = { request: call('r1', 0, 8420, { providerId: 'copilot' }), inFlight: false, spans, parent: null, children: [] };
    const graph = buildRequest(trace, t, label, { name: 'GitHub Copilot' });
    expect(graph.nodes.find((n) => n.id === 'provider:relay')!.data).toMatchObject({ title: 'Relay', tone: 'error' });
    expect(graph.edges.find((e) => e.id === 'e:attempt:0')!.target).toBe('provider:relay');
    expect(graph.edges.find((e) => e.id === 'e:attempt:1')!.target).toBe('provider');
    expect(graph.nodes.find((n) => n.id === 'attempt:0')!.data).toMatchObject({ lines: ['Relay', expect.any(String)] });
  });

  it('splits a request into Astra, retries, first byte, first token and generation', () => {
    const split = splitRequestTime(failoverSpans('r1'))!;
    expect(split.astraUs).toBe(5_000);
    expect(split.retryUs).toBe(620_000);
    expect(split.ttfbUs).toBe(940_000);
    expect(split.firstTokenUs).toBe(270_000);
    expect(split.generationUs).toBe(6_580_000);
    expect(split.totalUs).toBe(8_420_000);
    expect(split.routingUs).toBe(0);
  });

  const routed = (id: string, startUs: number, durationUs: number, attributes: TraceSpan['attributes']): TraceSpan =>
    span(id, 'gateway.resolve', startUs, durationUs, { attributes });

  it('draws a judged request as its own node and takes the judge out of resolve', () => {
    const spans = [
      span('r1', 'gateway.request', 0, 5_000_000),
      span('r1', 'gateway.privacy', 700, 2_000),
      routed('r1', 3_000, 2_030_000, {
        'astra.provider.name': 'Grok',
        'astra.route.mode': 'session',
        'astra.route.source': 'judge',
        'astra.route.grade': 'standard',
        'astra.route.model': 'grok-4.7',
        'astra.route.confidence': 0.82,
        'astra.route.judge.protocol': 'typesafe',
        'astra.route.judge.model': 'jev-1',
        'astra.route.judge.ms': 2010,
        'astra.route.judge.new_connection': true,
      }),
      span('r1', 'upstream.attempt', 2_050_000, 2_000_000, { attributes: { 'http.response.status_code': 200 } }),
    ];
    const trace: TraceRequest = { request: call('r1', 0, 5000, { requestedModel: 'astra-auto' }), inFlight: false, spans, parent: null, children: [] };
    const graph = buildRequest(trace, t, label, { name: 'Grok' });
    const order = graph.nodes.map((n) => n.id);
    expect(order.indexOf('routing')).toBeGreaterThan(order.indexOf('privacy'));
    expect(order.indexOf('routing')).toBeLessThan(order.indexOf('resolve'));
    expect(graph.nodes.find((n) => n.id === 'routing')!.data).toMatchObject({
      title: 'Smart routing',
      tone: 'ok',
      lines: ['standard', 'Judged · 2.01 s'],
    });
    // 2.03 s of resolve, of which 2.01 s was the judge
    expect(graph.nodes.find((n) => n.id === 'resolve')!.data.lines).toEqual(['Grok', '20 ms']);
    const split = splitRequestTime(spans)!;
    expect(split.routingUs).toBe(2_010_000);
    expect(split.astraUs).toBe(2_050_000);
  });

  it('warns on a fallback and does not charge a reused decision to the judge', () => {
    const fallback: TraceRequest = {
      request: call('r1', 0, 1000),
      inFlight: false,
      parent: null,
      children: [],
      spans: [
        routed('r1', 0, 320_000, {
          'astra.provider.name': 'Grok',
          'astra.route.source': 'fallback',
          'astra.route.grade': 'standard',
          'astra.route.fallback_reason': 'low_confidence',
          'astra.route.judge.ms': 300,
        }),
        span('r1', 'upstream.attempt', 320_000, 10_000, { attributes: { 'http.response.status_code': 200 } }),
      ],
    };
    const fallen = buildRequest(fallback, t, label, null);
    expect(fallen.nodes.find((n) => n.id === 'routing')!.data).toMatchObject({ tone: 'warn', lines: ['standard', 'Fallback · 300 ms'] });

    const moved: TraceRequest = {
      ...fallback,
      spans: [
        routed('r1', 0, 15_000, {
          'astra.provider.name': 'Grok',
          'astra.route.source': 'judge',
          'astra.route.grade': 'strong',
          'astra.route.adjusted_from': 'light',
        }),
      ],
    };
    expect(buildRequest(moved, t, label, null).nodes.find((n) => n.id === 'routing')!.data).toMatchObject({ tone: 'warn', lines: ['strong', 'Judged'] });

    const pinned: TraceRequest = {
      ...fallback,
      spans: [
        span('r1', 'gateway.request', 0, 100_000),
        routed('r1', 0, 15_000, { 'astra.provider.name': 'Grok', 'astra.route.source': 'pin', 'astra.route.grade': 'light' }),
        span('r1', 'upstream.attempt', 20_000, 10_000, { attributes: { 'http.response.status_code': 200 } }),
      ],
    };
    const graph = buildRequest(pinned, t, label, null);
    expect(graph.nodes.find((n) => n.id === 'routing')!.data).toMatchObject({ tone: 'ok', lines: ['light', 'Session pin'] });
    expect(graph.nodes.find((n) => n.id === 'resolve')!.data).toMatchObject({ lines: ['Grok', '15 ms'] });
    expect(splitRequestTime(pinned.spans)!.routingUs).toBe(0);
  });

  it('keeps the routing node when a pool resolves again, without charging the judge to the member that answered', () => {
    const spans = [
      routed('r1', 1_000, 2_000_000, {
        'astra.provider.name': 'Relay',
        'astra.route.source': 'judge',
        'astra.route.grade': 'fast',
        'astra.route.judge.ms': 1900,
      }),
      routed('r1', 2_100_000, 4_000, { 'astra.provider.name': 'GitHub Copilot' }),
      span('r1', 'upstream.attempt', 2_200_000, 100_000, { attributes: { 'http.response.status_code': 200, 'astra.provider.id': 'copilot' } }),
    ];
    const trace: TraceRequest = { request: call('r1', 0, 5000, { providerId: 'copilot' }), inFlight: false, spans, parent: null, children: [] };
    const graph = buildRequest(trace, t, label, { name: 'GitHub Copilot' });
    expect(graph.nodes.find((n) => n.id === 'routing')!.data).toMatchObject({ lines: ['fast', 'Judged · 1.90 s'], tone: 'ok' });
    expect(graph.nodes.find((n) => n.id === 'resolve')!.data.lines).toEqual(['GitHub Copilot', '4.0 ms']);
  });

  it('mentions the grade on a session call that was smart-routed', () => {
    const spans = [routed('m1', 0, 10, { 'astra.route.source': 'pin', 'astra.route.grade': 'light' })];
    const graph = buildSession([call('m1', 0, 1000)], spans, t, new Map());
    expect(graph.nodes.find((n) => n.id === 'm1')!.data.lines).toContain('Smart routing · light');
  });

  it('writes the tools between two calls on the line joining them and hangs sub-agents underneath', () => {
    const calls = [
      call('m1', 0, 3_000, { toolCalls: 1 }),
      call('s1', 3_200, 2_000, { callKind: 'sidechain', chainKey: 'fp:sub', parentRequestId: 'm1', linkSource: 'inferred', providerId: 'anthropic' }),
      call('m2', 6_000, 2_000, { parentRequestId: 'm1', step: 2, linkSource: 'tool_call' }),
    ];
    const spans = [...failoverSpans('m2'), span('m1', 'gateway.response', 0, 10, { attributes: { 'astra.response.tool_names': 'Task' } })];
    const graph = buildSession(calls, spans, t, new Map());
    expect(graph.nodes.map((n) => n.id).sort()).toEqual(['m1', 'm2', 's1']); // no tool nodes
    const step = graph.edges.find((e) => e.source === 'm1' && e.target === 'm2')!;
    expect(step.type).toBe('step');
    expect(step.data as StepData).toMatchObject({ label: 'Task · 3000 ms', selectId: 'tool:m2' });
    expect(graph.edges.map((e) => [e.source, e.target])).toEqual(expect.arrayContaining([['m1', 's1']]));
    const sub = graph.nodes.find((n) => n.id === 's1')!;
    expect(sub.position.y).toBeGreaterThan(graph.nodes.find((n) => n.id === 'm1')!.position.y);
    // two attempts: orange, flagged, and the turn's only anomaly
    expect(graph.nodes.find((n) => n.id === 'm2')!.data).toMatchObject({ tone: 'warn', flag: { text: t('traces.flag.retry'), tone: 'warn' } });
    expect(graph.anomalies).toEqual(['m2']);

    const { rows, totalMs } = buildTimeline(calls, spans, t);
    expect(totalMs).toBe(8_000);
    expect(rows.map((r) => r.kind)).toEqual(['turn', 'llm', 'sub', 'tool', 'llm', 'attempt-fail', 'attempt-ok']);
    expect(rows.find((r) => r.kind === 'tool')!.label).toBe(t('traces.session.toolRow', { names: 'Task' }));
    const m2 = rows.find((r) => r.id === 'm2')!;
    expect(m2.flag).toBe('retry');
    // waiting until the first token (1.835 s into the call), on the turn's axis
    expect(m2.segments).toEqual([{ kind: 'wait', from: 6_000, to: 7_835 }]);

    const time = splitTurnTime(calls, spans);
    expect(time.totalMs).toBe(8_000);
    expect(time.toolMs).toBe(1_000); // 5.2 → 6.0 s uncovered
    expect(time.retryMs).toBe(620);
  });

  const mains = (n: number, extra: (i: number) => Partial<TraceCall> = () => ({})) =>
    Array.from({ length: n }, (_, i) => call(`m${i}`, i * 4_000, 3_000, { step: i + 1, toolCalls: 1, ...extra(i) }));

  it('wraps a long main chain into rows, the wrapping step running back along the gap', () => {
    const graph = buildSession(mains(6), [], t, new Map(), { columns: 4, collapse: true, expanded: new Set() });
    const pos = (id: string) => graph.nodes.find((n) => n.id === id)!.position;
    expect([pos('m0').y, pos('m3').y]).toEqual([pos('m0').y, pos('m0').y]);
    expect(pos('m4')).toMatchObject({ x: pos('m0').x });
    expect(pos('m4').y).toBeGreaterThan(pos('m3').y);
    const data = (from: string, to: string) => graph.edges.find((e) => e.source === from && e.target === to)!.data as StepData;
    expect(data('m0', 'm1').wrap).toBeUndefined();
    const wrap = data('m3', 'm4').wrap!;
    expect(wrap.gapY).toBeGreaterThan(pos('m3').y);
    expect(wrap.gapY).toBeLessThan(pos('m4').y);
    expect(wrap.rightX).toBeGreaterThan(pos('m3').x);
    expect(wrap.leftX).toBeLessThan(pos('m4').x);
  });

  it('folds unremarkable runs of a long turn and keeps the ends and the anomalies', () => {
    const quiet = buildSession(mains(12), [], t, new Map());
    expect(quiet.nodes.map((n) => n.id)).toEqual(['m0', 'group:m1', 'm10', 'm11']);
    expect(quiet.nodes[1].data).toMatchObject({ group: true, title: 'LLM #2–#10' });
    expect(quiet.edges.map((e) => [e.source, e.target])).toEqual([
      ['m0', 'group:m1'],
      ['group:m1', 'm10'],
      ['m10', 'm11'],
    ]);

    const failing = buildSession(mains(12, (i) => (i === 5 ? { status: 'upstream_error', httpStatus: 500 } : {})), [], t, new Map());
    expect(failing.nodes.map((n) => n.id)).toEqual(['m0', 'group:m1', 'm5', 'group:m6', 'm10', 'm11']);
    expect(failing.anomalies).toEqual(['m5']);
    expect(failing.nodes.find((n) => n.id === 'm5')!.data).toMatchObject({ tone: 'error', flag: { tone: 'error' } });

    const opened = buildSession(mains(12), [], t, new Map(), { columns: 4, collapse: true, expanded: new Set(['group:m1']) });
    expect(opened.nodes).toHaveLength(12);
    // a short turn never folds
    expect(buildSession(mains(8), [], t, new Map()).nodes).toHaveLength(8);
  });

  it('draws the calls in flight after the last step, with a moving line and a growing bar', () => {
    const live = (id: string, extra: Partial<RequestSummary> = {}) =>
      ({ id, startedAtUtc: at(9_000), status: 'pending', providerId: 'copilot', requestedModel: 'claude-sonnet-5-5', sessionKey: 'cc:abc', ...extra }) as RequestSummary;
    const calls = mains(4);
    const spans = [span('m3', 'gateway.response', 0, 10, { attributes: { 'astra.response.tool_names': 'Bash' } })];
    const graph = buildSession(calls, spans, t, new Map(), { columns: 4, collapse: true, expanded: new Set(), pending: [live('p1'), live('p2'), live('m0')] });
    // m0 is stored already: only the two others are drawn, stacked in the next slot (a new row here)
    const p1 = graph.nodes.find((n) => n.id === 'p1')!;
    const p2 = graph.nodes.find((n) => n.id === 'p2')!;
    expect(graph.nodes.filter((n) => n.id === 'm0')).toHaveLength(1);
    expect(p1.data).toMatchObject({ pending: 'p1', title: 'LLM · sonnet-5-5' });
    expect(p1.position.x).toBe(graph.nodes.find((n) => n.id === 'm0')!.position.x);
    expect(p2.position.y).toBeGreaterThan(p1.position.y);
    const edge = graph.edges.find((e) => e.target === 'p1')!;
    expect(edge).toMatchObject({ source: 'm3', animated: true });
    expect(edge.data as StepData).toMatchObject({ tools: 'Bash' });
    expect((edge.data as StepData).wrap).toBeDefined();

    const { rows, totalMs } = buildTimeline(calls, spans, t, [live('p1')], T0 + 20_000);
    expect(totalMs).toBe(20_000);
    expect(rows[rows.length - 1]).toMatchObject({ id: 'p1', kind: 'live', startMs: 9_000, endMs: 20_000 });
  });

  it('splits a call into waiting, thinking, writing and tool calls from the response events', () => {
    const spans = [
      span('c', 'gateway.response', 50_000, 3_300_000, {
        events: [
          { name: 'reasoning_start', offset_us: 100_000 },
          { name: 'reasoning_end', offset_us: 2_100_000 },
          { name: 'text_start', offset_us: 2_200_000 },
          { name: 'text_end', offset_us: 3_000_000 },
          { name: 'tool_call', offset_us: 3_100_000, attributes: { name: 'Read' } },
          { name: 'tool_call', offset_us: 3_200_000, attributes: { name: 'Grep' } },
        ],
      }),
    ];
    const phases = callPhases(spans)!;
    expect(phases).toMatchObject({ waitMs: 100, thinkMs: 2_000, writeMs: 800, toolMs: 250, tools: ['Read', 'Grep'] });
    expect(phases.segments.map((s) => s.kind)).toEqual(['wait', 'think', 'write', 'tool']);
    expect(phaseLine(phases, t)).toBe('Thinking 2000 ms · Writing 800 ms · Writing tool calls 250 ms');
    expect(callPhases([])).toBeNull();
    // over a turn: the wait loses the retried attempt's time
    expect(turnPhases([call('c', 0, 3_400)], spans)).toEqual({ wait: 100, think: 2_000, write: 800, tool: 250 });
  });

  it('colours a call in flight by the stages the live feed reports', () => {
    const r = { startedAtUtc: at(0), totalMs: null, phases: 'thinking@1500,writing@6000,tool:Bash@9000' };
    expect(livePhases(r, T0 + 10_000)).toEqual([
      { kind: 'wait', from: 0, to: 1_500 },
      { kind: 'think', from: 1_500, to: 6_000 },
      { kind: 'write', from: 6_000, to: 9_000 },
      { kind: 'tool', from: 9_000, to: 10_000 },
    ]);
    // nothing produced yet: all waiting, up to now
    expect(livePhases({ startedAtUtc: at(0), totalMs: null, phases: null }, T0 + 2_000)).toEqual([{ kind: 'wait', from: 0, to: 2_000 }]);
  });

  it('says what the agent is doing: the live call’s stage, else what the last call left it doing', () => {
    const live = (stage: string | null, httpStatus: number | null = 200) =>
      ({ id: 'x', startedAtUtc: at(10_000), status: 'pending', providerId: 'copilot', httpStatus, stage, requestedModel: 'claude-sonnet-5-5' }) as RequestSummary;
    expect(agentStatus(live('thinking'), [], [])).toMatchObject({ kind: 'thinking', since: T0 + 10_000 });
    expect(agentStatus(live('tool:Bash'), [], [])).toMatchObject({ kind: 'tool', tool: 'Bash' });
    expect(agentStatus(live(null, null), [], [])).toMatchObject({ kind: 'upstream' });
    expect(agentStatus(live(null), [], [])).toMatchObject({ kind: 'waiting' });

    const toolSpans = [span('m1', 'gateway.response', 0, 10, { attributes: { 'astra.response.tool_names': 'Read,Read' } })];
    expect(agentStatus(undefined, [call('m1', 0, 3_000, { toolCalls: 2 })], toolSpans)).toEqual({ kind: 'tools', tools: 'Read×2', since: T0 + 3_000 });
    expect(agentStatus(undefined, [call('m1', 0, 3_000)], [])).toEqual({ kind: 'idle', since: T0 + 3_000 });
    expect(agentStatus(undefined, [call('m1', 0, 3_000, { status: 'upstream_error' })], [])).toMatchObject({ kind: 'failed' });
    expect(agentStatus(undefined, [], [])).toBeNull();
  });
});

// ---------------------------------------------------------------- page

const hooks = vi.hoisted(() => ({
  topology: null as TraceTopology | null,
  topologyError: null as unknown,
  session: null as TraceSessionDetail | null,
  request: null as TraceRequest | null,
  live: [] as RequestSummary[],
}));

vi.mock('../api/hooks', () => ({
  keys: {},
  useProviders: () => ({ data: [{ id: 'copilot', name: 'GitHub Copilot', icon: 'copilot', templateId: 'github-copilot' }] }),
  useTraceTopology: () => ({ data: hooks.topology ?? undefined, isLoading: false, isError: hooks.topologyError !== null, error: hooks.topologyError }),
  useTraceSessions: () => ({ data: [], isLoading: false }),
  useTraceSession: () => ({ data: hooks.session, isLoading: false }),
  useTraceRequest: () => ({ data: hooks.request ?? undefined, isLoading: false }),
  useRequests: () => ({ data: { items: [] }, isLoading: false }),
  useRequest: () => ({ data: undefined }),
  useRequestRate: () => ({ data: undefined }),
  useTunnel: () => ({ data: undefined }),
}));
vi.mock('../api/liveRequests', () => ({
  useLiveRequestFeed: () => {},
  useLiveRequestList: () => hooks.live,
  useLiveRequestsConnected: () => true,
  useLiveRequest: (id: string) => hooks.live.find((r) => r.id === id),
  peekLiveRequest: (id: string) => hooks.live.find((r) => r.id === id),
}));
vi.mock('../components/icons', () => ({
  CLIENT_META: { 'claude-code': { name: 'Claude Code' } },
  clientName: (kind: string) => (kind === 'claude-code' ? 'Claude Code' : kind),
  ClientIcon: () => <span />,
  ClientGlyph: () => <span />,
  ProviderIcon: () => <span />,
  ModelGlyph: () => <span />,
  ProtocolGlyph: () => <span />,
}));
vi.mock('../components/layout/AppShell', () => ({
  useShellLayout: () => ({ sidebarMode: 'expanded', sidebarWidth: 224, drawerOpen: false, setDrawerOpen: () => {}, toggleSidebar: () => {}, narrow: false }),
  TOOLBAR_RIGHT_RESERVE: 0,
}));
vi.mock('../shell/appearance', async (orig) => ({
  ...(await orig<Record<string, unknown>>()),
  useAppearance: () => ({ theme: 'light', resolvedTheme: 'light' }),
}));
vi.mock('motion/react', async (orig) => ({ ...(await orig<Record<string, unknown>>()), useReducedMotion: () => true }));

// React Flow measures the DOM; a stand-in renders the nodes through their real node components.
vi.mock('@xyflow/react', async () => {
  const { useState } = await import('react');
  type MockNode = { id: string; type: string; data: unknown; selected?: boolean };
  type MockEdge = { id: string; type: string; data?: unknown; label?: ReactNode };
  return {
    ReactFlowProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
    ReactFlow: ({
      nodes,
      edges,
      nodeTypes,
      edgeTypes,
      onNodeClick,
      children,
    }: {
      nodes: MockNode[];
      edges: MockEdge[];
      nodeTypes: Record<string, ComponentType<Record<string, unknown>>>;
      edgeTypes: Record<string, ComponentType<Record<string, unknown>>>;
      onNodeClick?: (e: unknown, n: MockNode) => void;
      children: ReactNode;
    }) => (
      <div>
        {nodes.map((n) => {
          const View = nodeTypes[n.type];
          return (
            <div key={n.id} data-testid={`node-${n.id}`} onClick={(e) => onNodeClick?.(e, n)}>
              <View id={n.id} data={n.data} selected={Boolean(n.selected)} type={n.type} />
            </div>
          );
        })}
        {edges.map((e) => {
          const View = edgeTypes[e.type];
          return View ? (
            <div key={e.id} data-testid={`edge-${e.id}`}>
              <View id={e.id} data={e.data} label={e.label} sourceX={0} sourceY={0} targetX={0} targetY={0} />
            </div>
          ) : null;
        })}
        {children}
      </div>
    ),
    Background: () => null,
    MiniMap: () => null,
    Panel: ({ children, className }: { children: ReactNode; className?: string }) => <div className={className}>{children}</div>,
    Handle: () => null,
    BaseEdge: () => null,
    EdgeLabelRenderer: ({ children }: { children: ReactNode }) => <>{children}</>,
    getBezierPath: () => ['', 0, 0],
    Position: { Left: 'left', Right: 'right', Top: 'top', Bottom: 'bottom' },
    BackgroundVariant: { Dots: 'dots' },
    ConnectionMode: { Loose: 'loose' },
    MarkerType: { ArrowClosed: 'arrowclosed' },
    useNodesState: (initial: MockNode[]) => {
      const [nodes, setNodes] = useState(initial);
      return [nodes, setNodes, () => {}];
    },
    useReactFlow: () => ({
      fitView: () => Promise.resolve(true),
      zoomIn: () => Promise.resolve(true),
      zoomOut: () => Promise.resolve(true),
      getNode: () => undefined,
      getZoom: () => 1,
      setCenter: () => Promise.resolve(true),
    }),
    useViewport: () => ({ x: 0, y: 0, zoom: 1 }),
  };
});

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

const show = (path: string) => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <MemoryRouter initialEntries={[path]}>
            <Routes>
              <Route path="/traces" element={<TracesPage />} />
              <Route path="/traces/sessions/:key" element={<TracesPage />} />
              <Route path="/traces/requests/:id" element={<TracesPage />} />
            </Routes>
          </MemoryRouter>
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

afterEach(() => {
  cleanup();
  localStorage.clear();
  hooks.topologyError = null;
  hooks.live = [];
});

describe('TracesPage', () => {
  it('shows the topology and opens details only with the 详情 switch on', () => {
    hooks.topology = topology;
    show('/traces');
    expect(screen.getByText('Claude Code')).toBeInTheDocument();
    expect(screen.getByText('GitHub Copilot')).toBeInTheDocument();

    // switch off (the default): a click selects, nothing pops up
    fireEvent.click(screen.getByTestId('node-provider:copilot'));
    expect(screen.queryByText(t('traces.kv.attempts'))).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('switch', { name: t('traces.canvas.details') }));
    expect(screen.getByText(t('traces.kv.attempts'))).toBeInTheDocument();
    expect(screen.getByText('gh-alice')).toBeInTheDocument();
    expect(localStorage.getItem('astra.traces.inspector')).toBe('1');

    // × turns the switch off again
    fireEvent.click(screen.getByRole('button', { name: t('traces.canvas.close') }));
    expect(screen.queryByText(t('traces.kv.attempts'))).not.toBeInTheDocument();
    expect(screen.getByRole('switch', { name: t('traces.canvas.details') })).toHaveAttribute('aria-checked', 'false');
  });

  it('says so when the connected server predates tracing instead of showing an empty canvas', async () => {
    const { ApiError } = await import('../api/client');
    hooks.topology = null;
    hooks.topologyError = new ApiError('Not found: GET /api/traces/topology', 404);
    show('/traces');
    expect(screen.getByText(t('traces.error.unsupportedTitle'))).toBeInTheDocument();
    expect(screen.queryByTestId('node-astra')).not.toBeInTheDocument();
  });

  const sessionOf = (calls: TraceCall[], spans: TraceSpan[], active = false): TraceSessionDetail => ({
    session: { sessionKey: 'cc:abc', clientKind: 'claude-code', firstAtUtc: at(0), lastAtUtc: at(4_000), requests: calls.length, turns: 1, errors: 0, costNanoUsd: 2_000_000, inputTokens: 2000, outputTokens: 100, providerIds: ['copilot'], active },
    turns: [{ turnId: 'm1', startedAtUtc: at(0), requests: calls.length, errors: 0, costNanoUsd: 2_000_000, durationMs: 6_000 }],
    turnId: 'm1',
    calls,
    spans,
  });

  it('shows a session turn as a canvas and as a timeline', () => {
    const calls = [call('m1', 0, 3_000, { toolCalls: 1 }), call('m2', 4_000, 2_000, { parentRequestId: 'm1', step: 2, linkSource: 'tool_call' })];
    hooks.session = sessionOf(calls, [span('m1', 'gateway.response', 0, 10, { attributes: { 'astra.response.tool_names': 'Read' } })]);
    show('/traces/sessions/cc%3Aabc');
    expect(screen.getByTestId('node-m1')).toBeInTheDocument();
    expect(screen.queryByTestId('node-tool:m2')).not.toBeInTheDocument();
    expect(screen.getByText(t('traces.session.time.title'))).toBeInTheDocument();

    // the tools are the label of the line between the calls; clicking it (details on) shows the gap
    fireEvent.click(screen.getByRole('switch', { name: t('traces.canvas.details') }));
    fireEvent.click(screen.getByRole('button', { name: 'Read · 1000 ms' }));
    expect(screen.getByText(t('traces.kv.gap'))).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: t('traces.session.view.timeline') }));
    expect(screen.getByText(t('traces.session.toolRow', { names: 'Read' }))).toBeInTheDocument();
    expect(screen.getByText(t('traces.phase.think'))).toBeInTheDocument(); // the phase legend
    expect(screen.queryByTestId('node-m1')).not.toBeInTheDocument();
  });

  it('opens a long turn on the timeline, jumps between anomalies and expands folded runs on the canvas', () => {
    const calls = Array.from({ length: 16 }, (_, i) =>
      call(`m${i}`, i * 4_000, 3_000, { step: i + 1, toolCalls: 1, ...(i === 7 ? { status: 'upstream_error' as const, httpStatus: 500 } : {}) }),
    );
    hooks.session = sessionOf(calls, []);
    show('/traces/sessions/cc%3Aabc');
    expect(screen.queryByTestId('node-m0')).not.toBeInTheDocument();
    expect(screen.getByText(t('traces.flag.error'))).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: t('traces.session.nextAnomaly', { count: 1 }) }));
    expect(screen.getByRole('button', { name: /^LLM #8 · / })).toHaveAttribute('aria-pressed', 'true');

    fireEvent.click(screen.getByRole('button', { name: t('traces.session.view.canvas') }));
    expect(screen.getByTestId('node-group:m1')).toBeInTheDocument();
    expect(screen.queryByTestId('node-m3')).not.toBeInTheDocument();
    fireEvent.click(screen.getByTestId('node-group:m1'));
    expect(screen.getByTestId('node-m3')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: t('traces.session.expandAll') }));
    expect(screen.getAllByTestId(/^node-m\d+$/)).toHaveLength(16);
  });

  it('draws the call in flight with what the model is doing', () => {
    hooks.session = sessionOf([call('m1', 0, 3_000, { toolCalls: 1 })], []);
    hooks.live = [
      { id: 'p1', startedAtUtc: new Date().toISOString(), status: 'pending', providerId: 'copilot', httpStatus: 200, stage: 'thinking', requestedModel: 'claude-sonnet-5-5', sessionKey: 'cc:abc' } as RequestSummary,
      { id: 'other', startedAtUtc: new Date().toISOString(), status: 'pending', providerId: 'copilot', sessionKey: 'cc:else' } as RequestSummary,
    ];
    show('/traces/sessions/cc%3Aabc');
    expect(screen.getByTestId('node-p1')).toHaveTextContent(t('traces.agent.thinking'));
    expect(screen.queryByTestId('node-other')).not.toBeInTheDocument();
    // the session looked quiet, but a call is running: the agent chip says so
    expect(screen.getAllByText(t('traces.agent.thinking')).length).toBe(2);
  });

  it('shows a call in flight that opened a new turn as that turn, not after the previous one', () => {
    hooks.session = sessionOf([call('m1', 0, 3_000)], []);
    hooks.live = [
      { id: 'p1', startedAtUtc: new Date().toISOString(), status: 'pending', providerId: 'copilot', stage: 'writing', httpStatus: 200, requestedModel: 'claude-sonnet-5-5', sessionKey: 'cc:abc', turnId: 'p1', callKind: 'main' } as RequestSummary,
    ];
    show('/traces/sessions/cc%3Aabc');
    expect(screen.getByTestId('node-p1')).toHaveTextContent(t('traces.agent.writing'));
    expect(screen.queryByTestId('node-m1')).not.toBeInTheDocument();
    expect(screen.getByText(t('traces.session.turn', { n: 2 }))).toBeInTheDocument();
  });

  it('tells what each step of a request recorded, and splits its generation by phase', () => {
    const spans = failoverSpans('r1').map((s) =>
      s.name === 'gateway.request'
        ? { ...s, attributes: { 'astra.request.messages': 42, 'astra.request.tools': 18, 'astra.request.system_chars': 12_000, 'gen_ai.request.max_tokens': 32_000 } }
        : s.name === 'gateway.response'
          ? {
              ...s,
              attributes: { ...s.attributes, 'astra.response.reasoning_chars': 900, 'astra.response.text_chars': 300 },
              events: [
                { name: 'first_token', offset_us: 1_835_000 },
                { name: 'reasoning_start', offset_us: 1_835_000 },
                { name: 'reasoning_end', offset_us: 4_835_000 },
                { name: 'text_start', offset_us: 4_900_000 },
                { name: 'text_end', offset_us: 7_900_000 },
              ],
            }
          : s,
    );
    hooks.request = { request: call('r1', 0, 8_420), inFlight: false, spans, parent: null, children: [] };
    show('/traces/requests/r1');
    // the time bar splits the generation into thinking and writing
    expect(screen.getAllByText(t('traces.phase.think')).length).toBeGreaterThan(0);
    expect(screen.queryByText(t('traces.request.time.generation'))).not.toBeInTheDocument();

    // the 详情 switch is one setting for every canvas: turn it on whatever an earlier view left it at
    const details = screen.getByRole('switch', { name: t('traces.canvas.details') });
    if (details.getAttribute('aria-checked') !== 'true') fireEvent.click(details);
    fireEvent.click(screen.getByTestId('node-agent'));
    expect(screen.getByText(t('traces.kv.toolDefs')).nextSibling).toHaveTextContent('18');
    expect(screen.getByText(t('traces.kv.systemChars')).nextSibling).toHaveTextContent(t('traces.unit.chars', { count: '12,000' }));

    fireEvent.click(screen.getByTestId('node-decode'));
    expect(screen.getByText(t('traces.kv.phases')).nextSibling).toHaveTextContent('Thinking 3000 ms · Writing 3000 ms');
    expect(screen.getByText(t('traces.kv.toolCalls')).nextSibling).toHaveTextContent('Read×2 · Grep');
    // and everything the span recorded, folded away
    expect(screen.getByText(t('traces.kv.raw'))).toBeInTheDocument();
    expect(screen.getByText('astra.response.reasoning_chars = 900')).toBeInTheDocument();
  });

  it('shows what the agent of an active session is doing', () => {
    hooks.session = sessionOf([call('m1', 0, 3_000, { toolCalls: 1 })], [span('m1', 'gateway.response', 0, 10, { attributes: { 'astra.response.tool_names': 'Bash' } })], true);
    show('/traces/sessions/cc%3Aabc');
    expect(screen.getByText(t('traces.agent.tools', { names: 'Bash' }))).toBeInTheDocument();
  });
});
