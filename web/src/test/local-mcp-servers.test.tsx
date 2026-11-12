import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { LocalMcpCandidate, LocalMcpServer } from '../api/types';
import { LocalMcpServers } from '../components/LocalMcpServers';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

const server = (over: Partial<LocalMcpServer> = {}): LocalMcpServer => ({
  id: 's1', name: 'files', transport: 'stdio', command: 'npx', args: ['-y', 'srv'], envNames: ['API_KEY'], url: '', headerNames: [],
  enabled: true, clients: ['claude-code'],
  placements: [
    { kind: 'claude-code', wanted: true, state: 'applied', file: '/h/.claude.json' },
    { kind: 'codex', wanted: false, state: 'missing', file: '/h/.codex/config.toml' },
  ],
  ...over,
});

function show(servers: LocalMcpServer[], candidates: LocalMcpCandidate[] = []) {
  localStorage.setItem('astra.locale', 'en');
  const calls: { method: string; url: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      calls.push({ method, url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
      const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url.endsWith('/api/mcp/local/import') && method === 'GET') return json(candidates);
      if (url.endsWith('/api/mcp/local/import')) return json({ imported: [server()], skipped: [] });
      if (url.endsWith('/api/mcp/local') && method === 'GET') return json(servers);
      return json(server());
    }),
  );
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <I18nProvider>
        <FeedbackProvider>
          <LocalMcpServers />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
  return { calls };
}

describe('own MCP servers', () => {
  it('lists a server with its command, env count and the clients it is written into', async () => {
    show([server()]);
    expect(await screen.findByText('files')).toBeTruthy();
    expect(screen.getByText(/npx -y srv/)).toBeTruthy();
    expect(screen.getByText(/1 environment variables/)).toBeTruthy();
    expect(screen.getByText('Claude Code')).toBeTruthy();
    expect(screen.queryByText('Codex')).toBeNull(); // not wanted and not there
  });

  it('shows a remote server by its address', async () => {
    show([server({ transport: 'http', command: '', args: [], envNames: [], url: 'https://docs.example.com/mcp', headerNames: ['Authorization'] })]);
    expect(await screen.findByText(/docs\.example\.com/)).toBeTruthy();
    expect(screen.getByText(/1 headers/)).toBeTruthy();
  });

  it('imports the picked entries only', async () => {
    const { calls } = show([], [
      { kind: 'claude-code', name: 'files', transport: 'stdio', summary: 'npx -y srv', importable: true, taken: false },
      { kind: 'claude-code', name: 'old', transport: 'http', summary: '', importable: false, reason: 'sse transport', taken: false },
    ]);
    fireEvent.click(await screen.findByRole('button', { name: /Import existing/ }));
    const switches = await screen.findAllByRole('switch');
    expect(switches).toHaveLength(2);
    expect((switches[1] as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(switches[0]!);
    fireEvent.click(screen.getByRole('button', { name: 'Import selected' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({ items: [{ kind: 'claude-code', name: 'files' }] });
  });
});
