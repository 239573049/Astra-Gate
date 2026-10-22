import { Download, FlaskConical, Plus, Server, Trash2 } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useNavigate, useSearchParams } from 'react-router';

import {
  useCreateProvider,
  usePriceKeys,
  useProviders,
  useTemplates,
  useTimeseries,
  useUpdateProvider,
} from '../api/hooks';
import type { ApiProtocol, AuthScheme, Provider, ProviderTemplate, TemplateEndpoint } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { Card } from '../components/arc/card/card';
import { ChipGroup } from '../components/arc/chip-group/chip-group';
import { PasswordField } from '../components/arc/password-field/password-field';
import { RadioCards } from '../components/arc/radio-cards/radio-cards';
import { Sparkline } from '../components/arc/sparkline/sparkline';
import { Stepper } from '../components/arc/stepper/stepper';
import { CLIENT_META, clientName, ClientIcon, ProviderIcon } from '../components/icons';
import { ImportProvidersSheet } from '../components/ImportProvidersSheet';
import { Page } from '../components/layout/Page';
import { ProviderQuotaLine } from '../components/ProviderQuotaSection';
import { ProviderTestDialog } from '../components/ProviderTestDialog';
import { Badge, Button, Disclosure, Dot, EmptyState, Field, Input, NumberInput, SearchField, Spinner, Switch, TextArea } from '../components/ui/controls';
import { errorText, Select, Sheet, useFeedback } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';
import { formatTokens, formatUsd } from '../lib/format';
import { useCommandListener } from '../shell/commands';
import type { ClientKind } from '../api/types';

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
  const [testing, setTesting] = useState<Provider | null>(null);
  const usage = useMemo(() => {
    const days = lastDays(USAGE_DAYS);
    const byProvider = new Map<string, ProviderUsage>();
    for (const pt of series.data ?? []) {
      const i = days.indexOf(pt.bucket);
      if (i < 0) continue;
      let u = byProvider.get(pt.key);
      if (!u) byProvider.set(pt.key, (u = { requests: days.map(() => 0), costUsd: 0 }));
      u.requests[i] = (u.requests[i] ?? 0) + pt.requests;
      u.costUsd += pt.costUsd;
    }
    return { days, byProvider };
  }, [series.data]);

  return (
    <Page
      title={t('nav.providers')}
      subtitle={providers.data ? t('providers.count', { count: providers.data.length }) : undefined}
      actions={
        <>
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
          <div className="grid grid-cols-[repeat(auto-fill,minmax(min(100%,260px),1fr))] gap-3">
            {providers.data.map((p) => (
              <ProviderCard
                key={p.id}
                p={p}
                days={usage.days}
                usage={usage.byProvider.get(p.name)}
                onOpen={() => navigate(`/providers/${p.id}`)}
                onTest={() => setTesting(p)}
              />
            ))}
          </div>
        )}
      </div>
      <ImportProvidersSheet open={importing} onOpenChange={setImporting} />
      <AddProviderSheet open={adding} onOpenChange={setAdding} onCreated={(p) => navigate(`/providers/${p.id}`)} />
      {/* Rendered outside the cards on purpose: a dialog portaled from inside a clickable card would still bubble
          its clicks up React's tree and open the provider behind it. */}
      {testing && <ProviderTestDialog provider={testing} open onOpenChange={(o) => !o && setTesting(null)} />}
    </Page>
  );
}

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

