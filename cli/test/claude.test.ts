import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import type { ChildProcess } from 'node:child_process';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const state = vi.hoisted(() => ({
  prepared: null as unknown,
  requests: [] as { path: string; method?: string; body?: unknown }[],
}));

vi.mock('../src/lib/server-lifecycle.js', () => ({
  ensureServerRunning: async () => ({
    base: 'http://127.0.0.1:17321',
    runtime: undefined,
    started: false,
  }),
}));

vi.mock('../src/lib/http.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/lib/http.js')>();
  return {
    ...actual,
    apiRequest: async (req: { path: string; method?: string; body?: unknown }) => {
      state.requests.push({ path: req.path, method: req.method, body: req.body });
      return state.prepared;
    },
  };
});

import {
  assertNoSettingsConflicts,
  assertSubscriptionAuth,
  buildClaudeEnv,
  exitCodeFrom,
  parseAuthStatus,
  parseSettingsJson,
  readLaunchEnv,
  runClaude,
  settingsConflicts,
  validateLaunchResponse,
  type ClaudeSpawn,
  type ClaudeSpawnOptions,
} from '../src/commands/claude';
import { AstraError } from '../src/errors';

const PROFILE_ID = '0123456789abcdef0123456789abcdef';
const LAUNCH_FILE_NAME = 'astra-launch.json';
const LAUNCH_CONTENT = {
  forceLoginMethod: 'claudeai',
  env: {
    ANTHROPIC_BASE_URL: 'https://api.anthropic.com',
    ANTHROPIC_API_KEY: '',
    ANTHROPIC_AUTH_TOKEN: '',
    CLAUDE_CODE_OAUTH_TOKEN: '',
    CLAUDE_CODE_USE_BEDROCK: '0',
    CLAUDE_CODE_ENABLE_TELEMETRY: '0',
    ENABLE_BETA_TRACING_DETAILED: '0',
    BETA_TRACING_ENDPOINT: '',
  },
};

interface FakeCall {
  command: string;
  args: string[];
  options: ClaudeSpawnOptions;
}

interface FakeResult {
  code: number | null;
  stdout?: string;
  error?: NodeJS.ErrnoException;
}

/** A child_process fake: no real `claude` is ever spawned, and every call is recorded. */
function fakeSpawn(script: (call: FakeCall) => FakeResult): {
  spawnImpl: ClaudeSpawn;
  calls: FakeCall[];
} {
  const calls: FakeCall[] = [];
  const spawnImpl: ClaudeSpawn = (command, args, options) => {
    const call: FakeCall = { command, args: [...args], options };
    calls.push(call);
    const result = script(call);
    const child = new EventEmitter() as unknown as ChildProcess;
    const stdout = new EventEmitter();
    const stderr = new EventEmitter();
    Object.assign(child, {
      stdout,
      stderr,
      stdin: { end: () => undefined },
      kill: () => true,
    });
    queueMicrotask(() => {
      if (result.stdout) stdout.emit('data', Buffer.from(result.stdout));
      if (result.error) child.emit('error', result.error);
      else child.emit('close', result.code);
    });
    return child;
  };
  return { spawnImpl, calls };
}

const AUTH_OK = JSON.stringify({ loggedIn: true, authMethod: 'claude.ai' });

function launchOutside(home: string): {
  profileId: string;
  configDirectory: string;
  settingsFile: string;
} {
  const directory = path.join(home, 'claude-profiles', PROFILE_ID);
  return {
    profileId: PROFILE_ID,
    configDirectory: directory,
    settingsFile: path.join(directory, LAUNCH_FILE_NAME),
  };
}

async function rejectionOf(promise: Promise<unknown>): Promise<Error> {
  try {
    await promise;
  } catch (err) {
    return err as Error;
  }
  throw new Error('expected the call to reject');
}

function errorOf(fn: () => unknown): Error {
  try {
    fn();
  } catch (err) {
    return err as Error;
  }
  throw new Error('expected the call to throw');
}

/** AstraError prints its message and its hint line; conflict details live in the hint. */
function textOf(error: Error): string {
  return `${error.message}\n${(error as AstraError).hint ?? ''}`;
}

