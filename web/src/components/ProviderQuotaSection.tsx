import { FlaskConical, Gauge } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';

import { useFetchProviderQuota, useQuotaTemplates, useSaveProviderQuotaConfig, useTestProviderQuota } from '../api/hooks';
import type {
  Provider,
  ProviderQuotaConfig,
  ProviderQuotaConfigInput,
  ProviderQuotaRequest,
  ProviderQuotaSnapshot,
  ProviderQuotaTest,
  QuotaTemplate,
} from '../api/types';
import { Alert } from './arc/alert/alert';
import { Badge, Button, CodeBlock, Group, Input, KV, NumberInput, Row, Switch, TextArea } from './ui/controls';
import { errorText, Select, Sheet, useFeedback } from './ui/overlays';
import { useI18n, type MessageKey } from '../i18n';
import { failureText, planPhrase, planResets, quotaEnabled, quotaFailure, quotaSummary } from '../lib/quota';
import { updatedAgo } from '../lib/tray';

const AUTO = '';
const CUSTOM = 'custom';

/** The parameters a custom query can use besides {{apiKey}} / {{baseUrl}} / {{origin}} / {{host}} (cc-switch's set). */
const CUSTOM_PARAMS: QuotaTemplate['params'] = [
  { name: 'userId', secret: false, required: false },
  { name: 'accessToken', secret: true, required: false },
];

/** Starting point of a custom query when no template applies (cc-switch's generic template, in extract form). */
const GENERIC_REQUEST: ProviderQuotaRequest = { method: 'GET', url: '{{baseUrl}}/user/balance', auth: 'provider', headers: {} };
const GENERIC_EXTRACT = { isValid: 'is_active', remaining: 'balance', unit: { $const: 'USD' } };

interface Form {
  enabled: boolean;
  template: string;
  intervalMinutes: number | null;
  timeoutSec: number | null;
  baseUrl: string;
  params: Record<string, string>;
  /** Newly typed secret values; empty = keep the stored one. */
  secrets: Record<string, string>;
  method: 'GET' | 'POST';
  url: string;
  auth: 'provider' | 'none';
  headers: string;
  extract: string;
}

const pretty = (v: unknown) => JSON.stringify(v ?? {}, null, 2);

function toForm(c: ProviderQuotaConfig): Form {
  return {
    enabled: c.enabled,
    template: c.template ?? AUTO,
    intervalMinutes: c.intervalMinutes ?? null,
    timeoutSec: c.timeoutSec,
    baseUrl: c.baseUrl ?? '',
    params: { ...c.params },
    secrets: {},
    method: c.request?.method ?? 'GET',
    url: c.request?.url ?? '',
    auth: c.request?.auth ?? 'provider',
    headers: pretty(c.request?.headers),
    extract: c.extract ? pretty(c.extract) : '',
  };
}

