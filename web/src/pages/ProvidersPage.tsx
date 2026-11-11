import { Archive, ChartColumn, ChevronUp, Download, FlaskConical, Plus, Server, Trash2 } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useNavigate, useSearchParams } from 'react-router';

import {
  useCreateProvider,
  usePriceKeys,
  useProviders,
  useProviderStats,
  useTemplates,
  useTimeseries,
} from '../api/hooks';
import type { ApiProtocol, AuthScheme, Provider, ProviderStat, ProviderTemplate, TemplateEndpoint } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { ChipGroup } from '../components/arc/chip-group/chip-group';
import { PasswordField } from '../components/arc/password-field/password-field';
import { RadioCards } from '../components/arc/radio-cards/radio-cards';
import { Stepper } from '../components/arc/stepper/stepper';
import { ProviderIcon } from '../components/icons';
import { ImportProvidersSheet } from '../components/ImportProvidersSheet';
import { ProviderBackupSheet } from '../components/ProviderBackupSheet';
import { Page } from '../components/layout/Page';
import {
  HEALTH_TONE,
  healthLabel,
  hostOf,
  MiniBars,
  OVERVIEW_RANGES,
  ProviderEnabledSwitch,
  ProviderOverview,
  providerHealth,
  ProvidersTable,
  successColor,
  useProviderChartsOpen,
  useProvidersView,
  type ProvidersView,
} from '../components/ProviderOverview';
import { ProviderTestDialog } from '../components/ProviderTestDialog';
import { Badge, Button, Disclosure, Dot, EmptyState, Field, Input, NumberInput, SearchField, Segmented, Spinner, Switch, TextArea } from '../components/ui/controls';
import { errorText, Select, Sheet, useFeedback } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';
import { formatPercent, formatTokens, formatUsd } from '../lib/format';
import { failureText, quotaEnabled, quotaFailure, quotaSummary } from '../lib/quota';
import { useCommandListener } from '../shell/commands';

export const PROTOCOL_OPTIONS: { value: ApiProtocol; label: string }[] = [
  { value: 'openai-chat', label: 'OpenAI Chat' },
  { value: 'openai-responses', label: 'OpenAI Responses' },
  { value: 'anthropic', label: 'Anthropic Messages' },
  { value: 'gemini', label: 'Gemini' },
];

export const AUTH_OPTIONS: { value: AuthScheme; label: string }[] = [
  { value: 'bearer', label: 'Authorization: Bearer' },
  { value: 'x-api-key', label: 'x-api-key' },
  { value: 'x-goog-api-key', label: 'x-goog-api-key' },
  { value: 'query-key', label: '?key=' },
  { value: 'none', label: '—' },
  { value: 'oauth-subscription', label: 'OAuth 订阅' },
];