describe('buildClaudeEnv', () => {
  it('scrubs endpoint, credential and telemetry variables, then applies the whitelist', () => {
    const env = buildClaudeEnv(
      {
        PATH: '/usr/bin',
        HOME: '/home/u',
        ANTHROPIC_API_KEY: 'sk-live-secret',
        ANTHROPIC_FEDERATION_TOKEN: 'fed-secret',
        OTEL_LOGS_EXPORTER: 'console',
        OTEL_EXPORTER_OTLP_LOGS_ENDPOINT: 'https://collector.example/v1/logs',
        CLAUDE_CODE_OAUTH_TOKEN: 'oauth-secret',
        CLAUDE_CODE_USE_BEDROCK: '1',
        ENABLE_BETA_TRACING_DETAILED: '1',
        BETA_TRACING_ENDPOINT: 'https://trace.example',
        CLAUDE_CODE_DISABLE_SANDBOX: '1',
        CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
        UNRELATED_VAR: 'kept',
      },
      {
        ...LAUNCH_CONTENT.env,
        OTEL_LOGS_EXPORTER: 'none',
        // A key the server has no business returning; it must not reach the child.
        NOT_ALLOWED: 'nope',
      },
      '/home/u/.astra/claude-profiles/abc',
    );

    expect(env.PATH).toBe('/usr/bin');
    expect(env.HOME).toBe('/home/u');
    expect(env.UNRELATED_VAR).toBe('kept');
    // Settings that harden the CLI are unrelated to telemetry or credentials and stay untouched.
    expect(env.CLAUDE_CODE_DISABLE_SANDBOX).toBe('1');
    expect(env.CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC).toBe('1');
    expect(env.OTEL_LOGS_EXPORTER).toBe('none');
    expect(env.ANTHROPIC_BASE_URL).toBe('https://api.anthropic.com');
    expect(env.ANTHROPIC_API_KEY).toBe('');
    expect(env.CLAUDE_CODE_USE_BEDROCK).toBe('0');
    expect(Object.keys(env)).not.toContain('NOT_ALLOWED');
    expect(Object.values(env)).not.toContain('sk-live-secret');
    expect(Object.values(env)).not.toContain('oauth-secret');
    expect(Object.values(env)).not.toContain('https://trace.example');
    expect(env.CLAUDE_CONFIG_DIR).toBe(path.resolve('/home/u/.astra/claude-profiles/abc'));
  });

  it('keeps the inherited variables when the launch file supplies nothing', () => {
    const env = buildClaudeEnv({ PATH: '/bin', OTEL_TRACES_EXPORTER: 'otlp' }, {}, '/tmp/p');
    expect(env.PATH).toBe('/bin');
    expect(env.OTEL_TRACES_EXPORTER).toBeUndefined();
    expect(env.CLAUDE_CONFIG_DIR).toBe(path.resolve('/tmp/p'));
  });
});

describe('launch file', () => {
  let workDir: string;

  beforeEach(() => {
    workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-claude-launch-'));
  });

  afterEach(() => {
    fs.rmSync(workDir, { recursive: true, force: true });
  });

  it('refuses a missing or unreadable launch file', () => {
    expect(() => readLaunchEnv(path.join(workDir, 'nope.json'))).toThrow(AstraError);
    const broken = path.join(workDir, 'broken.json');
    fs.writeFileSync(broken, '{ not json');
    expect(() => readLaunchEnv(broken)).toThrow(AstraError);
  });

  it('rejects executable settings in the launcher overlay', () => {
    const file = path.join(workDir, LAUNCH_FILE_NAME);
    fs.writeFileSync(file, JSON.stringify({ ...LAUNCH_CONTENT, hooks: { SessionStart: [] } }));
    expect(() => readLaunchEnv(file)).toThrow('unexpected settings');
  });

  it('reads only the env object', () => {
    const file = path.join(workDir, LAUNCH_FILE_NAME);
    fs.writeFileSync(
      file,
      JSON.stringify({ forceLoginMethod: 'claudeai', env: { OTEL_LOGS_EXPORTER: 'none' } }),
    );
    expect(readLaunchEnv(file)).toEqual({ OTEL_LOGS_EXPORTER: 'none' });
  });
});

