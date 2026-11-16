import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { Provider, ProviderAccount, ProviderUsage } from '../api/types';
import { keyState, ProviderKeyList } from '../components/ProviderKeyList';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

const spies = vi.hoisted(() => ({
  add: vi.fn(),
  patch: vi.fn(),
  remove: vi.fn(),
  reorder: vi.fn(),
  activate: vi.fn(),
  updatePolicy: vi.fn(),
  mode: 'failover' as 'manual' | 'failover' | 'balanced',
}));

vi.mock('../api/hooks', () => ({
  useProviderKeys: () => ({ data: keys, isError: false }),
  useProviderUsage: () => ({ data: usage }),
  useSubscriptionPolicy: () => ({ data: { clientPolicy: 'any', switchMode: spies.mode, claudeSubscription: false, mimicClaudeCode: false } }),
  useUpdateSubscriptionPolicy: () => ({ mutate: spies.updatePolicy }),
  useAddProviderKey: () => ({ mutate: spies.add, isPending: false }),
  usePatchProviderKey: () => ({ mutate: spies.patch, isPending: false }),
  useRemoveProviderKey: () => ({ mutate: spies.remove, isPending: false }),
  useReorderProviderKeys: () => ({ mutate: spies.reorder, isPending: false }),
  useActivateProviderKey: () => ({ mutate: spies.activate, isPending: false }),
}));

const future = new Date(Date.now() + 3_600_000).toISOString();
const key = (over: Partial<ProviderAccount>): ProviderAccount => ({
  id: 'k', providerId: 'p1', displayName: 'Key', status: 'active', createdAt: '2026-11-01T00:00:00Z', enabled: true,
  isCurrent: false, sortOrder: 0, kind: 'api_key', keyMasked: 'sk-…1111', ...over,
});

let keys: ProviderAccount[] = [];
const usage: ProviderUsage = {
  day: '2026-11-16', timeZone: 'UTC',
  accounts: [
    { accountId: 'k1', today: { requests: 7, inputTokens: 1, outputTokens: 1, tokens: 2000, costNanoUsd: 1 }, total: { requests: 70, inputTokens: 1, outputTokens: 1, tokens: 9000, costNanoUsd: 2_000_000_000 } },
  ],
};

const pooled = {
  id: 'p1', name: 'Relay', authScheme: 'bearer', settings: { key_pool: true },
  endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://r.example/v1' }, { protocol: 'anthropic', baseUrl: 'https://r.example' }],
} as unknown as Provider;
const single = { ...pooled, settings: {} } as unknown as Provider;

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
  for (const spy of [spies.add, spies.patch, spies.remove, spies.reorder, spies.activate, spies.updatePolicy]) spy.mockReset();
  spies.mode = 'failover';
  keys = [
    key({ id: 'k1', displayName: 'main', isCurrent: true, weight: 3 }),
    key({ id: 'k2', displayName: 'claude', protocol: 'anthropic', cooldownUntilUtc: future, lastError: '401 认证被上游拒绝', keyMasked: 'sk-…2222' }),
    key({ id: 'k3', displayName: 'spare', cooldownUntilUtc: future, lastError: '429 限流', keyMasked: 'sk-…3333' }),
    key({ id: 'k4', displayName: 'retired', enabled: false, keyMasked: 'sk-…4444' }),
  ];
});

afterEach(() => {
  cleanup();
  localStorage.clear();
});

describe('keyState', () => {
  it('tells an auth rejection from a rate limit, and a lapsed cooldown from none', () => {
    const now = Date.now();
    expect(keyState(key({ enabled: false }), now)).toBe('off');
    expect(keyState(key({ cooldownUntilUtc: future, lastError: '403 x' }), now)).toBe('authFailed');
    expect(keyState(key({ cooldownUntilUtc: future, lastError: '429 x' }), now)).toBe('cooling');
    expect(keyState(key({ cooldownUntilUtc: new Date(now - 1000).toISOString(), lastError: '401 x' }), now)).toBe('ok');
  });
});

describe('ProviderKeyList', () => {
  it('offers to add a second key to a provider that holds one', async () => {
    show(<ProviderKeyList provider={single} />);
    expect(screen.queryByText('main')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Add key' }));
    const dialog = await screen.findByRole('dialog');
    const save = within(dialog).getByRole('button', { name: 'Save' });
    expect(save).toBeDisabled(); // a key is required

    fireEvent.change(within(dialog).getByPlaceholderText('sk-…'), { target: { value: ' sk-new-0000 ' } });
    fireEvent.change(within(dialog).getByPlaceholderText(/backup, shared/), { target: { value: 'backup' } });
    fireEvent.click(save);
    expect(spies.add).toHaveBeenCalledWith({ key: 'sk-new-0000', name: 'backup' }, expect.anything());
  });

  it('lists every key with its state, protocol, weight and usage', () => {
    show(<ProviderKeyList provider={pooled} />);
    expect(screen.getByText('main')).toBeInTheDocument();
    expect(screen.getByText('×3')).toBeInTheDocument();
    expect(screen.getByText('Anthropic')).toBeInTheDocument();
    expect(screen.getByText(/Authentication failed, cooling until/)).toBeInTheDocument();
    expect(screen.getByText(/Rate-limited until/)).toBeInTheDocument();
    expect(screen.getByText('Off')).toBeInTheDocument();
    expect(screen.getByText(/Today: 7/)).toBeInTheDocument();
    expect(screen.getByText(/Failover:/)).toBeInTheDocument();
  });

  it('switches, reorders and deletes keys', async () => {
    show(<ProviderKeyList provider={pooled} />);
    fireEvent.click(screen.getAllByRole('switch', { name: 'Enable this key' })[0]!);
    expect(spies.patch).toHaveBeenCalledWith({ id: 'k1', enabled: false }, expect.anything());

    fireEvent.click(screen.getAllByRole('button', { name: 'Move down' })[0]!);
    expect(spies.reorder).toHaveBeenCalledWith(['k2', 'k1', 'k3', 'k4'], expect.anything());
    expect(screen.getAllByRole('button', { name: 'Move up' })[0]).toBeDisabled();

    fireEvent.click(screen.getAllByRole('button', { name: 'Delete' })[2]!);
    await screen.findByText('Delete key "spare"?');
    fireEvent.click(screen.getAllByRole('button', { name: 'Delete' }).at(-1)!);
    await waitFor(() => expect(spies.remove).toHaveBeenCalledWith('k3', expect.anything()));
  });

  it('edits a key: weight, protocol limit and a replacement secret', async () => {
    show(<ProviderKeyList provider={pooled} />);
    fireEvent.click(screen.getAllByRole('button', { name: 'Edit' })[1]!);
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('spinbutton'), { target: { value: '5' } });
    fireEvent.change(within(dialog).getByPlaceholderText('Enter a new key to replace'), { target: { value: 'sk-rotated' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(spies.patch).toHaveBeenCalledWith(
      expect.objectContaining({ id: 'k2', key: 'sk-rotated', weight: 5, protocol: 'anthropic', name: 'claude' }),
      expect.anything(),
    );
  });

  it('shows "Use" only in manual mode, on keys that are not current', () => {
    spies.mode = 'manual';
    show(<ProviderKeyList provider={pooled} />);
    const use = screen.getAllByRole('button', { name: 'Use' });
    expect(use).toHaveLength(3);
    fireEvent.click(use[0]!);
    expect(spies.activate).toHaveBeenCalledWith('k2', expect.anything());
  });
});
