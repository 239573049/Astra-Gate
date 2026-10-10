import { Download, Plus } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';

import { ApiError } from '../api/client';
import {
  keys,
  useCompleteProviderLogin,
  useFetchProviderAccountQuota,
  useImportCodexAccount,
  useImportCopilotAccount,
  useLocalCodexLogin,
  useLocalCopilotLogin,
  usePollProviderLogin,
  useProviderAccounts,
  useReorderProviderAccounts,
  useStartProviderLogin,
} from '../api/hooks';
import type { Provider, SubscriptionLoginStart } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { Button, Group, Input, Spinner } from './ui/controls';
import { SubscriptionAccountCard } from './SubscriptionAccountCard';
import { CopilotBatchImport } from './CopilotBatchImport';
import { SubscriptionPolicy } from './SubscriptionPolicy';
import { errorText, Sheet, useFeedback } from './ui/overlays';
import { useI18n } from '../i18n';


/**
 * 订阅账号区块（plan §5.4）：仅在 authScheme = oauth-subscription 的提供商上渲染。
 * 登录支持 PKCE（系统浏览器回调后自动检测新账号）与 device code（轮询直至完成/过期）。
 * 每个账号显示额度快照（进入时自动拉取，5 分钟内不重复）；过期/吊销的账号自动禁用：
 * 网关不再选用它们，行内置灰并提供「重新登录」。
 */
