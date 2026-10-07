import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ApiError } from '../api/client';
import { SubscriptionAccounts } from '../components/SubscriptionAccounts';
import type { Provider, ProviderAccount } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

const spies = vi.hoisted(() => ({
  start: vi.fn(),
  remove: vi.fn(),
}));

vi.mock('../api/hooks', () => ({
  keys: { providerAccounts: (id: string) => ['provider-accounts', id] as const },
  useProviderAccounts: () => ({ data: accounts, isLoading: false, refetch: vi.fn() }),
  useStartProviderLogin: () => ({ mutate: spies.start, isPending: false }),
  usePollProviderLogin: () => ({ mutateAsync: vi.fn() }),
  useRefreshProviderAccount: () => ({ mutate: vi.fn(), isPending: false, variables: undefined }),
  useDeleteProviderAccount: () => ({ mutate: spies.remove, isPending: false }),
  useFetchProviderAccountQuota: () => quotaSpies.fetch,
}));

const quotaSpies = vi.hoisted(() => ({
  fetch: { mutate: vi.fn(), isPending: false, variables: undefined },
}));

const accounts: ProviderAccount[] = [
  {
    id: 'acc-1', providerId: 'p1', displayName: 'me@example.com', accountEmail: 'me@example.com', plan: 'claude_pro',
    status: 'active', expiresAtUtc: '2026-10-06T13:00:00.000Z', lastRefreshAtUtc: null, createdAt: '2026-10-01T00:00:00.000Z',
  },
  {
    id: 'acc-2', providerId: 'p1', displayName: 'other@example.com', accountEmail: 'other@example.com', plan: null,
    status: 'expired', expiresAtUtc: null, lastRefreshAtUtc: null, createdAt: '2026-10-02T00:00:00.000Z',
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

  it('lists accounts with status badges, refresh and logout actions', () => {
    show(<SubscriptionAccounts provider={subscriptionProvider} />);

    expect(screen.getByText('me@example.com')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText('Expired')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Refresh token' })).toHaveLength(2);
    expect(screen.getAllByRole('button', { name: 'Sign out & delete' })).toHaveLength(2);
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

    fireEvent.click(screen.getAllByRole('button', { name: 'Sign out & delete' })[0]);
    expect(await screen.findByText(/Sign out account "me@example\.com"\?/)).toBeInTheDocument();

    // The sheet's confirm button (same label) is appended last.
    const confirmButtons = screen.getAllByRole('button', { name: 'Sign out & delete' });
    fireEvent.click(confirmButtons[confirmButtons.length - 1]);
    await waitFor(() => expect(spies.remove).toHaveBeenCalledWith('acc-1', expect.anything()));
  });
});
