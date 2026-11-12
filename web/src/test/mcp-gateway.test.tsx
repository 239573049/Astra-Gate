import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { McpServer } from '../api/types';
import { McpGateway } from '../components/McpGateway';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';

const spies = vi.hoisted(() => ({
  updateSettings: vi.fn(),
  create: vi.fn(),
  update: vi.fn(),
  startOAuth: vi.fn(),
  servers: [] as unknown[],
}));

vi.mock('../api/hooks', () => ({
  useMcpSettings: () => ({ data: { enabled: false, allowedOrigins: [], endpoint: 'http://127.0.0.1:17321/mcp' } }),
  useUpdateMcpSettings: () => ({ mutate: spies.updateSettings, isPending: false }),
  useMcpServers: () => ({ data: spies.servers }),
  useCreateMcpServer: () => ({ mutate: spies.create, isPending: false }),
  useUpdateMcpServer: () => ({ mutate: spies.update, isPending: false }),
  useDeleteMcpServer: () => ({ mutate: vi.fn(), isPending: false }),
  useTestMcpServer: () => ({ mutate: vi.fn(), isPending: false, variables: undefined }),
  useStartMcpOAuth: () => ({ mutate: spies.startOAuth, isPending: false }),
  useForgetMcpOAuth: () => ({ mutate: vi.fn(), isPending: false }),
  useMcpCalls: () => ({
    data: [{ id: 1, at: '2026-11-10T08:00:00Z', tokenId: 't', tokenName: 'Agent', serverId: 's1', serverSlug: 'github', tool: 'search', durationMs: 12, ok: false, errorCategory: 'needs_auth', frontEra: 'modern' }],
  }),
}));

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

// the per-client entries have their own test (client-mcp.test.tsx); this page test is about the page itself
vi.mock('../components/ClientMcpEntries', () => ({ ClientMcpEntries: () => null }));
// likewise the own-servers section (local-mcp-servers.test.tsx)
vi.mock('../components/LocalMcpServers', () => ({ LocalMcpServers: () => null }));

vi.mock('motion/react', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useReducedMotion: () => true,
}));

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

const server = (over: Partial<McpServer> = {}): McpServer => ({
  id: 's1', slug: 'github', name: 'GitHub', transport: 'http', url: 'https://mcp.example.com/mcp', authType: 'none', headerNames: [], enabled: true,
  timeoutSeconds: 60, status: 'ok', protocolVersion: '2026-07-28', boundTokens: 2, createdAt: '2026-11-10T00:00:00Z', updatedAt: '2026-11-10T00:00:00Z', ...over,
});

const show = () => {
  localStorage.setItem('astra.locale', 'en');
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <I18nProvider>
        <FeedbackProvider>
          <McpGateway />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
  Element.prototype.scrollIntoView = () => {};
  for (const spy of [spies.updateSettings, spies.create, spies.update, spies.startOAuth]) spy.mockReset();
  spies.servers = [];
  vi.stubGlobal('open', vi.fn());
});
afterEach(cleanup);

describe('MCP gateway page', () => {
  it('shows the address clients use and switches the gateway on', () => {
    show();
    expect(screen.getByText('http://127.0.0.1:17321/mcp')).toBeTruthy();
    fireEvent.click(screen.getByRole('switch', { name: t('mcp.gateway.enable') }));
    expect(spies.updateSettings).toHaveBeenCalledWith({ enabled: true }, expect.anything());
  });

  it('lists each server with what it last showed', () => {
    spies.servers = [server(), server({ id: 's2', slug: 'notion', name: 'Notion', status: 'needs_auth', authType: 'oauth', oauth: { authorized: false }, protocolVersion: null })];
    show();
    expect(screen.getByText('GitHub')).toBeTruthy();
    expect(screen.getByText(t('mcp.status.ok'))).toBeTruthy();
    // The status badge of the Notion server and the failed call below share the sentence.
    expect(screen.getAllByText(t('mcp.status.needs_auth'))).toHaveLength(2);
    expect(screen.getByText(/Protocol 2026-07-28/)).toBeTruthy();
    // The recent calls say who called what and how it ended — never what was passed.
    expect(screen.getByText('Agent')).toBeTruthy();
  });

  it('shows an empty state before the first server', () => {
    show();
    expect(screen.getByText(t('mcp.servers.emptyTitle'))).toBeTruthy();
  });

  it('opens the authorization page the server returned, and nothing when none is needed', () => {
    spies.servers = [server({ authType: 'oauth', oauth: { authorized: false }, status: 'needs_auth' })];
    spies.startOAuth.mockImplementation((_id: string, opts: { onSuccess: (r: unknown) => void }) => opts.onSuccess({ authorizationUrl: 'https://as.example/authorize?state=x' }));
    show();
    fireEvent.click(screen.getByRole('button', { name: t('mcp.server.authorize') }));
    expect(spies.startOAuth).toHaveBeenCalledWith('s1', expect.anything());
    expect(window.open).toHaveBeenCalledWith('https://as.example/authorize?state=x', '_blank', 'noopener');

    (window.open as ReturnType<typeof vi.fn>).mockClear();
    spies.startOAuth.mockImplementation((_id: string, opts: { onSuccess: (r: unknown) => void }) => opts.onSuccess({ authorizationUrl: null, message: 'nothing to do' }));
    fireEvent.click(screen.getByRole('button', { name: t('mcp.server.authorize') }));
    expect(window.open).not.toHaveBeenCalled();
  });

  it('creates a server from a name and an address', () => {
    show();
    fireEvent.click(screen.getByRole('button', { name: t('mcp.servers.add') }));
    const dialog = screen.getByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(t('mcp.server.name')), { target: { value: ' GitHub ' } });
    fireEvent.change(within(dialog).getByLabelText(t('mcp.server.url')), { target: { value: 'https://mcp.example.com/mcp' } });
    fireEvent.click(within(dialog).getByRole('button', { name: t('common.save') }));
    expect(spies.create).toHaveBeenCalledWith(
      { name: 'GitHub', url: 'https://mcp.example.com/mcp', transport: 'http', authType: 'none', enabled: true, timeoutSeconds: 60 },
      expect.anything(),
    );
  });

  it('never shows a stored header value and keeps it unless a new one is typed', async () => {
    spies.servers = [server({ authType: 'header', headerNames: ['Authorization'] })];
    show();
    fireEvent.pointerDown(screen.getByRole('button', { name: 'GitHub' }), { button: 0, ctrlKey: false, pointerType: 'mouse' }); // the row's action menu
    fireEvent.click(await screen.findByRole('menuitem', { name: t('common.edit') }), { detail: 1 });
    const dialog = screen.getByRole('dialog');
    const value = within(dialog).getByLabelText(t('mcp.server.headerValue')) as HTMLInputElement;
    expect(value.value).toBe('');
    expect(value.placeholder).toBe(t('mcp.server.headerKept'));
    fireEvent.click(within(dialog).getByRole('button', { name: t('common.save') }));
    // null = keep what the server stored.
    expect(spies.update.mock.calls[0]![0]).toMatchObject({ id: 's1', body: { authType: 'header', headers: { Authorization: null } } });
  });
});
