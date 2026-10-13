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

import { runPluginAdd, runPluginMarket, runPluginOptions, runPluginSwitch, runPluginUse } from '../src/commands/plugin';
import { AstraError } from '../src/errors';

const SPEC = '@astragate-community/middleware-word-guard@latest';
const LIST = {
  plugins: [
    { spec: SPEC, name: '@astragate-community/middleware-word-guard', shortName: 'word-guard', enabled: true, kinds: ['middleware'], optionsExample: { words: ['x'] } },
  ],
  runtime: { downloading: false },
};

describe('plugin commands', () => {
  let log: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    calls.length = 0;
    responses.clear();
    responses.set('GET /api/plugins', LIST);
    log = vi.spyOn(console, 'log').mockImplementation(() => {});
  });

  afterEach(() => log.mockRestore());

  it('finds a plugin by its short name, as the community READMEs write it', async () => {
    await runPluginSwitch('word-guard', false);
    expect(calls.at(-1)).toEqual({ path: `/api/plugins?spec=${encodeURIComponent(SPEC)}`, method: 'PATCH', body: { enabled: false } });
  });

  it('sets options from JSON, clears them with off, and refuses what is not an object', async () => {
    await runPluginOptions('word-guard', '{"words":["secret"]}');
    expect(calls.at(-1)?.body).toEqual({ options: { words: ['secret'] } });
    await runPluginOptions('word-guard', 'off');
    expect(calls.at(-1)?.body).toEqual({ options: null });
    await expect(runPluginOptions('word-guard', '[1]')).rejects.toBeInstanceOf(AstraError);
  });

  it('follows an install until its job ends', async () => {
    responses.set('POST /api/plugins', { id: 'j1', state: 'running', log: ['fetching'] });
    responses.set('GET /api/plugins/jobs/j1', { id: 'j1', state: 'succeeded', log: ['fetching', 'added'], result: SPEC });
    await runPluginAdd('@astragate-community/middleware-word-guard');
    expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual(['POST /api/plugins', 'GET /api/plugins/jobs/j1']);
  });

  it('a failed job fails the command', async () => {
    responses.set('POST /api/plugins', { id: 'j2', state: 'failed', log: [], error: 'npm has no such package' });
    await expect(runPluginAdd('nope')).rejects.toThrow('npm has no such package');
  });

  it('names an unknown plugin', async () => {
    await expect(runPluginUse('missing', 'zcode')).rejects.toThrow('No plugin "missing"');
  });

  it('lists the market with where each plugin comes from, and searches through the market', async () => {
    responses.set('GET /api/plugins/market', [
      { package: '@astra/middleware-hosted', name: 'Hosted', kind: 'middleware', latest: '1.0.0', summary: { en: 'Hosted here' }, origin: 'astra', tarballs: [{ version: '1.0.0' }] },
      { package: '@astra/plugin-npm', name: 'Npm', kind: 'provider', summary: {}, origin: 'astra', tarballs: [] },
      { package: '@astragate-community/middleware-model-map', name: 'Model Map', kind: 'middleware', summary: {}, origin: 'upstream', tarballs: [] },
    ]);
    await runPluginMarket();
    const out = log.mock.calls.map((c: unknown[]) => String(c[0])).join('\n');
    expect(out).toMatch(/@astra\/middleware-hosted\s+middleware\s+1\.0\.0\s+astra \(hosted\)/);
    expect(out).toMatch(/@astra\/plugin-npm\s+provider\s+astra\s/);
    expect(out).toMatch(/@astragate-community\/middleware-model-map\s+middleware\s+community\s/);

    responses.set('GET /api/plugins/search?q=zed%20auth', [{ spec: '@astra/plugin-zed', kind: 'provider', source: 'astra' }]);
    await runPluginMarket('zed auth');
    expect(calls.at(-1)?.path).toBe('/api/plugins/search?q=zed%20auth');
  });
});