describe('validateLaunchResponse', () => {
  const home = '/tmp/astra-home';

  it('accepts the Astra profile directory and astra-launch.json', () => {
    const launch = validateLaunchResponse(launchOutside(home), home);
    expect(launch).toEqual({
      profileId: PROFILE_ID,
      configDirectory: path.join(home, 'claude-profiles', PROFILE_ID),
      settingsFile: path.join(home, 'claude-profiles', PROFILE_ID, LAUNCH_FILE_NAME),
    });
  });

  it('refuses an id that is not 32 lowercase hex characters', () => {
    for (const profileId of ['../escape', '0123456789ABCDEF0123456789ABCDEF', 'short', '']) {
      expect(() => validateLaunchResponse({ ...launchOutside(home), profileId }, home)).toThrow(
        AstraError,
      );
    }
  });

  it('refuses a directory outside the Astra data directory', () => {
    const outside = path.join('/tmp/evil', PROFILE_ID);
    expect(() =>
      validateLaunchResponse(
        {
          profileId: PROFILE_ID,
          configDirectory: outside,
          settingsFile: path.join(outside, LAUNCH_FILE_NAME),
        },
        home,
      ),
    ).toThrow(AstraError);
  });

  it('refuses a traversal that leaves the profile directory', () => {
    const configDirectory = path.join(home, 'claude-profiles', PROFILE_ID, '..', 'other');
    expect(() =>
      validateLaunchResponse(
        {
          profileId: PROFILE_ID,
          configDirectory,
          settingsFile: path.join(configDirectory, LAUNCH_FILE_NAME),
        },
        home,
      ),
    ).toThrow(AstraError);
  });

  it('refuses a settings file that is not astra-launch.json in the profile directory', () => {
    const launch = launchOutside(home);
    expect(() =>
      validateLaunchResponse(
        { ...launch, settingsFile: path.join(launch.configDirectory, 'settings.json') },
        home,
      ),
    ).toThrow(AstraError);
    expect(() =>
      validateLaunchResponse({ ...launch, settingsFile: path.join(home, LAUNCH_FILE_NAME) }, home),
    ).toThrow(AstraError);
  });

  it('refuses a malformed payload', () => {
    expect(() => validateLaunchResponse(null, home)).toThrow(AstraError);
    expect(() => validateLaunchResponse(launchOutside(home).settingsFile, home)).toThrow(
      AstraError,
    );
    expect(() => validateLaunchResponse({ ...launchOutside(home), settingsFile: 7 }, home)).toThrow(
      AstraError,
    );
  });
});

