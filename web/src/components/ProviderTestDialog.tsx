// The provider connection test dialog: one model at a time, or a batch with live per-model results.
// Every test is a real request: it applies the privacy guard, is logged and billed, and is never retried.

import { Activity, ArrowLeft, ChevronDown, FlaskConical, HelpCircle, Plus, Square, X } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';

import { ApiError } from '../api/client';
import { useProviderModels, runProviderTest } from '../api/hooks';
import type { ApiProtocol, Provider, ProviderTestDone, ProviderTestRequest } from '../api/types';
import { Alert } from './arc/alert/alert';
import { Checkbox } from './arc/checkbox/checkbox';
import { CopyButton } from './arc/copy-button/copy-button';
import { Tooltip } from './arc/tooltip/tooltip';
import { ProviderIcon } from './icons';
import { Badge, Button, Dot, Field, Input, NumberInput, SearchField, Segmented, Switch, TextArea } from './ui/controls';
import { Select, Sheet } from './ui/overlays';
import { useI18n, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';
import { formatMs } from '../lib/format';
import { PROTOCOL_OPTIONS } from '../pages/ProvidersPage';

/** Batch tests run this many at a time; "stop the rest" cancels everything still waiting. */
const BATCH_CONCURRENCY = 3;
const DEFAULT_MAX_TOKENS = 256;
const MAX_TOKENS = 4096;

// ---------- run state ----------

type RunStatus = 'queued' | 'running' | 'success' | 'failed' | 'cancelled';

interface Run {
  modelId: string;
  status: RunStatus;
  httpStatus?: number | null;
  /** Milliseconds to the upstream response headers. */
  httpMs?: number | null;
  /** Milliseconds to the first token of the answer. */
  ttftMs?: number | null;
  totalMs?: number | null;
  text: string;
  reasoning: string;
  raw?: string;
  rawTruncated?: boolean;
  contentType?: string | null;
  /** The upstream response headers, credentials redacted (empty until the response arrives). */
  headers?: Record<string, string>;
  error?: string | null;
}

const STATUS_KEY: Record<RunStatus | 'idle', MessageKey> = {
  idle: 'providers.testDialog.status.idle',
  queued: 'providers.testDialog.status.queued',
  running: 'providers.testDialog.status.running',
  success: 'providers.testDialog.status.success',
  failed: 'providers.testDialog.status.failed',
  cancelled: 'providers.testDialog.status.cancelled',
};

/** The protocol a provider prefers, restricted to the endpoints it actually has. */
function preferredProtocol(provider: Provider): ApiProtocol {
  const configured = provider.endpoints.map((e) => e.protocol);
  return provider.preferredUpstreamProtocols.find((p) => configured.includes(p)) ?? configured[0] ?? 'openai-chat';
}

const numberOrNull = (value: unknown): number | null => (typeof value === 'number' ? value : null);
const stringOrNull = (value: unknown): string | null => (typeof value === 'string' ? value : null);

// ---------- dialog ----------

export function ProviderTestDialog({ provider, open, onOpenChange }: { provider: Provider; open: boolean; onOpenChange: (o: boolean) => void }) {
  const { t } = useI18n();
  const qc = useQueryClient();
  const models = useProviderModels(open ? provider.id : undefined);
  const protocols = provider.endpoints.map((e) => e.protocol);

  const [mode, setMode] = useState<'single' | 'batch'>('single');
  const [protocol, setProtocol] = useState<ApiProtocol>(() => preferredProtocol(provider));
  const [stream, setStream] = useState(true);
  const [prompt, setPrompt] = useState('');
  const [maxTokens, setMaxTokens] = useState<number | null>(DEFAULT_MAX_TOKENS);

  const [model, setModel] = useState('');
  const [single, setSingle] = useState<Run | null>(null);
  const [batch, setBatch] = useState<Record<string, Run>>({});
  const [order, setOrder] = useState<string[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [typed, setTyped] = useState<string[]>([]);
  const [search, setSearch] = useState('');
  const [detail, setDetail] = useState<string | null>(null);
  const [configOpen, setConfigOpen] = useState(false);

  const singleRun = useRef<AbortController | null>(null);
  const controllers = useRef(new Map<string, AbortController>());
  const stopping = useRef(false);
  const running = useRef(false);

  /** Model ids offered by the picker: the provider's own models, enabled first, then hand-typed ones. */
  const known = useMemo(() => {
    const list = [...(models.data ?? [])].sort((a, b) => Number(b.enabled) - Number(a.enabled) || a.sortOrder - b.sortOrder).map((m) => m.modelId);
    return [...list, ...typed.filter((id) => !list.includes(id))];
  }, [models.data, typed]);

  // Leaving the dialog cancels everything in flight; the server logs those runs as client-cancelled.
  useEffect(() => {
    if (open) {
      setModel((current) => current || (models.data ?? []).find((m) => m.enabled)?.modelId || (models.data ?? [])[0]?.modelId || '');
      return;
    }
    running.current = false;
    singleRun.current?.abort();
    for (const controller of controllers.current.values()) controller.abort();
    controllers.current.clear();
    setSingle(null);
    setBatch({});
    setOrder([]);
    setSelected(new Set());
    setTyped([]);
    setSearch('');
    setDetail(null);
  }, [open, models.data]);

  const request = useCallback(
    (modelId: string): ProviderTestRequest => ({
      modelId,
      protocol,
      stream,
      prompt: prompt.trim() || null,
      maxOutputTokens: maxTokens ?? DEFAULT_MAX_TOKENS,
    }),
    [protocol, stream, prompt, maxTokens],
  );

  const test = useCallback(
    (modelId: string, signal: AbortSignal, report: (state: Run) => void) => runTest(provider.id, modelId, request(modelId), signal, report),
    [provider.id, request],
  );

  const startSingle = () => {
    const modelId = model.trim();
    if (!modelId) return;
    singleRun.current?.abort();
    const controller = new AbortController();
    singleRun.current = controller;
    running.current = true;
    setDetail(null);
    setSingle({ modelId, status: 'running', text: '', reasoning: '' });
    void test(modelId, controller.signal, (state) => {
      if (state.status !== 'running' && state.status !== 'queued') afterRun();
      setSingle(state);
    });
  };

  const startBatch = (ids: string[]) => {
    if (ids.length === 0) return;
    running.current = true;
    stopping.current = false;
    setDetail(null);
    setOrder(ids);
    setBatch(Object.fromEntries(ids.map((id) => [id, { modelId: id, status: 'queued' as const, text: '', reasoning: '' }])));
    let next = 0;
    const worker = async () => {
      for (;;) {
        if (!running.current) return;
        const modelId = ids[next++];
        if (modelId === undefined) return;
        if (stopping.current) {
          setBatch((current) => ({ ...current, [modelId]: { ...current[modelId]!, status: 'cancelled' } }));
          continue;
        }
        const controller = new AbortController();
        controllers.current.set(modelId, controller);
        await test(modelId, controller.signal, (state) => setBatch((current) => ({ ...current, [modelId]: state })));
        controllers.current.delete(modelId);
        afterRun();
      }
    };
    void Promise.all(Array.from({ length: Math.min(BATCH_CONCURRENCY, ids.length) }, worker));
  };

  const afterRun = () => void qc.invalidateQueries({ queryKey: ['requests'] });

  const stopBatch = () => {
    stopping.current = true;
    setBatch((current) =>
      Object.fromEntries(Object.entries(current).map(([id, state]) => [id, state.status === 'queued' ? { ...state, status: 'cancelled' as const } : state])),
    );
  };

  const busy = single?.status === 'running' || order.some((id) => batch[id]?.status === 'running' || (batch[id]?.status === 'queued' && !stopping.current));
  const done = order.filter((id) => batch[id] && batch[id].status !== 'queued' && batch[id].status !== 'running').length;
  const succeeded = order.filter((id) => batch[id]?.status === 'success').length;
  const failed = order.filter((id) => batch[id]?.status === 'failed').length;

  const onToggle = (modelId: string, on: boolean) =>
    setSelected((current) => {
      const next = new Set(current);
      if (on) next.add(modelId);
      else next.delete(modelId);
      return next;
    });

  const close = () => {
    if (busy) return;
    onOpenChange(false);
  };

  // The dialog is portaled out of the DOM, but React still bubbles its events up the component tree: without this,
  // clicking a button here would also trigger the clickable provider card or row that opened it.
  const stop = (e: React.MouseEvent | React.KeyboardEvent) => e.stopPropagation();

  return (
    <span onClick={stop} onKeyDown={stop}>
      <Sheet
        open={open}
        onOpenChange={(next) => (next ? onOpenChange(true) : close())}
        width={1040}
        title={t('providers.testDialog.title', { name: provider.name })}
        description={t('providers.testDialog.subtitle')}
        footer={
          <>
            <span className="mr-auto text-[11px] text-[var(--text-muted)]">{t('providers.testDialog.realRequest')}</span>
          <Button disabled={busy} onClick={() => onOpenChange(false)} icon={<X className="size-3.5" />}>
            {t('common.close')}
          </Button>
          {mode === 'single' ? (
            busy ? (
              <Button variant="primary" icon={<Square className="size-3.5" />} onClick={() => singleRun.current?.abort()}>
                {t('providers.testDialog.stop')}
              </Button>
            ) : (
              <Button variant="primary" disabled={model.trim() === ''} icon={<FlaskConical className="size-3.5" />} onClick={startSingle}>
                {single ? t('providers.testDialog.retest') : t('providers.testDialog.start')}
              </Button>
            )
          ) : busy ? (
            <Button variant="primary" icon={<Square className="size-3.5" />} onClick={stopBatch}>
              {t('providers.testDialog.stopRest')}
            </Button>
          ) : (
            <Button
              variant="primary"
              disabled={selected.size === 0}
              icon={<FlaskConical className="size-3.5" />}
              onClick={() => startBatch([...selected])}
            >
              {t('providers.testDialog.testN', { count: selected.size })}
            </Button>
          )}
        </>
      }
    >
      <div className="flex items-center justify-between gap-3 pb-4">
        <ProviderIcon name={provider.name} icon={provider.icon} colorKey={provider.templateId} size={32} />
        <Segmented
          items={[
            { value: 'single', label: t('providers.testDialog.modeSingle') },
            { value: 'batch', label: t('providers.testDialog.modeBatch') },
          ]}
          value={mode}
          onChange={(value) => {
            setMode(value as 'single' | 'batch');
            setDetail(null);
          }}
          ariaLabel={t('providers.testDialog.config')}
        />
      </div>

      {mode === 'single' ? (
        <div className="grid grid-cols-[minmax(0,300px)_minmax(0,1fr)] gap-6">
          <div className="flex flex-col gap-4 border-r border-[var(--border)] pr-6">
            <h3 className="text-[13px] font-medium">{t('providers.testDialog.config')}</h3>
            <Field label={t('providers.testDialog.model')}>
              <ModelPicker value={model} models={known} onChange={setModel} />
            </Field>
            <ConfigFields
              protocols={protocols}
              protocol={protocol}
              onProtocol={setProtocol}
              stream={stream}
              onStream={setStream}
              maxTokens={maxTokens}
              onMaxTokens={setMaxTokens}
              prompt={prompt}
              onPrompt={setPrompt}
            />
            <p className="mt-auto text-[11px] text-[var(--text-muted)]">{t('providers.testDialog.savedCredentials')}</p>
          </div>
          <TestResultPanel run={single} onRetest={startSingle} />
        </div>
      ) : detail !== null ? (
        <div className="grid gap-4">
          <div className="flex flex-wrap items-center gap-3">
            <Button variant="plain" icon={<ArrowLeft className="size-4" />} aria-label={t('common.back')} onClick={() => setDetail(null)} />
            <span className="font-mono text-[13px]">{detail}</span>
          </div>
          <TestResultPanel run={batch[detail] ?? null} />
        </div>
      ) : (
        <>
          <BatchList
            known={known}
            selected={selected}
            onToggle={onToggle}
            onSelectAll={(on) => setSelected(on ? new Set(known) : new Set())}
            onClear={() => setSelected(new Set())}
            search={search}
            onSearch={setSearch}
            onAdd={(id) => {
              setTyped((current) => (current.includes(id) ? current : [...current, id]));
              setSelected((current) => new Set(current).add(id));
              setSearch('');
            }}
            configOpen={configOpen}
            onConfigOpen={setConfigOpen}
            protocols={protocols}
            protocol={protocol}
            onProtocol={setProtocol}
            stream={stream}
            onStream={setStream}
            maxTokens={maxTokens}
            onMaxTokens={setMaxTokens}
            prompt={prompt}
            onPrompt={setPrompt}
          />
          {order.length > 0 ? (
            <div className="mt-4 grid gap-2">
              <div className="text-[12px] text-[var(--text-secondary)]">
                {busy
                  ? t('providers.testDialog.progress', { done, total: order.length, ok: succeeded, failed })
                  : t('providers.testDialog.progressDone', { total: order.length, ok: succeeded, failed })}
              </div>
              <div className="h-1 overflow-hidden rounded-full bg-[var(--surface-strong)]">
                <div className="h-full rounded-full transition-[width]" style={{ width: `${(done / order.length) * 100}%`, background: 'var(--accent)' }} />
              </div>
            </div>
          ) : null}
          {order.length > 0 ? (
            <div className="mt-4">
              <TestTable order={order} runs={batch} onView={setDetail} />
            </div>
          ) : null}
        </>
      )}
      </Sheet>
    </span>
  );
}

// ---------- test configuration ----------

/**
 * The test-model picker: a searchable list of the provider's models that also accepts any model id typed by hand
 * (Arc's Combobox is select-only). Opening shows every model; typing filters. Keyboard: Down/Up move, Enter picks.
 */
function ModelPicker({ value, models, onChange }: { value: string; models: string[]; onChange: (value: string) => void }) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  // null = not filtering (everything is listed); a string only filters while the user is typing.
  const [query, setQuery] = useState<string | null>(null);
  const matches = query === null ? models : models.filter((id) => id.toLowerCase().includes(query.trim().toLowerCase()));

  const openList = () => {
    setQuery(null);
    setActive(Math.max(0, models.indexOf(value)));
    setOpen(true);
  };

  const pick = (id: string) => {
    onChange(id);
    setQuery(null);
    setOpen(false);
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (!open) return openList();
      if (matches.length === 0) return;
      const step = e.key === 'ArrowDown' ? 1 : -1;
      setActive((current) => (current + step + matches.length) % matches.length);
    } else if (e.key === 'Enter' && open) {
      e.preventDefault();
      if (matches[active]) pick(matches[active]);
    } else if (e.key === 'Escape') {
      setOpen(false);
    }
  };

  return (
    <div className="relative">
      <Input
        className="pr-9 font-mono"
        value={value}
        placeholder={t('providers.testDialog.modelPlaceholder')}
        aria-expanded={open}
        role="combobox"
        onFocus={openList}
        onClick={openList}
        onChange={(e) => {
          onChange(e.target.value);
          setQuery(e.target.value);
          setActive(0);
          setOpen(true);
        }}
        onKeyDown={onKeyDown}
      />
      <button
        type="button"
        className="absolute inset-y-0 right-0 grid w-8 place-items-center text-[var(--text-muted)] hover:text-[var(--text)]"
        aria-label={t('providers.testDialog.showModels')}
        onClick={() => (open ? setOpen(false) : openList())}
      >
        <ChevronDown className="size-4" />
      </button>
      {open && (
        <>
          {/* Catches the click that closes the list. */}
          <div className="fixed inset-0 z-40" onMouseDown={() => setOpen(false)} />
          <ul className="absolute inset-x-0 top-full z-50 mt-1 max-h-64 overflow-auto rounded-lg border border-[var(--border)] bg-[var(--surface)] py-1 shadow-lg">
            {matches.length === 0 ? (
              <li className="px-3 py-2 text-[12px] text-[var(--text-muted)]">{t('providers.testDialog.noModels')}</li>
            ) : (
              matches.map((id, index) => (
                <li key={id}>
                  <button
                    type="button"
                    className={cn(
                      'block w-full truncate px-3 py-1.5 text-left font-mono text-[12px] hover:bg-[var(--surface-strong)]',
                      index === active && 'bg-[var(--surface-strong)]',
                    )}
                    onMouseEnter={() => setActive(index)}
                    onMouseDown={(e) => {
                      e.preventDefault();
                      pick(id);
                    }}
                  >
                    {id}
                  </button>
                </li>
              ))
            )}
          </ul>
        </>
      )}
    </div>
  );
}