export function ProvidersPage() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const providers = useProviders();
  const [adding, setAdding] = useState(params.get('new') === '1');
  const [importing, setImporting] = useState(false);
  const [backingUp, setBackingUp] = useState(false);

  useEffect(() => {
    if (params.get('new') === '1') {
      setAdding(true);
      params.delete('new');
      setParams(params, { replace: true });
    }
  }, [params, setParams]);

  useCommandListener((cmd) => {
    if (cmd === 'new-provider') setAdding(true);
  });

  // One request for every card: per-provider daily buckets over the last week. The endpoint keys provider
  // series by the logged provider name (not id), so cards look their usage up by name.
  const series = useTimeseries('7d', 'day', 'provider');
  // Success rate, TTFT and tokens per provider id: the cards' and the table's week, and the overview's own range.
  const [range, setRange] = useState<OverviewRange>('7d');
  const week = useProviderStats('7d');
  const ranged = useProviderStats(range);
  const [view, setView] = useProvidersView();
  const [chartsOpen, setChartsOpen] = useProviderChartsOpen();
  const [testing, setTesting] = useState<Provider | null>(null);
  const usage = useMemo(() => {
    const days = lastDays(USAGE_DAYS);
    const byProvider = new Map<string, ProviderUsage>();
    let max = 0;
    for (const pt of series.data ?? []) {
      const i = days.indexOf(pt.bucket);
      if (i < 0) continue;
      let u = byProvider.get(pt.key);
      if (!u) byProvider.set(pt.key, (u = { requests: days.map(() => 0), costUsd: 0 }));
      u.requests[i] = (u.requests[i] ?? 0) + pt.requests;
      u.costUsd += pt.costUsd;
      max = Math.max(max, u.requests[i]);
    }
    return { days, labels: days.map((d) => d.slice(5)), byProvider, max };
  }, [series.data]);
  const stats7d = useMemo(() => new Map((week.data ?? []).map((s) => [s.providerId, s])), [week.data]);
  const requestsOf = (p: Provider) => usage.byProvider.get(p.name)?.requests ?? usage.days.map(() => 0);

  return (
    <Page
      title={t('nav.providers')}
      subtitle={providers.data ? t('providers.count', { count: providers.data.length }) : undefined}
      actions={
        <>
          <Button icon={<Archive className="size-3.5" />} onClick={() => setBackingUp(true)}>
            {t('providers.backup')}
          </Button>
          <Button icon={<Download className="size-3.5" />} onClick={() => setImporting(true)}>
            {t('providers.import')}
          </Button>
          <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setAdding(true)}>
            {t('providers.add')}
          </Button>
        </>
      }
    >
      <div className="mx-auto max-w-[1100px]">
        {providers.isLoading && <Spinner lines={4} />}
        {providers.isError && <Alert tone="danger" title={t('common.error')}>{errorText(providers.error)}</Alert>}
        {providers.data?.length === 0 && (
          <div className="card">
            <EmptyState
              icon={<Server className="size-6" />}
              title={t('providers.empty')}
              detail={t('providers.emptyDetail')}
              action={
                <div className="flex flex-wrap items-center justify-center gap-2">
                  <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setAdding(true)}>
                    {t('providers.add')}
                  </Button>
                  <Button icon={<Download className="size-3.5" />} onClick={() => setImporting(true)}>
                    {t('providers.import')}
                  </Button>
                </div>
              }
            />
          </div>
        )}
        {providers.data && providers.data.length > 0 && (
          <>
            <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
              <Segmented<OverviewRange>
                ariaLabel={t('providers.overview.range')}
                value={range}
                onChange={setRange}
                items={OVERVIEW_RANGES.map((r) => ({ value: r, label: t(`range.${r}`) }))}
              />
              <div className="flex items-center gap-2">
                <Button
                  variant="plain"
                  size="sm"
                  icon={chartsOpen ? <ChevronUp className="size-3.5" /> : <ChartColumn className="size-3.5" />}
                  onClick={() => setChartsOpen(!chartsOpen)}
                >
                  {chartsOpen ? t('providers.overview.hideCharts') : t('providers.overview.showCharts')}
                </Button>
                <Segmented<ProvidersView>
                  ariaLabel={t('providers.view.label')}
                  value={view}
                  onChange={setView}
                  items={[
                    { value: 'grid', label: t('providers.view.grid') },
                    { value: 'table', label: t('providers.view.table') },
                  ]}
                />
              </div>
            </div>
            <ProviderOverview providers={providers.data} stats={ranged.data ?? []} stats7d={stats7d} range={range} chartsOpen={chartsOpen} />
            {view === 'table' ? (
              <ProvidersTable
                providers={providers.data}
                stats7d={stats7d}
                usage={requestsOf}
                labels={usage.labels}
                max={usage.max}
                onOpen={(p) => navigate(`/providers/${p.id}`)}
              />
            ) : (
              <div className="grid grid-cols-[repeat(auto-fill,minmax(min(100%,260px),1fr))] gap-3">
                {providers.data.map((p) => (
                  <ProviderCard
                    key={p.id}
                    p={p}
                    labels={usage.labels}
                    max={usage.max}
                    requests={requestsOf(p)}
                    stat={stats7d.get(p.id)}
                    onOpen={() => navigate(`/providers/${p.id}`)}
                    onTest={() => setTesting(p)}
                  />
                ))}
              </div>
            )}
          </>
        )}
      </div>
      <ImportProvidersSheet open={importing} onOpenChange={setImporting} />
      <ProviderBackupSheet open={backingUp} onOpenChange={setBackingUp} />
      <AddProviderSheet open={adding} onOpenChange={setAdding} onCreated={(p) => navigate(`/providers/${p.id}`)} />
      {/* Rendered outside the cards on purpose: a dialog portaled from inside a clickable card would still bubble
          its clicks up React's tree and open the provider behind it. */}
      {testing && <ProviderTestDialog provider={testing} open onOpenChange={(o) => !o && setTesting(null)} />}
    </Page>
  );
}

type OverviewRange = (typeof OVERVIEW_RANGES)[number];

const USAGE_DAYS = 7;

interface ProviderUsage {
  /** Requests per local day, oldest first, aligned with `days`. */
  requests: number[];
  costUsd: number;
}

