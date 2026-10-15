import { cleanup, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { I18nProvider, translate } from '../i18n';
import { TokenDetailPage } from '../pages/TokenDetailPage';

const spies = vi.hoisted(() => ({ statsToken: [] as unknown[] }));

vi.mock('../api/hooks', () => {
  const ok = (data: unknown) => ({ data, isLoading: false, isError: false });
  return {
    useToken: () =>
      ok({
        id: 't1', name: 'Team', keyMasked: 'sk-astra-AbC123…', providerId: 'pa',
        pool: {
          strategy: 'weighted', sticky: 'session', failover: true, maxAttempts: 3,
          members: [
            { providerId: 'pa', weight: 2, tier: 1, missing: false, health: { state: 'healthy' } },
            { providerId: 'pb', weight: 1, tier: 1, missing: false, health: { state: 'half_open' } },
          ],
        },
      }),
    useProviders: () => ok([{ id: 'pa', name: 'Alpha' }, { id: 'pb', name: 'Beta' }]),
    useStatsSummary: (_range: string, _client: unknown, token: string) => {
      spies.statsToken.push(token);
      return ok({ range: '7d', costUsd: 1.5, requests: 4, successRate: 0.75, inputTokens: 100, outputTokens: 20, cacheReadTokens: 0, cacheWriteTokens: 0, reasoningTokens: 0, avgTtftMs: 420 });
    },
    useTimeseries: () => ok([]),
    useTopModels: () => ok([{ model: 'gpt-x', costUsd: 1.5, requests: 4, tokens: 120, inputTokens: 100, cacheReadTokens: 0 }]),
    useProviderStats: () =>
      ok([
        { providerId: 'pb', name: 'Beta', requests: 3, successRequests: 3, successRate: 1, costUsd: 1, inputTokens: 80, outputTokens: 15, avgTtftMs: 400, failoversOut: 0, failoversIn: 1 },
        { providerId: 'pa', name: 'Alpha', requests: 1, successRequests: 0, successRate: 0, costUsd: 0.5, inputTokens: 20, outputTokens: 5, avgTtftMs: 500, failoversOut: 1, failoversIn: 0 },
      ]),
    useRequests: () => ok({ items: [], total: 0, page: 1, pageSize: 10, totalCostNanoUsd: 0 }),
  };
});

vi.mock('../components/icons', () => ({ ProviderIcon: () => <span /> }));

vi.mock('../components/layout/AppShell', () => ({
  useShellLayout: () => ({ sidebarMode: 'expanded', sidebarWidth: 224, drawerOpen: false, setDrawerOpen: () => {}, toggleSidebar: () => {}, narrow: false }),
  TOOLBAR_RIGHT_RESERVE: 0,
}));

vi.mock('motion/react', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useReducedMotion: () => true,
}));

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);
// The bar chart animates in when it scrolls into view; jsdom has no IntersectionObserver.
vi.stubGlobal('IntersectionObserver', NoopResizeObserver);

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

afterEach(cleanup);

describe('token detail', () => {
  it('shows the token’s totals, pool health, per-provider failovers and a link to its requests', () => {
    localStorage.setItem('astra.locale', 'en');
    render(
      <QueryClientProvider client={new QueryClient()}>
        <I18nProvider>
          <MemoryRouter initialEntries={['/tokens/t1']}>
            <Routes>
              <Route path="/tokens/:id" element={<TokenDetailPage />} />
            </Routes>
          </MemoryRouter>
        </I18nProvider>
      </QueryClientProvider>,
    );
    expect(spies.statsToken).toContain('t1');
    expect(screen.getByText('Team')).toBeInTheDocument();
    expect(screen.getAllByText('$1.50').length).toBeGreaterThan(0);
    expect(screen.getByText(t('tokens.health.half_open'))).toBeInTheDocument();

    const table = screen.getByRole('table', { name: t('tokens.detail.byProvider') });
    const alpha = within(table).getByText('Alpha').closest('tr')!;
    // Alpha handed one request off, Beta took one over.
    expect(within(alpha).getAllByRole('cell').at(-2)).toHaveTextContent('1');
    expect(within(alpha).getAllByRole('cell').at(-1)).toHaveTextContent('0');

    const links = screen.getAllByRole('link', { name: t('tokens.detail.viewLogs') });
    expect(links[0]).toHaveAttribute('href', '/requests?token=t1');
  });
});
