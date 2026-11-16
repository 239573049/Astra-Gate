import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ClaudeBridgeStatus, Provider, ProviderAccount, WarmupAccount, WarmupSettings as Settings } from '../api/types';
import { ClaudeBridgePanel } from '../components/ClaudeBridgePanel';
import { WarmupSettings } from '../components/WarmupSettings';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

const spies = vi.hoisted(() => ({
  update: vi.fn(),
  patch: vi.fn(),
  warm: vi.fn(),
  add: vi.fn(),
  saveBridge: vi.fn(),
  startLogin: vi.fn(),
}));

let warmup: Settings;
let accounts: WarmupAccount[] = [];
let bridge: ClaudeBridgeStatus;

vi.mock('../api/hooks', () => ({
  useWarmupSettings: () => ({ data: warmup, isError: false }),
  useUpdateWarmupSettings: () => ({ mutate: spies.update, isPending: false }),
  useWarmupAccounts: () => ({ data: accounts }),
  usePatchWarmupAccount: () => ({ mutate: spies.patch }),
  useWarmNow: () => ({ mutate: spies.warm, isPending: false }),
  useClaudeBridge: () => ({ data: bridge }),
  useUpdateClaudeBridgeSettings: () => ({ mutate: spies.saveBridge, isPending: false }),
  useAddClaudeBridgeAccount: () => ({ mutate: spies.add, isPending: false }),
  useClaudeSignIn: () => ({ data: undefined, refetch: vi.fn() }),
  useStartClaudeLogin: () => ({ mutate: spies.startLogin, isPending: false }),
  useClaudeLogin: () => ({ data: undefined }),
}));

const show = (ui: React.ReactNode) => {
  localStorage.setItem('astra.locale', 'en');
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <I18nProvider>
        <FeedbackProvider>{ui}</FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  Object.values(spies).forEach((s) => s.mockReset());
  warmup = { enabled: false, mode: 'daily', times: ['06:00'], weekdays: [], claude: true, codex: true, timeZone: 'UTC' };
  accounts = [];
  bridge = {
    found: true, path: '/usr/local/bin/claude', version: '2.1.999', source: 'path', runs: [], claudeProviderId: 'claude', clientPolicy: 'claude-code-only',
    settings: { maxRuns: 6, idleMinutes: 10 },
  };
});
afterEach(cleanup);

describe('window warm-up settings', () => {
  it('is off by default and the switch turns it on', () => {
    show(<WarmupSettings />);
    const sw = screen.getByRole('switch', { name: 'Enable warm-up' });
    expect(sw.getAttribute('aria-checked')).toBe('false');
    fireEvent.click(sw);
    expect(spies.update.mock.calls[0][0]).toEqual({ enabled: true });
  });

  it('adds a time of day only when it looks like one and keeps them sorted', () => {
    show(<WarmupSettings />);
    const input = screen.getByPlaceholderText('06:00');
    fireEvent.change(input, { target: { value: '6am' } });
    expect((screen.getByRole('button', { name: 'Add a time' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(input, { target: { value: '11:05' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add a time' }));
    expect(spies.update.mock.calls[0][0]).toEqual({ times: ['06:00', '11:05'] });
  });

  it('picks the days of the week', () => {
    warmup.weekdays = [1];
    show(<WarmupSettings />);
    fireEvent.click(screen.getByRole('button', { name: 'Wed' }));
    expect(spies.update.mock.calls[0][0]).toEqual({ weekdays: [1, 3] });
  });

  it('lets each account follow the global setting, have its own mode, or be warmed now', () => {
    accounts = [
      {
        providerId: 'claude', providerName: 'Claude', accountId: 'a1', accountName: 'work', family: 'claude', mode: 'inherit', warmed: true,
        lastAt: '2026-11-16T06:00:00Z', lastResult: 'skipped', lastMessage: 'token lapsed', nextAt: '2026-11-17T06:00:00Z',
      },
    ];
    show(<WarmupSettings />);
    expect(screen.getByText(/Last: skipped.*token lapsed/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Off' }));
    expect(spies.patch.mock.calls[0][0]).toEqual({ id: 'a1', mode: 'off' });
    fireEvent.click(screen.getByRole('button', { name: 'Warm up now' }));
    expect(spies.warm.mock.calls[0][0]).toBe('a1');
  });
});

describe('Claude bridge panel', () => {
  const provider = { id: 'claude', name: 'Claude', templateId: 'claude-subscription', authScheme: 'oauth-subscription' } as unknown as Provider;
  const account = (over: Partial<ProviderAccount>): ProviderAccount => ({
    id: 'b1', providerId: 'claude', displayName: 'bridge', status: 'active', createdAt: '2026-11-01T00:00:00Z', enabled: true, isCurrent: false, sortOrder: 0, ...over,
  });

  it('shows what was found, warns that the provider serves Claude Code only, and adds an account with its own directory', () => {
    show(<ClaudeBridgePanel provider={provider} accounts={[]} />);
    expect(screen.getByText('Found claude 2.1.999')).toBeTruthy();
    expect(screen.getByText(/serves only Claude Code/)).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText('~/.claude-work'), { target: { value: '/data/work' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add a claude program account' }));
    expect(spies.add.mock.calls[0][0]).toMatchObject({ providerId: 'claude', configDir: '/data/work' });
  });

  it('says so when claude is missing', () => {
    bridge = { ...bridge, found: false, path: null, version: null, source: null, clientPolicy: 'any' };
    show(<ClaudeBridgePanel provider={provider} accounts={[]} />);
    expect(screen.getByText(/No claude program was found/)).toBeTruthy();
    expect(screen.queryByText(/serves only Claude Code/)).toBeNull();
  });

  it('lists the bridge accounts and signs one in', () => {
    show(<ClaudeBridgePanel provider={provider} accounts={[account({ kind: 'claude_bridge', claudeConfigDir: '/data/work' }), account({ id: 'o1', displayName: 'oauth', kind: 'oauth' })]} />);
    expect(screen.getByText('bridge')).toBeTruthy();
    expect(screen.queryByText('oauth')).toBeNull(); // the OAuth accounts are the list's own
    fireEvent.click(screen.getByRole('button', { name: 'Sign in…' }));
    expect(spies.startLogin.mock.calls[0][0]).toBe('b1');
  });

  it('saves the run settings', () => {
    show(<ClaudeBridgePanel provider={provider} accounts={[]} />);
    fireEvent.change(screen.getByPlaceholderText('Empty = look for it'), { target: { value: '/opt/claude' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(spies.saveBridge.mock.calls[0][0]).toEqual({ binaryPath: '/opt/claude', maxRuns: 6, idleMinutes: 10 });
  });
});