/** The last `n` local days as "yyyy-MM-dd", oldest first — the bucket format of /api/stats/timeseries. */
function lastDays(n: number): string[] {
  const now = new Date();
  return Array.from({ length: n }, (_, i) => {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() - (n - 1 - i));
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  });
}

/** Keeps clicks on a card's own controls (and their portaled menus) from also opening the provider. */
const stopClick = (e: React.MouseEvent) => e.stopPropagation();

/**
 * One compact provider card (~116 px): name row (icon, name, health dot, model count, test, switch), the host with the credential or
 * the balance, and the last week as bars on the shared scale with requests / success rate / cost. Protocols, bound clients and pricing
 * are on the provider's page.
 */
function ProviderCard({
  p,
  labels,
  max,
  requests,
  stat,
  onOpen,
  onTest,
}: {
  p: Provider;
  labels: string[];
  max: number;
  requests: number[];
  stat?: ProviderStat;
  onOpen: () => void;
  onTest: () => void;
}) {
  const { t, locale } = useI18n();
  const health = providerHealth(p, stat);
  const credential =
    p.authScheme === 'plugin'
      ? t('providers.card.plugin')
      : p.authScheme === 'oauth-subscription'
        ? t('providers.card.subscription')
        : p.authScheme === 'none'
          ? t('providers.card.noAuth')
          : p.hasApiKey
            ? (p.apiKeyMasked ?? '••••')
            : t('providers.noKey');
  const quota = quotaEnabled(p) ? quotaSummary(p.quota, t, locale) : null;
  const quotaFailed = quotaEnabled(p) ? quotaFailure(p.quota) : null;
  const total = requests.reduce((a, b) => a + b, 0);
  const host = hostOf(p.endpoints[0]?.baseUrl);

  return (
    // The whole card opens the provider; it holds its own controls, so it cannot be a <button>.
    <div
      className={cn(
        'card flex h-[116px] cursor-pointer flex-col gap-[7px] px-3 py-2.5 transition-colors hover:border-[var(--border-strong,var(--border))]',
        !p.enabled && 'opacity-60',
      )}
      role="button"
      tabIndex={0}
      aria-label={p.name}
      title={p.name}
      onClick={onOpen}
      onKeyDown={(e) => {
        if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) {
          e.preventDefault();
          onOpen();
        }
      }}
    >
      <div className="flex min-w-0 items-center gap-2">
        <span className={cn('inline-flex shrink-0', !p.enabled && 'grayscale')}>
          <ProviderIcon name={p.name} icon={p.icon} colorKey={p.templateId} size={24} />
        </span>
        <span className="min-w-0 truncate text-[14px] font-semibold">{p.name}</span>
        <span title={healthLabel(health, t)} className="inline-flex">
          <Dot tone={HEALTH_TONE[health]} />
        </span>
        {p.templateUpdateAvailable && <Badge tone="accent">{t('providers.templateUpdate')}</Badge>}
        <span className="ml-auto shrink-0 text-[11.5px] text-[var(--text-muted)]">{t('providers.modelCount', { count: p.modelCount })}</span>
        <div className="flex shrink-0 items-center" onClick={stopClick}>
          <Button
            variant="plain"
            size="sm"
            icon={<FlaskConical className="size-3.5" />}
            aria-label={t('providers.test')}
            title={t('providers.test')}
            onClick={onTest}
          />
          <ProviderEnabledSwitch provider={p} />
        </div>
      </div>
      <div className="flex min-w-0 gap-1.5 text-[11.5px] text-[var(--text-muted)]">
        <span className="min-w-0 shrink truncate font-mono" title={p.endpoints[0]?.baseUrl}>
          {host ?? '—'}
        </span>
        <span>·</span>
        {quotaFailed ? (
          <span className="min-w-0 truncate text-[var(--warning)]" title={quotaFailed.message}>
            {t('providers.quota.failed', { error: failureText(quotaFailed, t) })}
          </span>
        ) : quota ? (
          <span className="num min-w-0 truncate text-[var(--text-secondary)]" title={quota}>
            {quota}
          </span>
        ) : (
          <span className={cn('min-w-0 truncate', p.hasApiKey && p.authScheme !== 'oauth-subscription' && 'font-mono')}>{credential}</span>
        )}
      </div>
      <div className="mt-auto flex items-end justify-between gap-2.5" onClick={stopClick}>
        <MiniBars data={requests} labels={labels} max={max} />
        {total > 0 ? (
          <span className="flex gap-2.5 whitespace-nowrap text-[11.5px] text-[var(--text-secondary)]" title={t('providers.card.requests7d')}>
            <span>
              <b className="num font-semibold text-[var(--text)]">{formatTokens(total, true)}</b> {t('providers.card.requestsUnit')}
            </span>
            {stat && stat.requests > 0 && (
              <span className="num" style={{ color: successColor(stat.successRate) }} title={t('providers.col.successRate')}>
                {formatPercent(stat.successRate)}
              </span>
            )}
            {stat && stat.costUsd > 0 && <span className="num">{formatUsd(stat.costUsd)}</span>}
          </span>
        ) : (
          <span className="text-[11.5px] text-[var(--text-muted)]">{t('providers.card.noRequests7d')}</span>
        )}
      </div>
    </div>
  );
}

