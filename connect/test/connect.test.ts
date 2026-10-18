import fs from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { doctor } from '../src/commands/doctor.js';
import { join } from '../src/commands/join.js';
import { leave } from '../src/commands/leave.js';
import { status } from '../src/commands/status.js';
import { UserError } from '../src/lib/io.js';
import { defaultModelFor } from '../src/lib/clients.js';
import { readProfile } from '../src/lib/profile.js';
import { plainUi, silentUi } from '../src/lib/ui.js';
import { CONTEXT, KEY, startGateway, machine, type FakeGateway, type Machine } from './helpers.js';

const SETTINGS = '{\n  // my settings\n  "env": { "EDITOR": "vim" },\n  "includeCoAuthoredBy": false\n}\n';
const read = (m: Machine, rel: string) => fs.readFileSync(path.join(m.home, rel), 'utf8');

let gw: FakeGateway;
let m: Machine;

beforeEach(async () => {
  gw = await startGateway();
  m = machine({ '.claude/settings.json': SETTINGS }, { ASTRA_API_KEY: KEY });
});

afterEach(async () => {
  m.cleanup();
  await gw.close();
});

describe('join', () => {
  it('points a detected client at the gateway with the key, and saves the profile', async () => {
    const result = await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    expect(result.name).toBe('张三');
    expect(result.clients).toMatchObject([{ kind: 'claude-code', status: 'applied' }]);

    const settings = read(m, '.claude/settings.json');
    expect(settings).toContain('// my settings');
    expect(settings).toContain(`"ANTHROPIC_BASE_URL": "${gw.url}"`);
    expect(settings).toContain(`"ANTHROPIC_AUTH_TOKEN": "${KEY}"`);
    // Claude Code gets a Claude model of the key, not the first one in the list (gpt-5).
    expect(settings).toContain('"ANTHROPIC_MODEL": "claude-sonnet-4-5"');

    const profile = readProfile(m.rt.paths)!;
    expect(profile).toMatchObject({ gatewayUrl: gw.url, apiKey: KEY, name: '张三', clients: ['claude-code'] });
    expect(fs.statSync(m.rt.paths.profileFile).mode & 0o777).toBe(0o600);
  });

  it('takes every detected client with --clients all and honours --model', async () => {
    const result = await join(m.rt, silentUi, m.io, gw.url, { clients: 'all', model: 'gpt-5', yes: true });
    expect(result.clients.map((c) => c.kind)).toEqual(['claude-code']);
    expect(read(m, '.claude/settings.json')).toContain('"ANTHROPIC_MODEL": "gpt-5"');
  });

  it('refuses a model the key cannot use', async () => {
    await expect(join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', model: 'nope', yes: true })).rejects.toThrow(UserError);
    expect(read(m, '.claude/settings.json')).toBe(SETTINGS);
  });

  it('is idempotent: joining twice changes nothing the second time', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    const after = read(m, '.claude/settings.json');
    const again = await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    expect(again.clients[0]!.status).toBe('unchanged');
    expect(read(m, '.claude/settings.json')).toBe(after);
  });

  it('normalises the address the way the clients need it', async () => {
    await join(m.rt, silentUi, m.io, `${gw.url}/v1/`, { clients: 'claude-code', yes: true });
    expect(readProfile(m.rt.paths)!.gatewayUrl).toBe(gw.url);
  });

  it('sends the key as a bearer token and nothing else', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    expect(gw.requests.map((r) => r.path).sort()).toEqual(['/v1/astra/connect-context', '/v1/astra/key']);
    expect(gw.requests.every((r) => r.auth === `Bearer ${KEY}`)).toBe(true);
  });

  it('reads the key from stdin with --key-stdin, never from argv', async () => {
    const io = { ...m.io, env: {}, readStdin: async () => `${KEY}\n` };
    const result = await join(m.rt, silentUi, io, gw.url, { clients: 'claude-code', keyStdin: true, yes: true });
    expect(result.clients[0]!.status).toBe('applied');
  });

  it('stops with a clear message when there is no key', async () => {
    const io = { ...m.io, env: {} };
    await expect(join(m.rt, silentUi, io, gw.url, { clients: 'claude-code', yes: true })).rejects.toThrow('ASTRA_API_KEY');
  });

  it('says so when the key is wrong, and writes nothing', async () => {
    const io = { ...m.io, env: { ASTRA_API_KEY: 'sk-astra-wrong' } };
    await expect(join(m.rt, silentUi, io, gw.url, { clients: 'claude-code', yes: true })).rejects.toThrow('Key 无效');
    expect(read(m, '.claude/settings.json')).toBe(SETTINGS);
    expect(readProfile(m.rt.paths)).toBeNull();
  });

  it('explains a gateway that is too old for astra-connect', async () => {
    const old = await startGateway({ contextStatus: 404 });
    try {
      await expect(join(m.rt, silentUi, m.io, old.url, { clients: 'claude-code', yes: true })).rejects.toThrow('版本过旧');
    } finally {
      await old.close();
    }
  });

  it('passes on what the server says when the key has no provider', async () => {
    const bare = await startGateway({ contextStatus: 503 });
    try {
      await expect(join(m.rt, silentUi, m.io, bare.url, { clients: 'claude-code', yes: true })).rejects.toThrow('没有设置默认提供商');
    } finally {
      await bare.close();
    }
  });

  it('rejects clients it does not know and an address that is not one', async () => {
    await expect(join(m.rt, silentUi, m.io, gw.url, { clients: 'nope', yes: true })).rejects.toThrow('不支持的客户端');
    await expect(join(m.rt, silentUi, m.io, 'ftp://x', { clients: 'claude-code', yes: true })).rejects.toThrow('http');
  });

  it('works without a terminal and without --yes (plain output)', async () => {
    const result = await join(m.rt, plainUi, m.io, gw.url, { clients: 'claude-code' });
    expect(result.clients[0]!.status).toBe('applied');
  });
});

