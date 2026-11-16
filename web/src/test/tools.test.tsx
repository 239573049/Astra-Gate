import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { SearchKeyQuota, ToolInfo, ToolsSettings as ToolsSettingsData } from '../api/types';
import { moved, reordered, ToolsSettings } from '../components/ToolsSettings';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';

const spies = vi.hoisted(() => ({
  update: vi.fn(),
  addKey: vi.fn(),
  updateKey: vi.fn(),
  updateApi: vi.fn(),
  reorderApis: vi.fn(),
  reorderKeys: vi.fn(),
  test: vi.fn(),
  data: undefined as unknown,
  quota: undefined as unknown,
  tools: [] as unknown[],
  mutation: (fn: (...args: never[]) => unknown) => () => ({ mutate: fn, isPending: false }),
}));

vi.mock('../api/hooks', () => ({
  useToolsSettings: () => ({ data: spies.data }),
  useTools: () => ({ data: spies.tools }),
  useUpdateToolsSettings: spies.mutation(spies.update),
  useAddSearchApi: spies.mutation(vi.fn()),
  useReorderSearchApis: spies.mutation(spies.reorderApis),
  useUpdateSearchApi: spies.mutation(spies.updateApi),
  useDeleteSearchApi: spies.mutation(vi.fn()),
  useAddSearchKey: spies.mutation(spies.addKey),
  useUpdateSearchKey: spies.mutation(spies.updateKey),
  useDeleteSearchKey: spies.mutation(vi.fn()),
  useReorderSearchKeys: spies.mutation(spies.reorderKeys),
  useTestSearchApi: spies.mutation(spies.test),
  useSearchQuota: () => ({ data: spies.quota, refetch: vi.fn() }),
}));

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

const settings = (over: Partial<ToolsSettingsData['search']> = {}): ToolsSettingsData => ({
  search: {
    onBehalf: false,
    mode: 'auto',
    resultCount: 6,
    apis: [
      {
        vendor: 'tavily', name: 'Tavily', enabled: true, pick: 'turns', keysUrl: 'https://app.tavily.com/home', keyOptional: false, needsUrl: false,
        plugin: false, available: true, canReadQuota: true,
        keys: [
          { id: 'a1', masked: 'tvl…y-1', enabled: true },
          { id: 'b2', masked: 'tvl…y-2', enabled: false, resting: 'rate limited', restUntil: '2026-11-16T10:00:00Z' },
        ],
      },
      { vendor: 'searxng', name: 'SearXNG', enabled: false, pick: 'turns', url: 'http://127.0.0.1:8888', keyOptional: true, needsUrl: true, plugin: false, available: true, canReadQuota: false, keys: [] },
    ],
    available: [{ vendor: 'brave', name: 'Brave Search', keyOptional: false, needsUrl: false, plugin: false }],
    ...over,
  },
  fetch: { maxBytes: 5 * 1024 * 1024, timeoutSeconds: 30, maxChars: 20000, allowPrivate: false },
  image: { model: '', size: '' },
  mcpEndpoint: 'http://127.0.0.1:17321/mcp/astra',
});

const show = () => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <ToolsSettings />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  for (const s of [spies.update, spies.addKey, spies.updateKey, spies.updateApi, spies.reorderApis, spies.reorderKeys, spies.test]) s.mockReset();
  spies.data = settings();
  spies.quota = undefined;
  spies.tools = [
    { name: 'web_search', title: 'Web search', source: 'builtin', available: false, reason: 'no search API is on' },
    { name: 'web_fetch', title: 'Web fetch', description: 'Fetch a page', source: 'builtin', available: true },
    { name: 'demo.tool', title: 'tool', description: 'A plugin tool', source: 'plugin', available: true },
  ] satisfies ToolInfo[];
});
afterEach(cleanup);

describe('reordering', () => {
  it('moves a dragged id onto another and says nothing moved when it did not', () => {
    expect(reordered(['a', 'b', 'c'], 'a', 'c')).toEqual(['b', 'c', 'a']);
    expect(reordered(['a', 'b', 'c'], 'c', 'a')).toEqual(['c', 'a', 'b']);
    expect(reordered(['a', 'b', 'c'], 'b', 'b')).toBeNull();
    expect(reordered(['a', 'b'], 'x', 'a')).toBeNull();
    expect(moved(['a', 'b', 'c'], 'b', -1)).toEqual(['b', 'a', 'c']);
    expect(moved(['a', 'b', 'c'], 'a', -1)).toBeNull();
    expect(moved(['a', 'b', 'c'], 'c', 1)).toBeNull();
  });
});