function hostOf(url: string | undefined): string | null {
  if (!url) return null;
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

/** Keeps clicks on a card's own controls (and their portaled menus) from also opening the provider. */
const stopClick = (e: React.MouseEvent) => e.stopPropagation();

function ProviderCard({ p, days, usage, onOpen, onTest }: { p: Provider; days: string[]; usage?: ProviderUsage; onOpen: () => void; onTest: () => void }) {
  const { t } = useI18n();
  const update = useUpdateProvider(p.id);
  const { toast } = useFeedback();

  const protocols = [...new Set(p.endpoints.map((e) => e.protocol))];
  const missingKey = !p.hasApiKey && p.authScheme !== 'none' && p.authScheme !== 'oauth-subscription' && p.authScheme !== 'plugin';
  const pricing = [p.priceKey ? t('providers.priceKeyShort', { key: p.priceKey }) : null, p.priceMultiplier !== 1 ? `×${p.priceMultiplier}` : null]
    .filter(Boolean)
    .join(' · ');
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
  const state = !p.enabled
    ? { tone: 'neutral' as const, label: t('providers.card.disabled') }
    : missingKey
      ? { tone: 'orange' as const, label: t('providers.noKey') }
      : { tone: 'green' as const, label: t('providers.card.active') };
  const requests = usage?.requests ?? days.map(() => 0);
  const totalRequests = requests.reduce((a, b) => a + b, 0);
  const maxClients = p.templateUpdateAvailable ? 5 : 8;

  return (
    // The whole card opens the provider; like Row it holds its own controls, so it cannot be a <button>.
    // Uniform, compact height: the title is one line, the description is clamped to two lines with a
    // reserved minimum, and the footer is pinned to the bottom of the content column.
    <Card
      className="flex flex-col [&>div:first-child>div]:min-h-0! [&>div:last-child]:flex [&>div:last-child]:flex-1 [&>div:last-child]:flex-col [&>div:last-child]:p-3 [&>div:last-child>:last-child]:mt-auto! [&>div:last-child>:last-child]:min-h-[44px] [&>div:last-child>:last-child]:pt-2 [&>div:last-child>h3]:line-clamp-1 [&>div:last-child>h3]:text-[15px] [&>div:last-child>p]:line-clamp-2 [&>div:last-child>p]:min-h-[2lh] [&>div:last-child>p]:text-[12px]"
      data-density="compact"
      role="button"
      tabIndex={0}
      aria-label={p.name}
      onClick={onOpen}
      onKeyDown={(e) => {
        if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) {
          e.preventDefault();
          onOpen();
        }
      }}
      // Same translucent content layer as `.card`, so the window background still shows through.
      style={{ backgroundColor: 'var(--surface)' }}
      title={p.name}
      description={protocols.map((x) => PROTOCOL_OPTIONS.find((o) => o.value === x)?.label ?? x).join(' / ')}
      media={
        <div
          className="flex flex-col justify-between gap-1.5 self-stretch justify-self-stretch px-3 py-2.5"
          style={{ background: 'linear-gradient(135deg, color-mix(in oklab, var(--accent) 12%, transparent), transparent 70%)' }}
        >
          <div className="flex items-start justify-between gap-2">
            <span className={cn('transition-[filter,opacity]', !p.enabled && 'opacity-50 grayscale')}>
              <ProviderIcon name={p.name} icon={p.icon} colorKey={p.templateId} size={28} />
            </span>
            <span className="inline-flex items-center gap-1.5 rounded-full border border-[var(--border)] bg-[color-mix(in_oklab,var(--surface)_80%,transparent)] px-2 py-0.5 text-[11px] text-[var(--text-secondary)]">
              <Dot tone={state.tone} />
              {state.label}
            </span>
          </div>
          <div className="flex items-baseline justify-between gap-3 text-[11px] text-[var(--text-muted)]">
            <span className="min-w-0 truncate font-mono" title={p.endpoints[0]?.baseUrl}>
              {hostOf(p.endpoints[0]?.baseUrl) ?? '—'}
            </span>
            <span className="shrink-0">{t('providers.modelCount', { count: p.modelCount })}</span>
          </div>
        </div>
      }
      meta={<span className={cn(p.hasApiKey && p.authScheme !== 'oauth-subscription' && 'font-mono')}>{credential}</span>}
      status={pricing || undefined}
      action={
        <div className="flex items-center gap-1" onClick={stopClick}>
          <Button
            variant="plain"
            size="sm"
            icon={<FlaskConical className="size-3.5" />}
            aria-label={t('providers.test')}
            title={t('providers.test')}
            onClick={onTest}
          />
          <span className="ml-1 inline-flex">
            <Switch
              checked={p.enabled}
              label={t('common.enabled')}
              onChange={(enabled) => update.mutate({ enabled }, { onError: (e) => toast(errorText(e), 'error') })}
            />
          </span>
        </div>
      }
    >
      {/* Fixed one-row slot (also when empty), so every card keeps the same height. Overflow collapses into "+N". */}
      <div className="mt-1.5 flex h-5 flex-nowrap items-center gap-1 overflow-hidden">
        {p.templateUpdateAvailable && <Badge tone="accent">{t('providers.templateUpdate')}</Badge>}
        {p.boundClients.slice(0, maxClients).map((k) =>
          k in CLIENT_META ? (
            <span key={k} title={clientName(k)} className="inline-flex shrink-0">
              <ClientIcon kind={k as ClientKind} size={18} />
            </span>
          ) : (
            <Badge key={k} tone="green">{k}</Badge>
          ),
        )}
        {p.boundClients.length > maxClients && (
          <span
            title={p.boundClients.slice(maxClients).map((k) => CLIENT_META[k as ClientKind]?.name ?? k).join(', ')}
            className="shrink-0 text-[11px] text-[var(--text-muted)]"
          >
            +{p.boundClients.length - maxClients}
          </span>
        )}
      </div>
      {/* Balance / quota of the provider's usage query; its refresh button must not open the provider. Reserves its row when absent. */}
      <div className="min-h-[32px] [&>div]:mt-1" onClick={stopClick}>
        <ProviderQuotaLine provider={p} />
      </div>
      {/* Scrubbing the trend must not open the provider. */}
      <div className="mt-2" onClick={stopClick}>
        {totalRequests > 0 ? (
          <Sparkline
            label={t('providers.card.requests7d')}
            data={requests}
            labels={days.map((d) => d.slice(5))}
            value={formatTokens(totalRequests, true)}
            change={formatUsd(usage?.costUsd ?? 0)}
            width={320}
            height={28}
            formatValue={(v) => formatTokens(v, true)}
          />
        ) : (
          // Same footprint as the sparkline, so cards with and without traffic line up.
          <div className="grid gap-2">
            <div className="text-[13px] text-[var(--text-secondary)]">{t('providers.card.requests7d')}</div>
            <div className="grid aspect-[320/28] place-items-center border-b border-dashed border-[var(--border)] text-[11px] text-[var(--text-muted)]">
              {t('providers.card.noRequests')}
            </div>
          </div>
        )}
      </div>
    </Card>
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
