import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ClientMcpEntry } from '../api/types';
import { ClientMcpEntries } from '../components/ClientMcpEntries';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

const entry = (over: Partial<ClientMcpEntry> & Pick<ClientMcpEntry, 'kind'>): ClientMcpEntry => ({
  state: 'missing', file: '/home/u/.claude.json', url: null, tokenId: null, tokenName: null, gatewayEnabled: true,
  gatewayUrl: 'http://127.0.0.1:17321/mcp', error: null, ...over,
});

function show(entries: ClientMcpEntry[], tokens: { id: string; name: string; enabled: boolean; mcpServers: string[] }[]) {
  localStorage.setItem('astra.locale', 'en');
  const calls: { method: string; url: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      calls.push({ method, url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
      const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url.endsWith('/api/clients/mcp')) return json(entries);
      if (url.endsWith('/api/tokens')) return json(tokens);
      return json(entries[0]);
    }),
  );
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <I18nProvider>
        <FeedbackProvider>
          <ClientMcpEntries />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
  return { calls };
}

describe('client MCP entries', () => {
  it('writes the entry with the token the list offers, and never sends a key', async () => {
    const { calls } = show(
      [entry({ kind: 'claude-code' }), entry({ kind: 'codex', file: '/home/u/.codex/config.toml' })],
      [
        { id: 't1', name: 'Work', enabled: true, mcpServers: ['s1'] },
        { id: 't2', name: 'Nothing bound', enabled: true, mcpServers: [] },
      ],
    );
    const buttons = await screen.findAllByRole('button', { name: 'Write' });
    expect(buttons).toHaveLength(2);
    fireEvent.click(buttons[0]!);
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    const post = calls.find((c) => c.method === 'POST')!;
    expect(post.url).toContain('/api/clients/claude-code/mcp');
    expect(post.body).toEqual({ tokenId: 't1', force: false });
  });

  it('says so when no token has MCP servers bound, and does not offer a token', async () => {
    show([entry({ kind: 'claude-code' }), entry({ kind: 'codex' })], [{ id: 't2', name: 'Nothing bound', enabled: true, mcpServers: [] }]);
    expect(await screen.findByText(/No token has MCP servers bound yet/)).toBeTruthy();
    for (const b of await screen.findAllByRole('button', { name: 'Write' })) expect((b as HTMLButtonElement).disabled).toBe(true);
  });

  it('never offers to overwrite an entry the user wrote', async () => {
    show([entry({ kind: 'claude-code', state: 'conflict' }), entry({ kind: 'codex' })], [{ id: 't1', name: 'Work', enabled: true, mcpServers: ['s1'] }]);
    expect(await screen.findByText(/did not write, and Astra will not overwrite it/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Overwrite and write again' })).toBeNull();
    expect((screen.getAllByRole('button', { name: 'Write' })[0] as HTMLButtonElement).disabled).toBe(true);
  });

  it('offers the overwrite for a changed entry and warns while the gateway is off', async () => {
    show(
      [entry({ kind: 'claude-code', state: 'drifted', tokenId: 't1', tokenName: 'Work', gatewayEnabled: false }), entry({ kind: 'codex' })],
      [{ id: 't1', name: 'Work', enabled: true, mcpServers: ['s1'] }],
    );
    expect(await screen.findByRole('button', { name: 'Overwrite and write again' })).toBeTruthy();
    expect(screen.getAllByText(/The gateway is off/).length).toBeGreaterThan(0);
  });
});
