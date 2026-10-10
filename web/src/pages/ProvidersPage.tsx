import { Download, FlaskConical, Plus, Server } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';

import {
  useCreateProvider,
  usePriceKeys,
  useProviders,
  useTemplates,
  useTimeseries,
  useUpdateProvider,
} from '../api/hooks';
import type { ApiProtocol, AuthScheme, Provider, ProviderTemplate } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { Card } from '../components/arc/card/card';
import { PasswordField } from '../components/arc/password-field/password-field';
import { RadioCards } from '../components/arc/radio-cards/radio-cards';
import { Sparkline } from '../components/arc/sparkline/sparkline';
import { Stepper } from '../components/arc/stepper/stepper';
import { CLIENT_META, ProviderIcon } from '../components/icons';
import { ImportProvidersSheet } from '../components/ImportProvidersSheet';
import { Page } from '../components/layout/Page';
import { ProviderQuotaLine } from '../components/ProviderQuotaSection';
import { ProviderTestDialog } from '../components/ProviderTestDialog';
import { Badge, Button, Dot, EmptyState, Field, Input, SearchField, Spinner, Switch } from '../components/ui/controls';
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
          <div className="grid grid-cols-[repeat(auto-fill,minmax(min(100%,260px),1fr))] items-start gap-3">
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
  const missingKey = !p.hasApiKey && p.authScheme !== 'none' && p.authScheme !== 'oauth-subscription';
  const pricing = [p.priceKey ? t('providers.priceKeyShort', { key: p.priceKey }) : null, p.priceMultiplier !== 1 ? `×${p.priceMultiplier}` : null]
    .filter(Boolean)
    .join(' · ');
  const credential =
    p.authScheme === 'oauth-subscription'
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

  return (
    // The whole card opens the provider; like Row it holds its own controls, so it cannot be a <button>.
    <Card
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
          className="flex flex-col justify-between gap-2 self-stretch justify-self-stretch p-3"
          style={{ background: 'linear-gradient(135deg, color-mix(in oklab, var(--accent) 12%, transparent), transparent 70%)' }}
        >
          <div className="flex items-start justify-between gap-2">
            <span className={cn('transition-[filter,opacity]', !p.enabled && 'opacity-50 grayscale')}>
              <ProviderIcon name={p.name} icon={p.icon} colorKey={p.templateId} size={32} />
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
      {(p.templateUpdateAvailable || p.boundClients.length > 0) && (
        <div className="mt-2 flex flex-wrap gap-1">
          {p.templateUpdateAvailable && <Badge tone="accent">{t('providers.templateUpdate')}</Badge>}
          {p.boundClients.map((k) => (
            <Badge key={k} tone="green">
              {CLIENT_META[k as ClientKind]?.name ?? k}
            </Badge>
          ))}
        </div>
      )}
      {/* Balance / quota of the provider's usage query; its refresh button must not open the provider. */}
      <div onClick={stopClick}>
        <ProviderQuotaLine provider={p} />
      </div>
      {/* Scrubbing the trend must not open the provider. */}
      <div className="mt-3" onClick={stopClick}>
        {totalRequests > 0 ? (
          <Sparkline
            label={t('providers.card.requests7d')}
            data={requests}
            labels={days.map((d) => d.slice(5))}
            value={formatTokens(totalRequests, true)}
            change={formatUsd(usage?.costUsd ?? 0)}
            width={320}
            height={36}
            formatValue={(v) => formatTokens(v, true)}
          />
        ) : (
          // Same footprint as the sparkline, so cards with and without traffic line up.
          <div className="grid gap-3">
            <div className="text-[13px] text-[var(--text-secondary)]">{t('providers.card.requests7d')}</div>
            <div className="grid aspect-[320/36] place-items-center border-b border-dashed border-[var(--border)] text-[11px] text-[var(--text-muted)]">
              {t('providers.card.noRequests')}
            </div>
          </div>
        )}
      </div>
    </Card>
  );
}