interface ConfigFieldProps {
  protocols: ApiProtocol[];
  protocol: ApiProtocol;
  onProtocol: (value: ApiProtocol) => void;
  stream: boolean;
  onStream: (value: boolean) => void;
  maxTokens: number | null;
  onMaxTokens: (value: number | null) => void;
  prompt: string;
  onPrompt: (value: string) => void;
}

/** The configuration shared by both modes; the batch mode shows it in its own small dialog. */
function ConfigFields({ protocols, ...rest }: ConfigFieldProps) {
  const { t } = useI18n();
  const options = protocols.map((p) => ({ value: p, label: PROTOCOL_OPTIONS.find((o) => o.value === p)?.label ?? p }));
  return (
    <>
      <Field label={t('providers.testDialog.protocol')}>
        <Select value={rest.protocol} onChange={rest.onProtocol} options={options} />
      </Field>
      <div className="flex items-center justify-between gap-3">
        <span className="text-[12px] text-[var(--text-secondary)]">{t('providers.testDialog.stream')}</span>
        <Switch checked={rest.stream} label={t('providers.testDialog.stream')} onChange={rest.onStream} />
      </div>
      <Field label={t('providers.testDialog.maxTokens')}>
        <NumberInput
          value={rest.maxTokens}
          min={1}
          placeholder={String(DEFAULT_MAX_TOKENS)}
          label={t('providers.testDialog.maxTokens')}
          onChange={(value) => rest.onMaxTokens(value === null ? null : Math.min(MAX_TOKENS, Math.max(1, Math.round(value))))}
        />
      </Field>
      <Field label={t('providers.testDialog.prompt')} hint={t('providers.testDialog.promptHint')}>
        <TextArea rows={4} value={rest.prompt} placeholder="Reply with OK." onChange={(e) => rest.onPrompt(e.target.value)} />
      </Field>
    </>
  );
}