describe('settings conflicts', () => {
  it('rejects named profiles and alternate telemetry destinations from settings', () => {
    for (const key of ['ANTHROPIC_PROFILE', 'ANTHROPIC_FEDERATION_RULE_ID', 'OTEL_EXPORTER_OTLP_ENDPOINT', 'CLAUDE_CONFIG_DIR']) {
      expect(settingsConflicts('settings.json', { env: { [key]: 'private-value' } }))
        .toContainEqual({ file: 'settings.json', key: `env.${key}` });
    }
  });

  let workDir: string;

  const profileDir = (): string => path.join(workDir, 'profile');
  const projectDir = (): string => {
    const dir = path.join(workDir, 'repo');
    fs.mkdirSync(path.join(dir, '.git'), { recursive: true });
    fs.mkdirSync(path.join(dir, 'sub'), { recursive: true });
    return path.join(dir, 'sub');
  };
  const write = (relative: string, content: string): string => {
    const file = path.join(workDir, relative);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, content);
    return file;
  };

  beforeEach(() => {
    workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-claude-settings-'));
  });

  afterEach(() => {
    fs.rmSync(workDir, { recursive: true, force: true });
  });

  it('blocks a project settings file that carries a credential, naming the file but not the value', () => {
    const cwd = projectDir();
    const file = write(
      'repo/.claude/settings.json',
      JSON.stringify({ env: { ANTHROPIC_AUTH_TOKEN: 'sk-live-secret' } }),
    );
    const error = errorOf(() => assertNoSettingsConflicts(profileDir(), cwd));
    expect(error).toBeInstanceOf(AstraError);
    const text = textOf(error);
    expect(text).toContain(file);
    expect(text).toContain('env.ANTHROPIC_AUTH_TOKEN');
    expect(text).not.toContain('sk-live-secret');
  });

  it('walks from the cwd up to the git root, including settings.local.json', () => {
    const cwd = projectDir();
    const file = write(
      'repo/.claude/settings.local.json',
      JSON.stringify({ forceLoginGatewayUrl: 'https://relay.example' }),
    );
    const error = errorOf(() => assertNoSettingsConflicts(profileDir(), cwd));
    const text = textOf(error);
    expect(text).toContain(file);
    expect(text).not.toContain('relay.example');
  });

  it('blocks the profile settings and the user-level helpers too', () => {
    const profile = profileDir();
    fs.mkdirSync(profile, { recursive: true });
    fs.writeFileSync(
      path.join(profile, 'settings.json'),
      JSON.stringify({ apiKeyHelper: 'curl secret' }),
    );
    const cwd = projectDir();
    expect(textOf(errorOf(() => assertNoSettingsConflicts(profile, cwd)))).toContain(
      'apiKeyHelper',
    );
  });

  it('allows the official endpoint, empty placeholders and the claudeai login method', () => {
    const cwd = projectDir();
    write(
      'repo/.claude/settings.json',
      JSON.stringify({
        env: {
          ANTHROPIC_BASE_URL: 'https://api.anthropic.com',
          ANTHROPIC_API_KEY: '',
          ANTHROPIC_AUTH_TOKEN: '',
          CLAUDE_CODE_OAUTH_TOKEN: '',
          CLAUDE_CODE_USE_BEDROCK: '0',
          CLAUDE_CODE_USE_VERTEX: 'false',
          CLAUDE_CODE_USE_FOUNDRY: '0',
        },
        forceLoginMethod: 'claudeai',
      }),
    );
    expect(() => assertNoSettingsConflicts(profileDir(), cwd)).not.toThrow();
  });

  it('treats an external provider switch or a non-official base URL as a conflict', () => {
    expect(
      settingsConflicts('/tmp/s.json', { env: { CLAUDE_CODE_USE_BEDROCK: '1' } }).map((c) => c.key),
    ).toEqual(['env.CLAUDE_CODE_USE_BEDROCK']);
    expect(
      settingsConflicts('/tmp/s.json', { env: { ANTHROPIC_BASE_URL: 'http://relay.example' } }).map(
        (c) => c.key,
      ),
    ).toEqual(['env.ANTHROPIC_BASE_URL']);
    expect(
      settingsConflicts('/tmp/s.json', { forceLoginMethod: 'console', otelHeadersHelper: 'x' }).map(
        (c) => c.key,
      ),
    ).toEqual(['otelHeadersHelper', 'forceLoginMethod']);
  });

  it('refuses settings that are not a JSON object instead of guessing', () => {
    expect(() => parseSettingsJson('/tmp/s.json', '{ nope')).toThrow(AstraError);
    expect(() => parseSettingsJson('/tmp/s.json', '[1,2]')).toThrow(AstraError);
    expect(errorOf(() => parseSettingsJson('/tmp/s.json', 'oops')).message).toContain(
      '/tmp/s.json',
    );
    expect(parseSettingsJson('/tmp/s.json', '{"env":{}}')).toEqual({ env: {} });
  });

  it('refuses a malformed project settings file by path', () => {
    const cwd = projectDir();
    const file = write('repo/.claude/settings.json', 'not json at all');
    expect(errorOf(() => assertNoSettingsConflicts(profileDir(), cwd)).message).toContain(file);
  });
});

