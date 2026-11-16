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

import { parseGroupWords, parseMember, runGroupAdd, runGroupList, runGroupRemove, runGroupSet, runGroupShow } from '../src/commands/group';
import { AstraError } from '../src/errors';

const GROUP = {
  id: 'g1', slug: 'opus', modelId: 'group/opus', name: 'Opus anywhere', mode: 'smart', stays: 'auto', enabled: true, listed: true,
  members: [{ provider: 'claude', model: 'claude-opus-5-5', weight: 1 }, { provider: 'copilot', model: 'claude-opus-5.5', weight: 1 }],
};

beforeEach(() => {
  calls.length = 0;
  responses.clear();
  responses.set('GET /api/routing/groups', [GROUP]);
  vi.spyOn(console, 'log').mockImplementation(() => {});
});

describe('astra group words', () => {
  it('splits a member at the first slash only', () => {
    expect(parseMember('openrouter/anthropic/claude-opus')).toEqual({ provider: 'openrouter', model: 'anthropic/claude-opus', weight: 1 });
    expect(parseMember('group/fast')).toEqual({ group: 'fast', weight: 1 });
    expect(() => parseMember('nope')).toThrow(AstraError);
    expect(() => parseMember('a/')).toThrow(AstraError);
  });

  it('reads models, routing and stays', () => {
    const edit = parseGroupWords(['models=claude/opus,copilot/opus', 'routing=pace', 'stays=session', 'listed=no']);
    expect(edit.members?.map((m) => `${m.provider}/${m.model}`)).toEqual(['claude/opus', 'copilot/opus']);
    expect(edit).toMatchObject({ mode: 'pace', stays: 'session', listed: false });
  });

  it('adds and removes members from the current ones', () => {
    const added = parseGroupWords(['models+=openrouter/anthropic/opus', 'models+=claude/claude-opus-5-5'], GROUP.members);
    expect(added.members).toHaveLength(3);
    const removed = parseGroupWords(['models-=copilot/claude-opus-5.5'], GROUP.members);
    expect(removed.members?.map((m) => m.provider)).toEqual(['claude']);
  });

  it('sets weights on members and clears them with an empty weight=', () => {
    const edit = parseGroupWords(['weight=claude/claude-opus-5-5=3'], GROUP.members);
    expect(edit.members?.map((m) => m.weight)).toEqual([3, 1]);
    expect(parseGroupWords(['weight='], [{ ...GROUP.members[0]!, weight: 5 }]).members?.[0]?.weight).toBe(1);
    expect(() => parseGroupWords(['weight=claude/claude-opus-5-5=0'], GROUP.members)).toThrow(AstraError);
    expect(() => parseGroupWords(['weight=other/model=2'], GROUP.members)).toThrow(AstraError);
  });

  it('accepts magpie’s names and refuses unknown words', () => {
    expect(parseGroupWords(['routing=fast']).mode).toBe('smart');
    expect(() => parseGroupWords(['routing=sideways'])).toThrow(AstraError);
    expect(() => parseGroupWords(['stays=never'])).toThrow(AstraError);
    expect(() => parseGroupWords(['colour=red'])).toThrow(AstraError);
    expect(() => parseGroupWords(['models'])).toThrow(AstraError);
  });
});

describe('astra group', () => {
  it('lists groups', async () => {
    await runGroupList();
    expect(calls.at(-1)).toMatchObject({ method: 'GET', path: '/api/routing/groups' });
  });

  it('creates a group from words', async () => {
    responses.set('POST /api/routing/groups', GROUP);
    await runGroupAdd('Opus anywhere', ['models=claude/claude-opus-5-5,copilot/claude-opus-5.5', 'routing=smart']);
    const call = calls.at(-1)!;
    expect(call).toMatchObject({ method: 'POST', path: '/api/routing/groups' });
    expect(call.body).toMatchObject({ name: 'Opus anywhere', mode: 'smart', members: [{ provider: 'claude', model: 'claude-opus-5-5' }, { provider: 'copilot' }] });
  });

  it('needs members to create one', async () => {
    await expect(runGroupAdd('Empty', ['routing=smart'])).rejects.toBeInstanceOf(AstraError);
  });

  it('finds a group by slug, group/slug or name and changes it', async () => {
    for (const ref of ['opus', 'group/opus', 'opus anywhere', 'g1']) {
      calls.length = 0;
      await runGroupSet(ref, ['routing=usage']);
      expect(calls.at(-1)).toMatchObject({ method: 'PATCH', path: '/api/routing/groups/g1', body: { mode: 'usage' } });
    }
    await expect(runGroupSet('nope', ['routing=usage'])).rejects.toBeInstanceOf(AstraError);
  });

  it('shows members with their live state', async () => {
    responses.set('GET /api/routing/groups/g1/status', {
      lastReason: 'soonest_reset', lastDecisionAt: '2026-11-16T10:00:00Z',
      members: [{ key: 'claude/claude-opus-5-5', weight: 1, health: 'healthy', usedPercent: 40 }],
    });
    await runGroupShow('opus');
    const output = vi.mocked(console.log).mock.calls.flat().join('\n');
    expect(output).toContain('group/opus');
    expect(output).toContain('Last choice: soonest_reset');
  });

  it('removes a group', async () => {
    await runGroupRemove('opus');
    expect(calls.at(-1)).toMatchObject({ method: 'DELETE', path: '/api/routing/groups/g1' });
  });
});