describe('tools settings', () => {
  it('lists the search APIs with masked keys only, and why a key is resting', () => {
    show();
    expect(screen.getByText('Tavily')).toBeInTheDocument();
    expect(screen.getByText('SearXNG')).toBeInTheDocument();
    expect(screen.getByText('tvl…y-1')).toBeInTheDocument();
    expect(screen.getByText(t('tools.search.rest.rate_limited'))).toBeInTheDocument();
    expect(screen.getByRole('switch', { name: t('tools.onBehalf.enable') })).not.toBeChecked();
    expect(screen.getByRole('switch', { name: `tvl…y-2: ${t('tools.search.enabled')}` })).not.toBeChecked();
  });

  it('switches search on behalf of models on and sends only that change', () => {
    show();
    fireEvent.click(screen.getByRole('switch', { name: t('tools.onBehalf.enable') }));
    expect(spies.update.mock.calls[0]![0]).toEqual({ search: { onBehalf: true } });
  });

  it('adds a key from the field, trimmed, and keeps the button off while it is empty', () => {
    show();
    const add = screen.getAllByRole('button', { name: t('tools.search.addKey') })[0]!;
    expect(add).toBeDisabled();
    fireEvent.change(screen.getAllByLabelText(t('tools.search.key'))[0]!, { target: { value: '  tvly-new-key  ' } });
    expect(add).toBeEnabled();
    fireEvent.click(add);
    expect(spies.addKey.mock.calls[0]![0]).toEqual({ vendor: 'tavily', key: 'tvly-new-key' });
  });

  it('switches one key and one API off without touching the rest', () => {
    show();
    fireEvent.click(screen.getByRole('switch', { name: `tvl…y-2: ${t('tools.search.enabled')}` }));
    expect(spies.updateKey.mock.calls[0]![0]).toEqual({ vendor: 'tavily', id: 'b2', enabled: true });
    fireEvent.click(screen.getByRole('switch', { name: `SearXNG: ${t('tools.search.enabled')}` }));
    expect(spies.updateApi.mock.calls[0]![0]).toEqual({ vendor: 'searxng', body: { enabled: true } });
  });

  it('reorders the APIs with the arrows (the first cannot go up) and sends the new order', () => {
    show();
    const up = screen.getAllByRole('button', { name: t('tools.search.moveUp') });
    expect(up[0]).toBeDisabled();
    fireEvent.click(screen.getAllByRole('button', { name: t('tools.search.moveDown') })[0]!);
    expect(spies.reorderApis.mock.calls[0]![0]).toEqual(['searxng', 'tavily']);
  });

  it('chooses how several keys are picked', () => {
    show();
    fireEvent.click(screen.getByRole('button', { name: t('tools.search.pick.order') }));
    expect(spies.updateApi.mock.calls[0]![0]).toEqual({ vendor: 'tavily', body: { pick: 'order' } });
  });

  it('shows what each Tavily key has used, or that it is unknown', () => {
    spies.quota = {
      a1: { keyId: 'a1', known: true, unlimited: false, used: 950, limit: 1000, of: 'key', paygoLeft: 0, usedUp: false },
      b2: { keyId: 'b2', known: false, unlimited: false, used: 0, limit: 0, paygoLeft: 0, usedUp: false, error: '401' },
    } satisfies Record<string, SearchKeyQuota>;
    show();
    expect(screen.getByText(/950 of 1000 used/)).toBeInTheDocument();
    expect(screen.getByText(/usage unknown 401/)).toBeInTheDocument();
  });

  it('says why a tool is not available and lists the plugin tools', () => {
    show();
    expect(screen.getByText(t('tools.reason.web_search'))).toBeInTheDocument();
    expect(screen.getByText('demo.tool')).toBeInTheDocument();
    expect(screen.getByText('http://127.0.0.1:17321/mcp/astra')).toBeInTheDocument();
  });

  it('saves the image model and size together, only when they changed', () => {
    show();
    const save = screen.getAllByRole('button', { name: t('common.save') }).at(-1)!;
    expect(save).toBeDisabled();
    fireEvent.change(screen.getByLabelText(t('tools.image.model')), { target: { value: 'gpt-image-1' } });
    expect(save).toBeEnabled();
    fireEvent.click(save);
    expect(spies.update.mock.calls[0]![0]).toEqual({ image: { model: 'gpt-image-1', size: '' } });
  });

  it('turns the private-address switch on through the same update call', () => {
    show();
    fireEvent.click(screen.getByRole('switch', { name: t('tools.fetch.private') }));
    expect(spies.update.mock.calls[0]![0]).toEqual({ fetch: { allowPrivate: true } });
  });
});
