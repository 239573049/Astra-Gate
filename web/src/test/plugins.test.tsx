import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { MarketListing, Plugin, PluginList } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';
import { PluginsPage } from '../pages/PluginsPage';

const spies = vi.hoisted(() => ({ patch: vi.fn(), add: vi.fn(), reorder: vi.fn() }));

const wordGuard: Plugin = {
  spec: '@magpie-community/middleware-word-guard@latest',
  name: '@magpie-community/middleware-word-guard',
  shortName: 'word-guard',
  version: '0.1.1',
  enabled: true,
  kinds: ['middleware'],
  source: 'market',
  optionsExample: { words: ['x'] },
  isPath: false,
  middleware: { hooks: ['onRequest', 'onEvent'], calls: 12, avgMicros: 3.25, failures: 1, lastError: 'onEvent: TypeError: boom' },
};
const aider: Plugin = {
  spec: '/Users/me/agent-aider',
  name: '@magpie-community/agent-aider',
  shortName: 'agent-aider',
  enabled: false,
  kinds: ['agent'],
  isPath: true,
  agent: { id: 'aider', kind: 'plugin-aider', name: 'Aider' },
};
const list: PluginList = { plugins: [wordGuard, aider], preferences: {}, runtime: { downloading: false, version: '1.4.2', target: 'bun-darwin-aarch64' } };
const market: MarketListing[] = [
  { package: '@magpie-community/middleware-word-guard', name: 'Word Guard', kind: 'middleware', providers: [], summary: { en: 'Sensitive-word filter' }, latest: '0.1.1' },
  { package: '@magpie-community/opencode-zcode-auth', name: 'ZCode', kind: 'provider', providers: ['zcode'], summary: { en: 'GLM Coding Plan' }, latest: '0.1.15' },
];

vi.mock('../api/hooks', () => {
  const mutation = (fn: ReturnType<typeof vi.fn>) => () => ({ mutate: fn, isPending: false });
  return {
    usePlugins: () => ({ data: list, isLoading: false, isError: false }),
    usePluginHost: () => ({ data: { running: false, plugins: [] } }),
    usePluginJob: () => ({ data: undefined }),
    usePluginMarket: () => ({ data: market, isLoading: false, isError: false }),
    usePluginSearch: () => ({ data: undefined, isLoading: false }),
    usePatchPlugin: mutation(spies.patch),
    useAddPlugin: mutation(spies.add),
    useReorderPlugins: mutation(spies.reorder),
    useUpdatePlugin: mutation(vi.fn()),
    useRemovePlugin: mutation(vi.fn()),
    usePreferPlugin: mutation(vi.fn()),
    useInstallBun: mutation(vi.fn()),
    useMagpieImportPreview: () => ({ data: { found: true, path: '~/.config/magpie/plugins.json', signIns: 2, plugins: [{ spec: 'opencode-gemini-auth@latest', off: false, hasOptions: false, installed: false }] } }),
    useImportMagpie: mutation(vi.fn()),
  };
});

// The brand icons pull in a JSON data set jsdom can't import: a plain mark stands in.
vi.mock('../components/icons', () => ({ ProviderIcon: () => <span data-testid="provider-icon" /> }));

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

// jsdom has no ResizeObserver; a noop observer never fires its callback.
class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

function renderAt(path: string) {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <I18nProvider>
        <FeedbackProvider>
          <MemoryRouter initialEntries={[path]}>
            <Routes>
              <Route path="plugins" element={<PluginsPage />} />
              <Route path="plugins/:tab" element={<PluginsPage />} />
            </Routes>
          </MemoryRouter>
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('PluginsPage', () => {
  it('lists installed plugins with their kind, middleware counters and the client an agent adds', () => {
    renderAt('/plugins');
    expect(screen.getByText('word-guard')).toBeTruthy();
    expect(screen.getByText(/onRequest, onEvent/)).toBeTruthy();
    expect(screen.getByText(/onEvent: TypeError: boom/)).toBeTruthy();
    expect(screen.getByText(/Aider/)).toBeTruthy();
  });

  it('switches a plugin off and moves it in the middleware order', () => {
    renderAt('/plugins');
    const switches = screen.getAllByRole('switch');
    fireEvent.click(switches[0]!);
    expect(spies.patch).toHaveBeenCalledWith({ spec: wordGuard.spec, enabled: false }, expect.anything());

    const down = screen.getAllByRole('button', { name: /move down|下移/i })[0]!;
    fireEvent.click(down);
    expect(spies.reorder).toHaveBeenCalledWith([aider.spec, wordGuard.spec], expect.anything());
  });

  it('shows the market as cards, installed ones marked, and filters by kind', () => {
    renderAt('/plugins/market');
    expect(screen.getByText('ZCode')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /^(Install|安装)$/ })).toHaveLength(1);
    expect(screen.getAllByText(/^(Installed|已安装)$/).filter((el) => !el.closest('[role="tab"]'))).toHaveLength(1);

    fireEvent.click(screen.getByText(/^(Middleware|中间件) 1$/));
    expect(screen.queryByText('ZCode')).toBeNull();
    expect(screen.getByText('Word Guard')).toBeTruthy();
  });

  it('offers to import what magpie has', () => {
    renderAt('/plugins');
    expect(screen.getByRole('button', { name: /^(Import from magpie|从 magpie 导入)$/ })).toBeTruthy();
  });
});
