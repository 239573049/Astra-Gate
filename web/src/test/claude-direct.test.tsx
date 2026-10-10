import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ClaudeDirectRequest, ClientInfo, Provider } from '../api/types';
import { ClaudeDirectPanel, claudeDirectTotals } from '../components/ClaudeDirectPanel';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';
import { ClientsPage } from '../pages/ClientsPage';

// ---------- service-boundary mocks (the panel and ClientsPage stay real) ----------

const hookSpies = vi.hoisted(() => ({
  state: { data: undefined as unknown, isLoading: false, isError: false, error: null as unknown },
  requests: { data: undefined as unknown, isLoading: false, isError: false, error: null as unknown },
  create: vi.fn(),
  select: vi.fn(),
  requestsEnabled: [] as (string | null | undefined)[],
  requestsLive: [] as boolean[],
  enabled: vi.fn(),
}));

const directState = (over: Record<string, unknown> = {}) => ({
  mode: 'gateway',
  profileId: null as string | null,
  profiles: [] as { id: string; name: string; telemetryEnabled: boolean; configDirectory: string }[],
  ...over,
});

const profile = (id: string, name: string, telemetryEnabled = false) => ({
  id,
  name,
  telemetryEnabled,
  configDirectory: `/Users/me/.astra/claude-profiles/${id}`,
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
  useCreateClaudeProfile: () => ({ mutate: hookSpies.create, isPending: false }),
  useSelectClaudeDirect: () => ({ mutate: hookSpies.select, isPending: false }),
  useClients: () => ({ data: clients, isLoading: false, isError: false, error: null }),
  useProviders: () => ({ data: providers, isLoading: false, isError: false, error: null }),
  useClientModels: () => ({ data: ['claude-sonnet-4-5'], isLoading: false }),
  useSetBinding: () => ({ mutate: vi.fn(), isPending: false }),
  useSetBindings: () => ({ mutate: vi.fn(), isPending: false }),
  useDisableClient: () => ({ mutate: vi.fn(), isPending: false }),
  useForceRestore: () => ({ mutate: vi.fn(), isPending: false }),
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
  hookSpies.state = { data: directState(), isLoading: false, isError: false, error: null };
  hookSpies.requests = { data: [], isLoading: false, isError: false, error: null };
  hookSpies.create.mockClear();
  hookSpies.select.mockClear();
  hookSpies.enabled.mockClear();
  hookSpies.requestsEnabled.length = 0;
  hookSpies.requestsLive.length = 0;
});

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

describe('Claude direct profile creation', () => {
  it('creates the first login without any profile, and calls nobody before the user asks', async () => {
    hookSpies.state = { data: directState({ mode: 'direct', profileId: 'missing' }), isLoading: false, isError: false, error: null };
    show(<ClaudeDirectPanel enabled />);

    // No profile yet: the panel says how to fix it instead of dead-ending the mode switch.
    expect(screen.getByText(t('clients.claudeDirect.needProfile'))).toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.noProfiles'))).toBeInTheDocument();
    expect(hookSpies.create).not.toHaveBeenCalled();
    expect(hookSpies.select).not.toHaveBeenCalled();

    // The creation row is always usable, even with the mode already on direct.
    fireEvent.change(screen.getByRole('textbox', { name: t('clients.claudeDirect.name') }), { target: { value: '  Work account  ' } });
    fireEvent.click(screen.getByRole('button', { name: t('common.add') }));

    await waitFor(() => expect(hookSpies.create).toHaveBeenCalledTimes(1));
    // The trimmed name only; the profile id is the server's.
    expect(hookSpies.create.mock.calls[0][0]).toBe('Work account');
    const options = hookSpies.create.mock.calls[0][1];
    expect(typeof options.onError).toBe('function');
    expect(hookSpies.create.mock.calls[0]).toHaveLength(2);
  });
});

