import { useQueries } from '@tanstack/react-query';
import { BellRing, Ellipsis, FlaskConical, KeyRound, ListChecks, Pencil, Plus, Trash2, Webhook as WebhookIcon } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';

import { api, enc } from '../api/client';
import {
  keys,
  useAlertEvents,
  useAlertRules,
  useAlertStates,
  useCreateAlertRule,
  useCreateWebhook,
  useDeleteAlertRule,
  useDeleteWebhook,
  useProviders,
  useRedeliver,
  useResetWebhookSecret,
  useTestWebhook,
  useTokens,
  useUpdateAlertRule,
  useUpdateWebhook,
  useWebhookDeliveries,
  useWebhooks,
} from '../api/hooks';
import type { AlertEvent, AlertRule, AlertSubjectType, ProviderAccount, Webhook, WebhookFormat } from '../api/types';
import { ChipGroup } from './arc/chip-group/chip-group';
import { Badge, Button, CodeBlock, Field, Group, Input, NumberInput, Row, Segmented, Spinner, Switch } from './ui/controls';
import { errorText, Menu, Select, Sheet, useFeedback } from './ui/overlays';
import { useI18n, type MessageKey, type TFunction } from '../i18n';
import { formatDateTime } from '../lib/format';
import { systemActions } from '../shell/systemActions';

const FORMATS: WebhookFormat[] = ['generic', 'feishu', 'dingtalk', 'wecom', 'slack', 'discord'];

/** Where each platform's bot address comes from (placeholder only). */
const URL_EXAMPLES: Record<WebhookFormat, string> = {
  generic: 'https://',
  feishu: 'https://open.feishu.cn/open-apis/bot/v2/hook/…',
  dingtalk: 'https://oapi.dingtalk.com/robot/send?access_token=…',
  wecom: 'https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=…',
  slack: 'https://hooks.slack.com/services/…',
  discord: 'https://discord.com/api/webhooks/…',
};

/** Formats whose secret is the bot's own signing secret, entered by the user. */
const usesBotSecret = (f: WebhookFormat) => f === 'feishu' || f === 'dingtalk';

const eventName = (t: TFunction, id: string) => t(`alerts.event.${id}` as MessageKey);
const eventHint = (t: TFunction, id: string) => t(`alerts.event.${id}.hint` as MessageKey);

/** Settings → Notifications (plan: alerts-webhooks-token-limits §5): firing alerts, webhooks, alert rules. */
export function NotificationSettings() {
  return (
    <div className="flex flex-col">
      <FiringAlerts />
      <WebhooksGroup />
      <RulesGroup />
    </div>
  );
}

function FiringAlerts() {
  const { t, locale } = useI18n();
  const states = useAlertStates(true);
  return (
    <Group title={t('alerts.firing')}>
      {states.isLoading && <Spinner />}
      {states.data?.length === 0 && <Row label={<span className="text-[var(--text-muted)]">{t('alerts.firing.empty')}</span>} />}
      {states.data?.map((s) => (
        <Row
          key={`${s.ruleId}:${s.subjectId}`}
          icon={<BellRing className="size-4 text-[var(--danger)]" />}
          label={`${eventName(t, s.event)} · ${s.subjectName}`}
          detail={`${s.ruleName} · ${formatDateTime(s.changedAt, locale)}`}
        />
      ))}
    </Group>
  );
}

// ------------------------------------------------------------------ webhooks

