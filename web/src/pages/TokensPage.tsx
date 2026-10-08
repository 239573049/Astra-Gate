import { Copy, Ellipsis, KeyRound, Pencil, Plus, Power, RotateCcw, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';

import {
  useCreateToken,
  useDeleteToken,
  useProviderAccounts,
  useProviders,
  useResetToken,
  useRevealToken,
  useSettings,
  useTokens,
  useUpdateToken,
} from '../api/hooks';
import type { Token, TokenMutationResult, TokenStats } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { CLIENT_META, ClientGlyph } from '../components/icons';
import { Page } from '../components/layout/Page';
import { Badge, Button, EmptyState, Field, Input, Spinner } from '../components/ui/controls';
import { errorText, Menu, Select, Sheet, useFeedback } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { formatDateTime, formatPercent, formatTokens, formatTps, formatUsd } from '../lib/format';
import { systemActions } from '../shell/systemActions';

/** Tokens (plan: tokens): shared gateway credentials with today's and lifetime usage. */
export function TokensPage() {
  const { t } = useI18n();
  const tokens = useTokens();
  const settings = useSettings();
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<Token | null>(null);

  return (
    <Page
      title={t('nav.tokens')}
      subtitle={t('tokens.subtitle')}
      actions={
        <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setCreating(true)}>
          {t('tokens.add')}
        </Button>
      }
    >
      <div className="mx-auto flex max-w-[1100px] flex-col gap-4">
        {settings.data && (
          <p className="px-1 text-[12px] text-[var(--text-secondary)]">
            {t('tokens.directHint', { url: `${settings.data.gatewayBaseUrl}/v1` })}
          </p>
        )}
        {tokens.isLoading && <Spinner lines={4} />}
        {tokens.isError && <Alert tone="danger" title={t('common.error')}>{errorText(tokens.error)}</Alert>}
        {tokens.data?.length === 0 && (
          <div className="card">
            <EmptyState icon={<KeyRound className="size-6" />} title={t('tokens.empty')} />
          </div>
        )}
        {tokens.data?.map((token) => <TokenCard key={token.id} token={token} onEdit={() => setEditing(token)} />)}
      </div>
      <CreateTokenSheet open={creating} onOpenChange={setCreating} />
      <TokenSettingsSheet token={editing} onClose={() => setEditing(null)} />
    </Page>
  );
}

