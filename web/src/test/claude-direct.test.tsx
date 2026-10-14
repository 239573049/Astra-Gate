import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ClaudeDirectAccount, ClaudeDirectRequest, ClientInfo, Provider } from '../api/types';
import { ClaudeDirectPanel, claudeDirectTotals } from '../components/ClaudeDirectPanel';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';
import { ClientsPage } from '../pages/ClientsPage';

// ---------- service-boundary mocks (the panel and ClientsPage stay real) ----------

const hookSpies = vi.hoisted(() => ({
  state: { data: undefined as unknown, isLoading: false, isError: false, error: null as unknown },
  requests: { data: undefined as unknown, isLoading: false, isError: false, error: null as unknown },
  select: vi.fn(),
  requestsEnabled: [] as (string | null | undefined)[],
  requestsLive: [] as boolean[],
  enabled: vi.fn(),
  /** Overrides the page's client list for one test; reset before each. */
  clients: undefined as ClientInfo[] | undefined,
  repair: vi.fn(),
}));

const directState = (over: Record<string, unknown> = {}) => ({
  mode: 'gateway',
  accountId: null as string | null,
  accounts: [] as ClaudeDirectAccount[],
  ...over,
});

const account = (over: Partial<ClaudeDirectAccount> = {}): ClaudeDirectAccount => ({
  id: 'a'.repeat(32),
  name: 'Claude Pro',
  email: 'me@example.com',
  plan: 'Pro',
  status: 'active',
  enabled: true,
  telemetryEnabled: false,
  ...over,
});

vi.mock('../api/hooks', () => ({
  keys: {
    clients: ['clients'] as const,
    claudeDirect: ['claude-direct'] as const,
    claudeDirectRequests: (id: string) => ['claude-direct-requests', id] as const,
    clientModels: (kind: string) => ['client-models', kind] as const,
    clientInstallJob: (kind: string) => ['client-install-job', kind] as const,
    backups: (kind: string) => ['backups', kind] as const,
  },
  useClaudeDirect: (enabled: boolean) => {
    hookSpies.enabled(enabled);
    return hookSpies.state;
  },
  useClaudeDirectRequests: (id: string | null | undefined, live: boolean) => {
    hookSpies.requestsEnabled.push(id);
    hookSpies.requestsLive.push(live);
    return hookSpies.requests;
  },
  useSelectClaudeDirect: () => ({ mutate: hookSpies.select, isPending: false }),
  useClients: () => ({ data: hookSpies.clients ?? clients, isLoading: false, isError: false, error: null }),
  useProviders: () => ({ data: providers, isLoading: false, isError: false, error: null }),
  useClientModels: () => ({ data: ['claude-sonnet-4-5'], isLoading: false }),
  useSetBinding: () => ({ mutate: vi.fn(), isPending: false }),
  useSetBindings: () => ({ mutate: vi.fn(), isPending: false }),
  useDisableClient: () => ({ mutate: vi.fn(), isPending: false }),
  useForceRestore: () => ({ mutate: vi.fn(), isPending: false }),
  useRepairClientConfig: () => ({ mutate: hookSpies.repair, isPending: false }),
  useTokens: () => ({ data: [], isLoading: false }),
  useProviderAccounts: () => ({ data: [], isLoading: false }),
  useBackups: () => ({ data: [], isLoading: false, isError: false }),
  usePreviewEnable: () => ({ mutate: vi.fn(), isPending: false, isError: false, data: undefined }),
  useEnableClient: () => ({ mutate: vi.fn(), isPending: false }),
  useRestoreBackup: () => ({ mutate: vi.fn(), isPending: false, variables: undefined }),
  useClientInstallJob: () => ({ data: undefined, isLoading: false }),
  useStartClientInstall: () => ({ mutate: vi.fn(), isPending: false }),
  useCancelClientInstall: () => ({ mutate: vi.fn(), isPending: false }),
  useCheckClientUpdates: () => ({ mutate: vi.fn(), isPending: false }),
}));