describe('auth status', () => {
  it('accepts only a claude.ai subscription login', () => {
    expect(() => assertSubscriptionAuth(parseAuthStatus(AUTH_OK))).not.toThrow();
    expect(() =>
      assertSubscriptionAuth(parseAuthStatus('{"loggedIn":true,"authMethod":"claude.ai"}')),
    ).not.toThrow();
  });

  it('rejects an api key or oauth token login and a signed-out profile', () => {
    expect(() => assertSubscriptionAuth({ loggedIn: true, authMethod: 'api_key' })).toThrow(
      AstraError,
    );
    expect(() => assertSubscriptionAuth({ loggedIn: true, authMethod: 'oauth_token' })).toThrow(
      AstraError,
    );
    expect(() => assertSubscriptionAuth({ loggedIn: true, authMethod: null })).toThrow(AstraError);
    expect(() => assertSubscriptionAuth({ loggedIn: false, authMethod: null })).toThrow(
      /not signed in/,
    );
  });

  it('never puts the raw output in an error', () => {
    const error = errorOf(() => parseAuthStatus('{"loggedIn":"yes","token":"sk-live-secret"}'));
    expect(error).toBeInstanceOf(AstraError);
    expect(error.message).not.toContain('sk-live-secret');
    expect(error.message).not.toContain('loggedIn');
    expect(errorOf(() => parseAuthStatus('not json')).message).not.toContain('not json');
  });
});

describe('exitCodeFrom', () => {
  it('keeps a real exit code and turns a signal death into failure', () => {
    expect(exitCodeFrom(0)).toBe(0);
    expect(exitCodeFrom(3)).toBe(3);
    expect(exitCodeFrom(null)).toBe(1);
  });
});