// ---------- result panel ----------

function TestResultPanel({ run, onRetest }: { run: Run | null; onRetest?: () => void }) {
  const { t } = useI18n();
  const [view, setView] = useState<'preview' | 'raw' | 'headers'>('preview');
  const text = run?.text ?? '';
  const raw = run?.raw ?? '';
  const headers = Object.entries(run?.headers ?? {});
  // The upstream may translate the prompt internally: reasoning shows the upstream's own words, so it is never
  // restored through the privacy guard's placeholder map.
  const firstWait = run?.ttftMs != null && run?.httpMs != null ? Math.max(0, run.ttftMs - run.httpMs) : null;
  const copied = view === 'raw' ? raw : view === 'headers' ? headers.map(([name, value]) => `${name}: ${value}`).join('\n') : text;
  return (
    <div className="grid min-w-0 content-start gap-4">
      <div className="flex flex-wrap items-center justify-end gap-2">
        {onRetest && run && run.status !== 'running' ? (
          <Button size="sm" icon={<FlaskConical className="size-3.5" />} onClick={onRetest}>
            {t('providers.testDialog.retest')}
          </Button>
        ) : null}
        <Tooltip content={t('providers.testDialog.metricsHelp')}>
          <span className="inline-flex size-4 items-center justify-center text-[var(--text-muted)]" tabIndex={0}>
            <HelpCircle className="size-4" />
          </span>
        </Tooltip>
        <StatusBadge run={run} />
        {run?.httpStatus ? <Badge tone="neutral">HTTP {run.httpStatus}</Badge> : null}
      </div>
      <div className="grid grid-cols-2 gap-px overflow-hidden rounded-lg border border-[var(--border)] bg-[var(--border)] sm:grid-cols-4">
        <Metric label={t('providers.testDialog.metrics.http')} value={run?.httpMs} />
        <Metric label={t('providers.testDialog.metrics.firstWait')} value={firstWait} />
        <Metric label={t('providers.testDialog.metrics.ttft')} value={run?.ttftMs} />
        <Metric label={t('providers.testDialog.metrics.total')} value={run?.totalMs} />
      </div>
      <section className="card overflow-hidden">
        <header className="flex flex-wrap items-center gap-2 border-b border-[var(--border)] px-4 py-2.5">
          <h3 className="text-[12px] font-medium">{t('providers.testDialog.reply')}</h3>
          <div className="ml-auto flex items-center gap-2">
            <Segmented
              items={[
                { value: 'preview', label: t('providers.testDialog.preview') },
                { value: 'raw', label: t('providers.testDialog.raw') },
                { value: 'headers', label: t('providers.testDialog.headers') },
              ]}
              value={view}
              onChange={setView}
              ariaLabel={t('providers.testDialog.reply')}
            />
            <CopyButton value={copied} label={view === 'headers' ? t('providers.testDialog.headers') : t('providers.testDialog.reply')} iconOnly />
          </div>
        </header>
        <div className="h-[360px] overflow-auto px-4 py-3">
          {view === 'headers' ? (
            headers.length === 0 ? (
              <p className="text-[12px] text-[var(--text-muted)]">{t('providers.testDialog.headersEmpty')}</p>
            ) : (
              <table className="w-full text-[11px]">
                <tbody>
                  {headers.map(([name, value]) => (
                    <tr key={name} className="border-b border-[var(--border)] last:border-0 align-top">
                      <th className="w-[220px] py-1.5 pr-4 text-left font-mono font-normal text-[var(--text-secondary)]">{name}</th>
                      <td className="py-1.5 font-mono break-all whitespace-pre-wrap selectable">{value}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )
          ) : view === 'raw' ? (
            raw ? (
              <div className="grid gap-2">
                <p className="text-[11px] text-[var(--text-muted)]">
                  {t('providers.testDialog.rawNote')}
                  {run?.contentType ? ` · ${run.contentType}` : ''}
                </p>
                <pre className="font-mono text-[11px] leading-[1.6] break-all whitespace-pre-wrap selectable">{raw}</pre>
                {run?.rawTruncated ? <p className="text-[11px] text-[var(--warning)]">{t('providers.testDialog.rawTruncated')}</p> : null}
              </div>
            ) : (
              <Placeholder run={run} />
            )
          ) : run?.error ? (
            <Alert tone="danger" title={t('providers.testDialog.status.failed')}>
              {run.error}
            </Alert>
          ) : text || run?.reasoning ? (
            <div className="grid gap-3">
              {run?.reasoning ? (
                <div className="rounded-md border border-[var(--border)] bg-[var(--surface-strong)] p-3">
                  <div className="mb-1 text-[11px] text-[var(--text-muted)]">{t('providers.testDialog.reasoning')}</div>
                  <pre className="font-mono text-[11px] leading-[1.6] whitespace-pre-wrap selectable">{run.reasoning}</pre>
                </div>
              ) : null}
              {text ? <pre className="font-mono text-[12px] leading-[1.7] break-words whitespace-pre-wrap selectable">{text}</pre> : null}
            </div>
          ) : run?.status === 'success' ? (
            <p className="text-[12px] text-[var(--text-muted)]">{t('providers.testDialog.noText')}</p>
          ) : (
            <Placeholder run={run} />
          )}
        </div>
      </section>
    </div>
  );
}

function Placeholder({ run }: { run: Run | null }) {
  const { t } = useI18n();
  if (run?.status === 'running' || run?.status === 'queued') {
    return <div className="grid h-full place-items-center text-[12px] text-[var(--text-muted)]">{t(STATUS_KEY[run.status])}</div>;
  }
  return (
    <div className="grid h-full place-items-center content-center gap-2 text-center">
      <Activity className="mx-auto size-6 text-[var(--text-muted)]" />
      <div className="text-[13px]">{t('providers.testDialog.emptyTitle')}</div>
      <p className="mx-auto max-w-[320px] text-[11px] text-[var(--text-muted)]">{t('providers.testDialog.emptyDetail')}</p>
    </div>
  );
}

function Metric({ label, value }: { label: string; value: number | null | undefined }) {
  return (
    <div className="bg-[var(--surface)] px-4 py-3">
      <div className="text-[11px] text-[var(--text-muted)]">{label}</div>
      <div className="mt-1 font-mono text-[15px] tabular-nums">{value == null ? '—' : formatMs(value)}</div>
    </div>
  );
}

function StatusBadge({ run }: { run: Run | null }) {
  const { t } = useI18n();
  const status = run?.status ?? 'idle';
  const tone = status === 'success' ? 'green' : status === 'failed' ? 'red' : status === 'running' ? 'orange' : 'neutral';
  return (
    <span className="inline-flex items-center gap-1.5 text-[12px] text-[var(--text-secondary)]">
      <Dot tone={tone} />
      {t(STATUS_KEY[status])}
    </span>
  );
}

// ---------- batch list ----------

function BatchList({
  known,
  selected,
  onToggle,
  onSelectAll,
  onClear,
  search,
  onSearch,
  onAdd,
  configOpen,
  onConfigOpen,
  ...config
}: {
  known: string[];
  selected: Set<string>;
  onToggle: (modelId: string, on: boolean) => void;
  onSelectAll: (on: boolean) => void;
  onClear: () => void;
  search: string;
  onSearch: (value: string) => void;
  onAdd: (modelId: string) => void;
  configOpen: boolean;
  onConfigOpen: (open: boolean) => void;
} & ConfigFieldProps) {
  const { t } = useI18n();
  const query = search.trim();
  const needle = query.toLowerCase();
  const visible = needle ? known.filter((id) => id.toLowerCase().includes(needle)) : known;
  const canAdd = query !== '' && !known.some((id) => id.toLowerCase() === needle);
  return (
    <div className="grid gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="shrink-0 text-[12px] text-[var(--text-secondary)]">
          {t('providers.testDialog.selected', { n: selected.size, total: known.length })}
        </span>
        <div className="min-w-[220px] flex-1">
          <SearchField value={search} onChange={onSearch} placeholder={t('providers.testDialog.searchModels')} />
        </div>
        <Button variant="plain" disabled={selected.size === 0} onClick={onClear}>
          {t('providers.testDialog.clearSelection')}
        </Button>
        <Button icon={<FlaskConical className="size-3.5" />} onClick={() => onConfigOpen(true)}>
          {t('providers.testDialog.config')}
        </Button>
      </div>

      <div className="card max-h-[420px] overflow-auto">
        {visible.length === 0 && !canAdd ? (
          <p className="px-4 py-6 text-center text-[12px] text-[var(--text-muted)]">{t('providers.testDialog.noModels')}</p>
        ) : (
          <ul className="divide-y divide-[var(--border)]">
            <li className="flex items-center gap-3 px-4 py-2">
              <Checkbox
                checked={selected.size === known.length && known.length > 0 ? true : selected.size === 0 ? false : 'indeterminate'}
                onCheckedChange={(state) => onSelectAll(state !== false)}
                aria-label={t('providers.testDialog.clearSelection')}
              />
              <span className="text-[11px] text-[var(--text-muted)]">{t('providers.testDialog.ownFilter')}</span>
            </li>
            {canAdd ? (
              <li className="flex items-center gap-3 px-4 py-2.5">
                <Plus className="size-3.5 shrink-0 text-[var(--accent)]" />
                <button type="button" className="text-left font-mono text-[12px] text-[var(--accent)]" onClick={() => onAdd(query)}>
                  {t('providers.testDialog.addModel', { id: query })}
                </button>
              </li>
            ) : null}
            {visible.map((id) => (
              <li key={id} className="flex items-center gap-3 px-4 py-2.5">
                <Checkbox checked={selected.has(id)} onCheckedChange={(state) => onToggle(id, state !== false)} aria-label={id} />
                <span className="min-w-0 truncate font-mono text-[12px]" title={id}>
                  {id}
                </span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <Sheet
        open={configOpen}
        onOpenChange={onConfigOpen}
        width={480}
        title={t('providers.testDialog.config')}
        description={t('providers.testDialog.savedCredentials')}
      >
        <div className="grid gap-4">
          <ConfigFields {...config} />
        </div>
      </Sheet>
    </div>
  );
}

function TestTable({ order, runs, onView }: { order: string[]; runs: Record<string, Run>; onView: (modelId: string) => void }) {
  const { t } = useI18n();
  return (
    <div className="card overflow-hidden">
      <table className="w-full text-[12px]">
        <thead>
          <tr className="border-b border-[var(--border)] text-left text-[11px] text-[var(--text-muted)]">
            <th className="px-4 py-2 font-normal">{t('providers.testDialog.model')}</th>
            <th className="px-4 py-2 font-normal">{t('providers.testDialog.statusColumn')}</th>
            <th className="px-4 py-2 font-normal">{t('providers.testDialog.metrics.ttft')}</th>
            <th className="px-4 py-2 font-normal">{t('providers.testDialog.metrics.total')}</th>
            <th className="px-4 py-2 font-normal">{t('providers.testDialog.reply')}</th>
          </tr>
        </thead>
        <tbody>
          {order.map((id) => {
            const run = runs[id];
            return (
              <tr key={id} className="border-b border-[var(--border)] last:border-0">
                <td className="px-4 py-2 font-mono">{id}</td>
                <td className="px-4 py-2">
                  <StatusBadge run={run ?? null} />
                </td>
                <td className="px-4 py-2 font-mono tabular-nums">{run?.ttftMs == null ? '—' : formatMs(run.ttftMs)}</td>
                <td className="px-4 py-2 font-mono tabular-nums">{run?.totalMs == null ? '—' : formatMs(run.totalMs)}</td>
                <td className="px-4 py-2">
                  <button type="button" className="text-[var(--accent)] disabled:text-[var(--text-muted)]" disabled={!run} onClick={() => onView(id)}>
                    {t('providers.testDialog.view')}
                  </button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

// ---------- running one test ----------

/** Runs one test and folds its streamed events into the run state, reporting every step. */
async function runTest(
  providerId: string,
  modelId: string,
  request: ProviderTestRequest,
  signal: AbortSignal,
  report: (state: Run) => void,
): Promise<void> {
  let state: Run = { modelId, status: 'running', text: '', reasoning: '' };
  report(state);
  const patch = (next: Partial<Run>) => {
    state = { ...state, ...next };
    report(state);
  };
  try {
    await runProviderTest(
      providerId,
      request,
      (message) => {
        const data = (message.data ?? {}) as Record<string, unknown>;
        switch (message.event) {
          case 'headers':
            patch({
              httpStatus: numberOrNull(data.httpStatus),
              httpMs: numberOrNull(data.httpMs),
              contentType: stringOrNull(data.contentType),
              headers: headersOf(data.headers),
            });
            break;
          case 'delta': {
            const text = stringOrNull(data.text) ?? '';
            if (stringOrNull(data.kind) === 'reasoning') patch({ reasoning: state.reasoning + text });
            else patch({ text: state.text + text });
            break;
          }
          case 'done': {
            const done = message.data as ProviderTestDone;
            patch({
              status: done.ok ? 'success' : 'failed',
              error: done.error ?? null,
              httpStatus: done.httpStatus ?? null,
              httpMs: done.httpMs ?? null,
              ttftMs: done.ttftMs ?? null,
              totalMs: done.totalMs ?? null,
              text: done.text ?? '',
              reasoning: done.reasoning ?? '',
              raw: done.raw ?? '',
              rawTruncated: done.rawTruncated ?? false,
              contentType: done.contentType ?? null,
              headers: headersOf(done.headers),
            });
            break;
          }
          default:
            break;
        }
      },
      signal,
    );
  } catch (err) {
    if ((err as Error)?.name === 'AbortError') patch({ status: 'cancelled', error: null });
    else patch({ status: 'failed', error: err instanceof ApiError ? err.message : String(err) });
  }
}

/** Narrows a wire payload into the header map the panel shows (anything else becomes an empty list). */
function headersOf(value: unknown): Record<string, string> {
  if (value === null || typeof value !== 'object') return {};
  const result: Record<string, string> = {};
  for (const [name, raw] of Object.entries(value as Record<string, unknown>)) {
    if (typeof raw === 'string') result[name] = raw;
    else if (raw !== null && raw !== undefined) result[name] = String(raw);
  }
  return result;
}