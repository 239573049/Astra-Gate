import { Gauge, Plus, RotateCcw, Trash2 } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';

import { ApiError } from '../api/client';
import {
  keys,
  useDeleteProviderAccount,
  useFetchProviderAccountQuota,
  usePollProviderLogin,
  useProviderAccounts,
  useRefreshProviderAccount,
  useStartProviderLogin,
} from '../api/hooks';
import type { Provider, ProviderAccount, QuotaWindow, SubscriptionLoginStart } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { Badge, Button, Group, Row, Spinner } from './ui/controls';
import { errorText, Sheet, useFeedback } from './ui/overlays';
import { useI18n, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';

const STATUS_TONE = { active: 'green', expired: 'orange', revoked: 'red' } as const;
const STATUS_KEY: Record<ProviderAccount['status'], MessageKey> = {
  active: 'providers.subscription.status.active',
  expired: 'providers.subscription.status.expired',
  revoked: 'providers.subscription.status.revoked',
};

/**
 * 订阅账号区块（plan §5.4）：仅在 authScheme = oauth-subscription 的提供商上渲染。
 * 登录支持 PKCE（系统浏览器回调后自动检测新账号）与 device code（轮询直至完成/过期）。
 * 每个账号显示额度快照（进入时自动拉取，5 分钟内不重复）；过期/吊销的账号自动禁用：
 * 网关不再选用它们，行内置灰并提供「重新登录」。
 */
export function SubscriptionAccounts({ provider }: { provider: Provider }) {
  const { t, locale } = useI18n();
  const { toast, confirm } = useFeedback();
  const qc = useQueryClient();
  const isSubscription = provider.authScheme === 'oauth-subscription';
  const accounts = useProviderAccounts(provider.id, isSubscription);
  const start = useStartProviderLogin(provider.id);
  const poll = usePollProviderLogin(provider.id);
  const refresh = useRefreshProviderAccount(provider.id);
  const remove = useDeleteProviderAccount(provider.id);
  const quota = useFetchProviderAccountQuota(provider.id);

  const [loginOpen, setLoginOpen] = useState(false);
  const [login, setLogin] = useState<SubscriptionLoginStart | null>(null);
  const baseline = useRef<Set<string>>(new Set());
  const quotaAttempted = useRef<Set<string>>(new Set());

  // 进入时为活跃账号自动拉取一次额度（缺失或超过 5 分钟的快照）；失败保持静默，可手动重试。
  useEffect(() => {
    if (!isSubscription) return;
    for (const a of accounts.data ?? []) {
      if (a.status !== 'active' || quotaAttempted.current.has(a.id)) continue;
      const at = a.quota?.fetchedAtUtc ? new Date(a.quota.fetchedAtUtc).getTime() : 0;
      if (Number.isFinite(at) && Date.now() - at < 5 * 60_000) continue;
      quotaAttempted.current.add(a.id);
      quota.mutate(a.id, { onError: () => {} });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [accounts.data, isSubscription]);

  // PKCE: the browser callback completes the exchange server-side; detect the new account here.
  useEffect(() => {
    if (!loginOpen || login?.mode !== 'pkce') return;
    const timer = setInterval(() => void accounts.refetch(), 3000);
    return () => clearInterval(timer);
  }, [loginOpen, login, accounts]);

  useEffect(() => {
    if (!loginOpen || login?.mode !== 'pkce') return;
    const fresh = (accounts.data ?? []).find((a) => !baseline.current.has(a.id));
    if (fresh) finish(true, fresh.displayName || fresh.accountEmail || fresh.id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [accounts.data, loginOpen, login]);

  // Device flow: poll until done / expired.
  useEffect(() => {
    if (!loginOpen || login?.mode !== 'device') return;
    let stopped = false;
    const tick = async () => {
      try {
        const r = await poll.mutateAsync(login.state);
        if (stopped) return;
        if (r.status === 'done') finish(true, r.account.displayName || r.account.accountEmail || r.account.id);
        else if (r.status !== 'pending' && r.status !== 'slow_down')
          finish(false, undefined, 'error' in r ? (r.error ?? r.status) : r.status);
      } catch {
        // transient network error: keep polling until the login expires
      }
    };
    const timer = setInterval(() => void tick(), Math.max(login.interval || 5, 3) * 1000);
    return () => {
      stopped = true;
      clearInterval(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loginOpen, login]);

  if (!isSubscription) return null;

  const finish = (ok: boolean, name?: string, error?: string | null) => {
    void qc.invalidateQueries({ queryKey: keys.providerAccounts(provider.id) });
    setLogin(null);
    setLoginOpen(false);
    if (ok) toast(t('providers.subscription.loginOk', { name: name ?? '' }), 'success');
    else if (error) toast(`${t('providers.subscription.loginFailed')}: ${error}`, 'error');
  };

  const startLogin = (accountId?: string) => {
    baseline.current = new Set((accounts.data ?? []).map((a) => a.id));
    start.mutate(accountId, {
      onSuccess: (res) => {
        setLogin(res);
        setLoginOpen(true);
        if (res.mode === 'pkce') window.open(res.authorizeUrl, '_blank', 'noopener');
      },
      onError: (e) => {
        if (e instanceof ApiError && (e.details as { needsVerification?: boolean })?.needsVerification)
          toast(t('providers.subscription.needsVerification'), 'error');
        else toast(errorText(e), 'error');
      },
    });
  };

  const removeAccount = async (account: ProviderAccount) => {
    if (
      !(await confirm({
        title: t('providers.subscription.logoutConfirm', { name: account.displayName || account.id }),
        destructive: true,
        confirmLabel: t('providers.subscription.logout'),
      }))
    )
      return;
    remove.mutate(account.id, { onError: (e) => toast(errorText(e), 'error') });
  };

  const resetSuffix = (iso?: string | null) => {
    if (!iso) return '';
    const ms = new Date(iso).getTime() - Date.now();
    if (!Number.isFinite(ms) || ms <= 0) return '';
    const rtf = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
    const hours = ms / 3_600_000;
    if (hours >= 48) return ` (${rtf.format(Math.round(hours / 24), 'day')})`;
    if (hours >= 1) return ` (${rtf.format(Math.round(hours), 'hour')})`;
    return ` (${rtf.format(Math.max(1, Math.round(ms / 60_000)), 'minute')})`;
  };

  const quotaSummary = (a: ProviderAccount): string | null => {
    const q = a.quota;
    if (!q) return null;
    const fmtWindow = (w?: QuotaWindow | null, key?: MessageKey) =>
      w?.usedPercent == null ? null : `${t(key!, { percent: Math.round(w.usedPercent) })}${resetSuffix(w.resetsAtUtc)}`;
    const parts = [
      fmtWindow(q.session, 'providers.subscription.quotaSession'),
      fmtWindow(q.weekly, 'providers.subscription.quotaWeekly'),
      q.credits?.usedPercent != null
        ? t('providers.subscription.quotaCredits', { percent: Math.round(q.credits.usedPercent) })
        : q.credits?.prepaidBalance != null
          ? t('providers.subscription.quotaBalance', { balance: q.credits.prepaidBalance })
          : null,
      q.planLabel ?? null,
    ].filter(Boolean) as string[];
    return parts.length ? parts.join(' · ') : null;
  };

  const detailOf = (a: ProviderAccount) =>
    [
      a.accountEmail,
      a.plan,
      quotaSummary(a),
      a.expiresAtUtc ? `${t('providers.subscription.expires')} ${new Date(a.expiresAtUtc).toLocaleString()}` : null,
    ]
      .filter(Boolean)
      .join(' · ');

  return (
    <>
      <Group title={t('providers.subscription')} footer={t('providers.subscription.footer')}>
        {accounts.isLoading && <Spinner className="my-4" />}
        {!accounts.isLoading && (accounts.data?.length ?? 0) === 0 && (
          <div className="px-4 py-5 text-center text-[12px] text-[var(--text-secondary)]">
            {t('providers.subscription.none')}
          </div>
        )}
        {accounts.data?.map((a) => {
          const dead = a.status !== 'active';
          return (
            <Row key={a.id} label={a.displayName || a.id} detail={detailOf(a)} className={cn(dead && 'opacity-55')}>
              <Badge tone={STATUS_TONE[a.status]}>{t(STATUS_KEY[a.status])}</Badge>
              <Button
                size="sm"
                variant="plain"
                icon={<Gauge className="size-3.5" />}
                aria-label={t('providers.subscription.quotaRefresh')}
                loading={quota.isPending && quota.variables === a.id}
                disabled={dead}
                onClick={() =>
                  quota.mutate(a.id, {
                    onError: (e) => {
                      if (e instanceof ApiError && e.status === 501) toast(t('providers.subscription.quotaUnsupported'), 'error');
                      else toast(errorText(e), 'error');
                    },
                  })
                }
              />
              <Button
                size="sm"
                variant="plain"
                icon={<RotateCcw className="size-3.5" />}
                aria-label={t('providers.subscription.refreshToken')}
                loading={refresh.isPending && refresh.variables === a.id}
                disabled={a.status === 'revoked'}
                onClick={() =>
                  refresh.mutate(a.id, {
                    onSuccess: () => toast(t('providers.subscription.tokenRefreshed'), 'success'),
                    onError: (e) => toast(errorText(e), 'error'),
                  })
                }
              />
              {dead && (
                <Button size="sm" variant="plain" onClick={() => startLogin(a.id)}>
                  {t('providers.subscription.relogin')}
                </Button>
              )}
              <Button
                size="sm"
                variant="plain"
                icon={<Trash2 className="size-3.5" />}
                aria-label={t('providers.subscription.logout')}
                onClick={() => void removeAccount(a)}
              />
            </Row>
          );
        })}
        <div className="px-4 py-2">
          <Button size="sm" icon={<Plus className="size-3.5" />} loading={start.isPending} onClick={() => startLogin()}>
            {t('providers.subscription.login')}
          </Button>
        </div>
      </Group>

      <Sheet
        open={loginOpen}
        onOpenChange={(o) => {
          setLoginOpen(o);
          if (!o) setLogin(null);
        }}
        title={t('providers.subscription.login')}
        description={t('providers.subscription.loginDesc')}
      >
        {login?.mode === 'pkce' && (
          <div className="flex flex-col gap-3">
            <Alert tone="info" title={t('providers.subscription.waiting')}>
              {t('providers.subscription.waitingDetail')}
            </Alert>
            <div className="flex gap-2">
              <Button onClick={() => window.open(login.authorizeUrl, '_blank', 'noopener')}>
                {t('providers.subscription.openAuthorize')}
              </Button>
              <Button
                variant="primary"
                onClick={() => {
                  void qc.invalidateQueries({ queryKey: keys.providerAccounts(provider.id) });
                  void accounts.refetch();
                }}
              >
                {t('providers.subscription.doneAuthorize')}
              </Button>
            </div>
          </div>
        )}
        {login?.mode === 'device' && (
          <div className="flex flex-col gap-3">
            <div>
              <div className="text-[11px] text-[var(--text-secondary)]">{t('providers.subscription.deviceCode')}</div>
              <div className="selectable font-mono text-[18px] font-medium tracking-wide">{login.userCode}</div>
            </div>
            {login.verificationUrl && (
              <Button onClick={() => window.open(login.verificationUrl ?? undefined, '_blank', 'noopener')}>
                {t('providers.subscription.visitPage')}
              </Button>
            )}
            <Alert tone="info" title={t('providers.subscription.waiting')} />
          </div>
        )}
      </Sheet>
    </>
  );
}
