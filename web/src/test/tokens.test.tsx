import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { Token } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';
import { TokensPage } from '../pages/TokensPage';

const spies = vi.hoisted(() => ({ update: vi.fn(), tokens: [] as unknown[] }));

vi.mock('../api/hooks', () => {
  const idle = { mutate: vi.fn(), isPending: false };
  return {
    useTokens: () => ({ data: spies.tokens, isLoading: false, isError: false }),
    useSettings: () => ({ data: { gatewayBaseUrl: 'http://astra.test:8787' } }),
    useProviders: () => ({ data: [] }),
    useProviderAccounts: () => ({ data: [] }),
    useUpdateToken: () => ({ mutate: spies.update, isPending: false }),
    useCreateToken: () => idle,
    useDeleteToken: () => idle,
    useResetToken: () => idle,
    useRevealToken: () => idle,
  };
});

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

// The real icon module pulls in @lobehub/icons, whose JSON imports Node ESM rejects; the page only needs names and glyphs.
vi.mock('../components/icons', () => ({
  CLIENT_META: { codex: { name: 'Codex' }, 'claude-code': { name: 'Claude Code' } },
  ClientGlyph: () => <span />,
}));

vi.mock('../components/layout/AppShell', () => ({
  useShellLayout: () => ({ sidebarMode: 'expanded', sidebarWidth: 224, drawerOpen: false, setDrawerOpen: () => {}, toggleSidebar: () => {}, narrow: false }),
  TOOLBAR_RIGHT_RESERVE: 0,
}));

// Reduced motion keeps every transition at duration 0, so tests never race an animation.
vi.mock('motion/react', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useReducedMotion: () => true,
}));

// jsdom has no ResizeObserver (menus measure themselves); a noop one never fires.
class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

const stats = { costUsd: 0, requests: 0, inputTokens: 0, outputTokens: 0, totalTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0 };

const token = (over: Partial<Token> = {}): Token => ({
  id: 'default', name: 'Default', isDefault: true, enabled: true, keyMasked: 'sk-astra-AbC123…',
  createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z', clients: [], today: stats, total: stats,
  limitUsage: [], recentRequests: 0, ...over,
});

const show = () => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <MemoryRouter>
            <TokensPage />
          </MemoryRouter>
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  spies.update.mockReset();
  spies.tokens = [];
});
afterEach(cleanup);

describe('token limits', () => {
  it('shows each budget with its usage, the RPM window, allowed clients and the self-service address', () => {
    spies.tokens = [
      token({
        limits: { budgets: [{ metric: 'cost', period: 'month', limit: 10 }], rpm: 30 },
        limitUsage: [{ metric: 'cost', period: 'month', limit: 10, used: 9.5, remaining: 0.5, usedPercent: 95, exhausted: false, resetsAt: '2026-11-01T00:00:00+08:00' }],
        recentRequests: 4,
        allowedClients: ['codex'],
      }),
    ];
    show();
    expect(screen.getByRole('progressbar', { name: `${t('tokens.period.month')} · ${t('tokens.metric.cost')}` })).toHaveAttribute('aria-valuenow', '95');
    expect(screen.getByText(t('tokens.rpmUsage', { used: 4, limit: 30 }))).toBeInTheDocument();
    expect(screen.getByText('Codex')).toBeInTheDocument();
    expect(screen.getByText(/astra\.test:8787\/v1\/astra\/key/)).toBeInTheDocument();
  });

  it('marks a token whose budget is used up and reads "unlimited" without caps', () => {
    spies.tokens = [
      token({ limitUsage: [{ metric: 'requests', period: 'day', limit: 5, used: 5, remaining: 0, usedPercent: 100, exhausted: true, resetsAt: null }] }),
      token({ id: 't2', name: 'Free', isDefault: false }),
    ];
    show();
    expect(screen.getByText(t('tokens.limitExhausted'))).toBeInTheDocument();
  });

  it('saves edited limits and a client restriction, dropping empty budgets', async () => {
    spies.tokens = [token({ limits: { budgets: [{ metric: 'cost', period: 'month', limit: 10 }], rpm: null } })];
    show();
    // Radix opens the menu from the keyboard in jsdom (no pointer events).
    fireEvent.keyDown(screen.getByRole('button', { name: t('common.more') }), { key: 'Enter' });
    fireEvent.click(await screen.findByRole('menuitem', { name: t('tokens.settings') }));

    const limit = await screen.findByRole('spinbutton', { name: t('tokens.limitValue') });
    fireEvent.change(limit, { target: { value: '25' } });
    fireEvent.click(screen.getByRole('button', { name: t('tokens.addLimit') })); // left empty: dropped
    fireEvent.change(screen.getByRole('spinbutton', { name: t('tokens.rpm') }), { target: { value: '60' } });
    fireEvent.click(screen.getByRole('button', { name: t('tokens.allowedClientsOnly') }));
    fireEvent.click(screen.getByRole('button', { name: 'Codex' }));
    fireEvent.click(screen.getByRole('button', { name: t('common.save') }));

    await waitFor(() => expect(spies.update).toHaveBeenCalled());
    expect(spies.update.mock.calls[0]![0]).toMatchObject({
      id: 'default',
      limits: { budgets: [{ metric: 'cost', period: 'month', limit: 25 }], rpm: 60 },
      allowedClients: ['codex'],
    });
  });
});