const CATEGORY_ORDER = ['recommended', 'official', 'subscription', 'cn', 'aggregator', 'local', 'custom'] as const;

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
  const [search, setSearch] = useState('');
  const [template, setTemplate] = useState<ProviderTemplate | 'custom' | null>(null);
  // Step 1 selection (radio cards); "Continue" moves it into `template` (step 2).
  const [choice, setChoice] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [apiKey, setApiKey] = useState('');
  const [variantId, setVariantId] = useState('');
  const [priceKey, setPriceKey] = useState('');
  const [baseUrl, setBaseUrl] = useState('');
  const [protocol, setProtocol] = useState<ApiProtocol>('openai-chat');
  const [authScheme, setAuthScheme] = useState<AuthScheme>('bearer');

  useEffect(() => {
    if (!open) {
      setTemplate(null);
      setChoice(null);
      setSearch('');
      setName('');
      setApiKey('');
      setVariantId('');
      setPriceKey('');
      setBaseUrl('');
    }
  }, [open]);

  const grouped = useMemo(() => {
    const q = search.trim().toLowerCase();
    const list = (templates.data ?? []).filter((x) => !q || x.name.toLowerCase().includes(q) || x.id.includes(q));
    return CATEGORY_ORDER.map((c) => ({ category: c, items: list.filter((x) => x.category === c) })).filter((g) => g.items.length);
  }, [templates.data, search]);

  const pick = (tpl: ProviderTemplate | 'custom') => {
    setTemplate(tpl);
    if (tpl !== 'custom') {
      setName(tpl.name);
      setPriceKey(tpl.priceKey ?? tpl.id);
      setVariantId('');
      setAuthScheme(tpl.auth.scheme);
    } else {
      setName('');
      setPriceKey('');
      setVariantId('');
    }
  };

  const submit = () => {
    const custom = template === 'custom';
    create.mutate(
      {
        templateId: custom ? null : template!.id,
        variantId: variantId || null,
        name: name.trim() || null,
        apiKey: apiKey.trim() || null,
        priceKey,
        ...(custom ? { endpoints: [{ protocol, baseUrl: baseUrl.trim() }], authScheme } : {}),
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

  const tpl = template && template !== 'custom' ? template : null;
  const proceed = () => {
    if (choice === 'custom') pick('custom');
    else {
      const found = (templates.data ?? []).find((x) => x.id === choice);
      // 已创建实例的订阅模版不允许继续（双保险：置灰已不可选）。
      if (found && !addedSubscriptionTemplateIds.has(found.id)) pick(found);
    }
  };
  const canSubmit =
    template !== null &&
    (template === 'custom' ? /^https?:\/\//.test(baseUrl.trim()) && name.trim() !== '' : !tpl!.requiresApiKey || apiKey.trim() !== '');

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
        <div className="flex flex-col gap-5">
          <SearchField value={search} onChange={setSearch} placeholder={t('providers.searchTemplates')} />
          {templates.isLoading && <Spinner lines={4} />}
          {templates.isError && <Alert tone="danger" title={t('common.error')}>{errorText(templates.error)}</Alert>}
          {grouped.map((g) => (
            <section key={g.category}>
              <h3 className="mb-2 text-[12px] font-medium text-[var(--text-secondary)]">{t(`providers.category.${g.category}`)}</h3>
              <RadioCards
                name="provider-template"
                aria-label={t(`providers.category.${g.category}`)}
                minColumnWidth={170}
                value={g.items.some((x) => x.id === choice) ? choice : null}
                onValueChange={setChoice}
                options={g.items.map((x) => ({
                  value: x.id,
                  label: x.name,
                  description: [...new Set(x.endpoints.map((e) => e.protocol))].join(' · '),
                  icon: <ProviderIcon name={x.name} icon={x.icon} colorKey={x.id} size={24} />,
                  disabled: addedSubscriptionTemplateIds.has(x.id),
                  disabledReason: t('providers.subscriptionAdded'),
                }))}
              />
            </section>
          ))}
          <section>
            <h3 className="mb-2 text-[12px] font-medium text-[var(--text-secondary)]">{t('providers.custom')}</h3>
            <RadioCards
              name="provider-template-custom"
              aria-label={t('providers.custom')}
              layout="list"
              value={choice === 'custom' ? 'custom' : null}
              onValueChange={setChoice}
              options={[{ value: 'custom', label: t('providers.custom'), description: t('providers.customDetail'), icon: <Plus className="size-4" /> }]}
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
                }}
                options={[{ value: '', label: t('providers.variantDefault') }, ...tpl.variants.map((v) => ({ value: v.id, label: v.name }))]}
              />
            </Field>
          )}
          {template === 'custom' && (
            <>
              <Field label={t('providers.baseUrl')} hint={t('providers.baseUrlHint')}>
                <Input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder="https://api.example.com/v1" className="font-mono" />
              </Field>
              <div className="grid grid-cols-2 gap-3">
                <Field label={t('providers.protocol')}>
                  <Select value={protocol} onChange={setProtocol} options={PROTOCOL_OPTIONS} />
                </Field>
                <Field label={t('providers.authScheme')}>
                  <Select value={authScheme} onChange={setAuthScheme} options={AUTH_OPTIONS} />
                </Field>
              </div>
            </>
          )}
          {tpl?.authScheme === 'oauth-subscription' ? (
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
          {tpl && tpl.models.length > 0 && <p className="text-[12px] text-[var(--text-secondary)]">{t('providers.templateModels', { count: tpl.models.length })}</p>}
        </div>
      )}
    </Sheet>
  );
}