// The real @lobehub/icons barrel transitively imports JSON with import attributes that Node ESM rejects;
// icons.tsx only needs a brand component per provider.
vi.mock('@lobehub/icons', () => {
  const stub = (name: string) => {
    const mark = (variant: string) => (props: { size?: number }) => (
      <svg data-testid={`lobe-${name}`} data-variant={variant} data-size={props.size} />
    );
    return Object.assign(mark('mono'), { Avatar: mark('avatar'), Color: mark('color') });
  };
  return {
    Anthropic: stub('anthropic'),
    Claude: stub('claude'),
    ClaudeCode: stub('claude-code'),
    Codex: stub('codex'),
    DeepSeek: stub('deepseek'),
    Gemini: stub('gemini'),
    GeminiCLI: stub('gemini-cli'),
    GithubCopilot: stub('github-copilot'),
    Grok: stub('grok'),
    HermesAgent: stub('hermes-agent'),
    Kimi: stub('kimi'),
    LmStudio: stub('lm-studio'),
    Qoder: stub('qoder'),
    Meta: stub('meta'),
    Minimax: stub('minimax'),
    Moonshot: stub('moonshot'),
    Ollama: stub('ollama'),
    OpenAI: stub('openai'),
    OpenCode: stub('opencode'),
    OpenRouter: stub('openrouter'),
    Pi: stub('pi'),
    Qwen: stub('qwen'),
    SiliconCloud: stub('siliconcloud'),
    Volcengine: stub('volcengine'),
    XAI: stub('xai'),
    XiaomiMiMo: stub('xiaomi-mimo'),
    Baichuan: stub('baichuan'),
    ChatGLM: stub('chatglm'),
    Cohere: stub('cohere'),
    Doubao: stub('doubao'),
    Gemma: stub('gemma'),
    Hunyuan: stub('hunyuan'),
    LongCat: stub('longcat'),
    Mistral: stub('mistral'),
    Stepfun: stub('stepfun'),
    Wenxin: stub('wenxin'),
    Yi: stub('yi'),
    Zhipu: stub('zhipu'),
  };
});

vi.mock('../components/layout/AppShell', () => ({
  useShellLayout: () => ({
    sidebarMode: 'expanded',
    sidebarWidth: 224,
    drawerOpen: false,
    setDrawerOpen: () => {},
    toggleSidebar: () => {},
    narrow: false,
  }),
  TOOLBAR_RIGHT_RESERVE: 0,
}));

// Reduced motion keeps every transition at duration 0, so tests never race an animation.
vi.mock('motion/react', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useReducedMotion: () => true,
}));

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

// Radix' select trigger only opens on a mouse pointerdown; jsdom never sets pointerType.
const openSelect = async (name: string) => {
  fireEvent.pointerDown(screen.getByRole('combobox', { name }), { button: 0, ctrlKey: false, pointerType: 'mouse' });
  return await screen.findAllByRole('option');
};

// ---------- fixtures ----------

const claudeCode = (over: Partial<ClientInfo> = {}): ClientInfo =>
  ({
    kind: 'claude-code',
    name: 'Claude Code',
    protocol: 'anthropic',
    availability: 'available',
    mode: 'switch',
    detection: { installed: true, configExists: true, version: '2.0.0', configPaths: ['/Users/me/.claude/settings.json'] },
    status: 'disabled',
    enabled: false,
    extras: {},
    warnings: [],
    requiresRestart: false,
    install: undefined,
    ...over,
  }) as unknown as ClientInfo;

const codex = (over: Partial<ClientInfo> = {}): ClientInfo =>
  ({
    ...claudeCode(),
    kind: 'codex',
    name: 'Codex',
    protocol: 'openai-responses',
    detection: { installed: true, configExists: true, version: '1.0.0', configPaths: ['/Users/me/.codex/config.toml'] },
    ...over,
  }) as unknown as ClientInfo;

const clients: ClientInfo[] = [codex(), claudeCode()];
const providers: Provider[] = [];

const request = (over: Partial<ClaudeDirectRequest> = {}): ClaudeDirectRequest => ({
  id: 'r1',
  profileId: 'a'.repeat(32),
  sessionId: 's1',
  accountUuid: '0e1d8f3a-0000-4000-8000-000000000001',
  model: 'claude-sonnet-4-5',
  occurredAtUtc: '2026-10-10T08:00:00Z',
  status: 'success',
  inputTokens: 1000,
  outputTokens: 250,
  cacheReadTokens: 4000,
  cacheCreationTokens: 500,
  durationMs: 1200,
  estimatedCostNanoUsd: 1_500_000_000,
  ...over,
});

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

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

