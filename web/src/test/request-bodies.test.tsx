import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { I18nProvider, translate } from '../i18n';
import { RequestDetailSheet } from '../pages/RequestsPage';

const spies = vi.hoisted(() => ({ body: [] as { part: string; enabled: boolean }[] }));

vi.mock('../api/hooks', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useRequest: () => ({
    data: {
      id: 'r1', startedAt: '2026-10-16T00:00:00Z', status: 'success', stream: true, requestedModel: 'gpt-5',
      totalInputTokens: 1, totalOutputTokens: 1, cacheReadTokens: 0, cacheWriteTokens: 0, reasoningTokens: 0,
      costNanoUsd: 0, billingTrace: [], usageItems: [],
      bodies: {
        clientRequest: '{"model":"gpt-5","input":"hi"}',
        upstreamResponse: 'event: response.created\ndata: {"partial',
        sizes: { clientRequest: 30, upstreamResponse: 8 * 1024 * 1024 },
        previewLimit: 64 * 1024,
      },
    },
    isLoading: false,
    isError: false,
  }),
  useRequestBody: (_id: string, part: string, enabled: boolean) => {
    spies.body.push({ part, enabled });
    return { data: enabled ? 'event: response.created\ndata: {"whole":true}' : undefined, isFetching: false, isError: false };
  },
}));

vi.mock('../components/icons', () => ({ CLIENT_META: {}, ClientGlyph: () => <span />, ModelGlyph: () => <span />, ProtocolGlyph: () => <span />, ProviderIcon: () => <span /> }));

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

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

afterEach(cleanup);

describe('request detail bodies', () => {
  it('renders a body only once opened, and fetches a cut one whole on request', () => {
    localStorage.setItem('astra.locale', 'en');
    render(
      <QueryClientProvider client={new QueryClient()}>
        <I18nProvider>
          <MemoryRouter>
            <RequestDetailSheet id="r1" onClose={() => {}} />
          </MemoryRouter>
        </I18nProvider>
      </QueryClientProvider>,
    );

    const note = t('requests.body.truncated', { shown: '64 KB', total: '8.0 MB' });
    // Closed panels render nothing.
    expect(screen.queryByText(note)).not.toBeInTheDocument();
    expect(screen.queryByText(/"input"/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: t('requests.body.upstreamResponse') }));
    expect(screen.getByText(note)).toBeInTheDocument();
    expect(spies.body.every((c) => !c.enabled)).toBe(true);

    // By text: jsdom never runs the panel's open animation, so the panel keeps its hidden start styles for role queries.
    fireEvent.click(screen.getByText(t('requests.body.loadAll')).closest('button')!);
    expect(spies.body.at(-1)).toEqual({ part: 'upstream-response', enabled: true });
    expect(screen.getByText(/"whole":true/)).toBeInTheDocument();
    expect(screen.queryByText(t('requests.body.loadAll'))).not.toBeInTheDocument();

    // A body under the limit is shown whole, pretty-printed, with no note.
    fireEvent.click(screen.getByRole('button', { name: t('requests.body.clientRequest') }));
    expect(screen.getByText(/"input"/)).toBeInTheDocument();
  });
});