describe('leave', () => {
  it('restores the original file byte for byte and forgets the profile', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    const result = await leave(m.rt, silentUi, m.io, { yes: true });
    expect(result.clients[0]).toMatchObject({ kind: 'claude-code', drifted: [] });
    expect(read(m, '.claude/settings.json')).toBe(SETTINGS);
    expect(readProfile(m.rt.paths)).toBeNull();
  });

  it('leaves what the user changed since alone, until --force', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    const file = path.join(m.home, '.claude/settings.json');
    fs.writeFileSync(file, read(m, '.claude/settings.json').replace(gw.url, 'https://elsewhere.example'));

    const first = await leave(m.rt, silentUi, m.io, { yes: true });
    expect(first.clients[0]!.drifted).toContain('env.ANTHROPIC_BASE_URL');
    expect(read(m, '.claude/settings.json')).toContain('https://elsewhere.example');
    expect(readProfile(m.rt.paths)).not.toBeNull();
    process.exitCode = 0;

    const forced = await leave(m.rt, silentUi, m.io, { yes: true, force: true });
    expect(forced.clients[0]!.drifted).toEqual([]);
    expect(read(m, '.claude/settings.json')).toBe(SETTINGS);
  });

  it('can keep the saved gateway and key', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    await leave(m.rt, silentUi, m.io, { yes: true, keepProfile: true });
    expect(readProfile(m.rt.paths)).toMatchObject({ apiKey: KEY, clients: [] });
  });

  it('says there is nothing to do before joining', async () => {
    await expect(leave(m.rt, silentUi, m.io, { yes: true })).rejects.toThrow('尚未接入');
  });
});

describe('status', () => {
  it('shows the limits of the key and the state of each client', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    const result = await status(m.rt, silentUi);
    expect(result.reachable).toBe(true);
    expect(result.key).not.toContain(KEY);
    expect(result.key_info?.limits[0]).toMatchObject({ used: 50, limit: 200 });
    expect(result.clients).toMatchObject([{ kind: 'claude-code', state: 'enabled' }]);
  });

  it('reports a client whose config was changed', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    const file = path.join(m.home, '.claude/settings.json');
    fs.writeFileSync(file, read(m, '.claude/settings.json').replace(KEY, 'sk-other'));
    expect((await status(m.rt, silentUi)).clients[0]).toMatchObject({ state: 'drifted' });
  });

  it('still shows local state when the gateway is down', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    await gw.close();
    const result = await status(m.rt, silentUi);
    expect(result.reachable).toBe(false);
    expect(result.error).toBeTruthy();
    expect(result.clients).toHaveLength(1);
    gw = await startGateway(); // for afterEach
  });
});

describe('doctor', () => {
  it('is happy after a clean join', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    expect(await doctor(m.rt, silentUi)).toMatchObject({ ok: true, problems: [] });
  });

  it('names what is wrong', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    const file = path.join(m.home, '.claude/settings.json');
    fs.writeFileSync(file, read(m, '.claude/settings.json').replace(KEY, 'sk-other'));
    const result = await doctor(m.rt, silentUi);
    expect(result.ok).toBe(false);
    expect(result.problems.join('\n')).toContain('配置被改动过');
  });

  it('reports a key that stopped working', async () => {
    await join(m.rt, silentUi, m.io, gw.url, { clients: 'claude-code', yes: true });
    await gw.close();
    gw = await startGateway({ key: 'sk-astra-rotated' });
    const profile = readProfile(m.rt.paths)!;
    // The server now sits at another port; point the profile at it with the old key.
    fs.writeFileSync(m.rt.paths.profileFile, JSON.stringify({ ...profile, gatewayUrl: gw.url }));
    const result = await doctor(m.rt, silentUi);
    expect(result.problems.join('\n')).toContain('Key 无效');
  });
});

describe('default model per client', () => {
  it('picks the first model of the client\'s own family, else the first of the key', () => {
    const models = [...CONTEXT.models].reverse(); // gpt-5 first
    expect(defaultModelFor('claude-code', models)).toBe('claude-sonnet-4-5');
    expect(defaultModelFor('codex', models)).toBe('gpt-5');
    expect(defaultModelFor('gemini-cli', models)).toBe('gpt-5');
    expect(defaultModelFor('zed', models)).toBe('gpt-5');
    expect(defaultModelFor('claude-code', [])).toBeNull();
  });

  it('keeps the client\'s own model setting with nothing to pick from', async () => {
    const bare = await startGateway({ context: { name: '张三', models: [] } });
    try {
      await join(m.rt, silentUi, m.io, bare.url, { clients: 'claude-code', yes: true });
      expect(read(m, '.claude/settings.json')).not.toContain('ANTHROPIC_MODEL');
    } finally {
      await bare.close();
    }
  });
});