/** The panel on its own, inside a router: it links out to the providers page when no account exists yet. */
const showPanel = (ui: React.ReactNode) => show(<MemoryRouter initialEntries={['/clients/claude-code']}>{ui}</MemoryRouter>);

/** The page reads its client from the route; '/clients' falls back to codex, '/clients/claude-code' selects it. */
const showPage = (path = '/clients/claude-code') =>
  show(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/clients" element={<ClientsPage />} />
        <Route path="/clients/:kind" element={<ClientsPage />} />
      </Routes>
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', NoopResizeObserver);
  // Radix' select probes pointer capture and scrolls the current item into view; jsdom implements neither.
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
  Element.prototype.scrollIntoView = () => {};
  hookSpies.state = { data: directState(), isLoading: false, isError: false, error: null };
  hookSpies.requests = { data: [], isLoading: false, isError: false, error: null };
  hookSpies.select.mockClear();
  hookSpies.enabled.mockClear();
  hookSpies.clients = undefined;
  hookSpies.repair.mockClear();
  hookSpies.requestsEnabled.length = 0;
  hookSpies.requestsLive.length = 0;
});

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

describe('Claude direct account selection', () => {
  it('sends nobody to the providers page and switches nothing without an account', () => {
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    // No account to pick: the panel explains where accounts come from instead of dead-ending the mode switch.
    expect(screen.getByText(t('clients.claudeDirect.noAccount'))).toBeInTheDocument();
    expect(screen.getByRole('button', { name: t('clients.claudeDirect.goProviders') })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: t('clients.claudeDirect.direct') }));
    expect(hookSpies.select).not.toHaveBeenCalled();
    expect(screen.getByText(t('clients.claudeDirect.needAccountToast'))).toBeInTheDocument();
  });

  it('never queries a loopback-only refusal twice and says why the state is missing', () => {
    hookSpies.state = { data: undefined, isLoading: false, isError: true, error: new Error('Direct connection settings require a loopback server on this machine.') };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    expect(screen.getByText(t('clients.claudeDirect.loadFailed'))).toBeInTheDocument();
    expect(screen.getByText(/require a loopback server/)).toBeInTheDocument();
    // The account group needs the state, so it stays away rather than rendering empty controls.
    expect(screen.queryByText(t('clients.claudeDirect.noAccount'))).not.toBeInTheDocument();
  });

  it('is queried for Claude Code only, and every other client keeps it disabled', () => {
    hookSpies.state = { data: directState(), isLoading: false, isError: false, error: null };
    showPage('/clients');
    // The route has no client selected, so the page falls back to codex: no direct request at all.
    expect(hookSpies.enabled).toHaveBeenCalledWith(false);
    expect(hookSpies.enabled).not.toHaveBeenCalledWith(true);

    cleanup();
    hookSpies.enabled.mockClear();
    showPage('/clients/claude-code');
    expect(hookSpies.enabled).toHaveBeenCalledWith(true);
  });
});

