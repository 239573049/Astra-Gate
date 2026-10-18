import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ApiError } from '../api/client';
import { countImportEntries } from '../components/AccountImportSheet';
import { SubscriptionAccounts } from '../components/SubscriptionAccounts';
import type { AccountImportInfo, Provider, ProviderAccount, ProviderUsage } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

// jsdom has no ResizeObserver; menus and quota bars measure themselves on mount.
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
  updateAsync: vi.fn(),
  reorder: vi.fn(),
  updatePolicy: vi.fn(),
  importAccounts: vi.fn(),
  policy: { clientPolicy: 'claude-code-only', switchMode: 'manual', claudeSubscription: true, mimicClaudeCode: false } as {
    clientPolicy: 'claude-code-only' | 'any'; switchMode: 'manual' | 'failover' | 'balanced'; claudeSubscription: boolean; mimicClaudeCode: boolean;
  },
  // Flipped per test: whether this machine already has a `codex login`.
  localCodex: { available: false } as { available: boolean; accountEmail?: string; plan?: string; detail?: string },
  importCopilot: vi.fn(),
  // Flipped per test: whether a local GitHub authorization is visible to the cheap probe.
  localCopilot: { available: false } as { available: boolean; source?: string; detail?: string },
  importInfo: { supported: true, apiKey: false, formats: ['claude-refresh-token', 'claude-setup-token'], maxBatch: 200 } as AccountImportInfo,
  complete: vi.fn(),
}));

vi.mock('../api/hooks', () => ({
  keys: { providerAccounts: (id: string) => ['provider-accounts', id] as const },
  useProviderAccounts: () => ({ data: accounts, isLoading: false, refetch: vi.fn() }),
  useProviderUsage: () => ({ data: accountUsage, isError: false }),
  useStartProviderLogin: () => ({ mutate: spies.start, isPending: false }),
  usePollProviderLogin: () => ({ mutateAsync: spies.poll, isPending: false }),
  useCompleteProviderLogin: () => ({ mutateAsync: spies.complete, isPending: false }),
  useRefreshProviderAccount: () => ({ mutate: vi.fn(), isPending: false, variables: undefined }),
  useDeleteProviderAccount: () => ({ mutate: spies.remove, mutateAsync: vi.fn(), isPending: false }),
  useFetchProviderAccountQuota: () => quotaSpies.fetch,
  useLocalCodexLogin: () => ({ data: spies.localCodex, refetch: vi.fn() }),
  useImportCodexAccount: () => ({ mutate: spies.importCodex, isPending: false }),
  useLocalCopilotLogin: () => ({ data: spies.localCopilot, refetch: vi.fn() }),
  useImportCopilotAccount: () => ({ mutate: spies.importCopilot, isPending: false }),
  useAccountImportInfo: () => ({ data: spies.importInfo }),
  useImportAccounts: () => ({ mutate: spies.importAccounts, isPending: false, variables: undefined }),
  useActivateProviderAccount: () => ({ mutate: spies.activate, isPending: false }),
  useUpdateProviderAccount: () => ({ mutate: spies.update, mutateAsync: spies.updateAsync, isPending: false, variables: undefined }),
  useReorderProviderAccounts: () => ({ mutate: spies.reorder, isPending: false }),
  useSubscriptionPolicy: () => ({ data: spies.policy }),
  useUpdateSubscriptionPolicy: () => ({ mutate: spies.updatePolicy, isPending: false }),
  // Reset-credit cards are their own component; the detail sheet only decides whether to render them.
  useResetCredits: () => ({ data: { credits: [], available_count: 0 }, isLoading: false, isError: false }),
  useConsumeResetCredit: () => ({ mutate: vi.fn(), isPending: false, variables: undefined }),
}));

const quotaSpies = vi.hoisted(() => ({
  fetch: { mutate: vi.fn(), mutateAsync: vi.fn(), isPending: false, variables: undefined },
}));

