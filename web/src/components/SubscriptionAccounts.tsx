import { Download, KeyRound, ListPlus, LogIn, Plus, Power, Trash2 } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';

import { ApiError } from '../api/client';
import {
  keys,
  useAccountImportInfo,
  useActivateProviderAccount,
  useCompleteProviderLogin,
  useDeleteProviderAccount,
  useFetchProviderAccountQuota,
  useImportCodexAccount,
  useImportCopilotAccount,
  useLocalCodexLogin,
  useLocalCopilotLogin,
  usePollProviderLogin,
  useProviderAccounts,
  useRefreshProviderAccount,
  useReorderProviderAccounts,
  useStartProviderLogin,
  useSubscriptionPolicy,
  useUpdateProviderAccount,
  useUpdateSubscriptionPolicy,
} from '../api/hooks';
import type { Provider, ProviderAccount, SubscriptionLoginStart, SubscriptionPolicy as Policy } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { AccountImportSheet, type ImportMode } from './AccountImportSheet';
import { Button, Input, SearchField, Segmented, Spinner } from './ui/controls';
import { accountState, SubscriptionAccountCard, type AccountActions, type AccountState } from './SubscriptionAccountCard';
import { SubscriptionAccountDetail } from './SubscriptionAccountDetail';
import { SubscriptionPolicy } from './SubscriptionPolicy';
import { errorText, Menu, type MenuItem, Sheet, useFeedback } from './ui/overlays';
import { useI18n, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';

type Filter = 'all' | AccountState;
const FILTERS: { value: Filter; label: MessageKey }[] = [
  { value: 'all', label: 'providers.accounts.filter.all' },
  { value: 'usable', label: 'providers.accounts.filter.usable' },
  { value: 'cooling', label: 'providers.accounts.filter.cooling' },
  { value: 'dead', label: 'providers.accounts.filter.dead' },
  { value: 'disabled', label: 'providers.accounts.filter.disabled' },
];

/** Quota snapshots older than this are refreshed automatically when the list mounts. */
const QUOTA_FRESH_MS = 5 * 60_000;

/**
 * 「账户」标签页（plan §5.4）：仅在 authScheme = oauth-subscription 的提供商上渲染。
 * 顶部一行是账户池的统计与切换方式；其下是筛选 / 搜索 / 批量导入 / 添加账户；主体是紧凑卡片网格，
 * 支持多选批量启停 / 刷新额度 / 删除，点卡片打开详情（完整额度、名称、账户级代理）。
 * 登录支持 PKCE（系统浏览器回调后自动检测新账号）、设备码 / CLI（轮询直至完成）与粘贴授权码（Claude）。
 */
export function SubscriptionAccounts({ provider }: { provider: Provider }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const qc = useQueryClient();
  const isSubscription = provider.authScheme === 'oauth-subscription';
  const accounts = useProviderAccounts(provider.id, isSubscription);
  const policy = useSubscriptionPolicy(provider.id, isSubscription);
  const updatePolicy = useUpdateSubscriptionPolicy(provider.id);
  const importInfo = useAccountImportInfo(provider.id, isSubscription);
  const start = useStartProviderLogin(provider.id);
  const poll = usePollProviderLogin(provider.id);
  const complete = useCompleteProviderLogin(provider.id);
  const quota = useFetchProviderAccountQuota(provider.id);
  const refresh = useRefreshProviderAccount(provider.id);
  const remove = useDeleteProviderAccount(provider.id);
  const activate = useActivateProviderAccount(provider.id);
  const update = useUpdateProviderAccount(provider.id);
  const reorder = useReorderProviderAccounts(provider.id);
  // 只有 ChatGPT 订阅有"本机 codex 登录态"可导入。
  const isCodex = provider.templateId === 'openai-subscription';
  const local = useLocalCodexLogin(isSubscription && isCodex);
  const importCodex = useImportCodexAccount(provider.id);
  // Copilot 的本机凭据住在系统凭据存储里（VS Code 的 GitHub 会话）——macOS 上读它要弹系统授权框，
  // 所以探测只覆盖无副作用的来源，真正的读取发生在点「导入」的那一刻（那是用户的明确同意）。
  const isCopilot = provider.templateId === 'github-copilot-subscription';
  const localCopilot = useLocalCopilotLogin(isSubscription && isCopilot);
  const importCopilot = useImportCopilotAccount(provider.id);

  const [filter, setFilter] = useState<Filter>('all');
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [detailId, setDetailId] = useState<string | null>(null);
  const [importMode, setImportMode] = useState<ImportMode | null>(null);
  const [proxyLoginOpen, setProxyLoginOpen] = useState(false);
  const [loginProxy, setLoginProxy] = useState('');
  const [bulkBusy, setBulkBusy] = useState(false);

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

  const list = useMemo(() => accounts.data ?? [], [accounts.data]);

  // 进入时为活跃账号自动拉取一次额度（缺失或超过 5 分钟的快照）；失败保持静默，可手动重试。
  useEffect(() => {
    if (!isSubscription) return;
    for (const a of list) {
      if (a.status !== 'active' || quotaAttempted.current.has(a.id)) continue;
      const at = a.quota?.fetchedAtUtc ? new Date(a.quota.fetchedAtUtc).getTime() : 0;
      if (Number.isFinite(at) && Date.now() - at < QUOTA_FRESH_MS) continue;
      quotaAttempted.current.add(a.id);
      quota.mutate(a.id, { onError: () => {} });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [list, isSubscription]);

  // Selections and the open detail follow the list (deleted accounts drop out).
  useEffect(() => {
    const ids = new Set(list.map((a) => a.id));
    setSelected((s) => (Array.from(s).every((id) => ids.has(id)) ? s : new Set(Array.from(s).filter((id) => ids.has(id)))));
    if (detailId && !ids.has(detailId)) setDetailId(null);
  }, [list, detailId]);

  // PKCE: the browser callback completes the exchange server-side; detect the new account here.
  useEffect(() => {
    if (!loginOpen || login?.mode !== 'pkce') return;
    const timer = setInterval(() => void accounts.refetch(), 3000);
    return () => clearInterval(timer);
  }, [loginOpen, login, accounts]);

  useEffect(() => {
    if (!loginOpen || login?.mode !== 'pkce') return;
    const fresh = list.find((a) => !baseline.current.has(a.id));
    if (fresh) finish(true, fresh.displayName || fresh.accountEmail || fresh.id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [list, loginOpen, login]);

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

  const nameOf = (a: ProviderAccount) => a.displayName || a.accountEmail || a.keyMasked || a.id;

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

  const startLogin = (accountId?: string, httpProxy?: string) => {
    baseline.current = new Set(list.map((a) => a.id));
    setLoginError(null);
    start.mutate(
      { accountId, httpProxy },
      {
        onSuccess: (res) => {
          setProxyLoginOpen(false);
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
      },
    );
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

  const runCodexImport = () =>
    importCodex.mutate(undefined, {
      onSuccess: (r) => {
        toast(t('providers.subscription.importOk', { name: nameOf(r.account) }), r.warning ? 'info' : 'success');
        if (r.warning) toast(r.warning, 'error');
        void local.refetch();
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  const runCopilotImport = () =>
    importCopilot.mutate('', {
      onSuccess: (r) => {
        toast(t('providers.subscription.importOk', { name: nameOf(r.account) }), r.warning ? 'info' : 'success');
        if (r.warning) toast(r.warning, 'error');
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  /** Swaps the account at <index> with its neighbour in the failover order. */
  const move = (index: number, delta: -1 | 1) => {
    const ids = list.map((a) => a.id);
    const other = index + delta;
    if (other < 0 || other >= ids.length) return;
    [ids[index], ids[other]] = [ids[other]!, ids[index]!];
    reorder.mutate(ids, { onError: (e) => toast(errorText(e), 'error') });
  };

  const removeOne = async (a: ProviderAccount) => {
    const name = nameOf(a);
    if (!(await confirm({ title: t('providers.subscription.logoutConfirm', { name }), destructive: true, confirmLabel: t('providers.subscription.logout') })))
      return;
    remove.mutate(a.id, { onError: (e) => toast(errorText(e), 'error') });
  };

  const actions: AccountActions = {
    open: (a) => setDetailId(a.id),
    activate: (a) =>
      activate.mutate(a.id, {
        onSuccess: () => toast(t('providers.subscription.activated', { name: nameOf(a) }), 'success'),
        onError: (e) => toast(errorText(e), 'error'),
      }),
    toggleEnabled: (a) => update.mutate({ id: a.id, enabled: !a.enabled }, { onError: (e) => toast(errorText(e), 'error') }),
    refreshQuota: (a) =>
      quota.mutate(a.id, {
        onError: (e) => {
          if (e instanceof ApiError && e.status === 501) toast(t('providers.subscription.quotaUnsupported'), 'error');
          else toast(errorText(e), 'error');
        },
      }),
    refreshToken: (a) =>
      refresh.mutate(a.id, {
        onSuccess: () => toast(t('providers.subscription.tokenRefreshed'), 'success'),
        onError: (e) => toast(errorText(e), 'error'),
      }),
    relogin: (a) => startLogin(a.id),
    remove: (a) => void removeOne(a),
  };

  // ----- selection (bulk actions) -----

  const toggleSelect = (id: string) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  /** Runs one mutation per selected account, in order; reports failures, then clears the selection. */
  const bulk = async (run: (a: ProviderAccount) => Promise<unknown>) => {
    const targets = list.filter((a) => selected.has(a.id));
    setBulkBusy(true);
    let done = 0;
    for (const a of targets) {
      try {
        await run(a);
        done++;
      } catch (e) {
        toast(`${nameOf(a)}: ${errorText(e)}`, 'error');
      }
    }
    setBulkBusy(false);
    setSelected(new Set());
    void qc.invalidateQueries({ queryKey: keys.providerAccounts(provider.id) });
    if (done > 0) toast(t('providers.accounts.bulkDone', { n: done }), 'success');
  };

  const bulkDelete = async () => {
    if (
      !(await confirm({
        title: t('providers.accounts.bulkDeleteConfirm', { n: selected.size }),
        detail: t('providers.accounts.bulkDeleteDetail'),
        destructive: true,
        confirmLabel: t('common.delete'),
      }))
    )
      return;
    await bulk((a) => remove.mutateAsync(a.id));
  };

  // ----- derived view -----

  const states = list.map((a) => accountState(a));
  const countOf = (f: Filter) => (f === 'all' ? list.length : states.filter((s) => s === f).length);
  const needle = query.trim().toLowerCase();
  const visible = list
    .map((a, index) => ({ a, index, state: states[index]! }))
    .filter(({ a, state }) => (filter === 'all' || state === filter)
      && (needle.length === 0 || [a.displayName, a.accountEmail, a.keyMasked, a.plan].some((v) => v?.toLowerCase().includes(needle))));
  const detail = list.find((a) => a.id === detailId) ?? null;
  const info = importInfo.data;
  const selecting = selected.size > 0;

  const addItems: MenuItem[] = [
    { id: 'login', label: t('providers.accounts.addLogin'), icon: <LogIn className="size-3.5" />, onSelect: () => startLogin() },
    { id: 'login-proxy', label: t('providers.accounts.addLoginProxy'), onSelect: () => setProxyLoginOpen(true) },
    ...(isCodex && local.data?.available
      ? [{ id: 'codex', separatorBefore: true, label: t('providers.subscription.importCodex'), icon: <Download className="size-3.5" />, onSelect: runCodexImport }]
      : []),
    ...(isCopilot
      ? [{ id: 'copilot', separatorBefore: true, label: t('providers.subscription.importCopilot'), icon: <Download className="size-3.5" />, onSelect: runCopilotImport }]
      : []),
    ...(info?.supported
      ? [{ id: 'token', separatorBefore: !(isCodex && local.data?.available) && !isCopilot, label: t('providers.accounts.addToken'), onSelect: () => setImportMode('token') }]
      : []),
    ...(info?.apiKey ? [{ id: 'apikey', label: t('providers.accounts.addApiKey'), icon: <KeyRound className="size-3.5" />, onSelect: () => setImportMode('apiKey') }] : []),
  ];

  return (
    <>
      {/* 账户池一行：统计 + 切换方式 + 生效的出口。 */}
      <div className="card mb-3 flex flex-wrap items-center gap-x-4 gap-y-2 px-3 py-2">
        <div className="mr-auto flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-[var(--text-secondary)]">
          <span className="font-medium text-[var(--text)]">{t('providers.accounts.statTotal', { n: list.length })}</span>
          <span style={{ color: 'var(--success)' }}>{t('providers.accounts.statUsable', { n: countOf('usable') })}</span>
          {countOf('cooling') > 0 && <span style={{ color: 'var(--warning)' }}>{t('providers.accounts.statCooling', { n: countOf('cooling') })}</span>}
          {countOf('dead') > 0 && <span style={{ color: 'var(--danger)' }}>{t('providers.accounts.statDead', { n: countOf('dead') })}</span>}
          <span className="text-[11px] text-[var(--text-muted)]">
            {t('providers.accounts.providerProxy', { proxy: provider.httpProxy || t('providers.accounts.proxyFallbackSystem') })}
          </span>
        </div>
        {policy.data && <SwitchModePicker policy={policy.data} onChange={(switchMode) => updatePolicy.mutate({ switchMode }, { onError: (e) => toast(errorText(e), 'error') })} />}
      </div>
      {policy.data && (
        <p className="-mt-1 mb-3 px-1 text-[11px] text-[var(--text-muted)]">
          {t(
            policy.data.switchMode === 'balanced'
              ? 'providers.subscription.policy.balancedDetail'
              : policy.data.switchMode === 'failover'
                ? 'providers.subscription.policy.failoverDetail'
                : 'providers.subscription.policy.manualDetail',
          )}
        </p>
      )}

      <div className="mb-2 flex flex-wrap items-center gap-2">
        <SearchField className="w-52" value={query} onChange={setQuery} placeholder={t('providers.accounts.search')} />
        <div role="group" aria-label={t('providers.accounts.filterLabel')} className="mr-auto flex flex-wrap gap-1">
          {FILTERS.filter((f) => f.value === 'all' || countOf(f.value) > 0).map((f) => (
            <button
              key={f.value}
              type="button"
              aria-pressed={filter === f.value}
              onClick={() => setFilter(f.value)}
              className={cn(
                'rounded-full px-2.5 py-0.5 text-[11.5px] text-[var(--text-secondary)] transition-colors hover:bg-[var(--fill)]',
                filter === f.value && 'bg-[var(--fill)] text-[var(--text)]',
              )}
            >
              {t(f.label)}
              <span className="ml-1 text-[var(--text-muted)]">{countOf(f.value)}</span>
            </button>
          ))}
        </div>
        {info?.supported && (
          <Button size="sm" icon={<ListPlus className="size-3.5" />} onClick={() => setImportMode('batch')}>
            {t('providers.accounts.batchImport')}
          </Button>
        )}
        <Menu label={t('providers.accounts.add')} icon={<Plus className="size-3.5" />} items={addItems} />
      </div>

      {isCodex && local.data?.available && (
        <p className="mb-2 px-1 text-[11px] text-[var(--text-muted)]">
          {t('providers.subscription.localCodexFound', {
            who: local.data.accountEmail ?? local.data.plan ?? t('providers.subscription.unknownAccount'),
          })}
          {local.data.detail ? ` · ${local.data.detail}` : ''}
        </p>
      )}
      {isCopilot && (
        <p className="mb-2 px-1 text-[11px] text-[var(--text-muted)]">
          {localCopilot.data?.available
            ? t('providers.subscription.localCopilotFound', { source: localCopilot.data.source ?? '' })
            : t('providers.subscription.localCopilotHint')}
          {localCopilot.data?.detail ? ` · ${localCopilot.data.detail}` : ''}
        </p>
      )}

      {accounts.isLoading ? (
        <Spinner className="my-4" />
      ) : list.length === 0 ? (
        <button
          type="button"
          onClick={() => startLogin()}
          className="flex w-full flex-col items-center gap-1 rounded-[var(--radius-card,12px)] border border-dashed border-[var(--border)] px-4 py-6 text-[12px] text-[var(--text-secondary)] hover:border-[var(--accent)] hover:text-[var(--text)]"
        >
          <span>{t('providers.subscription.none')}</span>
          <span className="flex items-center gap-1 text-[var(--accent)]">
            <Plus className="size-3.5" /> {t('providers.subscription.login')}
          </span>
        </button>
      ) : visible.length === 0 ? (
        <div className="px-4 py-5 text-center text-[12px] text-[var(--text-secondary)]">{t('providers.accounts.noMatch')}</div>
      ) : (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(190px,1fr))] gap-3">
          {visible.map(({ a, index }) => (
            <SubscriptionAccountCard
              key={a.id}
              account={a}
              index={index}
              actions={actions}
              selected={selected.has(a.id)}
              selecting={selecting}
              onToggleSelect={toggleSelect}
              quotaPending={quota.isPending && quota.variables === a.id}
              onMove={
                list.length > 1
                  ? { up: index > 0 ? () => move(index, -1) : undefined, down: index < list.length - 1 ? () => move(index, 1) : undefined }
                  : undefined
              }
            />
          ))}
        </div>
      )}
      <p className="mt-2 px-1 text-[11px] text-[var(--text-muted)]">{t('providers.subscription.footer')}</p>

      {selecting && (
        <div className="card sticky bottom-3 z-10 mt-3 flex flex-wrap items-center gap-1.5 px-3 py-1.5 shadow-lg">
          <span className="mr-auto text-[12px] text-[var(--text-secondary)]">{t('providers.accounts.selected', { n: selected.size })}</span>
          <Button size="sm" icon={<Power className="size-3.5" />} disabled={bulkBusy} onClick={() => void bulk((a) => update.mutateAsync({ id: a.id, enabled: true }))}>
            {t('providers.subscription.enable')}
          </Button>
          <Button size="sm" disabled={bulkBusy} onClick={() => void bulk((a) => update.mutateAsync({ id: a.id, enabled: false }))}>
            {t('providers.subscription.disable')}
          </Button>
          <Button size="sm" disabled={bulkBusy} onClick={() => void bulk((a) => quota.mutateAsync(a.id))}>
            {t('providers.subscription.quotaRefresh')}
          </Button>
          <Button size="sm" className="text-[var(--danger)]" icon={<Trash2 className="size-3.5" />} disabled={bulkBusy} onClick={() => void bulkDelete()}>
            {t('common.delete')}
          </Button>
          <Button size="sm" variant="plain" onClick={() => setSelected(new Set())}>
            {t('providers.accounts.clearSelection')}
          </Button>
        </div>
      )}

      <div className="mt-5">
        <SubscriptionPolicy provider={provider} />
      </div>

      <SubscriptionAccountDetail
        provider={provider}
        account={detail}
        index={detail ? list.indexOf(detail) : 0}
        actions={actions}
        onClose={() => setDetailId(null)}
      />

      {info && (
        <AccountImportSheet
          providerId={provider.id}
          info={info}
          mode={importMode}
          providerProxy={provider.httpProxy}
          onClose={() => setImportMode(null)}
        />
      )}

      <Sheet
        open={proxyLoginOpen}
        onOpenChange={setProxyLoginOpen}
        title={t('providers.accounts.loginProxyTitle')}
        description={t('providers.accounts.loginProxyDesc')}
        footer={
          <Button variant="primary" loading={start.isPending} disabled={loginProxy.trim().length === 0} onClick={() => startLogin(undefined, loginProxy)}>
            {t('providers.accounts.loginProxyStart')}
          </Button>
        }
      >
        <Input
          mono
          label={t('providers.accounts.detail.proxy')}
          placeholder="socks5://127.0.0.1:1080"
          value={loginProxy}
          onChange={(e) => setLoginProxy(e.target.value)}
        />
      </Sheet>

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

/** The provider's account switching mode, inline in the pool bar (also on a plugin provider's accounts). */
export function SwitchModePicker({ policy, onChange }: { policy: Policy; onChange: (mode: Policy['switchMode']) => void }) {
  const { t } = useI18n();
  return (
    <Segmented
      ariaLabel={t('providers.subscription.policy.switchMode')}
      value={policy.switchMode}
      onChange={onChange}
      items={[
        { value: 'manual', label: t('providers.subscription.policy.manual') },
        { value: 'failover', label: t('providers.subscription.policy.failover') },
        { value: 'balanced', label: t('providers.subscription.policy.balanced') },
      ]}
    />
  );
}