describe('Claude direct mode and account switching', () => {
  it('switches to direct with the first usable account in the same request', async () => {
    const id = 'b'.repeat(32);
    hookSpies.state = { data: directState({ accounts: [account({ id, name: 'Work' })] }), isLoading: false, isError: false, error: null };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    fireEvent.click(screen.getByRole('button', { name: t('clients.claudeDirect.direct') }));
    await waitFor(() => expect(hookSpies.select).toHaveBeenCalledTimes(1));
    expect(hookSpies.select.mock.calls[0][0]).toEqual({ mode: 'direct', accountId: id });
    expect(typeof hookSpies.select.mock.calls[0][1].onError).toBe('function');
  });

  it('reports a refused switch (409) and leaves the control usable', async () => {
    const id = 'b'.repeat(32);
    hookSpies.state = { data: directState({ accounts: [account({ id, name: 'Work' })] }), isLoading: false, isError: false, error: null };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    fireEvent.click(screen.getByRole('button', { name: t('clients.claudeDirect.direct') }));
    await waitFor(() => expect(hookSpies.select).toHaveBeenCalledTimes(1));
    // The panel hands the server's message to the toast layer, so a refused switch is visible.
    hookSpies.select.mock.calls[0][1].onError(new Error('Disable the gateway take-over for Claude Code first.'));
    expect(await screen.findByText('Disable the gateway take-over for Claude Code first.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: t('clients.claudeDirect.direct') })).toBeEnabled();
  });

  it('picks another account without changing the mode, and skips the one already selected', async () => {
    const first = 'a'.repeat(32);
    const second = 'b'.repeat(32);
    hookSpies.state = {
      data: directState({ mode: 'direct', accountId: first, accounts: [account({ id: first, name: 'Work' }), account({ id: second, name: 'Personal', email: null, plan: null })] }),
      isLoading: false,
      isError: false,
      error: null,
    };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    // The picker shows the saved account and both options, labelled name + email + plan.
    expect(screen.getByRole('combobox', { name: t('clients.claudeDirect.account') })).toHaveTextContent('Work');
    const options = await openSelect(t('clients.claudeDirect.account'));
    expect(options.map((o) => o.textContent)).toEqual(['Work · me@example.com · Pro', 'Personal']);

    fireEvent.click(options.filter((o) => o.textContent === 'Personal')[0]!);
    await waitFor(() => expect(hookSpies.select).toHaveBeenCalledTimes(1));
    expect(hookSpies.select.mock.calls[0][0]).toEqual({ mode: 'direct', accountId: second });

    // Picking the one already in use is a no-op.
    fireEvent.click(options.filter((o) => o.textContent?.startsWith('Work'))[0]!);
    expect(hookSpies.select).toHaveBeenCalledTimes(1);
  });

  it('offers unusable accounts as disabled options with their status, and never makes them the default', async () => {
    const expired = 'a'.repeat(32);
    const usable = 'b'.repeat(32);
    hookSpies.state = {
      data: directState({ accounts: [account({ id: expired, name: 'Old', status: 'expired' }), account({ id: usable, name: 'Work' })] }),
      isLoading: false,
      isError: false,
      error: null,
    };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    const options = await openSelect(t('clients.claudeDirect.account'));
    const bad = options.find((o) => o.textContent?.startsWith('Old'))!;
    expect(bad).toHaveAttribute('aria-disabled', 'true');
    expect(bad.textContent).toContain(t('providers.subscription.status.expired'));
    expect(bad.textContent).toContain(t('clients.claudeDirect.accountDisabledSuffix', { status: t('providers.subscription.status.expired') }));

    // A disabled option is a dead end: nothing is sent, not even for the account it names.
    fireEvent.pointerDown(bad, { pointerType: 'mouse' });
    fireEvent.pointerUp(bad, { pointerType: 'mouse' });
    fireEvent.click(bad);
    expect(hookSpies.select).not.toHaveBeenCalled();

    // The still-open popover is a modal layer, so close it before reaching for the switch below.
    fireEvent.keyDown(document, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('option')).not.toBeInTheDocument());

    // The switch knows the expired account cannot serve and takes the active one instead.
    fireEvent.click(screen.getByRole('button', { name: t('clients.claudeDirect.direct') }));
    await waitFor(() => expect(hookSpies.select).toHaveBeenCalledTimes(1));
    expect(hookSpies.select.mock.calls[0][0]).toEqual({ mode: 'direct', accountId: usable });
  });

  it('warns that the enabled gateway take-over would override a direct connection', () => {
    hookSpies.state = { data: directState({ accounts: [account()] }), isLoading: false, isError: false, error: null };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled />);

    expect(screen.getByText(t('clients.claudeDirect.gatewayOnTitle'))).toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.gatewayOnDetail'))).toBeInTheDocument();
    // The switch stays clickable; the server is what refuses it (409).
    expect(screen.getByRole('button', { name: t('clients.claudeDirect.direct') })).toBeEnabled();
  });
});