describe('Claude direct mode and profile selection', () => {
  it('switches the mode, selecting a profile in the same request', async () => {
    hookSpies.state = { data: directState({ profiles: [profile('b'.repeat(32), 'Work')] }), isLoading: false, isError: false, error: null };
    show(<ClaudeDirectPanel enabled />);

    fireEvent.click(screen.getByRole('button', { name: t('clients.claudeDirect.direct') }));
    await waitFor(() => expect(hookSpies.select).toHaveBeenCalledTimes(1));
    expect(hookSpies.select.mock.calls[0][0]).toEqual({ mode: 'direct', profileId: 'b'.repeat(32) });
    expect(typeof hookSpies.select.mock.calls[0][1].onError).toBe('function');
  });

  it('reports a failed switch and leaves the control usable', async () => {
    hookSpies.state = { data: directState({ profiles: [profile('b'.repeat(32), 'Work')] }), isLoading: false, isError: false, error: null };
    show(<ClaudeDirectPanel enabled />);

    fireEvent.click(screen.getByRole('button', { name: t('clients.claudeDirect.direct') }));
    await waitFor(() => expect(hookSpies.select).toHaveBeenCalledTimes(1));
    // The panel hands the failure to the toast layer, so a refused switch is visible.
    hookSpies.select.mock.calls[0][1].onError(new Error('Choose an existing native Claude profile.'));
    expect(await screen.findByText('Choose an existing native Claude profile.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: t('clients.claudeDirect.direct') })).toBeEnabled();
  });

  it('selects another profile without changing the mode', async () => {
    hookSpies.state = {
      data: directState({ mode: 'direct', profileId: 'a'.repeat(32), profiles: [profile('a'.repeat(32), 'Work'), profile('b'.repeat(32), 'Personal')] }),
      isLoading: false,
      isError: false,
      error: null,
    };
    show(<ClaudeDirectPanel enabled />);

    fireEvent.click(screen.getByText('Personal'));
    await waitFor(() => expect(hookSpies.select).toHaveBeenCalledTimes(1));
    expect(hookSpies.select.mock.calls[0][0]).toEqual({ mode: 'direct', profileId: 'b'.repeat(32) });
  });

  it('surfaces a loopback-only refusal instead of an empty panel', () => {
    hookSpies.state = { data: undefined, isLoading: false, isError: true, error: new Error('Native Claude profiles and telemetry require a loopback server on this machine.') };
    show(<ClaudeDirectPanel enabled />);

    expect(screen.getByText(t('clients.claudeDirect.loadFailed'))).toBeInTheDocument();
    expect(screen.getByText(/require a loopback server/)).toBeInTheDocument();
    // The profile group needs the state, so it stays away rather than rendering empty controls.
    expect(screen.queryByRole('button', { name: t('common.add') })).not.toBeInTheDocument();
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

describe('Claude direct statistics consent', () => {
  it('records the consent of the selected profile and stops the polling when off', async () => {
    const id = 'a'.repeat(32);
    hookSpies.state = { data: directState({ mode: 'direct', profileId: id, profiles: [profile(id, 'Work', true)] }), isLoading: false, isError: false, error: null };
    show(<ClaudeDirectPanel enabled />);

    fireEvent.click(screen.getByRole('switch', { name: t('clients.claudeDirect.statsConsent') }));
    await waitFor(() => expect(hookSpies.select).toHaveBeenCalledTimes(1));
    expect(hookSpies.select.mock.calls[0][0]).toEqual({ mode: 'direct', profileId: id, telemetryEnabled: false });

    // While the consent is on, the statistics poll every 5 s; the label says it is local only.
    expect(hookSpies.requestsLive).toContain(true);
    expect(screen.getByText(t('clients.claudeDirect.statsConsentDetail'))).toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.statsFooter'))).toBeInTheDocument();
  });

  it('never polls without consent', () => {
    const id = 'a'.repeat(32);
    hookSpies.state = { data: directState({ mode: 'direct', profileId: id, profiles: [profile(id, 'Work', false)] }), isLoading: false, isError: false, error: null };
    show(<ClaudeDirectPanel enabled />);

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
    hookSpies.state = { data: directState({ mode: 'direct', profileId: id, profiles: [profile(id, 'Work', true)] }), isLoading: false, isError: false, error: null };
    hookSpies.requests = { data: [], isLoading: false, isError: false, error: null };
    show(<ClaudeDirectPanel enabled />);

    expect(screen.getByText(t('clients.claudeDirect.statsEmpty'))).toBeInTheDocument();
  });
});

describe('Claude direct launch commands', () => {
  it('copies the launcher command with the profile id and no credential', async () => {
    const id = 'c0ffee'.repeat(4).slice(0, 32);
    const writeText = vi.fn(async () => {});
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    hookSpies.state = { data: directState({ mode: 'direct', profileId: id, profiles: [profile(id, 'Work')] }), isLoading: false, isError: false, error: null };
    show(<ClaudeDirectPanel enabled />);

    fireEvent.click(screen.getAllByRole('button', { name: t('common.copy') })[0]!);

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1));
    expect(writeText).toHaveBeenCalledWith(`astra claude --profile ${id} --login`);
    // Every launcher row is a plain command: no secret, no token, nothing but the public profile id.
    const commands = screen.getAllByText(/^astra claude /).map((n) => n.textContent ?? '');
    expect(commands).toEqual([
      `astra claude --profile ${id} --login`,
      `astra claude --profile ${id} --status`,
      `astra claude --profile ${id}`,
    ]);
    expect(commands.join('\n')).not.toMatch(/token|secret|Bearer|key=/i);
  });

  it('reports no login state of its own', () => {
    const id = 'd'.repeat(32);
    hookSpies.state = { data: directState({ mode: 'direct', profileId: id, profiles: [profile(id, 'Work')] }), isLoading: false, isError: false, error: null };
    show(<ClaudeDirectPanel enabled />);

    expect(screen.getByText(t('clients.claudeDirect.launchFooter'))).toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.identityNote'))).toBeInTheDocument();
    expect(screen.queryByText(/signed in|logged in/i)).not.toBeInTheDocument();
  });

  it('offers the login command in gateway mode too, without any statistics', () => {
    const id = 'e'.repeat(32);
    hookSpies.state = { data: directState({ mode: 'gateway', profileId: id, profiles: [profile(id, 'Work')] }), isLoading: false, isError: false, error: null };
    show(<ClaudeDirectPanel enabled />);

    expect(screen.getByText(`astra claude --profile ${id} --login`)).toBeInTheDocument();
    expect(screen.getByText(t('clients.claudeDirect.gatewayHint'))).toBeInTheDocument();
    expect(screen.queryByText(t('clients.claudeDirect.statsTitle'))).not.toBeInTheDocument();
    expect(screen.queryByText(`astra claude --profile ${id}`)).not.toBeInTheDocument();
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
    hookSpies.state = { data: directState({ mode: 'direct', profileId: id, profiles: [profile(id, 'Work', true)] }), isLoading: false, isError: false, error: null };
    hookSpies.requests = { data: [request(), request({ id: 'r2', model: 'claude-opus-4-1', status: 'error', accountUuid: null })], isLoading: false, isError: false, error: null };
    show(<ClaudeDirectPanel enabled />);

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
  it('hides gateway configuration and maintenance while exposing direct login settings', () => {
    hookSpies.state = { data: directState({ mode: 'direct', profileId: 'f'.repeat(32) }), isLoading: false, isError: false, error: null };
    showPage();

    expect(screen.queryByText(t('clients.providerTitle'))).not.toBeInTheDocument();
    expect(screen.queryByText(t('clients.tokenTitle'))).not.toBeInTheDocument();
    expect(screen.queryByText(t('clients.modelTitle'))).not.toBeInTheDocument();
    // The client config cannot be enabled from here in direct mode.
    expect(screen.queryByRole('button', { name: t('clients.enable') })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: t('clients.claudeDirect.configure') })).toBeInTheDocument();
    // Default-config maintenance is unrelated to isolated profiles and stays in gateway mode.
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