const accounts: ProviderAccount[] = [
  {
    id: 'acc-1', providerId: 'p1', displayName: 'me@example.com', accountEmail: 'me@example.com', plan: 'claude_pro',
    status: 'active', expiresAtUtc: '2026-10-06T13:00:00.000Z', lastRefreshAtUtc: null, createdAt: '2026-10-01T00:00:00.000Z',
    enabled: true, isCurrent: true, sortOrder: 0, kind: 'oauth', source: 'login',
    quota: { fetchedAtUtc: new Date().toISOString(), session: { usedPercent: 62, windowMinutes: 300 }, weekly: { usedPercent: 21 } },
  },
  {
    id: 'acc-2', providerId: 'p1', displayName: 'other@example.com', accountEmail: 'other@example.com', plan: null,
    status: 'expired', expiresAtUtc: null, lastRefreshAtUtc: null, createdAt: '2026-10-02T00:00:00.000Z',
    enabled: true, isCurrent: false, sortOrder: 1, kind: 'oauth', source: 'batch',
  },
];

const subscriptionProvider = { id: 'p1', name: 'Claude 订阅', authScheme: 'oauth-subscription', httpProxy: null } as unknown as Provider;
const plainProvider = { id: 'p2', name: 'Static', authScheme: 'bearer' } as unknown as Provider;

const accountUsage: ProviderUsage = {
  day: '2026-10-18', timeZone: 'Asia/Shanghai',
  accounts: [
    { accountId: 'acc-1', today: { requests: 3, inputTokens: 100, outputTokens: 50, tokens: 150, costNanoUsd: 5_000_000 }, total: { requests: 12, inputTokens: 1000, outputTokens: 500, tokens: 1500, costNanoUsd: 250_000_000 } },
    { accountId: 'acc-2', today: { requests: 0, inputTokens: 0, outputTokens: 0, tokens: 0, costNanoUsd: 0 }, total: { requests: 4, inputTokens: 2000, outputTokens: 1000, tokens: 3000, costNanoUsd: 750_000_000 } },
  ],
};

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

/** Opens a Radix dropdown (needs pointerdown in jsdom) and picks the item; items need `detail` to fire. */
async function pick(trigger: HTMLElement, label: string | RegExp) {
  fireEvent.pointerDown(trigger, { button: 0, ctrlKey: false });
  const item = (await screen.findAllByRole('menuitem')).find((i) =>
    typeof label === 'string' ? i.textContent?.includes(label) : label.test(i.textContent ?? ''))!;
  fireEvent.click(item, { detail: 1 });
}

const addAccount = (label: string | RegExp) => pick(screen.getByRole('button', { name: /Add account/ }), label);