describe('Claude direct statistics consent', () => {
  it('records the consent of the selected account and stops the polling when off', async () => {
    const id = 'a'.repeat(32);
    hookSpies.state = { data: directState({ mode: 'direct', accountId: id, accounts: [account({ id, name: 'Work', telemetryEnabled: true })] }), isLoading: false, isError: false, error: null };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    fireEvent.click(screen.getByRole('switch', { name: t('clients.claudeDirect.statsConsent') }));
    await waitFor(() => expect(hookSpies.select).toHaveBeenCalledTimes(1));
    expect(hookSpies.select.mock.calls[0][0]).toEqual({ mode: 'direct', accountId: id, telemetryEnabled: false });

    // While the consent is on, the statistics poll every 5 s; the label says it is local only.
    expect(hookSpies.requestsLive).toContain(true);
    expect(screen.getByText(t('clients.claudeDirect.statsConsentDetail'))).toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.statsFooter'))).toBeInTheDocument();
  });

  it('never polls without consent', () => {
    const id = 'a'.repeat(32);
    hookSpies.state = { data: directState({ mode: 'direct', accountId: id, accounts: [account({ id, name: 'Work' })] }), isLoading: false, isError: false, error: null };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    expect(hookSpies.requestsLive.every((live) => live === false)).toBe(true);
    // The consent is off by default: the switch is not on and no statistics query is made.
    expect(screen.getByRole('switch', { name: t('clients.claudeDirect.statsConsent') })).not.toBeChecked();
    expect(hookSpies.requestsEnabled.every((id) => id == null)).toBe(true);
    // The switch stays reachable, but no statistics are shown while the consent is off.
    expect(screen.queryByText(t('clients.claudeDirect.statsEmpty'))).not.toBeInTheDocument();
    expect(screen.queryByText(t('clients.claudeDirect.statsTotals', { count: '0' }))).not.toBeInTheDocument();
  });

  it('shows an empty state instead of an empty table', () => {
    const id = 'a'.repeat(32);
    hookSpies.state = { data: directState({ mode: 'direct', accountId: id, accounts: [account({ id, name: 'Work', telemetryEnabled: true })] }), isLoading: false, isError: false, error: null };
    hookSpies.requests = { data: [], isLoading: false, isError: false, error: null };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    expect(screen.getByText(t('clients.claudeDirect.statsEmpty'))).toBeInTheDocument();
  });
});

describe('Claude direct launch command', () => {
  it('copies the launcher command with no account id and no credential', async () => {
    const id = 'c0ffee'.repeat(4).slice(0, 32);
    const writeText = vi.fn(async () => {});
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    hookSpies.state = { data: directState({ mode: 'direct', accountId: id, accounts: [account({ id, name: 'Work' })] }), isLoading: false, isError: false, error: null };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    fireEvent.click(screen.getByRole('button', { name: t('common.copy') }));

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1));
    expect(writeText).toHaveBeenCalledWith('astra claude');
    // The account lives on the server, so the command carries no id and no credential.
    expect(screen.getByText('astra claude')).toBeInTheDocument();
    expect(screen.queryByText(new RegExp(id))).not.toBeInTheDocument();
    expect(screen.getByText('astra claude').textContent).not.toMatch(/token|secret|bearer|key=/i);
  });

  it('reports no login state of its own and no account input', () => {
    const id = 'd'.repeat(32);
    hookSpies.state = { data: directState({ mode: 'direct', accountId: id, accounts: [account({ id, name: 'Work' })] }), isLoading: false, isError: false, error: null };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    expect(screen.getByText(t('clients.claudeDirect.launchNote'))).toBeInTheDocument();
    expect(screen.queryByText(/signed in|logged in|--login|--status|--profile/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: t('clients.claudeDirect.account') })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: t('common.add') })).not.toBeInTheDocument();
  });

  it('keeps the launcher out of gateway mode, where the gateway groups apply', () => {
    const id = 'e'.repeat(32);
    hookSpies.state = { data: directState({ mode: 'gateway', accountId: id, accounts: [account({ id, name: 'Work' })] }), isLoading: false, isError: false, error: null };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    expect(screen.queryByText('astra claude')).not.toBeInTheDocument();
    expect(screen.queryByText(t('clients.claudeDirect.statsTitle'))).not.toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.accountsTitle'))).toBeInTheDocument();
  });
});