const PLUGIN_CHOICE = 'plugin:';
const CATEGORY_ORDER = ['recommended', 'official', 'subscription', 'coding', 'cn', 'aggregator', 'inference', 'local', 'custom'] as const;
// Template tiles name protocols by their short form; the full name (PROTOCOL_OPTIONS) is the tooltip.
const PROTOCOL_SHORT: Record<ApiProtocol, string> = { 'openai-chat': 'Chat', 'openai-responses': 'Responses', anthropic: 'Anthropic', gemini: 'Gemini' };
const protocolName = (p: ApiProtocol) => PROTOCOL_OPTIONS.find((o) => o.value === p)?.label ?? p;
/** Compact tiles cut long names with an ellipsis; the tooltip keeps the whole text. */
const tip = (text: string) => <span title={text}>{text}</span>;
// "Claude 订阅（Pro/Max）" → ["Claude 订阅", "Pro/Max"]: a tile shows the plan on its second line instead of cutting it off.
const splitPlan = (name: string): [string, string | null] => {
  const m = /^(.+?)\s*[（(]([^（）()]+)[）)]$/.exec(name);
  return m ? [m[1], m[2]] : [name, null];
};

// Model ids typed into the wizard: one per line or comma separated, like the "Add models" sheet.
const MODEL_SPLIT = /[\s,，]+/;

/** The protocol order after the endpoints changed: removed protocols drop out, new ones join at the end. */
function followEndpoints(preferred: ApiProtocol[], before: TemplateEndpoint[], after: TemplateEndpoint[]): ApiProtocol[] {
  const had = new Set(before.map((e) => e.protocol));
  const now = after.map((e) => e.protocol);
  return [...preferred.filter((p) => now.includes(p)), ...now.filter((p) => !had.has(p) && !preferred.includes(p))];
}

/** A visible label over a group of controls (a Field would hand its label to every input inside). */
function Labeled({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <div className="flex min-w-0 flex-col">
      <div className="mb-2 text-[13px] font-medium">{label}</div>
      {children}
      {hint && <div className="mt-2 text-[11px] text-[var(--text-muted)]">{hint}</div>}
    </div>
  );
}

/** One address per protocol; a protocol already used by another row is not offered again. */
function EndpointsEditor({ value, onChange }: { value: TemplateEndpoint[]; onChange: (next: TemplateEndpoint[]) => void }) {
  const { t } = useI18n();
  const used = value.map((e) => e.protocol);
  const free = PROTOCOL_OPTIONS.filter((o) => !used.includes(o.value));
  const set = (i: number, patch: Partial<TemplateEndpoint>) => onChange(value.map((e, j) => (j === i ? { ...e, ...patch } : e)));
  return (
    <div className="grid gap-2">
      {value.map((e, i) => (
        <div key={i} className="flex flex-wrap items-center gap-2">
          <Select
            className="w-44"
            ariaLabel={t('providers.protocol')}
            value={e.protocol}
            onChange={(protocol) => set(i, { protocol })}
            options={PROTOCOL_OPTIONS.filter((o) => o.value === e.protocol || !used.includes(o.value))}
          />
          <Input
            className="min-w-52 flex-1"
            mono
            aria-label={t('providers.baseUrl')}
            placeholder="https://api.example.com/v1"
            value={e.baseUrl}
            onChange={(ev) => set(i, { baseUrl: ev.target.value })}
          />
          <label className="flex items-center gap-2 text-[12px] text-[var(--text-secondary)]" title={t('providers.fullUrlHint')}>
            <Switch checked={Boolean(e.fullUrl)} label={t('providers.fullUrl')} onChange={(fullUrl) => set(i, { fullUrl })} />
            {t('providers.fullUrl')}
          </label>
          <Button
            size="sm"
            variant="plain"
            icon={<Trash2 className="size-3.5" />}
            aria-label={t('common.delete')}
            disabled={value.length === 1}
            onClick={() => onChange(value.filter((_, j) => j !== i))}
          />
        </div>
      ))}
      {free.length > 0 && (
        <div>
          <Button
            size="sm"
            variant="plain"
            icon={<Plus className="size-3.5" />}
            onClick={() => onChange([...value, { protocol: free[0].value, baseUrl: value[0]?.baseUrl ?? '' }])}
          >
            {t('providers.addEndpoint')}
          </Button>
        </div>
      )}
    </div>
  );
}