export function SubscriptionAccounts({ provider }: { provider: Provider }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const qc = useQueryClient();
  const isSubscription = provider.authScheme === 'oauth-subscription';
  const accounts = useProviderAccounts(provider.id, isSubscription);
  const start = useStartProviderLogin(provider.id);
  const poll = usePollProviderLogin(provider.id);
  const complete = useCompleteProviderLogin(provider.id);
  const quota = useFetchProviderAccountQuota(provider.id);
  // 只有 ChatGPT 订阅有"本机 codex 登录态"可导入。
  const isCodex = provider.templateId === 'openai-subscription';
  const local = useLocalCodexLogin(isSubscription && isCodex);
  const importCodex = useImportCodexAccount(provider.id);
  // Copilot 的本机凭据住在系统凭据存储里（VS Code 的 GitHub 会话）——macOS 上读它要弹系统授权框，
  // 所以探测只覆盖无副作用的来源，真正的读取发生在点「导入」的那一刻（那是用户的明确同意）。
  const isCopilot = provider.templateId === 'github-copilot-subscription';
  const localCopilot = useLocalCopilotLogin(isSubscription && isCopilot);
  const importCopilot = useImportCopilotAccount(provider.id);
  const [copilotToken, setCopilotToken] = useState('');
  const reorder = useReorderProviderAccounts(provider.id);

  const [loginOpen, setLoginOpen] = useState(false);
  const [login, setLogin] = useState<SubscriptionLoginStart | null>(null);
  // 登录过程中最后一条错误：显示在弹窗里，避免"授权完了但界面还在等"。
  const [loginError, setLoginError] = useState<string | null>(null);
  // 手动"立即验证"的进行中状态（设备码/CLI 流程）。
  const [checking, setChecking] = useState(false);
  // Claude 的收尾：授权页把 code 显示出来，用户粘回来（client 注册的回调不是回环地址，回不来）。
  const [pasted, setPasted] = useState('');
  const baseline = useRef<Set<string>>(new Set());
  const quotaAttempted = useRef<Set<string>>(new Set());

  const runImport = () =>
    importCodex.mutate(undefined, {
      onSuccess: (r) => {
        const name = r.account.displayName || r.account.accountEmail || r.account.id;
        toast(t('providers.subscription.importOk', { name }), r.warning ? 'info' : 'success');
        if (r.warning) toast(r.warning, 'error');
        void local.refetch();
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  const runCopilotImport = () =>
    importCopilot.mutate(copilotToken, {
      onSuccess: (r) => {
        const name = r.account.displayName || r.account.accountEmail || r.account.id;
        toast(t('providers.subscription.importOk', { name }), r.warning ? 'info' : 'success');
        if (r.warning) toast(r.warning, 'error');
        setCopilotToken('');
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

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

  // Device flow and the server-mediated CLI flow (ZAI): poll until done / expired.
  useEffect(() => {
    if (!loginOpen || (login?.mode !== 'device' && login?.mode !== 'cli')) return;
    let stopped = false;
    let failures = 0;
    const tick = async () => {
      try {
        const r = await poll.mutateAsync(login.state);
        if (stopped) return;
        failures = 0;
        if (r.status === 'done') finish(true, r.account.displayName || r.account.accountEmail || r.account.id);
        else if (r.status !== 'pending' && r.status !== 'slow_down')
          finish(false, undefined, 'error' in r ? (r.error ?? r.status) : r.status);
      } catch (e) {
        if (stopped) return;
        // 会话没了（404）或后端换令牌失败（400）：立刻报出来，别让弹窗永远停在"等待授权…"。
        // 网络抖动才继续轮询。
        if (e instanceof ApiError && (e.status === 404 || e.status === 400)) {
          finish(false, undefined, errorText(e));
          return;
        }
        if (++failures >= 5) finish(false, undefined, errorText(e));
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
    if (ok) {
      setLogin(null);
      setLoginError(null);
      setLoginOpen(false);
      toast(t('providers.subscription.loginOk', { name: name ?? '' }), 'success');
      return;
    }
    // 失败时保留弹窗并显示原因：用户需要一个"再试一次/关闭"的落点。
    if (error) setLoginError(error);
  };

  const startLogin = (accountId?: string) => {
    baseline.current = new Set((accounts.data ?? []).map((a) => a.id));
    setLoginError(null);
    start.mutate(accountId, {
      onSuccess: (res) => {
        setLogin(res);
        setLoginOpen(true);
        // PKCE 与 CLI 都是"打开授权页 → 服务端等地回调/轮询"，device 由用户自己去输码。
        if (res.mode !== 'device') window.open(res.authorizeUrl, '_blank', 'noopener');
      },
      onError: (e) => {
        if (e instanceof ApiError && (e.details as { needsVerification?: boolean })?.needsVerification)
          toast(t('providers.subscription.needsVerification'), 'error');
        else toast(errorText(e), 'error');
      },
    });
  };

  /**
   * 手动验证一次授权（设备码 / CLI 流程用）：不等自动轮询的下一拍，立刻向服务端确认。
   * 即使后台轮询已经把这次会话消费掉（404），只要账号已经进了列表就算成功——
   * 用户点这个按钮时的期望是"我授权完了，告诉我到底成没成"。
   */
  const checkNow = async () => {
    if (!login) return;
    setChecking(true);
    try {
      const r = await poll.mutateAsync(login.state);
      if (r.status === 'done') finish(true, r.account.displayName || r.account.accountEmail || r.account.id);
      else if (r.status === 'pending' || r.status === 'slow_down') toast(t('providers.subscription.stillWaiting'), 'info');
      else finish(false, undefined, 'error' in r ? (r.error ?? r.status) : r.status);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) {
        const fresh = await accounts.refetch();
        const added = (fresh.data ?? []).find((a) => !baseline.current.has(a.id));
        if (added) finish(true, added.displayName || added.accountEmail || added.id);
        else finish(false, undefined, errorText(e));
      } else {
        finish(false, undefined, errorText(e));
      }
    } finally {
      setChecking(false);
    }
  };

  /** 把授权页上显示的 code 交给服务端换令牌（Claude）。 */
  const submitPasted = async () => {
    if (!login || login.mode !== 'paste' || pasted.trim().length === 0) return;
    setChecking(true);
    try {
      const r = await complete.mutateAsync({ state: login.state, code: pasted.trim() });
      if (r.status === 'done') {
        setPasted('');
        finish(true, r.account.displayName || r.account.accountEmail || r.account.id);
      } else {
        finish(false, undefined, 'error' in r ? (r.error ?? r.status) : r.status);
      }
    } catch (e) {
      finish(false, undefined, errorText(e));
    } finally {
      setChecking(false);
    }
  };

  /** Swaps the account at <index> with its neighbour in the failover order. */
  const move = (index: number, delta: -1 | 1) => {
    const ids = (accounts.data ?? []).map((a) => a.id);
    const other = index + delta;
    if (other < 0 || other >= ids.length) return;
    [ids[index], ids[other]] = [ids[other]!, ids[index]!];
    reorder.mutate(ids, { onError: (e) => toast(errorText(e), 'error') });
  };

  return (
    <>
      <SubscriptionPolicy provider={provider} />
      <Group title={t('providers.subscription')} footer={t('providers.subscription.footer')}>
        {accounts.isLoading && <Spinner className="my-4" />}
        {!accounts.isLoading && (accounts.data?.length ?? 0) === 0 && (
          <div className="px-4 py-5 text-center text-[12px] text-[var(--text-secondary)]">
            {t('providers.subscription.none')}
          </div>
        )}
        {accounts.data?.map((a, i, list) => (
          <SubscriptionAccountCard
            key={a.id}
            provider={provider}
            account={a}
            onRelogin={startLogin}
            onMove={list.length > 1 ? { up: i > 0 ? () => move(i, -1) : undefined, down: i < list.length - 1 ? () => move(i, 1) : undefined } : undefined}
          />
        ))}
        <div className="flex flex-wrap items-center gap-2 px-4 py-2">
          <Button size="sm" icon={<Plus className="size-3.5" />} loading={start.isPending} onClick={() => startLogin()}>
            {t('providers.subscription.login')}
          </Button>
          {/* 已在本机 `codex login` 过的话，可以直接接过来，不必再走一遍浏览器授权。 */}
          {isCodex && local.data?.available && (
            <Button
              size="sm"
              icon={<Download className="size-3.5" />}
              loading={importCodex.isPending}
              onClick={runImport}
              title={t('providers.subscription.importCodexHint')}
            >
              {t('providers.subscription.importCodex')}
            </Button>
          )}
          {/* Copilot 的长期凭据是一份 GitHub token（Copilot 短时令牌由它换来），本机常见来源是
              VS Code 的 GitHub 会话。探测只在顺手能拿到时提示；拿不到也保留按钮——
              点它才是去读系统凭据存储、或使用下面手填 token 的时机。 */}
          {isCopilot && (
            <>
              <Button
                size="sm"
                icon={<Download className="size-3.5" />}
                loading={importCopilot.isPending}
                onClick={runCopilotImport}
                title={t('providers.subscription.importCopilotHint')}
              >
                {t('providers.subscription.importCopilot')}
              </Button>
              <Input
                className="w-56"
                mono
                value={copilotToken}
                onChange={(e) => setCopilotToken(e.target.value)}
                placeholder={t('providers.subscription.importCopilotPlaceholder')}
                aria-label={t('providers.subscription.importCopilotToken')}
              />
              <CopilotBatchImport providerId={provider.id} />
            </>
          )}
        </div>
        {isCodex && local.data?.available && (
          <div className="px-4 pb-2 text-[11px] text-[var(--text-muted)]">
            {t('providers.subscription.localCodexFound', {
              who: local.data.accountEmail ?? local.data.plan ?? t('providers.subscription.unknownAccount'),
            })}
            {local.data.detail ? ` · ${local.data.detail}` : ''}
          </div>
        )}
        {isCopilot && (
          <div className="px-4 pb-2 text-[11px] text-[var(--text-muted)]">
            {localCopilot.data?.available
              ? t('providers.subscription.localCopilotFound', {
                  source: localCopilot.data.source ?? '',
                })
              : t('providers.subscription.localCopilotHint')}
            {localCopilot.data?.detail ? ` · ${localCopilot.data.detail}` : ''}
          </div>
        )}
      </Group>

      <Sheet
        open={loginOpen}
        onOpenChange={(o) => {
          setLoginOpen(o);
          if (!o) {
            setLogin(null);
            setLoginError(null);
          }
        }}
        title={t('providers.subscription.login')}
        description={t('providers.subscription.loginDesc')}
      >
        {loginError && (
          <Alert tone="danger" title={t('providers.subscription.loginFailed')}>
            {loginError}
          </Alert>
        )}
        {login?.mode === 'paste' && (
          <div className="flex flex-col gap-3">
            <Alert tone="info" title={t('providers.subscription.pasteTitle')}>
              {t('providers.subscription.pasteDetail')}
            </Alert>
            <div className="flex flex-wrap gap-2">
              <Button onClick={() => window.open(login.authorizeUrl, '_blank', 'noopener')}>
                {t('providers.subscription.openAuthorize')}
              </Button>
            </div>
            <Input
              mono
              value={pasted}
              onChange={(e) => setPasted(e.target.value)}
              placeholder={t('providers.subscription.pastePlaceholder')}
              aria-label={t('providers.subscription.pasteCode')}
            />
            {/* 授权页上显示的通常是「授权码#state」，直接整段粘过来即可。 */}
            <Button variant="primary" loading={checking} onClick={() => void submitPasted()}>
              {t('providers.subscription.pasteSubmit')}
            </Button>
          </div>
        )}
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
        {login?.mode === 'cli' && (
          <div className="flex flex-col gap-3">
            <Alert tone="info" title={t('providers.subscription.waiting')}>
              {t('providers.subscription.cliDetail')}
            </Alert>
            <Button onClick={() => window.open(login.authorizeUrl, '_blank', 'noopener')}>
              {t('providers.subscription.openAuthorize')}
            </Button>
          </div>
        )}
        {login?.mode === 'device' && (
          <div className="flex flex-col gap-3">
            <div>
              <div className="text-[11px] text-[var(--text-secondary)]">{t('providers.subscription.deviceCode')}</div>
              <div className="selectable font-mono text-[18px] font-medium tracking-wide">{login.userCode}</div>
            </div>
            <div className="flex flex-wrap gap-2">
              {login.verificationUrl && (
                <Button onClick={() => window.open(login.verificationUrl ?? undefined, '_blank', 'noopener')}>
                  {t('providers.subscription.visitPage')}
                </Button>
              )}
              {/* 授权完成后不必等自动轮询的下一拍：立刻问一次服务端。 */}
              <Button variant="primary" loading={checking} onClick={() => void checkNow()}>
                {t('providers.subscription.checkNow')}
              </Button>
            </div>
            <Alert tone="info" title={t('providers.subscription.waiting')}>
              {t('providers.subscription.checkNowHint')}
            </Alert>
          </div>
        )}
      </Sheet>
    </>
  );
}
