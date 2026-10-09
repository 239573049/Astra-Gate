import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ApiError } from '../api/client';
import { SubscriptionAccounts } from '../components/SubscriptionAccounts';
import type { Provider, ProviderAccount } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

// jsdom has no ResizeObserver; the account card's quota bars measure themselves on mount.
class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

const spies = vi.hoisted(() => ({
  start: vi.fn(),
  remove: vi.fn(),
  importCodex: vi.fn(),
  poll: vi.fn(),
  activate: vi.fn(),
  update: vi.fn(),
  reorder: vi.fn(),
  updatePolicy: vi.fn(),
  policy: { clientPolicy: 'claude-code-only', switchMode: 'manual', claudeSubscription: true, mimicClaudeCode: false } as {
    clientPolicy: 'claude-code-only' | 'any'; switchMode: 'manual' | 'failover'; claudeSubscription: boolean; mimicClaudeCode: boolean;
  },
  // Flipped per test: whether this machine already has a `codex login`.
  localCodex: { available: false } as { available: boolean; accountEmail?: string; plan?: string; detail?: string },
  importCopilot: vi.fn(),
  // Flipped per test: whether a local GitHub authorization is visible to the cheap probe.
  localCopilot: { available: false } as { available: boolean; source?: string; detail?: string },
  complete: vi.fn(),
}));

vi.mock('../api/hooks', () => ({
  keys: { providerAccounts: (id: string) => ['provider-accounts', id] as const },
  useProviderAccounts: () => ({ data: accounts, isLoading: false, refetch: vi.fn() }),
  useStartProviderLogin: () => ({ mutate: spies.start, isPending: false }),
  usePollProviderLogin: () => ({ mutateAsync: spies.poll, isPending: false }),
  useCompleteProviderLogin: () => ({ mutateAsync: spies.complete, isPending: false }),
  useRefreshProviderAccount: () => ({ mutate: vi.fn(), isPending: false, variables: undefined }),
  useDeleteProviderAccount: () => ({ mutate: spies.remove, isPending: false }),
  useFetchProviderAccountQuota: () => quotaSpies.fetch,
  useLocalCodexLogin: () => ({ data: spies.localCodex, refetch: vi.fn() }),
  useImportCodexAccount: () => ({ mutate: spies.importCodex, isPending: false }),
  useLocalCopilotLogin: () => ({ data: spies.localCopilot, refetch: vi.fn() }),
  useImportCopilotAccount: () => ({ mutate: spies.importCopilot, isPending: false }),
  useActivateProviderAccount: () => ({ mutate: spies.activate, isPending: false }),
  useUpdateProviderAccount: () => ({ mutate: spies.update, isPending: false }),
  useReorderProviderAccounts: () => ({ mutate: spies.reorder, isPending: false }),
  useSubscriptionPolicy: () => ({ data: spies.policy }),
  useUpdateSubscriptionPolicy: () => ({ mutate: spies.updatePolicy, isPending: false }),
  // Reset-credit cards are their own component; the account list only decides whether to render them.
  useResetCredits: () => ({ data: { credits: [], available_count: 0 }, isLoading: false, isError: false }),
  useConsumeResetCredit: () => ({ mutate: vi.fn(), isPending: false, variables: undefined }),
}));

const quotaSpies = vi.hoisted(() => ({
  fetch: { mutate: vi.fn(), isPending: false, variables: undefined },
}));

const accounts: ProviderAccount[] = [
  {
    id: 'acc-1', providerId: 'p1', displayName: 'me@example.com', accountEmail: 'me@example.com', plan: 'claude_pro',
    status: 'active', expiresAtUtc: '2026-10-06T13:00:00.000Z', lastRefreshAtUtc: null, createdAt: '2026-10-01T00:00:00.000Z',
    enabled: true, isCurrent: true, sortOrder: 0,
  },
  {
    id: 'acc-2', providerId: 'p1', displayName: 'other@example.com', accountEmail: 'other@example.com', plan: null,
    status: 'expired', expiresAtUtc: null, lastRefreshAtUtc: null, createdAt: '2026-10-02T00:00:00.000Z',
    enabled: true, isCurrent: false, sortOrder: 1,
  },
];

