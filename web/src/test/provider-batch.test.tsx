import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('../components/icons', () => ({ ProviderIcon: () => <span /> }));
vi.mock('../components/layout/Page', () => ({
  Page: ({ title, actions, children }: { title: string; actions?: React.ReactNode; children: React.ReactNode }) => (
    <div>
      <h1>{title}</h1>
      {actions}
      {children}
    </div>
  ),
}));

import type { Provider } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';
import { ProvidersPage } from '../pages/ProvidersPage';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

const PROVIDERS: Provider[] = [
  {
    id: 'p1',
    templateId: 'openai',
    templateVersion: 1,
    templateUpdateAvailable: false,
    name: 'Alpha',
    icon: 'openai',
    category: 'official',
    endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://alpha.example/v1' }],
    preferredUpstreamProtocols: ['openai-chat'],
    authScheme: 'bearer',
    hasApiKey: true,
    apiKeyMasked: 'sk-…1111',
    extraHeaders: {},
    httpProxy: null,
    priceMultiplier: 1,
    priceKey: 'openai',
    adapterId: null,
    settings: {},
    enabled: true,
    sortOrder: 0,
    notes: null,
    website: null,
    modelCount: 2,
    boundClients: ['codex'],
    createdAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-01T00:00:00Z',
    quotaConfig: { enabled: false, effectiveIntervalMinutes: 15, timeoutSec: 15, params: {}, secrets: [] },
    quota: null,
    quotaCheckedAtUtc: null,
  },
  {
    id: 'p2',
    templateId: 'deepseek',
    templateVersion: 1,
    templateUpdateAvailable: false,
    name: 'Beta',
    icon: 'deepseek',
    category: 'official',
    endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://beta.example/v1' }],
    preferredUpstreamProtocols: ['openai-chat'],
    authScheme: 'bearer',
    hasApiKey: true,
    apiKeyMasked: 'sk-…2222',
    extraHeaders: {},
    httpProxy: null,
    priceMultiplier: 1,
    priceKey: 'deepseek',
    adapterId: null,
    settings: {},
    enabled: true,
    sortOrder: 1,
    notes: null,
    website: null,
    modelCount: 1,
    boundClients: [],
    createdAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-01T00:00:00Z',
    quotaConfig: { enabled: false, effectiveIntervalMinutes: 15, timeoutSec: 15, params: {}, secrets: [] },
    quota: null,
    quotaCheckedAtUtc: null,
  },
];

function show() {
  localStorage.setItem('astra.locale', 'en');
  const calls: { method: string; url: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const body = init?.body ? JSON.parse(String(init.body)) : undefined;
      calls.push({ method, url, body });
      const json = (v: unknown) => new Response(JSON.stringify(v), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url.endsWith('/api/providers')) return json(PROVIDERS);
      if (url.endsWith('/api/providers/batch/enabled')) return json({ affected: (body as { ids: string[] }).ids.length });
      if (url.endsWith('/api/providers/batch/delete')) return json({ affected: (body as { ids: string[] }).ids.length });
      return json([]);
    }),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <MemoryRouter>
            <ProvidersPage />
          </MemoryRouter>
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
  return { calls };
}

describe('provider batch operations', () => {
  it('selects providers and disables or deletes them in one request', async () => {
    const { calls } = show();
    await screen.findByText('Alpha');

    fireEvent.click(screen.getByRole('button', { name: 'Batch actions' }));
    const bar = await screen.findByTestId('providers-batch-bar');
    expect(bar.textContent).toContain('0 selected');

    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
    expect(bar.textContent).toContain('2 selected');

    fireEvent.click(screen.getByRole('button', { name: 'Disable' }));
    await waitFor(() => expect(calls.some((c) => c.url.endsWith('/api/providers/batch/enabled'))).toBe(true));
    expect(calls.find((c) => c.url.endsWith('/api/providers/batch/enabled'))!.body).toEqual({ ids: ['p1', 'p2'], enabled: false });

    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    await screen.findByText('Delete 2 selected providers?');
    expect(screen.getByText(/codex/)).toBeTruthy();

    const confirmButtons = screen.getAllByRole('button', { name: 'Delete' });
    fireEvent.click(confirmButtons.at(-1)!);
    await waitFor(() => expect(calls.some((c) => c.url.endsWith('/api/providers/batch/delete'))).toBe(true));
    expect(calls.find((c) => c.url.endsWith('/api/providers/batch/delete'))!.body).toEqual({ ids: ['p1', 'p2'] });
  });
});