beforeEach(() => {
  for (const spy of [spies.start, spies.remove, spies.importCodex, spies.importCopilot, spies.importAccounts, spies.complete,
    spies.poll, spies.activate, spies.update, spies.updateAsync, spies.reorder, spies.updatePolicy])
    spy.mockReset();
  spies.updateAsync.mockResolvedValue([]);
  spies.policy = { clientPolicy: 'claude-code-only', switchMode: 'manual', claudeSubscription: true, mimicClaudeCode: false };
  spies.localCodex = { available: false };
  spies.localCopilot = { available: false };
  spies.importInfo = { supported: true, apiKey: false, formats: ['claude-refresh-token', 'claude-setup-token'], maxBatch: 200 };
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
    show(<SubscriptionAccounts provider={plainProvider} />);
    expect(screen.queryByTestId('account-card')).toBeNull();
    expect(screen.queryByRole('button', { name: /Add account/ })).toBeNull();
  });

  it('shows one compact card per account with identity, plan, state and its quota bars', () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);

    const cards = screen.getAllByTestId('account-card');
    expect(cards).toHaveLength(2);
    const [first, second] = cards as [HTMLElement, HTMLElement];
    expect(within(first).getByText('me@example.com')).toBeInTheDocument();
    expect(within(first).getByText('claude_pro')).toBeInTheDocument();
    expect(within(first).getByText('In use')).toBeInTheDocument();
    expect(within(first).getByRole('progressbar', { name: '5h' })).toHaveAttribute('aria-valuenow', '62');
    expect(within(first).getByRole('progressbar', { name: 'Week' })).toHaveAttribute('aria-valuenow', '21');
    // A dead login swaps its quota row for a re-login button.
    expect(within(second).getByRole('img', { name: 'Expired' })).toBeInTheDocument();
    expect(within(second).getByRole('button', { name: 'Sign in again' })).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'More' })).toHaveLength(2);

    // The pool bar counts the accounts by state.
    expect(screen.getByText('2 accounts')).toBeInTheDocument();
    expect(screen.getByText('1 usable')).toBeInTheDocument();
    expect(screen.getByText('1 signed out')).toBeInTheDocument();
  });

  it('shows each account\'s own today and total usage on its card and in its detail', () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    const [first, second] = screen.getAllByTestId('account-card');
    const usage = within(first).getByRole('table', { name: 'Request usage' });
    expect(within(usage).getByRole('columnheader', { name: 'Today' })).toBeInTheDocument();
    expect(within(usage).getByRole('columnheader', { name: 'Total' })).toBeInTheDocument();
    expect(within(usage).getByRole('row', { name: 'Requests 3 12' })).toBeInTheDocument();
    expect(within(usage).getByRole('row', { name: 'Tokens 150 1,500' })).toBeInTheDocument();
    expect(within(usage).getByRole('row', { name: 'Cost $0.005 $0.25' })).toBeInTheDocument();
    expect(within(second).getByRole('row', { name: 'Requests 0 4' })).toBeInTheDocument();
    fireEvent.click(first);
    const detail = screen.getByRole('dialog');
    expect(within(detail).getByRole('row', { name: 'Requests 3 12' })).toBeInTheDocument();
    expect(within(detail).getByText(/Asia\/Shanghai/)).toBeInTheDocument();
  });

  it('filters by state and searches by name', () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);

    fireEvent.click(screen.getByRole('button', { name: /Signed out/ }));
    expect(screen.getAllByTestId('account-card')).toHaveLength(1);
    expect(screen.getByText('other@example.com')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /^All/ }));
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'ME@' } });
    expect(screen.getAllByTestId('account-card')).toHaveLength(1);
    expect(screen.getByText('me@example.com')).toBeInTheDocument();
  });

  it('starts a PKCE login from the add menu: opens the authorization page and waits for the callback', async () => {
    spies.start.mockImplementation((_vars: unknown, opts?: { onSuccess?: (r: unknown) => void }) =>
      opts?.onSuccess?.({ mode: 'pkce', state: 'st-1', authorizeUrl: 'https://claude.ai/oauth/authorize?x=1' }));

    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    await addAccount('Sign in with browser');

    await waitFor(() => expect(spies.start).toHaveBeenCalledTimes(1));
    expect(spies.start.mock.calls[0]![0]).toEqual({ accountId: undefined, httpProxy: undefined });
    expect(window.open).toHaveBeenCalledWith('https://claude.ai/oauth/authorize?x=1', '_blank', 'noopener');
    expect(screen.getByText('Waiting for authorization…')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: "I've finished authorizing" })).toBeInTheDocument();
  });

  it('signs a new account in through its own proxy', async () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    await addAccount('Sign in through a proxy');

    fireEvent.change(await screen.findByLabelText('Account proxy'), { target: { value: 'socks5://127.0.0.1:1080' } });
    fireEvent.click(screen.getByRole('button', { name: 'Start sign-in' }));
    expect(spies.start).toHaveBeenCalledWith({ accountId: undefined, httpProxy: 'socks5://127.0.0.1:1080' }, expect.anything());
  });

  it('paste flow: finishes the Claude login by handing the pasted code to the server', async () => {
    // Claude 的 redirect_uri 是它自己注册的非回环地址，所以授权页回不来——服务端让前端"粘回来"。
    spies.start.mockImplementation((_vars: unknown, opts?: { onSuccess?: (r: unknown) => void }) =>
      opts?.onSuccess?.({ mode: 'paste', state: 'st-p', authorizeUrl: 'https://claude.com/cai/oauth/authorize?code=true' }));
    spies.complete.mockResolvedValue({ status: 'done', account: accounts[0] });

    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    await addAccount('Sign in with browser');

    expect(await screen.findByLabelText('Authorization code')).toBeInTheDocument();
    // 授权页上显示的是「授权码#state」，整段粘过来。
    fireEvent.change(screen.getByLabelText('Authorization code'), { target: { value: 'ac-1#st-9' } });
    fireEvent.click(screen.getByRole('button', { name: 'Finish sign-in' }));

    await waitFor(() => expect(spies.complete).toHaveBeenCalledWith({ state: 'st-p', code: 'ac-1#st-9' }));
    await waitFor(() => expect(screen.queryByLabelText('Authorization code')).not.toBeInTheDocument());
  });

  it('device flow: shows the code and offers a manual "check now" that resolves the login', async () => {
    spies.start.mockImplementation((_vars: unknown, opts?: { onSuccess?: (r: unknown) => void }) =>
      opts?.onSuccess?.({
        mode: 'device', state: 'st-dev', userCode: 'ABCD-1234', verificationUrl: 'https://github.com/login/device', interval: 5,
      }));
    spies.poll.mockResolvedValue({ status: 'done', account: accounts[0] });

    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    await addAccount('Sign in with browser');

    expect(await screen.findByText('ABCD-1234')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'I authorized — check now' }));

    await waitFor(() => expect(spies.poll).toHaveBeenCalledWith('st-dev'));
    await waitFor(() => expect(screen.queryByText('ABCD-1234')).not.toBeInTheDocument());
  });

  it('re-logs a signed-out account in from its card', () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in again' }));
    expect(spies.start).toHaveBeenCalledWith({ accountId: 'acc-2', httpProxy: undefined }, expect.anything());
  });

  it('offers to import an existing local codex login for the ChatGPT subscription only', async () => {
    spies.localCodex = { available: true, accountEmail: 'codex@example.com', plan: 'promax' };
    const codexProvider = {
      id: 'p3', name: 'ChatGPT 订阅（Codex）', authScheme: 'oauth-subscription', templateId: 'openai-subscription',
    } as unknown as Provider;

    show(<SubscriptionAccounts provider={codexProvider} />);
    expect(screen.getByText(/Found a local Codex sign-in: codex@example\.com/)).toBeInTheDocument();
    await addAccount('Import local Codex sign-in');
    expect(spies.importCodex).toHaveBeenCalledTimes(1);

    // 非 ChatGPT 订阅（Claude）永远不显示这个入口
    cleanup();
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    fireEvent.pointerDown(screen.getByRole('button', { name: /Add account/ }), { button: 0, ctrlKey: false });
    const items = await screen.findAllByRole('menuitem');
    expect(items.some((i) => i.textContent?.includes('Import local Codex sign-in'))).toBe(false);
  });

  it('offers to import a local GitHub authorization for the Copilot subscription', async () => {
    spies.localCopilot = { available: true, source: 'VS Code' };
    const copilotProvider = {
      id: 'p4', name: 'GitHub Copilot 订阅', authScheme: 'oauth-subscription', templateId: 'github-copilot-subscription',
    } as unknown as Provider;

    show(<SubscriptionAccounts provider={copilotProvider} />);
    expect(screen.getByText(/Found a local GitHub authorization: VS Code/)).toBeInTheDocument();
    await addAccount('Import local GitHub authorization');
    // 没有手填 token 时让服务端自己去探测本机来源。
    expect(spies.importCopilot).toHaveBeenCalledWith('', expect.anything());
  });

  it('batch-imports pasted tokens: previews first, then imports and lists every outcome', async () => {
    spies.importAccounts.mockImplementation((req: { dryRun?: boolean }, opts?: { onSuccess?: (r: unknown) => void }) =>
      opts?.onSuccess?.(req.dryRun
        ? {
            imported: 2, duplicates: 0, failed: 1,
            items: [
              { index: 1, source: 'sk-a••••aaaa', status: 'ready', kind: 'refresh_token' },
              { index: 2, source: 'sk-a••••bbbb', status: 'ready', kind: 'setup_token' },
              { index: 3, source: '••••', status: 'invalid_format', message: 'Not a Claude token' },
            ],
          }
        : {
            imported: 1, duplicates: 0, failed: 1,
            items: [
              { index: 1, source: 'sk-a••••aaaa', status: 'imported', kind: 'refresh_token', account: { ...accounts[0], displayName: 'new@example.com' } },
              { index: 2, source: 'sk-a••••bbbb', status: 'failed', kind: 'setup_token', message: 'Token expired' },
            ],
          }));

    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    fireEvent.click(screen.getByRole('button', { name: 'Batch import' }));

    const box = await screen.findByLabelText('Tokens to import (one per line)');
    expect(screen.getByText(/refresh token \(sk-ant-ort01-…\)/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Import' })).toBeDisabled();
    const text = '# mine\nsk-ant-ort01-aaa\n\n"sk-ant-oat01-bbb", junk';
    fireEvent.change(box, { target: { value: text } });
    expect(screen.getByText('3 entries recognised')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Account proxy (optional)'), { target: { value: 'http://127.0.0.1:7890' } });
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));
    expect(spies.importAccounts).toHaveBeenCalledWith({ text, asApiKey: undefined, dryRun: true, httpProxy: 'http://127.0.0.1:7890' }, expect.anything());
    expect(await screen.findByText('2 ready, 0 duplicates, 1 unrecognised')).toBeInTheDocument();
    expect(screen.getAllByText('Ready')).toHaveLength(2);
    expect(screen.getByText('setup-token')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Import' }));
    expect(spies.importAccounts).toHaveBeenLastCalledWith({ text, asApiKey: undefined, dryRun: undefined, httpProxy: 'http://127.0.0.1:7890' }, expect.anything());
    expect(await screen.findByText('new@example.com')).toBeInTheDocument();
    expect(screen.getByText('Token expired')).toBeInTheDocument();
    // A failure keeps the pasted text so it can be fixed and retried.
    expect(screen.getByLabelText('Tokens to import (one per line)')).toHaveValue(text);
  });

  it('adds API keys only where the subscription takes them, and hides import where it has none', async () => {
    spies.importInfo = { supported: true, apiKey: true, formats: ['kimi-api-key', 'refresh-token'], maxBatch: 200 };
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    await addAccount('Add API key');

    const box = await screen.findByLabelText('API keys (one per line)');
    expect(screen.getByText(/Kimi Code API key/)).toBeInTheDocument();
    fireEvent.change(box, { target: { value: 'sk-kimi-123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Import' }));
    expect(spies.importAccounts).toHaveBeenCalledWith({ text: 'sk-kimi-123', asApiKey: true, dryRun: undefined, httpProxy: undefined }, expect.anything());

    cleanup();
    spies.importInfo = { supported: false, apiKey: false, formats: [], maxBatch: 200 };
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    expect(screen.queryByRole('button', { name: 'Batch import' })).toBeNull();
    fireEvent.pointerDown(screen.getByRole('button', { name: /Add account/ }), { button: 0, ctrlKey: false });
    const items = (await screen.findAllByRole('menuitem')).map((i) => i.textContent);
    expect(items.some((i) => i?.includes('Paste a token'))).toBe(false);
    expect(items.some((i) => i?.includes('Add API key'))).toBe(false);
  });

  it('switches the account switching mode from the pool bar', () => {
    spies.policy = { clientPolicy: 'any', switchMode: 'balanced', claudeSubscription: false, mimicClaudeCode: false };
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    expect(screen.getByText(/Spreads requests over all usable accounts/)).toBeInTheDocument();

    fireEvent.click(screen.getByText('Automatic failover'));
    expect(spies.updatePolicy).toHaveBeenCalledWith({ switchMode: 'failover' }, expect.anything());
  });

  it('surfaces the coming-soon hint when the provider flow is not verified yet', async () => {
    spies.start.mockImplementation((_vars: unknown, opts?: { onError?: (e: unknown) => void }) =>
      opts?.onError?.(new ApiError('not verified', 400, { needsVerification: true })));

    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    await addAccount('Sign in with browser');

    await waitFor(() =>
      expect(screen.getByText(/Coming soon: this subscription's sign-in flow/)).toBeInTheDocument());
  });

  it('asks for confirmation before signing an account out', async () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    await pick(screen.getAllByRole('button', { name: 'More' })[0]!, 'Sign out & delete');
    expect(await screen.findByText(/Sign out account "me@example\.com"\?/)).toBeInTheDocument();

    // The sheet's confirm button (same label) is appended last.
    const confirmButtons = screen.getAllByRole('button', { name: 'Sign out & delete' });
    fireEvent.click(confirmButtons[confirmButtons.length - 1]!);
    await waitFor(() => expect(spies.remove).toHaveBeenCalledWith('acc-1', expect.anything()));
  });

  it('switches to another account from its menu', async () => {
    const second = accounts[1]!;
    accounts[1] = { ...second, status: 'active' };
    try {
      show(<SubscriptionAccounts provider={subscriptionProvider} />);
      await pick(screen.getAllByRole('button', { name: 'More' })[1]!, 'Switch to this account');
      await waitFor(() => expect(spies.activate).toHaveBeenCalledWith('acc-2', expect.anything()));
    } finally {
      accounts[1] = second;
    }
  });

  it('moves an account down in the failover order', async () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    await pick(screen.getAllByRole('button', { name: 'More' })[0]!, 'Move down');
    await waitFor(() => expect(spies.reorder).toHaveBeenCalledWith(['acc-2', 'acc-1'], expect.anything()));
  });

  it('selects several accounts and disables them in one go', async () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select me@example.com' }));
    expect(screen.getByText('1 selected')).toBeInTheDocument();
    // While selecting, clicking a card toggles it instead of opening its details.
    fireEvent.click(screen.getByText('other@example.com'));
    expect(screen.getByText('2 selected')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Disable' }));
    await waitFor(() => expect(spies.updateAsync).toHaveBeenCalledTimes(2));
    expect(spies.updateAsync).toHaveBeenCalledWith({ id: 'acc-1', enabled: false });
    expect(spies.updateAsync).toHaveBeenCalledWith({ id: 'acc-2', enabled: false });
    await waitFor(() => expect(screen.queryByText(/selected$/)).toBeNull());
  });

  it('opens the details of an account and saves its own proxy', async () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    fireEvent.click(screen.getByText('me@example.com'));

    const proxy = await screen.findByLabelText('Account proxy');
    expect(screen.getByText(/empty follows the provider \(system proxy\)/)).toBeInTheDocument();
    fireEvent.change(proxy, { target: { value: 'http://10.0.0.2:3128' } });
    const saves = screen.getAllByRole('button', { name: 'Save' });
    fireEvent.click(saves[saves.length - 1]!);
    expect(spies.update).toHaveBeenCalledWith({ id: 'acc-1', httpProxy: 'http://10.0.0.2:3128' }, expect.anything());
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

    // Other subscriptions have no client policy block; the switching mode stays in the pool bar.
    cleanup();
    spies.policy = { clientPolicy: 'any', switchMode: 'manual', claudeSubscription: false, mimicClaudeCode: false };
    show(<SubscriptionAccounts provider={subscriptionProvider} />);
    expect(screen.queryByText('Claude Code only')).toBeNull();
    expect(screen.getByRole('group', { name: 'Account switching' })).toBeInTheDocument();
  });
});

describe('countImportEntries', () => {
  it('counts like the server splits: lines, separators, JSON documents and JSON lines', () => {
    expect(countImportEntries('')).toBe(0);
    expect(countImportEntries('# only\n\n')).toBe(0);
    expect(countImportEntries('a1\n"b2", c3; d4')).toBe(4);
    expect(countImportEntries('tid=abc;exp=1;sku=x\ngho_1')).toBe(2);
    expect(countImportEntries('{\n  "claudeAiOauth": {"refreshToken": "x"}\n}')).toBe(1);
    expect(countImportEntries('[{"a":1}, "tok", ""]')).toBe(2);
    expect(countImportEntries('{"a":1}\n{"b":2}\ntok')).toBe(3);
  });
});