function TokenCard({ token, onEdit }: { token: Token; onEdit: () => void }) {
  const { t, locale } = useI18n();
  const { toast, confirm } = useFeedback();
  const providers = useProviders();
  const reveal = useRevealToken();
  const update = useUpdateToken();
  const reset = useResetToken();
  const remove = useDeleteToken();
  const direct = (providers.data ?? []).find((p) => p.id === token.providerId);

  const reportRewrite = (r: TokenMutationResult, done: string) => {
    toast(done, 'success');
    if (r.skipped.length > 0)
      toast(t('tokens.skipped', { clients: r.skipped.map((k) => CLIENT_META[k]?.name ?? k).join(', ') }), 'error');
  };

  const copy = () =>
    reveal.mutate(token.id, {
      onSuccess: async ({ token: plain }) => {
        if (await systemActions.copy(plain)) toast(t('tokens.copied'));
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  const toggle = async () => {
    if (token.enabled && !(await confirm({ title: t('tokens.disableConfirm', { name: token.name }), detail: t('tokens.disableDetail'), confirmLabel: t('tokens.disable'), destructive: true })))
      return;
    update.mutate({ id: token.id, enabled: !token.enabled }, { onError: (e) => toast(errorText(e), 'error') });
  };

  const doReset = async () => {
    if (!(await confirm({ title: t('tokens.resetConfirm', { name: token.name }), detail: t('tokens.resetDetail'), confirmLabel: t('tokens.reset'), destructive: true })))
      return;
    reset.mutate(token.id, {
      onSuccess: (r) => reportRewrite(r, t('tokens.resetDone', { count: r.rewritten.length })),
      onError: (e) => toast(errorText(e), 'error'),
    });
  };

  const doDelete = async () => {
    if (!(await confirm({ title: t('tokens.deleteConfirm', { name: token.name }), detail: t('tokens.deleteDetail'), confirmLabel: t('common.delete'), destructive: true })))
      return;
    remove.mutate(token.id, {
      onSuccess: (r) => reportRewrite(r, t('tokens.deleted', { name: token.name })),
      onError: (e) => toast(errorText(e), 'error'),
    });
  };

  return (
    <article className="card p-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="truncate text-[15px] font-medium">{token.name}</h2>
            {token.isDefault && <Badge tone="accent">{t('tokens.default')}</Badge>}
            {!token.enabled && <Badge tone="orange">{t('tokens.disabledBadge')}</Badge>}
          </div>
          <div className="mt-1 flex items-center gap-1.5">
            <code className="font-mono text-[12px] text-[var(--text-secondary)] selectable">{token.keyMasked}</code>
            <Button size="sm" variant="plain" icon={<Copy className="size-3.5" />} aria-label={t('tokens.copy')} title={t('tokens.copy')} loading={reveal.isPending} onClick={copy} />
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Button size="sm" icon={<Pencil className="size-3" />} onClick={onEdit}>
            {t('tokens.settings')}
          </Button>
          <Menu
            label={t('common.more')}
            icon={<Ellipsis className="size-4" />}
            items={[
              { id: 'toggle', label: token.enabled ? t('tokens.disable') : t('tokens.enable'), icon: <Power className="size-3.5" />, onSelect: () => void toggle() },
              { id: 'reset', label: t('tokens.reset'), icon: <RotateCcw className="size-3.5" />, onSelect: () => void doReset() },
              ...(token.isDefault
                ? []
                : [{ id: 'delete', label: t('tokens.delete'), icon: <Trash2 className="size-3.5" />, destructive: true, separatorBefore: true, onSelect: () => void doDelete() }]),
            ]}
          />
        </div>
      </div>

      <dl className="mt-3 grid gap-x-6 gap-y-1 text-[12px] sm:grid-cols-3">
        <div className="min-w-0">
          <dt className="text-[var(--text-muted)]">{t('tokens.clients')}</dt>
          <dd className="mt-0.5 flex min-h-5 flex-wrap items-center gap-1.5">
            {token.clients.length === 0
              ? <span className="text-[var(--text-secondary)]">{t('tokens.noClients')}</span>
              : token.clients.map((k) => (
                  <span key={k} className="inline-flex items-center gap-1" title={CLIENT_META[k]?.name ?? k}>
                    <ClientGlyph kind={k} size={16} />
                    <span className="sr-only">{CLIENT_META[k]?.name ?? k}</span>
                  </span>
                ))}
          </dd>
        </div>
        <div className="min-w-0">
          <dt className="text-[var(--text-muted)]">{t('tokens.directProvider')}</dt>
          <dd className="mt-0.5 truncate text-[var(--text-secondary)]">{direct?.name ?? (token.providerId ? token.providerId : t('tokens.directNone'))}</dd>
        </div>
        <div className="min-w-0">
          <dt className="text-[var(--text-muted)]">{t('tokens.lastUsedTitle')}</dt>
          <dd className="mt-0.5 truncate text-[var(--text-secondary)]">
            {token.lastUsedAt ? formatDateTime(token.lastUsedAt, locale) : t('tokens.neverUsed')}
          </dd>
        </div>
      </dl>

      <StatsRow title={t('tokens.today')} stats={token.today} labels={{ cost: t('tokens.todayCost'), tokens: t('tokens.todayTokens') }} />
      <StatsRow title={t('tokens.total')} stats={token.total} labels={{ cost: t('tokens.totalCost'), tokens: t('tokens.totalTokens'), requests: t('tokens.totalRequests') }} />
    </article>
  );
}

/** Cost, tokens, cache hit rate and TPS (plus request count for lifetime numbers). */
function StatsRow({ title, stats, labels }: { title: string; stats: TokenStats; labels: { cost: string; tokens: string; requests?: string } }) {
  const { t } = useI18n();
  return (
    <section className="mt-4">
      <h3 className="mb-2 text-[12px] font-medium text-[var(--text-secondary)]">{title}</h3>
      <div className={labels.requests ? 'grid grid-cols-2 gap-3 md:grid-cols-5' : 'grid grid-cols-2 gap-3 md:grid-cols-4'}>
        <Stat label={labels.cost} value={formatUsd(stats.costUsd)} context={t('tokens.requestsContext', { count: formatTokens(stats.requests) })} />
        <Stat
          label={labels.tokens}
          value={formatTokens(stats.totalTokens, true)}
          context={t('overview.tokensContext', { input: formatTokens(stats.inputTokens, true), output: formatTokens(stats.outputTokens, true) })}
        />
        {labels.requests && <Stat label={labels.requests} value={formatTokens(stats.requests)} context={title} />}
        <Stat
          label={t('tokens.cacheHit')}
          value={stats.cacheHitRate == null ? '—' : formatPercent(stats.cacheHitRate)}
          context={t('usage.cacheAmounts', { read: formatTokens(stats.cacheReadTokens, true), write: formatTokens(stats.cacheWriteTokens, true) })}
        />
        <Stat label={t('tokens.tps')} value={stats.tps == null ? '—' : formatTps(stats.tps)} context={t('tokens.tpsContext')} />
      </div>
    </section>
  );
}

function Stat({ label, value, context }: { label: string; value: string; context: string }) {
  return (
    <div className="min-w-0 rounded-[var(--radius-control)] bg-[var(--surface-muted)] px-3 py-2.5">
      <div className="truncate text-[11px] text-[var(--text-secondary)]">{label}</div>
      <div className="num mt-0.5 truncate text-[18px] leading-tight font-semibold selectable" title={value}>
        {value}
      </div>
      <div className="mt-0.5 truncate text-[11px] text-[var(--text-muted)]" title={context}>
        {context}
      </div>
    </div>
  );
}

/** Direct-call provider picker: "" = no direct calls; only enabled providers are offered. */
function useProviderOptions() {
  const { t } = useI18n();
  const providers = useProviders();
  const enabled = (providers.data ?? []).filter((p) => p.enabled);
  return {
    providers: enabled,
    options: [{ value: '', label: t('tokens.directNone') }, ...enabled.map((p) => ({ value: p.id, label: p.name }))],
  };
}

function CreateTokenSheet({ open, onOpenChange }: { open: boolean; onOpenChange: (o: boolean) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const create = useCreateToken();
  const { options } = useProviderOptions();
  const [name, setName] = useState('');
  const [providerId, setProviderId] = useState('');

  useEffect(() => {
    if (!open) return;
    setName('');
    setProviderId('');
  }, [open]);

  const submit = () =>
    create.mutate(
      { name: name.trim(), providerId: providerId || null },
      {
        onSuccess: (tk) => {
          onOpenChange(false);
          toast(t('tokens.created', { name: tk.name }), 'success');
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('tokens.createTitle')}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!name.trim()} loading={create.isPending} onClick={submit}>
            {t('common.create')}
          </Button>
        </>
      }
    >
      <div className="grid gap-4">
        <Field label={t('tokens.name')}>
          <Input value={name} maxLength={64} placeholder={t('tokens.namePlaceholder')} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('tokens.directProvider')}>
          <Select value={providerId} onChange={setProviderId} options={options} />
        </Field>
      </div>
    </Sheet>
  );
}

function TokenSettingsSheet({ token, onClose }: { token: Token | null; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateToken();
  const { providers, options } = useProviderOptions();
  const [name, setName] = useState('');
  const [providerId, setProviderId] = useState('');
  const [accountId, setAccountId] = useState('');
  const provider = providers.find((p) => p.id === providerId);
  const isSubscription = provider?.authScheme === 'oauth-subscription';
  const accounts = useProviderAccounts(provider?.id, isSubscription);

  useEffect(() => {
    if (!token) return;
    setName(token.name);
    setProviderId(token.providerId ?? '');
    setAccountId(token.accountId ?? '');
  }, [token]);

  const save = () => {
    if (!token) return;
    update.mutate(
      { id: token.id, name: name.trim(), providerId: providerId || null, accountId: isSubscription ? accountId || null : null },
      {
        onSuccess: () => {
          onClose();
          toast(t('common.saved'), 'success');
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );
  };

  return (
    <Sheet
      open={token !== null}
      onOpenChange={(o) => !o && onClose()}
      title={t('tokens.settingsTitle', { name: token?.name ?? '' })}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!name.trim()} loading={update.isPending} onClick={save}>
            {t('common.save')}
          </Button>
        </>
      }
    >
      <div className="grid gap-4">
        <Field label={t('tokens.name')}>
          <Input value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('tokens.directProvider')}>
          <Select
            value={providerId}
            onChange={(v) => {
              setProviderId(v);
              setAccountId('');
            }}
            options={options}
          />
        </Field>
        {isSubscription && (
          <Field label={t('tokens.directAccount')}>
            <Select
              value={accountId}
              onChange={setAccountId}
              options={[{ value: '', label: t('clients.accountDefault') }, ...(accounts.data ?? []).map((a) => ({ value: a.id, label: a.displayName || a.id }))]}
            />
          </Field>
        )}
      </div>
    </Sheet>
  );
}
