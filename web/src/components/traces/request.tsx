import { useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router';

import { ApiError } from '../../api/client';
import { useRequests, useTraceRequest } from '../../api/hooks';
import type { ClientKind, TraceRequest, TraceSpan } from '../../api/types';
import { useI18n, type TFunction } from '../../i18n';
import { formatDateTime, formatMs, formatTokens, formatUsd } from '../../lib/format';
import { ClientIcon, ProviderIcon } from '../icons';
import { RequestDetailSheet } from '../../pages/RequestsPage';
import { Button, EmptyState, Spinner } from '../ui/controls';
import { TraceCanvas } from './canvas';
import { TraceError } from './error';
import { Inspector, TimeBar } from './inspector';
import { attr, attrNum, attrStr, buildRequest, callPhases, formatUs, PHASE_COLOR, splitRequestTime, SPAN, toolSummary, type ProviderBrand } from './model';
import { labelOfClient, TraceChip, useProviderBrands } from './topology';

/** The most recent requests, to pick one to trace. */
export function RecentRequests() {
  const { t, locale } = useI18n();
  const navigate = useNavigate();
  const requests = useRequests({ page: 1, pageSize: 30 }, true);
  if (requests.isLoading && !requests.data) return <Spinner lines={6} />;
  const items = requests.data?.items ?? [];
  if (items.length === 0) return <EmptyState title={t('traces.request.none')} />;
  return (
    <div className="flex flex-col gap-2">
      <span className="text-[12px] text-[var(--text-secondary)]">{t('traces.request.pick')}</span>
      <div className="card divide-y divide-[var(--separator)]">
        {items.map((r) => (
          <button
            key={r.id}
            type="button"
            onClick={() => navigate(`/traces/requests/${encodeURIComponent(r.id)}`)}
            className="grid w-full grid-cols-[150px_minmax(0,1fr)_minmax(0,1fr)_90px_80px] items-center gap-3 px-3.5 py-2 text-left text-[12.5px] hover:bg-[var(--surface-muted)] max-md:grid-cols-2"
          >
            <span className="num text-[var(--text-secondary)]">{formatDateTime(r.startedAtUtc, locale)}</span>
            <span className="flex min-w-0 items-center gap-2">
              {r.clientKind ? <ClientIcon kind={r.clientKind as ClientKind} size={18} /> : null}
              <span className="truncate">{r.clientKind ? labelOfClient(r.clientKind) : t('traces.topology.direct')}</span>
            </span>
            <span className="truncate">{r.requestedModel ?? '—'}</span>
            <span className={r.status === 'success' ? 'text-[var(--text-secondary)]' : 'text-[var(--red)]'}>{r.httpStatus ?? r.status}</span>
            <span className="num text-right">{formatMs(r.totalMs)}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

/** B · one request: the request leg through Astra to the provider, and the response leg back. */
export function RequestTraceView({ id }: { id: string }) {
  const { t, locale } = useI18n();
  const query = useTraceRequest(id);
  const brands = useProviderBrands();
  const [selected, setSelected] = useState<string | null>(null);
  const [logOpen, setLogOpen] = useState(false);
  const data = query.data;
  const brand = data?.request.providerId ? (brands.get(data.request.providerId) ?? null) : null;
  const graph = useMemo(() => (data ? buildRequest(data, t, labelOfClient, brand) : { nodes: [], edges: [] }), [data, t, brand]);
  const split = useMemo(() => (data ? splitRequestTime(data.spans) : null), [data]);
  // generation by what the model produced (the rest: pauses between phases)
  const generation = useMemo(() => {
    const p = data ? callPhases(data.spans) : null;
    if (!split || !p || p.thinkMs + p.writeMs + p.toolMs <= 0) return null;
    let left = split.generationUs;
    const take = (ms: number) => {
      const us = Math.min(left, Math.max(0, ms * 1000));
      left -= us;
      return us;
    };
    return { think: take(p.thinkMs), write: take(p.writeMs), tool: take(p.toolMs), rest: left };
  }, [data, split]);

  if (query.isLoading && !data) return <Spinner lines={6} />;
  if (query.isError && !data && !(query.error instanceof ApiError && query.error.status === 404 && !/\/api\/traces/.test(query.error.message)))
    return <TraceError error={query.error} />;
  if (!data) return <EmptyState title={t('traces.request.notFound')} action={<Link to="/traces/requests">{t('traces.request.pick')}</Link>} />;
  const r = data.request;
  const attempts = data.spans.filter((s) => s.name === SPAN.attempt).length;

  return (
    <div className="flex h-full min-h-0 flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <TraceChip>
          <span className={`inline-block size-[7px] rounded-full ${r.status === 'success' ? 'bg-[var(--green)]' : data.inFlight ? 'bg-[var(--orange)]' : 'bg-[var(--red)]'}`} />
          <b>{data.inFlight ? t('traces.request.pending') : r.status}</b> {r.httpStatus ?? ''}
        </TraceChip>
        <TraceChip>
          {r.clientKind ? <ClientIcon kind={r.clientKind as ClientKind} size={16} /> : null}
          {r.clientKind ? labelOfClient(r.clientKind) : t('traces.topology.direct')} →
          {brand ? <ProviderIcon name={brand.name} icon={brand.icon} colorKey={brand.templateId} size={16} /> : null}
          <b>{brand?.name ?? r.providerName ?? '—'}</b>
        </TraceChip>
        <TraceChip>{r.requestedModel ?? '—'}</TraceChip>
        {attempts > 1 ? <TraceChip>{t('traces.session.tries', { count: attempts })}</TraceChip> : null}
        <TraceChip>
          {t('traces.kv.duration')} <b>{formatMs(r.totalMs)}</b>
        </TraceChip>
        {r.ttftMs ? (
          <TraceChip>
            {t('traces.kv.firstToken')} <b>{formatMs(r.ttftMs)}</b>
          </TraceChip>
        ) : null}
        <TraceChip>
          <b>{formatUsd(r.costNanoUsd / 1e9)}</b>
        </TraceChip>
        <span className="ml-auto flex flex-wrap gap-2">
          {r.sessionKey ? (
            <Link className="text-[12.5px] text-[var(--accent)]" to={`/traces/sessions/${encodeURIComponent(r.sessionKey)}${r.turnId ? `?turn=${encodeURIComponent(r.turnId)}` : ''}`}>
              {t('traces.request.openSession')} →
            </Link>
          ) : null}
          <Button size="sm" onClick={() => setLogOpen(true)}>
            {t('traces.openInLog')}
          </Button>
        </span>
      </div>
      {data.inFlight ? <div className="card p-3 text-[12.5px] text-[var(--text-secondary)]">{t('traces.request.inFlight')}</div> : null}
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-[11.5px] text-[var(--text-secondary)]">
        <span className="inline-flex items-center gap-1.5">
          <span className="inline-block h-0.5 w-[18px] bg-[var(--label-3)]" />
          {t('traces.request.legend.request')}
        </span>
        <span className="inline-flex items-center gap-1.5">
          <span className="inline-block h-0.5 w-[18px] bg-[var(--accent)]" />
          {t('traces.request.legend.response')}
        </span>
        <span className="inline-flex items-center gap-1.5">
          <span className="inline-block h-0.5 w-[18px]" style={{ background: 'repeating-linear-gradient(90deg, var(--red) 0 5px, transparent 5px 9px)' }} />
          {t('traces.request.legend.failed')}
        </span>
      </div>
      {data.spans.length > 0 ? (
        <TraceCanvas
          className="min-h-0 flex-1"
          graph={graph}
          selected={selected}
          onSelect={setSelected}
          inspector={selected ? <NodeInspector id={selected} data={data} t={t} locale={locale} brand={brand} /> : null}
        />
      ) : null}
      {split ? (
        <TimeBar
          title={t('traces.request.time.title')}
          total={formatUs(split.totalUs)}
          parts={[
            { label: t('traces.request.time.astra'), value: split.astraUs, text: formatUs(split.astraUs), color: 'var(--accent-2)' },
            { label: t('traces.request.time.retries'), value: split.retryUs, text: formatUs(split.retryUs), color: 'var(--red)' },
            { label: t('traces.request.time.ttfb'), value: split.ttfbUs, text: formatUs(split.ttfbUs), color: 'var(--label-3)' },
            { label: t('traces.request.time.firstToken'), value: split.firstTokenUs, text: formatUs(split.firstTokenUs), color: 'color-mix(in oklab, var(--accent) 45%, transparent)' },
            ...(generation
              ? [
                  { label: t('traces.phase.think'), value: generation.think, text: formatUs(generation.think), color: PHASE_COLOR.think },
                  { label: t('traces.phase.write'), value: generation.write, text: formatUs(generation.write), color: PHASE_COLOR.write },
                  { label: t('traces.phase.tool'), value: generation.tool, text: formatUs(generation.tool), color: PHASE_COLOR.tool },
                  ...(generation.rest >= 1000
                    ? [{ label: t('traces.request.time.generationOther'), value: generation.rest, text: formatUs(generation.rest), color: 'color-mix(in oklab, var(--accent) 35%, transparent)' }]
                    : []),
                ]
              : [{ label: t('traces.request.time.generation'), value: split.generationUs, text: formatUs(split.generationUs), color: 'var(--accent)' }]),
          ]}
        />
      ) : null}
      <RequestDetailSheet id={logOpen ? r.id : null} onClose={() => setLogOpen(false)} />
    </div>
  );
}

const bytes = (n: number | undefined) => (n === undefined ? '—' : `${formatTokens(n)} B`);

/** Everything a span recorded — its attributes, then its events at their offsets — for the folded block of an inspector. */
function rawOf(spans: (TraceSpan | undefined)[], t: TFunction): { title: string; lines: string[] } | null {
  const lines = spans.flatMap((s) =>
    s
      ? [
          ...(spans.length > 1 ? [`# ${s.name} · +${formatUs(s.startUs)} · ${formatUs(s.durationUs)}`] : []),
          ...Object.entries(s.attributes ?? {})
            .sort(([a], [b]) => a.localeCompare(b))
            .map(([k, v]) => `${k} = ${String(v)}`),
          ...(s.events ?? []).map(
            (e) => `@ +${formatUs(e.offset_us)} ${e.name}${e.attributes ? ` ${Object.entries(e.attributes).map(([k, v]) => `${k}=${String(v)}`).join(' ')}` : ''}`,
          ),
          ...(s.status === 'error' && s.statusMessage ? [`! ${s.statusMessage}`] : []),
        ]
      : [],
  );
  return lines.length > 0 ? { title: t('traces.kv.raw'), lines } : null;
}

const linkTo = (id: string, label: string) => (
  <Link key={id} className="text-[12.5px] text-[var(--accent)]" to={`/traces/requests/${encodeURIComponent(id)}`}>
    {label}
  </Link>
);

function NodeInspector({ id, data, t, locale, brand }: { id: string; data: TraceRequest; t: TFunction; locale: string; brand: ProviderBrand | null }) {
  const r = data.request;
  const span = (name: string) => data.spans.find((s) => s.name === name);
  const stage = (name: string) => t(`traces.stage.${name}` as 'traces.stage.gateway.route');
  const dur = (s: TraceSpan | undefined) => formatUs(s?.durationUs);
  const yes = (v: unknown) => (v === true ? t('traces.yes') : v === false ? t('traces.no') : '—');
  // when the step began, from the moment the request arrived
  const at = (s: TraceSpan | undefined): [string, string] => [t('traces.kv.startAt'), s ? `+${formatUs(s.startUs)}` : '—'];
  const root = span(SPAN.request);
  const count = (n: number | undefined) => (n === undefined ? '—' : String(n));

  switch (id) {
    case 'agent':
      return (
        <Inspector
          kind={t('traces.kind.agent')}
          icon={r.clientKind ? <ClientIcon kind={r.clientKind as ClientKind} size={28} /> : undefined}
          title={r.clientKind ? labelOfClient(r.clientKind) : t('traces.topology.direct')}
          sub={formatDateTime(r.startedAtUtc, locale)}
          rows={[
            [t('traces.kv.session'), r.sessionKey ?? '—'],
            [t('traces.kv.step'), r.step ? `${t(`traces.kind.${r.callKind ?? 'main'}` as 'traces.kind.main')} · #${r.step}` : '—'],
            [t('traces.kv.link'), t(`traces.link.${r.linkSource ?? 'new_turn'}` as 'traces.link.tool_call')],
            [t('traces.kv.protocols'), `${r.inboundProtocol}${r.stream ? ' · stream' : ''}`],
            [t('traces.kv.model'), r.requestedModel ?? '—'],
            [t('traces.kv.messages'), count(attrNum(root, 'astra.request.messages'))],
            [t('traces.kv.toolDefs'), count(attrNum(root, 'astra.request.tools'))],
            [t('traces.kv.systemChars'), attrNum(root, 'astra.request.system_chars') !== undefined ? t('traces.unit.chars', { count: formatTokens(attrNum(root, 'astra.request.system_chars') ?? 0) }) : '—'],
            [t('traces.kv.maxTokens'), attrNum(root, 'gen_ai.request.max_tokens') !== undefined ? formatTokens(attrNum(root, 'gen_ai.request.max_tokens') ?? 0) : '—'],
            [t('traces.kv.reasoning'), [r.reasoningEffort, r.reasoningMode].filter(Boolean).join(' · ') || '—'],
            [t('traces.kv.traceparent'), r.parentSpanId ? `${r.traceId?.slice(0, 8)}… / ${r.parentSpanId}` : '—'],
            [t('traces.kv.requestId'), r.id],
          ]}
          raw={rawOf([root], t)}
          actions={[
            data.parent ? linkTo(data.parent.id, `← ${t('traces.request.parent')}`) : null,
            ...data.children.map((c) => linkTo(c.id, `${t('traces.request.child')} →`)),
          ]}
        />
      );
    case 'route': {
      const route = span(SPAN.route);
      return (
        <Inspector
          kind={stage(SPAN.route)}
          title={t('traces.node.entry')}
          sub={dur(route)}
          rows={[
            at(route),
            [t('traces.kv.token'), attrStr(route, 'astra.token.id') ?? '—'],
            [t('traces.kv.client'), attrStr(route, 'astra.client.kind') ?? t('traces.topology.direct')],
            [t('traces.kv.bytes'), bytes(attrNum(span(SPAN.body), 'astra.body.bytes'))],
            [t('traces.kv.bodyTime'), dur(span(SPAN.body))],
          ]}
          raw={rawOf([route, span(SPAN.body)], t)}
          code={route?.status === 'error' ? { title: t('traces.kv.status'), lines: [r.errorMessage ?? route.statusMessage ?? ''] } : null}
        />
      );
    }
    case 'middleware': {
      const m = span(SPAN.middleware);
      return (
        <Inspector
          kind={stage(SPAN.middleware)}
          title={t('traces.node.middleware')}
          sub={dur(m)}
          rows={[
            at(m),
            [t('traces.kv.rewritten'), yes(attr(m, 'astra.middleware.rewritten'))],
            [t('traces.kv.bytes'), `${bytes(attrNum(m, 'astra.body.bytes_before'))} → ${bytes(attrNum(m, 'astra.body.bytes'))}`],
          ]}
          raw={rawOf([m], t)}
        />
      );
    }
    case 'privacy': {
      const p = span(SPAN.privacy);
      return (
        <Inspector
          kind={stage(SPAN.privacy)}
          title={t('traces.node.privacy')}
          sub={dur(p)}
          rows={[
            at(p),
            [t('traces.kv.hits'), attrNum(p, 'astra.privacy.hits') ?? 0],
            [t('traces.kv.categories'), attrStr(p, 'astra.privacy.categories') ?? '—'],
            [t('traces.kv.rewritten'), yes(attr(p, 'astra.privacy.rewritten'))],
            [t('traces.kv.restored'), attrNum(p, 'astra.privacy.restorable') ?? 0],
            [t('traces.kv.dryRun'), yes(attr(p, 'astra.privacy.dry_run'))],
          ]}
          raw={rawOf([p], t)}
        />
      );
    }
    case 'resolve': {
      const s = span(SPAN.resolve);
      return (
        <Inspector
          kind={stage(SPAN.resolve)}
          title={t('traces.node.resolve')}
          sub={dur(s)}
          rows={[
            at(s),
            [t('traces.kv.provider'), attrStr(s, 'astra.provider.name') ?? r.providerName ?? '—'],
            [t('traces.kv.model'), attrStr(s, 'gen_ai.request.model') ?? r.requestedModel ?? '—'],
            [t('traces.kv.protocols'), attrStr(s, 'astra.protocol.upstream') ?? '—'],
            [t('traces.kv.capabilities'), formatUs(attrNum(s, 'astra.resolve.capabilities_us'))],
          ]}
          raw={rawOf([s], t)}
        />
      );
    }
    case 'encode': {
      const e = span(SPAN.encode);
      const warnings = attrStr(e, 'astra.encode.warnings');
      return (
        <Inspector
          kind={stage(SPAN.encode)}
          title={attr(e, 'astra.passthrough') === true ? t('traces.node.passthrough') : t('traces.node.encode')}
          sub={dur(e)}
          rows={[
            at(e),
            [t('traces.kv.protocols'), `${attrStr(e, 'astra.protocol.inbound') ?? r.inboundProtocol} → ${attrStr(e, 'astra.protocol.upstream') ?? r.upstreamProtocol ?? '—'}`],
            [t('traces.kv.model'), `${attrStr(e, 'gen_ai.request.model') ?? '—'} → ${attrStr(e, 'astra.upstream.model') ?? '—'}`],
            [t('traces.kv.rewritten'), yes(attr(e, 'astra.encode.rewritten'))],
            [t('traces.kv.lossy'), attrNum(e, 'astra.encode.lossy') ?? 0],
            [t('traces.kv.bytes'), bytes(attrNum(e, 'astra.body.bytes'))],
            ...(attrStr(e, 'astra.claude.mode') ? [[t('traces.kv.claudeMode'), attrStr(e, 'astra.claude.mode')] as [string, string]] : []),
          ]}
          code={warnings ? { title: t('traces.kv.lossy'), lines: warnings.split('; ') } : null}
          raw={rawOf([e], t)}
        />
      );
    }
    case 'provider':
      return (
        <Inspector
          kind={t('traces.kind.provider')}
          icon={brand ? <ProviderIcon name={brand.name} icon={brand.icon} colorKey={brand.templateId} size={28} /> : undefined}
          title={brand?.name ?? r.providerName ?? '—'}
          sub={r.upstreamProtocol ?? undefined}
          rows={[
            [t('traces.kv.model'), r.responseModel && r.responseModel !== r.upstreamModel ? `${r.upstreamModel ?? '—'} → ${r.responseModel}` : (r.upstreamModel ?? r.responseModel ?? '—')],
            [t('traces.kv.finish'), r.finishReason ?? '—'],
            [t('traces.kv.tokens'), `${formatTokens(r.totalInputTokens, true)} / ${formatTokens(r.totalOutputTokens, true)}`],
            [t('traces.kv.cacheRead'), formatTokens(r.cacheReadTokens, true)],
            [t('traces.kv.reasoningTokens'), formatTokens(r.reasoningTokens, true)],
            [t('traces.kv.ttfb'), r.ttfbMs ? formatMs(r.ttfbMs) : '—'],
            [t('traces.kv.generation'), r.generationMs ? formatMs(r.generationMs) : '—'],
            [t('traces.kv.speed'), r.outputTps ? `${r.outputTps.toFixed(1)} tok/s` : '—'],
            [t('traces.kv.upstreamId'), r.upstreamRequestId ?? '—'],
            [t('traces.kv.cost'), formatUsd(r.costNanoUsd / 1e9)],
          ]}
        />
      );
    case 'decode':
    case 'reencode':
    case 'restore':
    case 'middlewareResponse': {
      const s = span(SPAN.response);
      const phases = callPhases(data.spans);
      const reasoningChars = attrNum(s, 'astra.response.reasoning_chars');
      const textChars = attrNum(s, 'astra.response.text_chars');
      return (
        <Inspector
          kind={stage(SPAN.response)}
          title={t(`traces.node.${id}` as 'traces.node.decode')}
          sub={dur(s)}
          rows={[
            at(s),
            [t('traces.kv.firstToken'), r.ttftMs ? formatMs(r.ttftMs) : '—'],
            [
              t('traces.kv.phases'),
              phases
                ? (['wait', 'think', 'write', 'tool'] as const)
                    .map((k) => [k, k === 'wait' ? phases.waitMs : k === 'think' ? phases.thinkMs : k === 'write' ? phases.writeMs : phases.toolMs] as const)
                    .filter(([k, ms]) => k === 'wait' || ms > 0)
                    .map(([k, ms]) => `${t(`traces.phase.${k}`)} ${formatMs(ms)}`)
                    .join(' · ')
                : '—',
            ],
            [
              t('traces.kv.chars'),
              reasoningChars !== undefined || textChars !== undefined ? `${formatTokens(reasoningChars ?? 0)} / ${formatTokens(textChars ?? 0)}` : '—',
            ],
            [t('traces.kv.toolCalls'), phases && phases.tools.length > 0 ? toolSummary(phases.tools) : String(attrNum(s, 'astra.response.tool_calls') ?? 0)],
            [t('traces.kv.finish'), attrStr(s, 'gen_ai.response.finish_reasons') ?? r.finishReason ?? '—'],
            [t('traces.kv.reads'), attrNum(s, 'astra.response.reads') ?? 0],
            [t('traces.kv.events'), attrNum(s, 'astra.response.events') ?? 0],
            [t('traces.kv.maxGap'), formatMs(attrNum(s, 'astra.response.max_gap_ms') ?? 0)],
            [t('traces.kv.restored'), attrNum(s, 'astra.restore.placeholders') ?? 0],
            [t('traces.kv.protocols'), `${r.upstreamProtocol ?? '—'} → ${attrStr(s, 'astra.protocol.client') ?? r.inboundProtocol}`],
          ]}
          code={s?.status === 'error' && s.statusMessage ? { title: t('traces.kv.status'), lines: [s.statusMessage] } : null}
          raw={rawOf([s], t)}
        />
      );
    }
    default: {
      if (!id.startsWith('attempt:')) return null;
      const index = Number(id.slice('attempt:'.length));
      const a = data.spans.filter((s) => s.name === SPAN.attempt).sort((x, y) => x.startUs - y.startUs)[index];
      if (!a) return null;
      const headers = Object.entries(a.attributes ?? {})
        .filter(([k]) => k.startsWith('astra.ratelimit.'))
        .map(([k, v]) => `${k.slice('astra.ratelimit.'.length)}: ${v}`);
      const retry = attrStr(a, 'astra.attempt.retry');
      return (
        <Inspector
          kind={stage(SPAN.attempt)}
          title={`${t('traces.node.attempt', { n: index + 1 })} · ${attrStr(a, 'astra.account.id') ?? '—'}`}
          sub={`${attrNum(a, 'http.response.status_code') ?? '—'} · ${formatUs(a.durationUs)}`}
          rows={[
            at(a),
            [t('traces.kv.http'), attrNum(a, 'http.response.status_code') ?? '—'],
            [t('traces.kv.ttfb'), formatUs(a.durationUs)],
            [t('traces.kv.credentials'), `${formatUs(attrNum(a, 'astra.credentials_us'))}${attr(a, 'astra.credentials.refreshed') === true ? ` · ${t('traces.kv.refreshed')}` : ''}`],
            [t('traces.kv.retry'), retry ? t(`traces.retry.${retry}` as 'traces.retry.rate_limited_failover') : '—'],
            [t('traces.kv.upstreamId'), attrStr(a, 'astra.upstream.request_id') ?? '—'],
            ...(attrNum(a, 'astra.upstream.processing_ms') !== undefined
              ? [[t('traces.kv.processing'), formatMs(attrNum(a, 'astra.upstream.processing_ms') ?? 0)] as [string, string]]
              : []),
          ]}
          code={headers.length > 0 ? { title: t('traces.kv.rateLimitHeaders'), lines: headers } : a.statusMessage && a.status === 'error' ? { title: t('traces.kv.status'), lines: [a.statusMessage] } : null}
          raw={rawOf([a], t)}
        />
      );
    }
  }
}