function WebhooksGroup() {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const webhooks = useWebhooks();
  const remove = useDeleteWebhook();
  const test = useTestWebhook();
  const resetSecret = useResetWebhookSecret();
  const [editing, setEditing] = useState<Webhook | 'new' | null>(null);
  const [deliveriesOf, setDeliveriesOf] = useState<Webhook | null>(null);
  const [secret, setSecret] = useState<string | null>(null);

  const runTest = (hook: Webhook) =>
    test.mutate(hook.id, {
      onSuccess: (d) =>
        d.status === 'success'
          ? toast(t('alerts.webhooks.testOk', { status: d.httpStatus ?? '' }), 'success')
          : toast(t('alerts.webhooks.testFailed', { error: d.error ?? d.status }), 'error'),
      onError: (e) => toast(errorText(e), 'error'),
    });

  const doReset = async (hook: Webhook) => {
    if (!(await confirm({ title: t('alerts.webhooks.resetSecretConfirm', { name: hook.name }), detail: t('alerts.webhooks.resetSecretDetail'), confirmLabel: t('alerts.webhooks.resetSecret'), destructive: true })))
      return;
    resetSecret.mutate(hook.id, { onSuccess: (r) => setSecret(r.secret ?? null), onError: (e) => toast(errorText(e), 'error') });
  };

  const doDelete = async (hook: Webhook) => {
    if (!(await confirm({ title: t('alerts.webhooks.deleteConfirm', { name: hook.name }), detail: t('alerts.webhooks.deleteDetail'), confirmLabel: t('common.delete'), destructive: true })))
      return;
    remove.mutate(hook.id, { onSuccess: () => toast(t('alerts.webhooks.deleted', { name: hook.name })), onError: (e) => toast(errorText(e), 'error') });
  };

  return (
    <Group title={t('alerts.webhooks')} footer={t('alerts.webhooks.hint')}>
      {webhooks.isLoading && <Spinner />}
      {webhooks.data?.length === 0 && <Row label={<span className="text-[var(--text-muted)]">{t('alerts.webhooks.empty')}</span>} />}
      {webhooks.data?.map((hook) => (
        <Row
          key={hook.id}
          icon={<WebhookIcon className="size-4 text-[var(--text-muted)]" />}
          label={
            <span className="inline-flex items-center gap-2">
              {hook.name}
              {!hook.enabled && <Badge tone="orange">{t('alerts.webhooks.disabledBadge')}</Badge>}
              {hook.format !== 'generic' && <Badge tone="accent">{t(`alerts.webhooks.format.${hook.format}`)}</Badge>}
              {!hook.hasSecret && (hook.format === 'generic' || usesBotSecret(hook.format)) && <Badge>{t('alerts.webhooks.unsigned')}</Badge>}
            </span>
          }
          detail={<span className="font-mono selectable">{hook.url}</span>}
        >
          <Button size="sm" icon={<FlaskConical className="size-3.5" />} loading={test.isPending && test.variables === hook.id} onClick={() => runTest(hook)}>
            {t('alerts.webhooks.test')}
          </Button>
          <Menu
            label={t('common.more')}
            icon={<Ellipsis className="size-4" />}
            items={[
              { id: 'edit', label: t('alerts.webhooks.edit'), icon: <Pencil className="size-3.5" />, onSelect: () => setEditing(hook) },
              { id: 'deliveries', label: t('alerts.webhooks.deliveries'), icon: <ListChecks className="size-3.5" />, onSelect: () => setDeliveriesOf(hook) },
              { id: 'secret', label: t('alerts.webhooks.resetSecret'), icon: <KeyRound className="size-3.5" />, onSelect: () => void doReset(hook) },
              { id: 'delete', label: t('common.delete'), icon: <Trash2 className="size-3.5" />, destructive: true, separatorBefore: true, onSelect: () => void doDelete(hook) },
            ]}
          />
        </Row>
      ))}
      <div className="px-4 py-2">
        <Button size="sm" icon={<Plus className="size-3.5" />} onClick={() => setEditing('new')}>
          {t('alerts.webhooks.add')}
        </Button>
      </div>
      <WebhookSheet hook={editing} onClose={() => setEditing(null)} onSecret={setSecret} />
      <DeliveriesSheet hook={deliveriesOf} onClose={() => setDeliveriesOf(null)} />
      <SecretSheet secret={secret} onClose={() => setSecret(null)} />
    </Group>
  );
}

type HeaderDraft = { name: string; value: string; stored: boolean };