describe('Claude direct statistics totals', () => {
  it('adds cache reads and creations into the total input and sums the client estimate', () => {
    const totals = claudeDirectTotals([
      request({ inputTokens: 1000, outputTokens: 250, cacheReadTokens: 4000, cacheCreationTokens: 500, estimatedCostNanoUsd: 1_500_000_000 }),
      request({ id: 'r2', status: 'error', inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheCreationTokens: 0, estimatedCostNanoUsd: null }),
    ]);
    expect(totals).toEqual({
      requests: 2,
      inputTokens: 1010,
      totalInputTokens: 5510,
      outputTokens: 255,
      failed: 1,
      estimatedCostNanoUsd: 1_500_000_000,
    });
  });

  it('is all zeros for no data', () => {
    expect(claudeDirectTotals([])).toEqual({ requests: 0, inputTokens: 0, totalInputTokens: 0, outputTokens: 0, failed: 0, estimatedCostNanoUsd: 0 });
  });

  it('renders the totals and each request with model, status, tokens, time and account', () => {
    const id = 'a'.repeat(32);
    hookSpies.state = { data: directState({ mode: 'direct', accountId: id, accounts: [account({ id, name: 'Work', telemetryEnabled: true })] }), isLoading: false, isError: false, error: null };
    hookSpies.requests = { data: [request(), request({ id: 'r2', model: 'claude-opus-4-1', status: 'error', accountUuid: null })], isLoading: false, isError: false, error: null };
    showPanel(<ClaudeDirectPanel enabled gatewayEnabled={false} />);

    // Latest 2: total input 11,000 (cache reads and creations included), total output 500.
    expect(screen.getByText(t('clients.claudeDirect.statsTotals', { count: '2' }))).toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.statsInDetail', { input: '11,000', cache: '8,000', output: '500' }))).toBeInTheDocument();
    // The client's own estimate, in dollars (nano-USD / 1e9), labelled as an estimate.
    expect(screen.getByText('$3.00')).toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.statsCostDetail'))).toBeInTheDocument();
    expect(screen.getByText('claude-sonnet-4-5')).toBeInTheDocument();
    expect(screen.getByText('claude-opus-4-1')).toBeInTheDocument();
    expect(screen.getByText(t('status.success'))).toBeInTheDocument();
    expect(screen.getAllByText(t('clients.claudeDirect.statusError'))).toHaveLength(2);
    expect(screen.getAllByText(/2026/).length).toBe(2);
    expect(screen.getByText(/0e1d8f3a-0000-4000-8000-000000000001/)).toBeInTheDocument();
  });
});

describe('ClientsPage in direct mode', () => {
  it('hides gateway configuration and maintenance while exposing direct settings', () => {
    hookSpies.state = { data: directState({ mode: 'direct', accountId: 'f'.repeat(32) }), isLoading: false, isError: false, error: null };
    showPage();

    expect(screen.queryByText(t('clients.providerTitle'))).not.toBeInTheDocument();
    expect(screen.queryByText(t('clients.tokenTitle'))).not.toBeInTheDocument();
    expect(screen.queryByText(t('clients.modelTitle'))).not.toBeInTheDocument();
    // The client config cannot be enabled from here in direct mode.
    expect(screen.queryByRole('button', { name: t('clients.enable') })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: t('clients.claudeDirect.configure') })).toBeInTheDocument();
    // Default-config maintenance is unrelated to the direct connection and stays in gateway mode.
    expect(screen.queryByText(t('clients.maintenance'))).not.toBeInTheDocument();
    expect(screen.queryByText(t('clients.backups'))).not.toBeInTheDocument();
    expect(screen.queryByText(t('clients.copyConfigPath'))).not.toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.modeTitle'))).toBeInTheDocument();
  });

  it('keeps the gateway groups in gateway mode', () => {
    hookSpies.state = { data: directState(), isLoading: false, isError: false, error: null };
    showPage();

    expect(screen.getByText(t('clients.providerTitle'))).toBeInTheDocument();
    expect(screen.getByText(t('clients.tokenTitle'))).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: t('clients.claudeDirect.configure') })).not.toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.modeTitle'))).toBeInTheDocument();
  });
});

describe('Config repair', () => {
  const file = '/Users/me/Library/Application Support/Claude-3p/claude_desktop_config.json';

  it('names the file the client cannot read and repairs it on request', () => {
    hookSpies.clients = [codex(), claudeCode({ repairs: [{ file, problem: 'trailing-comma', line: 53 }] })];
    showPage();
    expect(screen.getByText(/can't read this config file/)).toBeTruthy();
    expect(screen.getByText(file)).toBeTruthy();
    expect(screen.getByText(new RegExp(t('clients.repair.problem.trailing-comma', { line: 53 })))).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: t('clients.repair.action') }));
    expect(hookSpies.repair).toHaveBeenCalledWith('claude-code', expect.anything());
  });

  it('shows nothing when every config file is readable', () => {
    showPage();
    expect(screen.queryByText(/can't read this config file/)).toBeNull();
    expect(screen.queryByRole('button', { name: t('clients.repair.action') })).toBeNull();
  });
});