describe('runClaude', () => {
  let workDir: string;
  let homeDir: string;
  let originalHome: string | undefined;
  let log: ReturnType<typeof vi.spyOn>;
  let launch: { profileId: string; configDirectory: string; settingsFile: string };

  beforeEach(() => {
    workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-claude-run-'));
    fs.mkdirSync(path.join(workDir, '.git'));
    homeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-claude-home-'));
    originalHome = process.env.ASTRA_HOME;
    process.env.ASTRA_HOME = homeDir;
    launch = {
      profileId: PROFILE_ID,
      configDirectory: path.join(homeDir, 'claude-profiles', PROFILE_ID),
      settingsFile: path.join(homeDir, 'claude-profiles', PROFILE_ID, LAUNCH_FILE_NAME),
    };
    fs.mkdirSync(launch.configDirectory, { recursive: true, mode: 0o700 });
    fs.writeFileSync(launch.settingsFile, JSON.stringify(LAUNCH_CONTENT), { mode: 0o600 });
    state.prepared = launch;
    state.requests.length = 0;
    log = vi.spyOn(console, 'log').mockImplementation(() => {});
  });

  afterEach(() => {
    log.mockRestore();
    process.exitCode = undefined;
    if (originalHome === undefined) delete process.env.ASTRA_HOME;
    else process.env.ASTRA_HOME = originalHome;
    fs.rmSync(workDir, { recursive: true, force: true });
    fs.rmSync(homeDir, { recursive: true, force: true });
  });

  const run = (opts: Parameters<typeof runClaude>[0], spawnImpl: ClaudeSpawn): Promise<void> =>
    runClaude(opts, { spawn: spawnImpl, env: { PATH: '/usr/bin' }, cwd: workDir });

  it('runs the official session with --settings and keeps its exit code', async () => {
    const { spawnImpl, calls } = fakeSpawn((call) =>
      call.args[0] === 'auth' ? { code: 0, stdout: AUTH_OK } : { code: 3 },
    );
    await run({ profile: PROFILE_ID }, spawnImpl);

    expect(calls.map((c) => c.args)).toEqual([
      ['auth', 'status'],
      ['--settings', launch.settingsFile],
    ]);
    expect(calls[1]?.command).toBe('claude');
    expect(calls[1]?.options.shell).toBe(false);
    expect(calls[1]?.options.stdio).toBe('inherit');
    expect(calls[1]?.options.env?.CLAUDE_CONFIG_DIR).toBe(launch.configDirectory);
    expect(calls[1]?.options.env?.ANTHROPIC_BASE_URL).toBe('https://api.anthropic.com');
    expect(process.exitCode).toBe(3);
    expect(state.requests).toEqual([
      {
        path: '/api/clients/claude-code/direct/prepare',
        method: 'POST',
        body: { action: 'run', profileId: PROFILE_ID },
      },
    ]);
  });

  it('omits the profile id when none was given', async () => {
    const { spawnImpl } = fakeSpawn((call) =>
      call.args[0] === 'auth' ? { code: 0, stdout: AUTH_OK } : { code: 0 },
    );
    await run({}, spawnImpl);
    expect(state.requests[0]?.body).toEqual({ action: 'run' });
  });

  it('refuses to run when the profile is signed in with an api key', async () => {
    const { spawnImpl, calls } = fakeSpawn(() => ({
      code: 0,
      stdout: JSON.stringify({ loggedIn: true, authMethod: 'api_key' }),
    }));
    const error = await rejectionOf(run({}, spawnImpl));
    expect(error).toBeInstanceOf(AstraError);
    expect(error.message).toContain('api_key');
    expect(calls.map((c) => c.args)).toEqual([['auth', 'status']]);
  });

  it('refuses to run when the profile is not signed in at all', async () => {
    const { spawnImpl } = fakeSpawn(() => ({
      code: 0,
      stdout: '{"loggedIn":false,"authMethod":null}',
    }));
    expect(await rejectionOf(run({}, spawnImpl))).toBeInstanceOf(AstraError);
  });

  it('blocks a run when project settings would override the profile', async () => {
    const file = path.join(workDir, '.claude', 'settings.json');
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify({ env: { ANTHROPIC_API_KEY: 'sk-project-secret' } }));
    const { spawnImpl, calls } = fakeSpawn((call) =>
      call.args[0] === 'auth' ? { code: 0, stdout: AUTH_OK } : { code: 0 },
    );
    const error = await rejectionOf(run({}, spawnImpl));
    const text = textOf(error);
    expect(text).toContain(file);
    expect(text).not.toContain('sk-project-secret');
    expect(calls).toEqual([]);
  });

  it('--login signs in and confirms the subscription afterwards', async () => {
    const { spawnImpl, calls } = fakeSpawn((call) =>
      call.args[1] === 'login' ? { code: 0 } : { code: 0, stdout: AUTH_OK },
    );
    await run({ login: true }, spawnImpl);
    expect(calls.map((c) => c.args)).toEqual([
      ['auth', 'login'],
      ['auth', 'status'],
    ]);
    expect(state.requests[0]?.body).toEqual({ action: 'login' });
    expect(process.exitCode).toBe(0);
  });

  it('--login never reports ready while the profile is still not signed in', async () => {
    const { spawnImpl } = fakeSpawn((call) =>
      call.args[1] === 'login'
        ? { code: 0 }
        : { code: 0, stdout: '{"loggedIn":false,"authMethod":null}' },
    );
    expect(await rejectionOf(run({ login: true }, spawnImpl))).toBeInstanceOf(AstraError);
  });

  it('--status asks the server and runs the official auth status', async () => {
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 0 }));
    await run({ status: true }, spawnImpl);
    expect(calls.map((c) => c.args)).toEqual([['auth', 'status']]);
    expect(state.requests[0]?.body).toEqual({ action: 'status' });
    expect(process.exitCode).toBe(0);
  });

  it('refuses --login with --status and a bad profile id before touching the server', async () => {
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 0 }));
    expect(await rejectionOf(run({ login: true, status: true }, spawnImpl))).toBeInstanceOf(
      AstraError,
    );
    expect(await rejectionOf(run({ profile: '../etc' }, spawnImpl))).toBeInstanceOf(AstraError);
    expect(calls).toEqual([]);
    expect(state.requests).toEqual([]);
  });

  it('refuses a profile path that did not come from this machine', async () => {
    const outside = path.join('/tmp/evil', PROFILE_ID);
    state.prepared = {
      profileId: PROFILE_ID,
      configDirectory: outside,
      settingsFile: path.join(outside, LAUNCH_FILE_NAME),
    };
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 0 }));
    expect(await rejectionOf(run({ status: true }, spawnImpl))).toBeInstanceOf(AstraError);
    expect(calls).toEqual([]);
  });

  it('reports a missing official CLI instead of running a session', async () => {
    const enoent = Object.assign(new Error('spawn claude ENOENT'), { code: 'ENOENT' });
    const { spawnImpl } = fakeSpawn(() => ({ code: null, error: enoent }));
    const error = await rejectionOf(run({ status: true }, spawnImpl));
    expect(error.message).toContain('not found on PATH');
  });
});
