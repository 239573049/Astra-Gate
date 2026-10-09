import { ChevronRight, Copy, Ellipsis, KeyRound, Pencil, Plus, Power, RotateCcw, Trash2 } from 'lucide-react';
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
import { formatDateTime, formatPercent, formatTime, formatTokens, formatTps, formatUsd } from '../lib/format';
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
        {tokens.data && tokens.data.length > 0 && (
          <div className="card divide-y divide-[var(--border-subtle)] overflow-hidden p-0">
            {tokens.data.map((token) => (
              <TokenRow key={token.id} token={token} defaultOpen={tokens.data.length === 1} onEdit={() => setEditing(token)} />
            ))}
          </div>
        )}
      </div>
      <CreateTokenSheet open={creating} onOpenChange={setCreating} />
      <TokenSettingsSheet token={editing} onClose={() => setEditing(null)} />
    </Page>
  );
}

function TokenRow({ token, defaultOpen, onEdit }: { token: Token; defaultOpen: boolean; onEdit: () => void }) {
  const { t, locale } = useI18n();
  const { toast, confirm } = useFeedback();
  const providers = useProviders();
  const reveal = useRevealToken();
  const update = useUpdateToken();
  const reset = useResetToken();
  const remove = useDeleteToken();
  const [open, setOpen] = useState(defaultOpen);
  const direct = (providers.data ?? []).find((p) => p.id === token.providerId);
  const detailId = `token-detail-${token.id}`;

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

  const lastUsedFull = token.lastUsedAt ? formatDateTime(token.lastUsedAt, locale) : t('tokens.neverUsed');

  return (
    <article>
      {/* The whole row toggles; the chevron button carries the accessible state and nested controls stop propagation. */}
      <div
        className="flex cursor-pointer flex-wrap items-center gap-x-5 gap-y-2 px-4 py-3 hover:bg-[var(--surface-muted)]"
        onClick={() => setOpen((o) => !o)}
      >
        <div className="flex min-w-0 flex-1 basis-56 items-start gap-2">
          <button
            type="button"
            className="mt-0.5 rounded text-[var(--text-muted)]"
            aria-expanded={open}
            aria-controls={detailId}
            aria-label={t('tokens.details')}
            onClick={(e) => {
              e.stopPropagation();
              setOpen((o) => !o);
            }}
          >
            <ChevronRight className={`size-4 transition-transform ${open ? 'rotate-90' : ''}`} />
          </button>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className={`truncate text-[14px] font-medium ${token.enabled ? '' : 'text-[var(--text-muted)]'}`}>{token.name}</h2>
              {token.isDefault && <Badge tone="accent">{t('tokens.default')}</Badge>}
              {!token.enabled && <Badge tone="orange">{t('tokens.disabledBadge')}</Badge>}
            </div>
            <div className="mt-0.5 flex items-center gap-1" onClick={(e) => e.stopPropagation()}>
              <code className="font-mono text-[12px] text-[var(--text-secondary)] selectable">{token.keyMasked}</code>
              <Button size="sm" variant="plain" icon={<Copy className="size-3.5" />} aria-label={t('tokens.copy')} title={t('tokens.copy')} loading={reveal.isPending} onClick={copy} />
            </div>
          </div>
        </div>

        <div className="hidden w-16 items-center gap-1.5 md:flex" title={token.clients.map((k) => CLIENT_META[k]?.name ?? k).join(', ') || t('tokens.noClients')}>
          {token.clients.length === 0 ? (
            <span className="text-[12px] text-[var(--text-muted)]">—</span>
          ) : (
            token.clients.map((k) => <ClientGlyph key={k} kind={k} size={16} />)
          )}
        </div>
        <Summary label={t('tokens.today')} value={formatUsd(token.today.costUsd)} context={t('tokens.requestsContext', { count: formatTokens(token.today.requests) })} />
        <Summary label={t('tokens.total')} value={formatUsd(token.total.costUsd)} context={`${formatTokens(token.total.totalTokens, true)} Token`} />
        <Summary
          label={t('tokens.lastUsedTitle')}
          value={token.lastUsedAt ? formatTime(token.lastUsedAt, locale) : '—'}
          context={token.lastUsedAt ? '' : t('tokens.neverUsed')}
          title={lastUsedFull}
        />
        <div onClick={(e) => e.stopPropagation()}>
          <Menu
            label={t('common.more')}
            icon={<Ellipsis className="size-4" />}
            items={[
              { id: 'settings', label: t('tokens.settings'), icon: <Pencil className="size-3.5" />, onSelect: onEdit },
              { id: 'toggle', label: token.enabled ? t('tokens.disable') : t('tokens.enable'), icon: <Power className="size-3.5" />, onSelect: () => void toggle() },
              { id: 'reset', label: t('tokens.reset'), icon: <RotateCcw className="size-3.5" />, onSelect: () => void doReset() },
              ...(token.isDefault
                ? []
                : [{ id: 'delete', label: t('tokens.delete'), icon: <Trash2 className="size-3.5" />, destructive: true, separatorBefore: true, onSelect: () => void doDelete() }]),
            ]}
          />
        </div>
      </div>

      {open && (
        <div id={detailId} className="border-t border-[var(--border-subtle)] bg-[var(--surface-muted)] px-4 py-3 md:pl-10">
          <dl className="flex flex-wrap gap-x-8 gap-y-2 text-[12px]">
            <div className="min-w-0">
              <dt className="text-[var(--text-muted)]">{t('tokens.clients')}</dt>
              <dd className="mt-0.5 flex min-h-5 flex-wrap items-center gap-x-3 gap-y-1 text-[var(--text-secondary)]">
                {token.clients.length === 0
                  ? t('tokens.noClients')
                  : token.clients.map((k) => (
                      <span key={k} className="inline-flex items-center gap-1">
                        <ClientGlyph kind={k} size={14} />
                        {CLIENT_META[k]?.name ?? k}
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
              <dd className="mt-0.5 truncate text-[var(--text-secondary)]">{lastUsedFull}</dd>
            </div>
          </dl>
          <StatsTable today={token.today} total={token.total} />
        </div>
      )}
    </article>
  );
}

/** One fixed-width summary column of a collapsed row, so the columns line up across tokens. */
function Summary({ label, value, context, title }: { label: string; value: string; context: string; title?: string }) {
  return (
    <div className="w-24 min-w-0" title={title}>
      <div className="truncate text-[11px] text-[var(--text-muted)]">{label}</div>
      <div className="num truncate text-[14px] leading-tight font-semibold">{value}</div>
      <div className="truncate text-[11px] text-[var(--text-muted)]">{context || '\u00a0'}</div>
    </div>
  );
}

/** Today and lifetime usage side by side: one row per period, one column per metric. */
function StatsTable({ today, total }: { today: TokenStats; total: TokenStats }) {
  const { t } = useI18n();
  const rows: [string, TokenStats][] = [
    [t('tokens.today'), today],
    [t('tokens.total'), total],
  ];
  const th = 'px-3 py-1.5 text-right font-normal whitespace-nowrap text-[var(--text-muted)]';
  const td = 'num px-3 py-1.5 text-right whitespace-nowrap';
  return (
    <div className="mt-3 overflow-x-auto rounded-[var(--radius-control)] bg-[var(--surface)]">
      <table className="w-full text-[12px]">
        <thead>
          <tr className="border-b border-[var(--border-subtle)]">
            <th className={`${th} text-left`} />
            <th className={th}>{t('tokens.cost')}</th>
            <th className={th}>{t('tokens.requests')}</th>
            <th className={th}>{t('tokens.tokens')}</th>
            <th className={th}>{t('tokens.inOut')}</th>
            <th className={th}>{t('tokens.cacheHit')}</th>
            <th className={th} title={t('tokens.tpsContext')}>{t('tokens.tps')}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map(([label, s]) => (
            <tr key={label}>
              <th scope="row" className="px-3 py-1.5 text-left font-medium whitespace-nowrap text-[var(--text-secondary)]">{label}</th>
              <td className={`${td} font-semibold selectable`}>{formatUsd(s.costUsd)}</td>
              <td className={td}>{formatTokens(s.requests)}</td>
              <td className={td}>{formatTokens(s.totalTokens, true)}</td>
              <td className={`${td} text-[var(--text-secondary)]`}>
                {formatTokens(s.inputTokens, true)} / {formatTokens(s.outputTokens, true)}
              </td>
              <td className={td} title={t('usage.cacheAmounts', { read: formatTokens(s.cacheReadTokens, true), write: formatTokens(s.cacheWriteTokens, true) })}>
                {s.cacheHitRate == null ? '—' : formatPercent(s.cacheHitRate)}
              </td>
              <td className={td}>{s.tps == null ? '—' : formatTps(s.tps)}</td>
            </tr>
          ))}
        </tbody>
      </table>
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