function WebhookSheet({ hook, onClose, onSecret }: { hook: Webhook | 'new' | null; onClose: () => void; onSecret: (s: string) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const create = useCreateWebhook();
  const update = useUpdateWebhook();
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [timeoutSec, setTimeoutSec] = useState<number | null>(10);
  const [useProxy, setUseProxy] = useState(true);
  const [enabled, setEnabled] = useState(true);
  const [signed, setSigned] = useState(true);
  const [format, setFormat] = useState<WebhookFormat>('generic');
  const [botSecret, setBotSecret] = useState('');
  const [headers, setHeaders] = useState<HeaderDraft[]>([]);
  const isNew = hook === 'new';

  useEffect(() => {
    if (hook === null) return;
    const h = hook === 'new' ? null : hook;
    setName(h?.name ?? '');
    setUrl(h?.url ?? '');
    setTimeoutSec(h?.timeoutSec ?? 10);
    setUseProxy(h?.useProxy ?? true);
    setEnabled(h?.enabled ?? true);
    setSigned(h ? h.hasSecret : true);
    setFormat(h?.format ?? 'generic');
    setBotSecret('');
    setHeaders((h?.headerNames ?? []).map((n) => ({ name: n, value: '', stored: true })));
  }, [hook]);

  const save = () => {
    const headerSet = Object.fromEntries(headers.filter((h) => h.name.trim()).map((h) => [h.name.trim(), h.value]));
    const body = { name: name.trim(), url: url.trim(), timeoutSec: timeoutSec ?? 10, useProxy, enabled, headers: Object.keys(headerSet).length > 0 ? headerSet : null };
    const done = { onError: (e: unknown) => toast(errorText(e), 'error') };
    const formatChanged = !isNew && hook !== null && hook.format !== format;
    // Bot formats: a typed secret replaces the stored one; signing off clears it; empty keeps it (same format only).
    const secret = usesBotSecret(format) ? (!signed ? '' : botSecret.trim() || undefined) : undefined;
    if (isNew)
      create.mutate({ ...body, format, ...(format === 'generic' ? { signed } : secret ? { secret } : {}) }, {
        ...done,
        onSuccess: (r) => {
          onClose();
          if (r.secret) onSecret(r.secret);
        },
      });
    else if (hook)
      update.mutate({
        id: hook.id, ...body,
        ...(formatChanged ? { format } : {}),
        ...(format === 'generic' ? (signed || !hook.hasSecret || formatChanged ? {} : { signed: false }) : secret !== undefined ? { secret } : {}),
      }, {
        ...done,
        onSuccess: () => {
          onClose();
          toast(t('common.saved'), 'success');
        },
      });
  };

  return (
    <Sheet
      open={hook !== null}
      onOpenChange={(o) => !o && onClose()}
      title={isNew ? t('alerts.webhooks.add') : t('alerts.webhooks.edit')}
      width={540}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!name.trim() || !url.trim()} loading={create.isPending || update.isPending} onClick={save}>
            {t('common.save')}
          </Button>
        </>
      }
    >
      <div className="grid gap-4">
        <Field label={t('alerts.webhooks.name')}>
          <Input value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('alerts.webhooks.format')} hint={t(`alerts.webhooks.format.${format}.hint`)}>
          <Select
            ariaLabel={t('alerts.webhooks.format')}
            value={format}
            onChange={(f: WebhookFormat) => {
              setFormat(f);
              setSigned(f === 'generic' || (hook !== 'new' && hook !== null && hook.format === f && hook.hasSecret) || usesBotSecret(f));
            }}
            options={FORMATS.map((f) => ({ value: f, label: t(`alerts.webhooks.format.${f}`) }))}
          />
        </Field>
        <Field label={t('alerts.webhooks.url')}>
          <Input mono value={url} placeholder={URL_EXAMPLES[format]} onChange={(e) => setUrl(e.target.value)} />
        </Field>
        {usesBotSecret(format) && signed && (
          <Field label={t('alerts.webhooks.botSecret')} hint={t('alerts.webhooks.botSecretHint')}>
            <Input
              mono
              type="password"
              autoComplete="off"
              value={botSecret}
              placeholder={hook !== 'new' && hook !== null && hook.format === format && hook.hasSecret ? t('alerts.webhooks.headerKept') : 'SEC…'}
              onChange={(e) => setBotSecret(e.target.value)}
            />
          </Field>
        )}
        <Field label={t('alerts.webhooks.timeout')}>
          <NumberInput value={timeoutSec} min={1} step={1} label={t('alerts.webhooks.timeout')} onChange={setTimeoutSec} />
        </Field>
        <Field label={t('alerts.webhooks.headers')} hint={t('alerts.webhooks.headersHint')}>
          <div className="grid gap-2">
            {headers.map((h, i) => (
              <div key={i} className="grid grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)_auto] items-center gap-2">
                <Input mono aria-label={t('alerts.webhooks.headerName')} placeholder={t('alerts.webhooks.headerName')} value={h.name}
                  onChange={(e) => setHeaders(headers.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
                <Input mono aria-label={t('alerts.webhooks.headerValue')} placeholder={h.stored ? t('alerts.webhooks.headerKept') : t('alerts.webhooks.headerValue')} value={h.value}
                  onChange={(e) => setHeaders(headers.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)))} />
                <Button size="sm" variant="plain" icon={<Trash2 className="size-3.5" />} aria-label={t('common.delete')} onClick={() => setHeaders(headers.filter((_, j) => j !== i))} />
              </div>
            ))}
            <div>
              <Button size="sm" icon={<Plus className="size-3.5" />} onClick={() => setHeaders([...headers, { name: '', value: '', stored: false }])}>
                {t('alerts.webhooks.addHeader')}
              </Button>
            </div>
          </div>
        </Field>
        <div className="card divide-y divide-[var(--border)]">
          <Row label={t('alerts.webhooks.enabled')}>
            <Switch checked={enabled} label={t('alerts.webhooks.enabled')} onChange={setEnabled} />
          </Row>
          <Row label={t('alerts.webhooks.useProxy')} detail={t('alerts.webhooks.useProxyHint')}>
            <Switch checked={useProxy} label={t('alerts.webhooks.useProxy')} onChange={setUseProxy} />
          </Row>
          {(format === 'generic' ? isNew || (hook !== null && hook.format === 'generic' && hook.hasSecret) : usesBotSecret(format)) && (
            <Row label={t('alerts.webhooks.signed')} detail={format === 'generic' ? t('alerts.webhooks.signedHint') : t('alerts.webhooks.botSignedHint')}>
              <Switch checked={signed} label={t('alerts.webhooks.signed')} onChange={setSigned} />
            </Row>
          )}
        </div>
      </div>
    </Sheet>
  );
}