function HeadersEditor({ value, onChange }: { value: [string, string][]; onChange: (next: [string, string][]) => void }) {
  const { t } = useI18n();
  const set = (i: number, h: [string, string]) => onChange(value.map((x, j) => (j === i ? h : x)));
  return (
    <div className="grid gap-2">
      {value.map(([k, v], i) => (
        <div key={i} className="flex gap-2">
          <Input className="w-44" mono aria-label={t('providers.headerName')} placeholder="Header" value={k} onChange={(e) => set(i, [e.target.value, v])} />
          <Input className="min-w-0 flex-1" mono aria-label={t('providers.headerValue')} placeholder="value" value={v} onChange={(e) => set(i, [k, e.target.value])} />
          <Button size="sm" variant="plain" icon={<Trash2 className="size-3.5" />} aria-label={t('common.delete')} onClick={() => onChange(value.filter((_, j) => j !== i))} />
        </div>
      ))}
      <div>
        <Button size="sm" variant="plain" icon={<Plus className="size-3.5" />} onClick={() => onChange([...value, ['', '']])}>
          {t('providers.addHeader')}
        </Button>
      </div>
    </div>
  );
}

function AddProviderSheet({ open, onOpenChange, onCreated }: { open: boolean; onOpenChange: (o: boolean) => void; onCreated: (p: Provider) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const templates = useTemplates();
  const priceKeys = usePriceKeys();
  const create = useCreateProvider();
  const providers = useProviders();
  // 订阅类模版单实例：已有实例的模版在向导里置灰，账号到该提供商内部添加。
  const addedSubscriptionTemplateIds = useMemo(
    () =>
      new Set(
        (providers.data ?? [])
          .filter((p) => p.authScheme === 'oauth-subscription' && p.templateId)
          .map((p) => p.templateId as string),
      ),
    [providers.data],
  );
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [template, setTemplate] = useState<ProviderTemplate | 'custom' | null>(null);
  // Step 1 selection (radio cards); "Continue" moves it into `template` (step 2).
  const [choice, setChoice] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [apiKey, setApiKey] = useState('');
  const [variantId, setVariantId] = useState('');
  const [priceKey, setPriceKey] = useState('');
  const [authScheme, setAuthScheme] = useState<AuthScheme>('bearer');
  // Step 2's "more options" (issue #8): start at the template's defaults, sent as the provider's own values.
  const [endpoints, setEndpoints] = useState<TemplateEndpoint[]>([]);
  const [preferred, setPreferred] = useState<ApiProtocol[]>([]);
  const [headers, setHeaders] = useState<[string, string][]>([]);
  const [proxy, setProxy] = useState('');
  const [multiplier, setMultiplier] = useState<number>(1);
  const [notes, setNotes] = useState('');
  const [models, setModels] = useState('');

  useEffect(() => {
    if (!open) {
      setTemplate(null);
      setChoice(null);
      setSearch('');
      setName('');
      setApiKey('');
      setVariantId('');
      setPriceKey('');
    }
  }, [open]);

  const grouped = useMemo(() => {
    const q = search.trim().toLowerCase();
    const list = (templates.data ?? []).filter((x) => !q || x.name.toLowerCase().includes(q) || x.id.includes(q));
    return CATEGORY_ORDER.map((c) => ({ category: c, items: list.filter((x) => x.category === c) })).filter((g) => g.items.length);
  }, [templates.data, search]);

  // 插件提供的提供商由插件同步生成（无需创建），选中即打开它，在里面登录账号。
  const pluginProviders = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (providers.data ?? []).filter(
      (p) =>
        p.authScheme === 'plugin' &&
        !(p.settings.plugin as { absent?: boolean } | undefined)?.absent &&
        (!q || p.name.toLowerCase().includes(q) || p.id.includes(q)),
    );
  }, [providers.data, search]);

  const pick = (tpl: ProviderTemplate | 'custom') => {
    setTemplate(tpl);
    setVariantId('');
    setProxy('');
    setNotes('');
    setModels('');
    if (tpl !== 'custom') {
      setName(tpl.name);
      setPriceKey(tpl.priceKey ?? tpl.id);
      setAuthScheme(tpl.auth.scheme);
      setEndpoints(tpl.endpoints.map((e) => ({ ...e })));
      setPreferred([...tpl.preferredUpstreamProtocols]);
      setHeaders(Object.entries(tpl.defaultHeaders ?? {}));
      setMultiplier(tpl.defaultPriceMultiplier ?? 1);
    } else {
      setName('');
      setPriceKey('');
      setAuthScheme('bearer');
      setEndpoints([{ protocol: 'openai-chat', baseUrl: '' }]);
      setPreferred(['openai-chat']);
      setHeaders([]);
      setMultiplier(1);
    }
  };

  const changeEndpoints = (next: TemplateEndpoint[]) => {
    setPreferred((p) => followEndpoints(p, endpoints, next));
    setEndpoints(next);
  };

  const tpl = template && template !== 'custom' ? template : null;
  // Subscription templates sign in with accounts after creation; their addresses and headers are the template's.
  const accounts = tpl?.authScheme === 'oauth-subscription';
  const modelIds = models.split(MODEL_SPLIT).map((m) => m.trim()).filter(Boolean);
  const alternatives = (tpl?.keyAlternatives ?? []).flatMap((id) => (templates.data ?? []).filter((x) => x.id === id));

  const submit = () => {
    const custom = template === 'custom';
    create.mutate(
      {
        templateId: custom ? null : template!.id,
        variantId: variantId || null,
        name: name.trim() || null,
        apiKey: apiKey.trim() || null,
        priceKey,
        ...(accounts
          ? {}
          : {
              endpoints: endpoints.map((e) => ({ ...e, baseUrl: e.baseUrl.trim() })),
              authScheme,
              preferredUpstreamProtocols: preferred.filter((p) => endpoints.some((e) => e.protocol === p)),
              extraHeaders: Object.fromEntries(headers.filter(([k]) => k.trim()).map(([k, v]) => [k.trim(), v])),
              httpProxy: proxy.trim() || null,
              priceMultiplier: multiplier,
              notes: notes.trim() || null,
              ...(modelIds.length > 0 ? { models: modelIds } : {}),
            }),
      },
      {
        onSuccess: (p) => {
          onOpenChange(false);
          toast(t('providers.created', { name: p.name }), 'success');
          onCreated(p);
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );
  };

  const proceed = () => {
    if (choice === 'custom') pick('custom');
    else if (choice?.startsWith(PLUGIN_CHOICE)) {
      const found = pluginProviders.find((p) => PLUGIN_CHOICE + p.id === choice);
      if (found) {
        onOpenChange(false);
        onCreated(found);
      }
    } else {
      const found = (templates.data ?? []).find((x) => x.id === choice);
      // 已创建实例的订阅模版不允许继续（双保险：置灰已不可选）。
      if (found && !addedSubscriptionTemplateIds.has(found.id)) pick(found);
    }
  };
  const endpointsOk = accounts || (endpoints.length > 0 && endpoints.every((e) => /^https?:\/\//.test(e.baseUrl.trim())));
  const canSubmit =
    template !== null &&
    endpointsOk &&
    (template === 'custom' ? name.trim() !== '' : !tpl!.requiresApiKey || authScheme === 'none' || apiKey.trim() !== '');

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      width={640}
      title={template ? t('providers.addTitleDetails') : t('providers.addTitle')}
      description={template ? (tpl ? tpl.name : t('providers.custom')) : t('providers.addDetail')}
      footer={
        template ? (
          <>
            <Button className="mr-auto" variant="plain" onClick={() => setTemplate(null)}>
              {t('common.back')}
            </Button>
            <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
            <Button variant="primary" disabled={!canSubmit} loading={create.isPending} onClick={submit}>
              {t('providers.create')}
            </Button>
          </>
        ) : (
          <>
            <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
            <Button variant="primary" disabled={!choice} onClick={proceed}>
              {t('common.continue')}
            </Button>
          </>
        )
      }
    >
      <Stepper
        className="mb-6"
        label={t('providers.addTitle')}
        current={template ? 1 : 0}
        steps={[
          { id: 'template', label: t('providers.step.template') },
          { id: 'details', label: t('providers.step.details') },
        ]}
      />
      {!template ? (
        <div className="flex flex-col gap-4">
          <SearchField value={search} onChange={setSearch} placeholder={t('providers.searchTemplates')} />
          {templates.isLoading && <Spinner lines={4} />}
          {templates.isError && <Alert tone="danger" title={t('common.error')}>{errorText(templates.error)}</Alert>}
          {grouped.map((g) => (
            <section key={g.category}>
              <h3 className="mb-2 text-[12px] font-medium text-[var(--text-secondary)]">{t(`providers.category.${g.category}`)}</h3>
              <RadioCards
                name="provider-template"
                aria-label={t(`providers.category.${g.category}`)}
                density="compact"
                minColumnWidth={168}
                value={g.items.some((x) => x.id === choice) ? choice : null}
                onValueChange={setChoice}
                options={g.items.map((x) => {
                  const protocols = [...new Set(x.endpoints.map((e) => e.protocol))];
                  const [label, plan] = splitPlan(x.name);
                  return {
                    value: x.id,
                    label: <span title={x.name}>{label}</span>,
                    description: <span title={protocols.map(protocolName).join(' · ')}>{plan ?? protocols.map((p) => PROTOCOL_SHORT[p]).join(' · ')}</span>,
                    icon: <ProviderIcon name={x.name} icon={x.icon} colorKey={x.id} size={28} />,
                    disabled: addedSubscriptionTemplateIds.has(x.id),
                    disabledReason: tip(t('providers.subscriptionAdded')),
                  };
                })}
              />
            </section>
          ))}
          <section>
            <h3 className="mb-2 text-[12px] font-medium text-[var(--text-secondary)]">{t('providers.category.plugin')}</h3>
            {pluginProviders.length > 0 ? (
              <RadioCards
                name="provider-template-plugin"
                aria-label={t('providers.category.plugin')}
                density="compact"
                minColumnWidth={168}
                value={choice?.startsWith(PLUGIN_CHOICE) ? choice : null}
                onValueChange={setChoice}
                options={pluginProviders.map((p) => ({
                  value: PLUGIN_CHOICE + p.id,
                  label: tip(p.name),
                  description: tip(t('providers.pluginProvided')),
                  icon: <ProviderIcon name={p.name} icon={p.icon} colorKey={p.id} size={28} />,
                }))}
              />
            ) : (
              <div className="flex flex-wrap items-center gap-3 rounded-[var(--radius-md)] border border-dashed border-[var(--border)] px-3 py-2.5">
                <p className="min-w-0 flex-1 text-[12px] text-[var(--text-secondary)]">{t('providers.pluginHint')}</p>
                <Button
                  size="sm"
                  onClick={() => {
                    onOpenChange(false);
                    navigate('/plugins/market');
                  }}
                >
                  {t('plugins.browseMarket')}
                </Button>
              </div>
            )}
          </section>
          <section>
            <h3 className="mb-2 text-[12px] font-medium text-[var(--text-secondary)]">{t('providers.custom')}</h3>
            <RadioCards
              name="provider-template-custom"
              aria-label={t('providers.custom')}
              layout="list"
              density="compact"
              value={choice === 'custom' ? 'custom' : null}
              onValueChange={setChoice}
              options={[
                {
                  value: 'custom',
                  label: t('providers.custom'),
                  description: t('providers.customDetail'),
                  icon: (
                    <span className="inline-flex size-7 items-center justify-center rounded-[8px] border border-dashed border-[var(--border-strong)] text-[var(--text-secondary)]">
                      <Plus className="size-4" />
                    </span>
                  ),
                },
              ]}
            />
          </section>
        </div>
      ) : (
        <div className="grid gap-4">
          <Field label={t('providers.name')}>
            <Input value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          {tpl?.variants && tpl.variants.length > 0 && (
            <Field label={t('providers.variant')}>
              <Select
                value={variantId}
                onChange={(v) => {
                  setVariantId(v);
                  const variant = tpl.variants?.find((x) => x.id === v);
                  setPriceKey(variant?.priceKey ?? tpl.priceKey ?? tpl.id);
                  changeEndpoints((variant?.endpointsOverride ?? tpl.endpoints).map((e) => ({ ...e })));
                }}
                options={[{ value: '', label: t('providers.variantDefault') }, ...tpl.variants.map((v) => ({ value: v.id, label: v.name }))]}
              />
            </Field>
          )}
          {template === 'custom' && (
            <>
              <Labeled label={t('providers.section.endpoints')} hint={t('providers.baseUrlHint')}>
                <EndpointsEditor value={endpoints} onChange={changeEndpoints} />
              </Labeled>
              <Field label={t('providers.authScheme')}>
                <Select value={authScheme} onChange={setAuthScheme} options={AUTH_OPTIONS} />
              </Field>
            </>
          )}
          {accounts ? (
            <Alert tone="info" title={t('providers.subscriptionWizardHint')}>
              {t('providers.subscriptionWizardDetail')}
            </Alert>
          ) : (
            <div className="font-mono">
              <PasswordField
                label={t('providers.apiKey')}
                description={t('providers.apiKeyHint')}
                autoComplete="off"
                placeholder="sk-…"
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
              />
              {tpl?.apiKeyUrl && (
                <a className="mt-2 inline-block font-sans text-[12px] text-[var(--accent)]" href={tpl.apiKeyUrl} target="_blank" rel="noreferrer">
                  {t('providers.getKey')}
                </a>
              )}
            </div>
          )}
          {tpl && alternatives.length > 0 && (
            <div className="flex flex-wrap items-center gap-2 text-[12px] text-[var(--text-secondary)]">
              <span>{t('providers.keyAlternative.wizard', { name: tpl.name, alt: alternatives.map((a) => a.name).join(' / ') })}</span>
              {alternatives.map((a) => (
                <Button
                  key={a.id}
                  size="sm"
                  variant="plain"
                  onClick={() => {
                    setChoice(a.id);
                    pick(a);
                  }}
                >
                  {t('providers.keyAlternative.pick', { alt: a.name })}
                </Button>
              ))}
            </div>
          )}
          <Field label={t('providers.priceKey')} hint={t('providers.priceKeyHint')}>
            <Select
              value={priceKey}
              onChange={setPriceKey}
              options={[
                { value: '', label: t('providers.priceKeyNone') },
                ...(priceKeys.data ?? []).map((k) => ({ value: k.priceKey, label: k.label, detail: k.priceKey })),
              ]}
            />
          </Field>
          {!accounts && (
            <Disclosure title={t('providers.moreOptions')}>
              <div className="grid gap-4">
                {tpl && (
                  <Labeled label={t('providers.section.endpoints')} hint={t('providers.endpointsFooter')}>
                    <EndpointsEditor value={endpoints} onChange={changeEndpoints} />
                  </Labeled>
                )}
                {new Set(endpoints.map((e) => e.protocol)).size > 1 && (
                  <Labeled label={t('providers.preferred')} hint={t('providers.preferredHint')}>
                    <ChipGroup
                      label={t('providers.preferred')}
                      options={PROTOCOL_OPTIONS.filter((o) => endpoints.some((e) => e.protocol === o.value)).map((o) => {
                        const idx = preferred.indexOf(o.value);
                        return { value: o.value, label: idx >= 0 ? `${idx + 1}. ${o.label}` : o.label };
                      })}
                      value={preferred}
                      onValueChange={(next) => {
                        // The chips report options order; keep the priority order in which protocols were picked.
                        const kept = preferred.filter((x) => next.includes(x));
                        const added = next.filter((x) => !kept.includes(x as ApiProtocol)) as ApiProtocol[];
                        setPreferred([...kept, ...added]);
                      }}
                    />
                  </Labeled>
                )}
                {tpl && (
                  <Field label={t('providers.authScheme')}>
                    <Select value={authScheme} onChange={setAuthScheme} options={AUTH_OPTIONS} />
                  </Field>
                )}
                <Labeled label={t('providers.headers')}>
                  <HeadersEditor value={headers} onChange={setHeaders} />
                </Labeled>
                <Field label={t('providers.proxy')} hint={t('providers.proxyHint')}>
                  <Input mono placeholder="http://127.0.0.1:7890" value={proxy} onChange={(e) => setProxy(e.target.value)} />
                </Field>
                {/* Labeled, not Field: a Field's hint would wrap inside the narrow number box. */}
                <Labeled label={t('providers.multiplier')} hint={t('providers.multiplierHint')}>
                  <NumberInput className="w-28" label={t('providers.multiplier')} step={0.01} min={0} value={multiplier} onChange={(v) => setMultiplier(v ?? 1)} />
                </Labeled>
                <Field
                  label={t('providers.initialModels')}
                  hint={tpl ? t('providers.initialModelsTemplateHint', { count: tpl.models.length }) : t('providers.initialModelsCustomHint')}
                >
                  <TextArea rows={3} mono value={models} onChange={(e) => setModels(e.target.value)} />
                </Field>
                <Field label={t('providers.notes')}>
                  <Input value={notes} onChange={(e) => setNotes(e.target.value)} />
                </Field>
                <p className="text-[11px] text-[var(--text-muted)]">{t('providers.moreOptionsDetail')}</p>
              </div>
            </Disclosure>
          )}
          {tpl && tpl.models.length > 0 && modelIds.length === 0 && (
            <p className="text-[12px] text-[var(--text-secondary)]">{t('providers.templateModels', { count: tpl.models.length })}</p>
          )}
        </div>
      )}
    </Sheet>
  );
}
