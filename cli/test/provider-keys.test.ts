import { beforeEach, describe, expect, it, vi } from 'vitest';

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

vi.mock('../src/lib/prompt.js', () => ({
  promptHidden: vi.fn(async () => 'typed-key'),
  confirmPrompt: vi.fn(async () => true),
}));

import {
  runProviderKeysAdd,
  runProviderKeysList,
  runProviderKeysRemove,
  runProviderKeysSwitch,
} from '../src/commands/admin';
import { AstraError } from '../src/errors';

const KEYS = [
  { id: 'k1', displayName: 'Key 1', enabled: true, keyMasked: 'sk-…1111', weight: 1 },
  { id: 'k2', displayName: 'backup', enabled: true, keyMasked: 'sk-…2222', weight: 3, protocol: 'anthropic' },
];

beforeEach(() => {
  calls.length = 0;
  responses.clear();
  responses.set('GET /api/providers', [{ id: 'p1', name: 'Relay' }]);
  responses.set('GET /api/providers/p1/keys', KEYS);
  vi.spyOn(console, 'log').mockImplementation(() => {});
});

describe('astra provider keys', () => {
  it('lists the keys of a provider found by name', async () => {
    await runProviderKeysList('relay');
    expect(calls.at(-1)).toMatchObject({ method: 'GET', path: '/api/providers/p1/keys' });
  });

  it('adds a key with its options and reads a missing --key from the prompt', async () => {
    responses.set('POST /api/providers/p1/keys', KEYS);
    await runProviderKeysAdd('p1', { name: 'backup', weight: '3', protocol: 'anthropic' });
    expect(calls.at(-1)).toMatchObject({
      method: 'POST',
      path: '/api/providers/p1/keys',
      body: { key: 'typed-key', name: 'backup', weight: 3, protocol: 'anthropic' },
    });
    await expect(runProviderKeysAdd('p1', { key: 'sk-x', weight: 'heavy' })).rejects.toBeInstanceOf(AstraError);
  });

  it('removes and switches a key by name or position', async () => {
    await runProviderKeysRemove('p1', 'backup');
    expect(calls.at(-1)).toMatchObject({ method: 'DELETE', path: '/api/providers/p1/keys/k2' });
    await runProviderKeysSwitch('p1', '1', false);
    expect(calls.at(-1)).toMatchObject({ method: 'PATCH', path: '/api/providers/p1/keys/k1', body: { enabled: false } });
    await expect(runProviderKeysRemove('p1', 'nope')).rejects.toBeInstanceOf(AstraError);
  });
});
