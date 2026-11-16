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

import {
  runClientShortlist,
  runClientUse,
  runProfileList,
  runProfileRemove,
  runProfileSave,
  runProfileUse,
} from '../src/commands/admin';
import { AstraError } from '../src/errors';

const PROFILE = { id: '01P', name: 'Work', clients: [{ kind: 'codex', enabled: true }], active: true, drifted: false };

describe('profile commands', () => {
  let log: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    calls.length = 0;
    responses.clear();
    responses.set('GET /api/profiles', [PROFILE]);
    log = vi.spyOn(console, 'log').mockImplementation(() => {});
  });

  afterEach(() => log.mockRestore());

  it('save creates a new profile, and overwrites one of the same name', async () => {
    responses.set('POST /api/profiles', { ...PROFILE, name: 'Home' });
    await runProfileSave('Home');
    expect(calls.at(-1)).toEqual({ path: '/api/profiles', method: 'POST', body: { name: 'Home' } });

    responses.set('PUT /api/profiles/01P', PROFILE);
    await runProfileSave(' work ');
    expect(calls.at(-1)).toEqual({ path: '/api/profiles/01P', method: 'PUT', body: { overwrite: true } });
  });

  it('use applies by name and reports skipped clients; --dry-run only previews', async () => {
    const plan = {
      profile: PROFILE,
      steps: [
        { kind: 'codex', action: 'enable' },
        { kind: 'gemini-cli', action: 'skipped', reason: 'not-installed' },
      ],
      dropped: [{ kind: 'codex', what: 'provider', id: 'gone', reason: 'missing' }],
    };
    responses.set('POST /api/profiles/Work/apply', plan);
    responses.set('GET /api/profiles/Work/preview', plan);
    await runProfileUse('Work');
    expect(calls.at(-1)?.method).toBe('POST');
    const out = log.mock.calls.map((c) => String(c[0])).join('\n');
    expect(out).toContain('gemini-cli');
    expect(out).toContain('not-installed');

    calls.length = 0;
    await runProfileUse('Work', { dryRun: true });
    expect(calls).toEqual([{ path: '/api/profiles/Work/preview', method: 'GET', body: undefined }]);
  });

  it('list and rm', async () => {
    await runProfileList();
    expect(log.mock.calls.map((c) => String(c[0])).join('\n')).toContain('Work');
    await runProfileRemove('Work');
    expect(calls.at(-1)).toEqual({ path: '/api/profiles/Work', method: 'DELETE', body: undefined });
  });
});

describe('client use and shortlist', () => {
  let log: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    calls.length = 0;
    responses.clear();
    responses.set('GET /api/providers', [{ id: 'p1', name: 'deepseek' }]);
    responses.set('GET /api/clients', [{ kind: 'claude-code', providerId: 'p1' }]);
    log = vi.spyOn(console, 'log').mockImplementation(() => {});
  });

  afterEach(() => log.mockRestore());

  it('use <provider>/<model> enables the client on that pair', async () => {
    await runClientUse('claude-code', 'deepseek/deepseek-v4-pro');
    expect(calls.find((c) => c.path === '/api/clients/claude-code/enable')?.body).toEqual({
      providerId: 'p1',
      model: 'deepseek-v4-pro',
    });
  });

  it('a target without a known provider prefix is a model of the current provider', async () => {
    await runClientUse('claude-code', 'org/model-x');
    expect(calls.find((c) => c.path === '/api/clients/claude-code/enable')?.body).toEqual({
      providerId: 'p1',
      model: 'org/model-x',
    });
  });

  it('use without any provider fails before enabling', async () => {
    responses.set('GET /api/clients', [{ kind: 'claude-code' }]);
    await expect(runClientUse('claude-code', 'model-x')).rejects.toBeInstanceOf(AstraError);
    expect(calls.some((c) => c.path.endsWith('/enable'))).toBe(false);
  });

  it('shortlist sends the ids; none clears it', async () => {
    await runClientShortlist('codex', ['a', 'b']);
    expect(calls.at(-1)).toEqual({ path: '/api/clients/codex/shortlist', method: 'PUT', body: { models: ['a', 'b'] } });
    await runClientShortlist('codex', []);
    expect(calls.at(-1)?.body).toEqual({ models: [] });
  });
});