function parseObject(text: string): Record<string, unknown> | null {
  try {
    const v: unknown = JSON.parse(text || '{}');
    return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/**
 * Balance / quota query of an API-key provider (cc-switch's "usage query"): pick a built-in template or
 * write a custom request + declarative extractor, test it unsaved, and query now. Subscription providers
 * show their quota per account instead, so this section is not rendered for them.
 */
export function ProviderQuotaSection({ provider }: { provider: Provider }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const templates = useQuotaTemplates();
  const save = useSaveProviderQuotaConfig(provider.id);
  const test = useTestProviderQuota(provider.id);
  const fetchNow = useFetchProviderQuota();
  const config = provider.quotaConfig;
  const base = useMemo(() => toForm(config), [config]);
  const [form, setForm] = useState<Form>(base);
  const [result, setResult] = useState<ProviderQuotaTest | null>(null);

  useEffect(() => setForm(base), [base]);

  const all = templates.data ?? [];
  const suggested = all.find((x) => x.id === config.suggestedTemplate);
  const chosen = form.template === AUTO ? suggested : all.find((x) => x.id === form.template);
  const isCustom = form.template === CUSTOM;
  const params = isCustom ? CUSTOM_PARAMS : (chosen?.params ?? []);

  const set = (patch: Partial<Form>) => setForm((f) => ({ ...f, ...patch }));
  const dirty = JSON.stringify(form) !== JSON.stringify(base);

  const changeTemplate = (template: string) => {
    if (template !== CUSTOM || form.url) return set({ template });
    // Start a custom query from the template that applies now, so the user edits a working example.
    const from = chosen ?? suggested;
    set({
      template,
      method: from?.request.method ?? GENERIC_REQUEST.method,
      url: from?.request.url ?? GENERIC_REQUEST.url,
      auth: from?.request.auth ?? GENERIC_REQUEST.auth,
      headers: pretty(from?.request.headers ?? GENERIC_REQUEST.headers),
      extract: pretty(from?.extract ?? GENERIC_EXTRACT),
    });
  };

  /** The body for save / test; null (after a toast) when the custom JSON does not parse. */
  const input = (): ProviderQuotaConfigInput | null => {
    const body: ProviderQuotaConfigInput = {
      enabled: form.enabled,
      template: form.template === AUTO ? null : form.template,
      intervalMinutes: form.intervalMinutes,
      timeoutSec: form.timeoutSec ?? 10,
      baseUrl: form.baseUrl.trim() || null,
      params: Object.fromEntries(Object.entries(form.params).filter(([, v]) => v.trim() !== '').map(([k, v]) => [k, v.trim()])),
      secrets: Object.fromEntries(Object.entries(form.secrets).filter(([, v]) => v.trim() !== '')),
    };
    if (isCustom) {
      const headers = parseObject(form.headers);
      const extract = parseObject(form.extract);
      if (!headers || !extract) {
        toast(t('providers.quota.invalidJson', { field: t(headers ? 'providers.quota.extract' : 'providers.quota.headers') }), 'error');
        return null;
      }
      body.request = { method: form.method, url: form.url.trim(), auth: form.auth, headers: headers as Record<string, string> };
      body.extract = extract;
    }
    return body;
  };

  const onSave = () => {
    const body = input();
    if (!body) return;
    const turnedOn = body.enabled && !config.enabled;
    save.mutate(body, {
      onSuccess: () => {
        toast(t('common.saved'), 'success');
        // Freshly enabled: show a balance right away instead of waiting for the background refresh.
        if (turnedOn) fetchNow.mutate(provider.id, { onError: (e) => toast(errorText(e), 'error') });
      },
      onError: (e) => toast(errorText(e), 'error'),
    });
  };

  const onTest = () => {
    const body = input();
    if (!body) return;
    test.mutate(body, { onSuccess: setResult, onError: (e) => toast(errorText(e), 'error') });
  };

  const intervalNow = config.effectiveIntervalMinutes > 0
    ? t('providers.quota.intervalEvery', { minutes: config.effectiveIntervalMinutes })
    : t('providers.quota.intervalOff');
  const templateOptions = [
    { value: AUTO, label: suggested ? t('providers.quota.templateAuto', { name: suggested.name }) : t('providers.quota.templateNone') },
    ...all.map((x) => ({ value: x.id, label: x.name, detail: t(`providers.quota.kind.${x.kind}` as MessageKey) })),
    { value: CUSTOM, label: t('providers.quota.custom') },
  ];

  return (
    <>
      <Group title={t('providers.quota.title')} footer={t('providers.quota.footer')}>
        <Row label={t('providers.quota.enable')} detail={t('providers.quota.enableHint')}>
          <Switch checked={form.enabled} label={t('providers.quota.enable')} onChange={(enabled) => set({ enabled })} />
        </Row>
        <Row label={t('providers.quota.template')}>
          <Select className="w-64" ariaLabel={t('providers.quota.template')} value={form.template} onChange={changeTemplate} options={templateOptions} />
        </Row>
        {params.map((p) => {
          const label = t(`providers.quota.param.${p.name}` as MessageKey);
          const stored = config.secrets.includes(p.name);
          return p.secret ? (
            <Row key={p.name} label={label} detail={stored ? t('providers.quota.secretStored') : undefined}>
              <Input
                type="password"
                autoComplete="off"
                className="w-64 font-mono"
                aria-label={label}
                placeholder={stored ? '••••••••' : ''}
                value={form.secrets[p.name] ?? ''}
                onChange={(e) => set({ secrets: { ...form.secrets, [p.name]: e.target.value } })}
              />
            </Row>
          ) : (
            <Row key={p.name} label={label}>
              <Input className="w-64 font-mono" aria-label={label} value={form.params[p.name] ?? ''} onChange={(e) => set({ params: { ...form.params, [p.name]: e.target.value } })} />
            </Row>
          );
        })}
        {params.length > 0 && <div className="px-4 pb-2 text-[11px] text-[var(--text-muted)]">{t('providers.quota.paramsHint')}</div>}
        {isCustom && (
          <div className="flex flex-col gap-3 px-4 py-3">
            <div className="flex flex-wrap items-center gap-2">
              <Select
                className="w-28"
                ariaLabel={t('providers.quota.method')}
                value={form.method}
                onChange={(method) => set({ method })}
                options={[{ value: 'GET', label: 'GET' }, { value: 'POST', label: 'POST' }]}
              />
              <Input className="min-w-60 flex-1 font-mono" aria-label={t('providers.quota.url')} value={form.url} onChange={(e) => set({ url: e.target.value })} />
              <Select
                className="w-52"
                ariaLabel={t('providers.quota.auth')}
                value={form.auth}
                onChange={(auth) => set({ auth })}
                options={[
                  { value: 'provider', label: t('providers.quota.auth.provider') },
                  { value: 'none', label: t('providers.quota.auth.none') },
                ]}
              />
            </div>
            <div className="text-[11px] text-[var(--text-muted)]">{t('providers.quota.urlHint')}</div>
            <TextArea label={t('providers.quota.headers')} mono rows={3} value={form.headers} onChange={(e) => set({ headers: e.target.value })} />
            <TextArea label={t('providers.quota.extract')} mono rows={8} value={form.extract} onChange={(e) => set({ extract: e.target.value })} />
            <div className="text-[11px] text-[var(--text-muted)]">{t('providers.quota.extractHint')}</div>
          </div>
        )}
        <Row label={t('providers.quota.interval')} detail={t('providers.quota.intervalHint', { value: intervalNow })}>
          <NumberInput className="w-28" label={t('providers.quota.interval')} min={0} step={1} value={form.intervalMinutes} onChange={(intervalMinutes) => set({ intervalMinutes })} />
        </Row>
        <Row label={t('providers.quota.timeout')}>
          <NumberInput className="w-28" label={t('providers.quota.timeout')} min={1} step={1} value={form.timeoutSec} onChange={(timeoutSec) => set({ timeoutSec })} />
        </Row>
        <Row label={t('providers.quota.baseUrl')} detail={t('providers.quota.baseUrlHint')}>
          <Input className="w-64 font-mono" aria-label={t('providers.quota.baseUrl')} placeholder={provider.endpoints[0]?.baseUrl} value={form.baseUrl} onChange={(e) => set({ baseUrl: e.target.value })} />
        </Row>
        <div className="flex flex-wrap items-center gap-2 px-4 py-3">
          <Button size="sm" variant="primary" disabled={!dirty} loading={save.isPending} onClick={onSave}>
            {t('common.save')}
          </Button>
          <Button size="sm" icon={<FlaskConical className="size-3.5" />} loading={test.isPending} onClick={onTest} title={t('providers.quota.testDesc')}>
            {t('providers.quota.test')}
          </Button>
          <Button
            size="sm"
            icon={<Gauge className="size-3.5" />}
            disabled={dirty}
            loading={fetchNow.isPending}
            onClick={() => fetchNow.mutate(provider.id, { onError: (e) => toast(errorText(e), 'error') })}
          >
            {t('providers.quota.fetch')}
          </Button>
        </div>
        <div className="px-4 pb-4">
          <QuotaSnapshotView snapshot={provider.quota} checkedAtUtc={provider.quotaCheckedAtUtc} />
        </div>
      </Group>

      <Sheet
        open={result !== null}
        onOpenChange={(open) => !open && setResult(null)}
        title={t('providers.quota.test')}
        description={t('providers.quota.testDesc')}
        width={560}
      >
        {result && (
          <div className="flex flex-col gap-3">
            <Alert tone={result.ok ? 'success' : 'danger'} title={result.ok ? t('providers.quota.testOk') : t('providers.quota.testFailed')}>
              {!result.ok && failureText({ code: result.errorCode ?? null, message: result.error ?? '' }, t)}
              {!result.ok && result.error && result.errorCode && <div className="mt-1 text-[11px] opacity-80">{result.error}</div>}
            </Alert>
            {result.url && <KV label={t('providers.quota.requestUrl')} mono>{result.url}</KV>}
            {result.httpStatus != null && <KV label={t('providers.quota.httpStatus')}>{result.httpStatus}</KV>}
            {result.snapshot && (
              <div>
                <div className="mb-1 text-[12px] text-[var(--text-secondary)]">{t('providers.quota.extracted')}</div>
                <QuotaSnapshotView snapshot={result.snapshot} />
              </div>
            )}
            {result.raw !== undefined && result.raw !== null && (
              <div>
                <div className="mb-1 text-[12px] text-[var(--text-secondary)]">{t('providers.quota.rawResponse')}</div>
                <CodeBlock>{pretty(result.raw)}</CodeBlock>
              </div>
            )}
          </div>
        )}
      </Sheet>
    </>
  );
}

/** Plans of a snapshot (one line each), its label, age, and a newer failure (shown above the last good result). */
export function QuotaSnapshotView({ snapshot, checkedAtUtc }: { snapshot?: ProviderQuotaSnapshot | null; checkedAtUtc?: string | null }) {
  const { t, locale } = useI18n();
  const failure = quotaFailure(snapshot);
  const plans = snapshot?.plans ?? [];
  const fetched = snapshot?.fetchedAtUtc ? new Date(snapshot.fetchedAtUtc).getTime() : 0;
  if (!snapshot && !checkedAtUtc) return <div className="text-[12px] text-[var(--text-muted)]">{t('providers.quota.never')}</div>;
  return (
    <div className="flex flex-col gap-2">
      {failure && (
        <Alert tone="warning" title={t('providers.quota.failed', { error: failureText(failure, t) })}>
          <span className="text-[11px]">{failure.message}</span>
          {plans.length > 0 && <div className="mt-1 text-[11px]">{t('providers.quota.lastGood')}</div>}
        </Alert>
      )}
      {snapshot?.isValid === false && <Alert tone="warning" title={t('providers.quota.invalid')}>{snapshot.invalidMessage}</Alert>}
      {plans.length > 0 && (
        <div className="flex flex-col gap-1">
          {plans.map((p, i) => (
            <div key={i} className="flex flex-wrap items-baseline gap-x-2 text-[13px]">
              {p.name && <Badge>{p.name}</Badge>}
              <span className="num">{planPhrase(p, t, locale) ?? '—'}</span>
              {planResets(p, t, locale) && <span className="text-[11px] text-[var(--text-muted)]">{planResets(p, t, locale)}</span>}
              {p.extra && <span className="text-[11px] text-[var(--text-muted)]">{p.extra}</span>}
            </div>
          ))}
        </div>
      )}
      <div className="flex flex-wrap items-center gap-2 text-[11px] text-[var(--text-muted)]">
        {snapshot?.planLabel && <Badge tone="accent">{snapshot.planLabel}</Badge>}
        {fetched > 0 && <span>{t('providers.quota.fetchedAt', { time: updatedAgo(fetched, locale, t('tray.justNow')) ?? '' })}</span>}
      </div>
    </div>
  );
}

/** The provider card's balance line (API-key providers with the usage query on) plus a refresh button. */
export function ProviderQuotaLine({ provider }: { provider: Provider }) {
  const { t, locale } = useI18n();
  const { toast } = useFeedback();
  const fetchNow = useFetchProviderQuota();
  if (!quotaEnabled(provider)) return null;
  const summary = quotaSummary(provider.quota, t, locale);
  const failure = quotaFailure(provider.quota);
  return (
    <div className="mt-3 flex min-w-0 items-center gap-2 text-[12px]">
      <div className="min-w-0 flex-1">
        {summary && <div className="num truncate text-[var(--text-secondary)]" title={summary}>{summary}</div>}
        {failure && (
          <div className="truncate text-[var(--warning)]" title={failure.message}>
            {t('providers.quota.failed', { error: failureText(failure, t) })}
          </div>
        )}
        {!summary && !failure && <div className="text-[var(--text-muted)]">{t('providers.quota.never')}</div>}
      </div>
      <Button
        size="sm"
        variant="plain"
        icon={<Gauge className="size-3.5" />}
        aria-label={t('providers.quota.refresh')}
        title={t('providers.quota.refresh')}
        loading={fetchNow.isPending}
        onClick={() => fetchNow.mutate(provider.id, { onError: (e) => toast(errorText(e), 'error') })}
      />
    </div>
  );
}