const subscriptionProvider = { id: 'p1', name: 'Claude 订阅', authScheme: 'oauth-subscription' } as unknown as Provider;
const plainProvider = { id: 'p2', name: 'Static', authScheme: 'bearer' } as unknown as Provider;

const show = (ui: React.ReactNode) => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>{ui}</FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  spies.start.mockReset();
  spies.remove.mockReset();
  spies.importCodex.mockReset();
  spies.importCopilot.mockReset();
  spies.complete.mockReset();
  spies.poll.mockReset();
  spies.activate.mockReset();
  spies.update.mockReset();
  spies.reorder.mockReset();
  spies.updatePolicy.mockReset();
  spies.policy = { clientPolicy: 'claude-code-only', switchMode: 'manual', claudeSubscription: true, mimicClaudeCode: false };
  spies.localCodex = { available: false };
  spies.localCopilot = { available: false };
  quotaSpies.fetch.mutate.mockReset();
  quotaSpies.fetch.isPending = false;
  quotaSpies.fetch.variables = undefined;
  vi.spyOn(window, 'open').mockImplementation(() => null);
});

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.restoreAllMocks();
});

describe('SubscriptionAccounts', () => {
  it('renders nothing for providers that are not subscription-backed', () => {
    const { container } = show(<SubscriptionAccounts provider={plainProvider} />);
    // The feedback provider keeps a toast portal alive; the section itself must be absent.
    expect(screen.queryByText('Subscription accounts')).not.toBeInTheDocument();
    expect(container.querySelector('.card')).toBeNull();
  });

  it('lists one card per account with identity, plan, status and per-account actions', () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);

    // 每个账号一张卡片：身份 + 套餐 + 状态 + 额度刷新按钮 + ⋯ 菜单
    expect(screen.getByText('me@example.com')).toBeInTheDocument();
    expect(screen.getByText('claude_pro')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText('Expired')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Refresh quota' })).toHaveLength(2);
    expect(screen.getAllByRole('button', { name: 'More' })).toHaveLength(2);
  });

  it('starts a PKCE login: opens the authorization page and waits for the callback', async () => {
    spies.start.mockImplementation((_vars: unknown, opts?: { onSuccess?: (r: unknown) => void }) =>
      opts?.onSuccess?.({ mode: 'pkce', state: 'st-1', authorizeUrl: 'https://claude.ai/oauth/authorize?x=1' }));

    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => expect(spies.start).toHaveBeenCalledTimes(1));
    expect(window.open).toHaveBeenCalledWith('https://claude.ai/oauth/authorize?x=1', '_blank', 'noopener');
    expect(screen.getByText('Waiting for authorization…')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: "I've finished authorizing" })).toBeInTheDocument();
  });

  it('paste flow: finishes the Claude login by handing the pasted code to the server', async () => {
    // Claude 的 redirect_uri 是它自己注册的非回环地址，所以授权页回不来——服务端让前端"粘回来"。
    spies.start.mockImplementation((_vars: unknown, opts?: { onSuccess?: (r: unknown) => void }) =>
      opts?.onSuccess?.({ mode: 'paste', state: 'st-p', authorizeUrl: 'https://claude.com/cai/oauth/authorize?code=true' }));
    spies.complete.mockResolvedValue({ status: 'done', account: accounts[0] });

    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByLabelText('Authorization code')).toBeInTheDocument();
    // 授权页上显示的是「授权码#state」，整段粘过来。
    fireEvent.change(screen.getByLabelText('Authorization code'), { target: { value: 'ac-1#st-9' } });
    fireEvent.click(screen.getByRole('button', { name: 'Finish sign-in' }));

    await waitFor(() => expect(spies.complete).toHaveBeenCalledWith({ state: 'st-p', code: 'ac-1#st-9' }));
    // 成功后按成功路径收尾（弹窗关闭）。
    await waitFor(() => expect(screen.queryByLabelText('Authorization code')).not.toBeInTheDocument());
  });

  it('device flow: shows the code and offers a manual "check now" that resolves the login', async () => {
    spies.start.mockImplementation((_vars: unknown, opts?: { onSuccess?: (r: unknown) => void }) =>
      opts?.onSuccess?.({
        mode: 'device', state: 'st-dev', userCode: 'ABCD-1234', verificationUrl: 'https://github.com/login/device', interval: 5,
      }));
    // 手动验证时服务端说已完成。
    spies.poll.mockResolvedValue({ status: 'done', account: accounts[0] });

    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText('ABCD-1234')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'I authorized — check now' }));

    await waitFor(() => expect(spies.poll).toHaveBeenCalledWith('st-dev'));
    await waitFor(() => expect(screen.queryByText('ABCD-1234')).not.toBeInTheDocument());
  });

  it('offers to import an existing local codex login for the ChatGPT subscription only', () => {
    spies.localCodex = { available: true, accountEmail: 'codex@example.com', plan: 'promax' };
    const codexProvider = {
      id: 'p3', name: 'ChatGPT 订阅（Codex）', authScheme: 'oauth-subscription', templateId: 'openai-subscription',
    } as unknown as Provider;

    show(<SubscriptionAccounts provider={codexProvider} />);

    // 找到本机登录态时显示导入入口与账号提示
    expect(screen.getByText(/Found a local Codex sign-in: codex@example\.com/)).toBeInTheDocument();
    const importButton = screen.getByRole('button', { name: /Import local Codex sign-in/ });
    fireEvent.click(importButton);
    expect(spies.importCodex).toHaveBeenCalledTimes(1);

    // 没有本机登录态就不显示这个入口
    cleanup();
    spies.localCodex = { available: false };
    show(<SubscriptionAccounts provider={codexProvider} />);
    expect(screen.queryByRole('button', { name: /Import local Codex sign-in/ })).toBeNull();

    // 非 ChatGPT 订阅（Claude）永远不显示这个入口
    cleanup();
    spies.localCodex = { available: true, accountEmail: 'codex@example.com' };
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    expect(screen.queryByRole('button', { name: /Import local Codex sign-in/ })).toBeNull();
  });

  it('offers to import a local GitHub authorization for the Copilot subscription only', () => {
    spies.localCopilot = { available: true, source: 'VS Code' };
    const copilotProvider = {
      id: 'p4',
      name: 'GitHub Copilot 订阅',
      authScheme: 'oauth-subscription',
      templateId: 'github-copilot-subscription',
    } as unknown as Provider;

    show(<SubscriptionAccounts provider={copilotProvider} />);

    expect(screen.getByText(/Found a local GitHub authorization: VS Code/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Import local GitHub authorization/ }));
    // 没有手填 token 时让服务端自己去探测本机来源。
    expect(spies.importCopilot).toHaveBeenCalledWith('', expect.anything());

    // 非 Copilot 订阅永远不显示这个入口。
    cleanup();
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    expect(screen.queryByRole('button', { name: /Import local GitHub authorization/ })).toBeNull();
  });

  it('passes a pasted GitHub token through when the machine has no discoverable one', () => {
    const copilotProvider = {
      id: 'p4',
      name: 'GitHub Copilot 订阅',
      authScheme: 'oauth-subscription',
      templateId: 'github-copilot-subscription',
    } as unknown as Provider;

    show(<SubscriptionAccounts provider={copilotProvider} />);
    // 探测拿不到东西（macOS 上的凭据只有用户点了才算数），按钮仍然在。
    expect(screen.getByText(/Import reads the VS Code entry/)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('GitHub token'), { target: { value: 'gho_pasted' } });
    fireEvent.click(screen.getByRole('button', { name: /Import local GitHub authorization/ }));
    expect(spies.importCopilot).toHaveBeenCalledWith('gho_pasted', expect.anything());
  });

  it('surfaces the coming-soon hint when the provider flow is not verified yet', async () => {
    spies.start.mockImplementation((_vars: unknown, opts?: { onError?: (e: unknown) => void }) =>
      opts?.onError?.(new ApiError('not verified', 400, { needsVerification: true })));

    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() =>
      expect(screen.getByText(/Coming soon: this subscription's sign-in flow/)).toBeInTheDocument());
  });

  it('asks for confirmation before signing an account out', async () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);

    // 退出登录在卡片的 ⋯ 菜单里（每个账号一个菜单）。菜单项必须带 detail 才能被 Radix 触发。
    // Radix 的 DropdownMenu 在 jsdom 里要靠 pointerdown 才展开。
    fireEvent.pointerDown(screen.getAllByRole('button', { name: 'More' })[0]!, { button: 0, ctrlKey: false });
    const item = (await screen.findAllByRole('menuitem')).find((i) => i.textContent?.includes('Sign out & delete'))!;
    fireEvent.click(item, { detail: 1 });
    expect(await screen.findByText(/Sign out account "me@example\.com"\?/)).toBeInTheDocument();

    // The sheet's confirm button (same label) is appended last.
    const confirmButtons = screen.getAllByRole('button', { name: 'Sign out & delete' });
    fireEvent.click(confirmButtons[confirmButtons.length - 1]);
    await waitFor(() => expect(spies.remove).toHaveBeenCalledWith('acc-1', expect.anything()));
  });

  it('marks the account in use and switches to another one from its menu', async () => {
    const second = accounts[1]!;
    accounts[1] = { ...second, status: 'active' };
    try {
      show(<SubscriptionAccounts provider={subscriptionProvider} />);
      expect(screen.getByText('In use')).toBeInTheDocument();

      fireEvent.pointerDown(screen.getAllByRole('button', { name: 'More' })[1]!, { button: 0, ctrlKey: false });
      const item = (await screen.findAllByRole('menuitem')).find((i) => i.textContent?.includes('Switch to this account'))!;
      fireEvent.click(item, { detail: 1 });
      await waitFor(() => expect(spies.activate).toHaveBeenCalledWith('acc-2', expect.anything()));
    } finally {
      accounts[1] = second;
    }
  });

  it('moves an account down in the failover order', async () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);

    fireEvent.pointerDown(screen.getAllByRole('button', { name: 'More' })[0]!, { button: 0, ctrlKey: false });
    const item = (await screen.findAllByRole('menuitem')).find((i) => i.textContent?.includes('Move down'))!;
    fireEvent.click(item, { detail: 1 });
    await waitFor(() => expect(spies.reorder).toHaveBeenCalledWith(['acc-2', 'acc-1'], expect.anything()));
  });

  it('shows the Claude Code only policy and warns once it is lifted', () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    expect(screen.getByText('Claude Code only')).toBeInTheDocument();
    expect(screen.queryByText(/does not impersonate Claude Code/)).toBeNull();

    fireEvent.click(screen.getByRole('switch', { name: 'Claude Code only' }));
    expect(spies.updatePolicy).toHaveBeenCalledWith({ clientPolicy: 'any' }, expect.anything());

    cleanup();
    spies.policy = { clientPolicy: 'any', switchMode: 'failover', claudeSubscription: true, mimicClaudeCode: false };
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    expect(screen.getByText(/does not impersonate Claude Code/)).toBeInTheDocument();

    // Other subscriptions have no client policy row.
    cleanup();
    spies.policy = { clientPolicy: 'any', switchMode: 'manual', claudeSubscription: false, mimicClaudeCode: false };
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    expect(screen.queryByText('Claude Code only')).toBeNull();
    expect(screen.getByText('Account switching')).toBeInTheDocument();
  });
});
