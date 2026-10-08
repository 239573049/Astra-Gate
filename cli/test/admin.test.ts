import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const calls = vi.hoisted(() => [] as { path: string; method: string; body?: unknown }[]);
const responses = vi.hoisted(() => new Map<string, unknown>());

vi.mock('../src/lib/server-lifecycle.js', () => ({
  ensureServerRunning: async () => ({ base: 'http://127.0.0.1:17321', runtime: undefined, started: false }),
}));

vi.mock('../src/lib/http.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/lib/http.js')>();
  return {
    ...actual,
    apiRequest: async (req: { path: string; method: string; body?: unknown }) => {
      calls.push({ path: req.path, method: req.method, body: req.body });
      return responses.get(`${req.method} ${req.path}`) ?? {};
    },
  };
});

import { runClientEnable, runTokenList } from '../src/commands/admin';
import { AstraError } from '../src/errors';

const PROVIDERS = [{ id: 'p1', name: 'Alpha' }];
const TOKENS = [
  {
    id: 'default',
    name: 'Default token',
    isDefault: true,
    enabled: true,
    clients: ['codex', 'opencode'],
    today: { costUsd: 1.5, totalTokens: 1200 },
    total: { costUsd: 12.25, totalTokens: 90000 },
  },
  { id: '01TOKEN', name: 'Work', isDefault: false, enabled: false, clients: [], today: { costUsd: 0, totalTokens: 0 }, total: { costUsd: 0 } },
];

describe('token commands', () => {
  let log: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    calls.length = 0;
    responses.clear();
    responses.set('GET /api/providers', PROVIDERS);
    responses.set('GET /api/tokens', TOKENS);
    log = vi.spyOn(console, 'log').mockImplementation(() => {});
  });

  afterEach(() => log.mockRestore());

  it('enable without --token leaves the token choice to the server', async () => {
    await runClientEnable('codex', { provider: 'Alpha' });
    const enable = calls.find((c) => c.path === '/api/clients/codex/enable');
    expect(enable?.body).toEqual({ providerId: 'p1' });
    expect(calls.some((c) => c.path === '/api/tokens')).toBe(false);
  });

  it('enable --token resolves a token by name or id', async () => {
    await runClientEnable('codex', { provider: 'p1', model: 'gpt-5', token: 'work' });
    expect(calls.find((c) => c.path === '/api/clients/codex/enable')?.body).toEqual({
      providerId: 'p1',
      model: 'gpt-5',
      tokenId: '01TOKEN',
    });
    calls.length = 0;
    await runClientEnable('opencode', { provider: 'p1', token: 'default' });
    expect(calls.find((c) => c.path === '/api/clients/opencode/enable')?.body).toEqual({
      providerId: 'p1',
      tokenId: 'default',
    });
  });

  it('enable --token with an unknown token fails before enabling', async () => {
    await expect(runClientEnable('codex', { provider: 'p1', token: 'nope' })).rejects.toBeInstanceOf(AstraError);
    expect(calls.some((c) => c.path.endsWith('/enable'))).toBe(false);
  });

  it('token list prints usage but never a plaintext token', async () => {
    await runTokenList();
    const out = log.mock.calls.map((c) => String(c[0])).join('\n');
    expect(out).toContain('TODAY COST');
    expect(out).toContain('Default token');
    expect(out).toContain('codex,opencode');
    expect(out).toContain('$1.50');
    expect(out).toContain('$12.25');
    expect(out).not.toContain('sk-astra-');
  });
});