function SecretSheet({ secret, onClose }: { secret: string | null; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  return (
    <Sheet
      open={secret !== null}
      onOpenChange={(o) => !o && onClose()}
      title={t('alerts.webhooks.secretTitle')}
      description={t('alerts.webhooks.secretShown')}
      footer={
        <>
          <Button onClick={async () => secret && (await systemActions.copy(secret)) && toast(t('common.copied'))}>{t('common.copy')}</Button>
          <Button variant="primary" onClick={onClose}>{t('common.done')}</Button>
        </>
      }
    >
      <code className="block rounded-[var(--radius-control)] bg-[var(--surface-muted)] p-3 font-mono text-[12px] break-all selectable">{secret}</code>
    </Sheet>
  );
}

function DeliveriesSheet({ hook, onClose }: { hook: Webhook | null; onClose: () => void }) {
  const { t, locale } = useI18n();
  const { toast } = useFeedback();
  const deliveries = useWebhookDeliveries(hook?.id);
  const redeliver = useRedeliver();
  const tone = { success: 'green', pending: 'orange', failed: 'red' } as const;
  return (
    <Sheet open={hook !== null} onOpenChange={(o) => !o && onClose()} title={`${t('alerts.webhooks.deliveries')} · ${hook?.name ?? ''}`} width={640}>
      {deliveries.isLoading && <Spinner lines={4} />}
      {deliveries.data?.length === 0 && <p className="text-[12px] text-[var(--text-muted)]">{t('alerts.delivery.empty')}</p>}
      <div className="grid max-h-[60vh] gap-2 overflow-y-auto">
        {deliveries.data?.map((d) => (
          <details key={d.id} className="rounded-[var(--radius-control)] bg-[var(--surface-muted)] px-3 py-2 text-[12px]">
            <summary className="flex cursor-default flex-wrap items-center gap-2">
              <Badge tone={tone[d.status]}>{t(`alerts.delivery.status.${d.status}`)}</Badge>
              <span>{d.event === 'test' ? t('alerts.webhooks.test') : eventName(t, d.event)}</span>
              <span className="text-[var(--text-muted)]">{formatDateTime(d.createdAt, locale)}</span>
              <span className="text-[var(--text-muted)]">
                {d.httpStatus ? `HTTP ${d.httpStatus}` : ''} {d.error && d.status !== 'success' ? d.error : ''}
              </span>
              <span className="ml-auto text-[var(--text-muted)]">
                {d.status === 'pending' && d.nextAttemptAt ? t('alerts.delivery.next', { time: formatDateTime(d.nextAttemptAt, locale) }) : t('alerts.delivery.attempts', { count: d.attempts })}
              </span>
            </summary>
            <div className="mt-2 grid gap-2">
              <div className="text-[var(--text-muted)]">{t('alerts.delivery.payload')}</div>
              <CodeBlock>{JSON.stringify(d.payload, null, 2)}</CodeBlock>
              {d.responseSnippet && (
                <>
                  <div className="text-[var(--text-muted)]">{t('alerts.delivery.response')}</div>
                  <CodeBlock language="text">{d.responseSnippet}</CodeBlock>
                </>
              )}
              {d.status !== 'pending' && (
                <div>
                  <Button size="sm" loading={redeliver.isPending && redeliver.variables === d.id}
                    onClick={() => redeliver.mutate(d.id, { onSuccess: () => toast(t('alerts.delivery.redelivered')), onError: (e) => toast(errorText(e), 'error') })}>
                    {t('alerts.delivery.redeliver')}
                  </Button>
                </div>
              )}
            </div>
          </details>
        ))}
      </div>
    </Sheet>
  );
}

// ------------------------------------------------------------------ rules

/** The subjects an event's rules can be scoped to: providers with a balance query, subscription accounts, tokens. */
function useSubjects(type: AlertSubjectType | undefined) {
  const providers = useProviders();
  const tokens = useTokens();
  const subscriptionProviders = useMemo(
    () => (providers.data ?? []).filter((p) => p.authScheme === 'oauth-subscription'),
    [providers.data],
  );
  const accounts = useQueries({
    queries: subscriptionProviders.map((p) => ({
      queryKey: keys.providerAccounts(p.id),
      queryFn: () => api<ProviderAccount[]>('GET', `/api/providers/${enc(p.id)}/accounts`),
      enabled: type === 'account',
    })),
  });
  if (type === 'provider')
    return (providers.data ?? [])
      .filter((p) => p.authScheme !== 'oauth-subscription' && p.authScheme !== 'plugin')
      .map((p) => ({ value: p.id, label: p.name }));
  if (type === 'token') return (tokens.data ?? []).map((tk) => ({ value: tk.id, label: tk.name }));
  if (type === 'account')
    return subscriptionProviders.flatMap((p, i) =>
      (accounts[i]?.data ?? []).map((a) => ({ value: a.id, label: `${p.name} / ${a.displayName || a.id}` })),
    );
  return [];
}

function RulesGroup() {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const rules = useAlertRules();
  const webhooks = useWebhooks();
  const update = useUpdateAlertRule();
  const remove = useDeleteAlertRule();
  const [editing, setEditing] = useState<AlertRule | 'new' | null>(null);
  const hookName = (id: string) => webhooks.data?.find((w) => w.id === id)?.name ?? id;

  const doDelete = async (rule: AlertRule) => {
    if (!(await confirm({ title: t('alerts.rules.deleteConfirm', { name: rule.name }), confirmLabel: t('common.delete'), destructive: true }))) return;
    remove.mutate(rule.id, { onSuccess: () => toast(t('alerts.rules.deleted', { name: rule.name })), onError: (e) => toast(errorText(e), 'error') });
  };

  return (
    <Group title={t('alerts.rules')} footer={t('alerts.rules.hint')}>
      {rules.isLoading && <Spinner />}
      {rules.data?.length === 0 && <Row label={<span className="text-[var(--text-muted)]">{t('alerts.rules.empty')}</span>} />}
      {rules.data?.map((rule) => (
        <Row
          key={rule.id}
          icon={<BellRing className="size-4 text-[var(--text-muted)]" />}
          label={
            <span className="inline-flex items-center gap-2">
              {rule.name}
              {rule.firing > 0 && <Badge tone="red">{t('alerts.rules.firing', { count: rule.firing })}</Badge>}
              {!rule.enabled && <Badge tone="orange">{t('alerts.rules.disabledBadge')}</Badge>}
            </span>
          }
          detail={`${eventName(t, rule.event)} → ${rule.webhookIds.map(hookName).join(', ') || '—'}`}
        >
          <Switch checked={rule.enabled} label={t('alerts.rules.enabled')}
            onChange={(enabled) => update.mutate({ id: rule.id, enabled }, { onError: (e) => toast(errorText(e), 'error') })} />
          <Menu
            label={t('common.more')}
            icon={<Ellipsis className="size-4" />}
            items={[
              { id: 'edit', label: t('alerts.rules.edit'), icon: <Pencil className="size-3.5" />, onSelect: () => setEditing(rule) },
              { id: 'delete', label: t('common.delete'), icon: <Trash2 className="size-3.5" />, destructive: true, separatorBefore: true, onSelect: () => void doDelete(rule) },
            ]}
          />
        </Row>
      ))}
      <div className="px-4 py-2">
        <Button size="sm" icon={<Plus className="size-3.5" />} onClick={() => setEditing('new')}>
          {t('alerts.rules.add')}
        </Button>
      </div>
      <RuleSheet rule={editing} onClose={() => setEditing(null)} />
    </Group>
  );
}

type Condition = Record<string, string | number | null>;

function defaults(event: AlertEvent | undefined): Condition {
  const c: Condition = {};
  for (const p of event?.params ?? []) c[p.name] = p.kind === 'choice' ? (p.choices?.[0] ?? '') : p.kind === 'number' ? (p.default ?? null) : '';
  return c;
}

function RuleSheet({ rule, onClose }: { rule: AlertRule | 'new' | null; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const events = useAlertEvents();
  const webhooks = useWebhooks();
  const create = useCreateAlertRule();
  const update = useUpdateAlertRule();
  const [name, setName] = useState('');
  const [eventId, setEventId] = useState('');
  const [condition, setCondition] = useState<Condition>({});
  const [scope, setScope] = useState<'all' | 'only'>('all');
  const [subjects, setSubjects] = useState<string[]>([]);
  const [hookIds, setHookIds] = useState<string[]>([]);
  const [notifyResolved, setNotifyResolved] = useState(false);
  const [repeat, setRepeat] = useState<number | null>(null);
  const [cooldown, setCooldown] = useState<number | null>(60);
  const event = events.data?.find((e) => e.id === eventId);
  const subjectOptions = useSubjects(event?.subjectType);
  const isNew = rule === 'new';

  useEffect(() => {
    if (rule === null) return;
    const r = rule === 'new' ? null : rule;
    const first = events.data?.[0];
    setName(r?.name ?? '');
    setEventId(r?.event ?? first?.id ?? '');
    setCondition(r ? { ...defaults(events.data?.find((e) => e.id === r.event)), ...r.condition } : defaults(first));
    setScope(r?.subjects ? 'only' : 'all');
    setSubjects(r?.subjects ?? []);
    setHookIds(r?.webhookIds ?? (webhooks.data ?? []).map((w) => w.id));
    setNotifyResolved(r?.notifyResolved ?? false);
    setRepeat(r?.repeatMinutes ?? null);
    setCooldown(r?.cooldownMinutes ?? 60);
    // Only when the sheet opens (events / webhooks resolve before it can be used).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rule]);

  const save = () => {
    const cleaned = Object.fromEntries(Object.entries(condition).filter(([, v]) => v !== null && v !== '')) as Record<string, string | number>;
    const body = {
      name: name.trim(), event: eventId, condition: cleaned, subjects: scope === 'all' ? null : subjects, webhookIds: hookIds,
      notifyResolved, repeatMinutes: repeat && repeat > 0 ? Math.floor(repeat) : null, cooldownMinutes: Math.max(0, Math.floor(cooldown ?? 0)),
    };
    const done = { onSuccess: () => { onClose(); toast(t('common.saved'), 'success'); }, onError: (e: unknown) => toast(errorText(e), 'error') };
    if (isNew) create.mutate(body, done);
    else if (rule) update.mutate({ id: rule.id, ...body }, done);
  };

  return (
    <Sheet
      open={rule !== null}
      onOpenChange={(o) => !o && onClose()}
      title={isNew ? t('alerts.rules.add') : t('alerts.rules.edit')}
      width={560}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!name.trim() || !eventId} loading={create.isPending || update.isPending} onClick={save}>
            {t('common.save')}
          </Button>
        </>
      }
    >
      <div className="grid gap-4">
        <Field label={t('alerts.rules.name')}>
          <Input value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('alerts.rules.event')} hint={eventId ? eventHint(t, eventId) : undefined}>
          <Select
            ariaLabel={t('alerts.rules.event')}
            value={eventId}
            onChange={(id) => {
              setEventId(id);
              setCondition(defaults(events.data?.find((e) => e.id === id)));
              setSubjects([]);
            }}
            options={(events.data ?? []).map((e) => ({ value: e.id, label: eventName(t, e.id) }))}
          />
        </Field>
        {event?.params.map((p) => (
          <Field key={p.name} label={t(`alerts.param.${p.name}` as MessageKey)}>
            {p.kind === 'number' ? (
              <NumberInput value={typeof condition[p.name] === 'number' ? (condition[p.name] as number) : null} min={0}
                label={t(`alerts.param.${p.name}` as MessageKey)} onChange={(v) => setCondition({ ...condition, [p.name]: v })} />
            ) : p.kind === 'choice' ? (
              <Select ariaLabel={t(`alerts.param.${p.name}` as MessageKey)} value={String(condition[p.name] ?? '')}
                onChange={(v) => setCondition({ ...condition, [p.name]: v })}
                options={(p.choices ?? []).map((c) => ({ value: c, label: t(`alerts.choice.${p.name}.${c}` as MessageKey) }))} />
            ) : (
              <Input mono value={String(condition[p.name] ?? '')} aria-label={t(`alerts.param.${p.name}` as MessageKey)}
                placeholder={t(`alerts.param.${p.name}.placeholder` as MessageKey)} onChange={(e) => setCondition({ ...condition, [p.name]: e.target.value })} />
            )}
          </Field>
        ))}
        {event && (
          <Field label={`${t('alerts.rules.subjects')} · ${t(`alerts.subject.${event.subjectType}`)}`}>
            <div className="grid gap-2">
              <Segmented ariaLabel={t('alerts.rules.subjects')} value={scope} onChange={setScope}
                items={[{ value: 'all', label: t('alerts.rules.subjectsAll') }, { value: 'only', label: t('alerts.rules.subjectsOnly') }]} />
              {scope === 'only' && (
                <ChipGroup label={t('alerts.rules.subjects')} value={subjects} onValueChange={setSubjects}
                  options={[...subjectOptions, ...subjects.filter((s) => !subjectOptions.some((o) => o.value === s)).map((s) => ({ value: s, label: s }))]} />
              )}
            </div>
          </Field>
        )}
        <Field label={t('alerts.rules.webhooks')}>
          {(webhooks.data ?? []).length === 0 ? (
            <p className="text-[12px] text-[var(--text-muted)]">{t('alerts.rules.noWebhooks')}</p>
          ) : (
            <ChipGroup label={t('alerts.rules.webhooks')} value={hookIds} onValueChange={setHookIds}
              options={(webhooks.data ?? []).map((w) => ({ value: w.id, label: w.name }))} />
          )}
        </Field>
        <div className="grid grid-cols-2 gap-4">
          <Field label={t('alerts.rules.repeat')} hint={t('alerts.rules.repeatHint')}>
            <NumberInput value={repeat} min={1} step={1} label={t('alerts.rules.repeat')} onChange={setRepeat} />
          </Field>
          <Field label={t('alerts.rules.cooldown')} hint={t('alerts.rules.cooldownHint')}>
            <NumberInput value={cooldown} min={0} step={1} label={t('alerts.rules.cooldown')} onChange={setCooldown} />
          </Field>
        </div>
        <div className="card">
          <Row label={t('alerts.rules.notifyResolved')}>
            <Switch checked={notifyResolved} label={t('alerts.rules.notifyResolved')} onChange={setNotifyResolved} />
          </Row>
        </div>
      </div>
    </Sheet>
  );
}
