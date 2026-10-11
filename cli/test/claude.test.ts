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
      if (state.prepared instanceof Error) throw state.prepared;
      return state.prepared;
    },
  };
});

import {
  assertNoSettingsConflicts,
  buildClaudeEnv,
  claudeConfigDir,
  exitCodeFrom,
  parseSettingsJson,
  runClaude,
  settingsConflicts,
  settingsFilesToCheck,
  validateLaunchResponse,
  type ClaudeSpawn,
  type ClaudeSpawnOptions,
} from '../src/commands/claude';
import { AstraError } from '../src/errors';

const SERVER_TOKEN = 'sk-ant-oat-live-secret';
const ACCOUNT_ID = 'acct_1ab2';
const EXPIRES_LATER = new Date(Date.now() + 2 * 60 * 60 * 1000).toISOString();
const EXPIRES_SOON = new Date(Date.now() + 5 * 60 * 1000).toISOString();

const PREPARE_PATH = '/api/clients/claude-code/direct/prepare';

/** What POST /api/clients/claude-code/direct/prepare answers with. */
function preparedLaunch(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    accountId: ACCOUNT_ID,
    accountName: 'Work subscription',
    expiresAtUtc: EXPIRES_LATER,
    env: {
      ANTHROPIC_BASE_URL: 'https://api.anthropic.com',
      ANTHROPIC_API_KEY: '',
      ANTHROPIC_AUTH_TOKEN: '',
      CLAUDE_CODE_OAUTH_TOKEN: SERVER_TOKEN,
      CLAUDE_CODE_USE_BEDROCK: '0',
      CLAUDE_CODE_ENABLE_TELEMETRY: '0',
      OTEL_LOGS_EXPORTER: 'none',
    },
    ...overrides,
  };
}

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
function fakeSpawn(script: (call: FakeCall) => FakeResult = () => ({ code: 0 })): {
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
  it('keeps proxies and locale variables, and scrubs endpoint, credential and telemetry variables', () => {
    const env = buildClaudeEnv(
      {
        PATH: '/usr/bin',
        HOME: '/home/u',
        HTTPS_PROXY: 'http://proxy.example:3128',
        HTTP_PROXY: 'http://proxy.example:3128',
        NO_PROXY: 'localhost',
        ALL_PROXY: 'socks5://proxy.example:1080',
        LANG: 'en_US.UTF-8',
        CLAUDE_CONFIG_DIR: '/home/u/.claude',
        // The gateway takeover the user may have enabled: it must not outrank the subscription token.
        ANTHROPIC_BASE_URL: 'http://127.0.0.1:17321',
        ANTHROPIC_AUTH_TOKEN: 'astra-gateway-token',
        ANTHROPIC_FEDERATION_RULE_ID: 'fed-rule',
        OTEL_LOGS_EXPORTER: 'console',
        OTEL_EXPORTER_OTLP_LOGS_ENDPOINT: 'https://collector.example/v1/logs',
        CLAUDE_CODE_OAUTH_TOKEN: 'inherited-oauth-token',
        CLAUDE_CODE_DISABLE_SANDBOX: '1',
        UNRELATED_VAR: 'kept',
      },
      {
        CLAUDE_CODE_OAUTH_TOKEN: SERVER_TOKEN,
        ANTHROPIC_BASE_URL: 'https://api.anthropic.com',
        ANTHROPIC_API_KEY: '',
        ANTHROPIC_AUTH_TOKEN: '',
        CLAUDE_CODE_ENABLE_TELEMETRY: '0',
        OTEL_LOGS_EXPORTER: 'none',
        // A key the server has no business returning; it must not reach the child.
        NOT_ALLOWED: 'nope',
      },
    );

    expect(env.PATH).toBe('/usr/bin');
    expect(env.HOME).toBe('/home/u');
    expect(env.HTTPS_PROXY).toBe('http://proxy.example:3128');
    expect(env.HTTP_PROXY).toBe('http://proxy.example:3128');
    expect(env.NO_PROXY).toBe('localhost');
    expect(env.ALL_PROXY).toBe('socks5://proxy.example:1080');
    expect(env.LANG).toBe('en_US.UTF-8');
    // Claude Code keeps its own configuration directory; Astra does not repoint it.
    expect(env.CLAUDE_CONFIG_DIR).toBe('/home/u/.claude');
    // Settings that harden the CLI are unrelated to telemetry or credentials and stay untouched.
    expect(env.CLAUDE_CODE_DISABLE_SANDBOX).toBe('1');
    expect(env.UNRELATED_VAR).toBe('kept');
    expect(env.CLAUDE_CODE_OAUTH_TOKEN).toBe(SERVER_TOKEN);
    expect(env.ANTHROPIC_BASE_URL).toBe('https://api.anthropic.com');
    expect(env.ANTHROPIC_API_KEY).toBe('');
    expect(env.ANTHROPIC_AUTH_TOKEN).toBe('');
    expect(env.OTEL_LOGS_EXPORTER).toBe('none');
    expect(Object.keys(env)).not.toContain('NOT_ALLOWED');
    for (const gone of ['astra-gateway-token', 'inherited-oauth-token', 'fed-rule', 'console']) {
      expect(Object.values(env)).not.toContain(gone);
    }
  });

  it('keeps the inherited variables when the server supplies nothing', () => {
    const env = buildClaudeEnv({ PATH: '/bin', OTEL_TRACES_EXPORTER: 'otlp', NO_PROXY: '*' }, {});
    expect(env.PATH).toBe('/bin');
    expect(env.NO_PROXY).toBe('*');
    expect(env.OTEL_TRACES_EXPORTER).toBeUndefined();
  });
});

describe('claudeConfigDir', () => {
  it('prefers an explicit directory, then $CLAUDE_CONFIG_DIR, then ~/.claude', () => {
    expect(claudeConfigDir({ CLAUDE_CONFIG_DIR: '/custom/claude' }, '/tmp/override')).toBe(
      path.resolve('/tmp/override'),
    );
    expect(claudeConfigDir({ CLAUDE_CONFIG_DIR: '/custom/claude' })).toBe('/custom/claude');
    expect(claudeConfigDir({})).toBe(path.join(os.homedir(), '.claude'));
  });
});

describe('validateLaunchResponse', () => {
  it('accepts ids, names, an ISO expiry and a whitelisted string environment', () => {
    const launch = validateLaunchResponse(preparedLaunch());
    expect(launch.accountId).toBe(ACCOUNT_ID);
    expect(launch.accountName).toBe('Work subscription');
    expect(launch.expiresAt.toISOString()).toBe(EXPIRES_LATER);
    expect(launch.env.CLAUDE_CODE_OAUTH_TOKEN).toBe(SERVER_TOKEN);
    expect(launch.env.ANTHROPIC_API_KEY).toBe('');
  });

  it('refuses an unknown environment key instead of ignoring it', () => {
    const error = errorOf(() =>
      validateLaunchResponse(
        preparedLaunch({ env: { CLAUDE_CODE_OAUTH_TOKEN: SERVER_TOKEN, NOT_ALLOWED: 'nope' } }),
      ),
    );
    expect(error).toBeInstanceOf(AstraError);
    expect(error.message).toContain('NOT_ALLOWED');
    expect(error.message).not.toContain(SERVER_TOKEN);
    expect(error.message).not.toContain('nope');
  });

  it('refuses a value that is not a string', () => {
    for (const value of [1, true, null, { a: 'b' }, ['x']]) {
      expect(() =>
        validateLaunchResponse(
          preparedLaunch({
            env: { CLAUDE_CODE_OAUTH_TOKEN: SERVER_TOKEN, OTEL_LOGS_EXPORTER: value },
          }),
        ),
      ).toThrow(AstraError);
    }
    expect(() =>
      validateLaunchResponse(preparedLaunch({ env: { CLAUDE_CODE_OAUTH_TOKEN: 42 } })),
    ).toThrow(AstraError);
  });

  it('refuses a missing or empty subscription token', () => {
    expect(() => validateLaunchResponse(preparedLaunch({ env: {} }))).toThrow(AstraError);
    expect(() =>
      validateLaunchResponse(preparedLaunch({ env: { CLAUDE_CODE_OAUTH_TOKEN: '' } })),
    ).toThrow(AstraError);
  });

  it('refuses a malformed payload', () => {
    const malformed: unknown[] = [
      null,
      'nope',
      preparedLaunch({ accountId: 7 }),
      preparedLaunch({ accountName: 7 }),
      preparedLaunch({ expiresAtUtc: 'not a date' }),
      preparedLaunch({ expiresAtUtc: 1_700_000_000 }),
      preparedLaunch({ env: 'nope' }),
      preparedLaunch({ env: [] }),
    ];
    for (const raw of malformed) expect(() => validateLaunchResponse(raw)).toThrow(AstraError);
  });
});

describe('settings conflicts', () => {
  it('rejects named profiles and alternate telemetry destinations from settings', () => {
    for (const key of [
      'ANTHROPIC_PROFILE',
      'ANTHROPIC_FEDERATION_RULE_ID',
      'OTEL_EXPORTER_OTLP_ENDPOINT',
      'CLAUDE_CONFIG_DIR',
    ]) {
      expect(
        settingsConflicts('settings.json', { env: { [key]: 'private-value' } }),
      ).toContainEqual({ file: 'settings.json', key: `env.${key}` });
    }
  });

  let workDir: string;

  const configDir = (): string => path.join(workDir, 'claude-config');
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
    const error = errorOf(() => assertNoSettingsConflicts(configDir(), cwd));
    expect(error).toBeInstanceOf(AstraError);
    const text = textOf(error);
    expect(text).toContain(file);
    expect(text).toContain('env.ANTHROPIC_AUTH_TOKEN');
    expect(text).not.toContain('sk-live-secret');
    expect(text).toContain('Clients → Claude Code');
  });

  it('blocks the user-level settings file of the configuration directory', () => {
    const file = write(
      'claude-config/settings.json',
      JSON.stringify({ env: { ANTHROPIC_BASE_URL: 'http://127.0.0.1:17321' } }),
    );
    const error = errorOf(() => assertNoSettingsConflicts(configDir(), projectDir()));
    const text = textOf(error);
    expect(text).toContain(file);
    expect(text).toContain('env.ANTHROPIC_BASE_URL');
    expect(text).not.toContain('127.0.0.1:17321');
  });

  it('scans exactly the user-level settings file and the project files up to the git root', () => {
    const cwd = projectDir();
    const files = settingsFilesToCheck(configDir(), cwd);
    expect(files).toContain(path.join(configDir(), 'settings.json'));
    expect(files).toContain(path.join(workDir, 'repo', '.claude', 'settings.json'));
    expect(files).toContain(path.join(workDir, 'repo', '.claude', 'settings.local.json'));
    expect(files).toContain(path.join(workDir, 'repo', 'sub', '.claude', 'settings.local.json'));
    // The walk stops at the git root and never reaches the temp directory above it.
    expect(files).not.toContain(path.join(workDir, '.claude', 'settings.json'));
  });

  it('walks from the cwd up to the git root, including settings.local.json', () => {
    const cwd = projectDir();
    const file = write(
      'repo/.claude/settings.local.json',
      JSON.stringify({ forceLoginGatewayUrl: 'https://relay.example' }),
    );
    const error = errorOf(() => assertNoSettingsConflicts(configDir(), cwd));
    const text = textOf(error);
    expect(text).toContain(file);
    expect(text).not.toContain('relay.example');
  });

  it('blocks the user-level helpers too', () => {
    const file = write(
      'claude-config/settings.json',
      JSON.stringify({ apiKeyHelper: 'curl secret' }),
    );
    const error = errorOf(() => assertNoSettingsConflicts(configDir(), projectDir()));
    expect(textOf(error)).toContain('apiKeyHelper');
    expect(textOf(error)).toContain(file);
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
    expect(() => assertNoSettingsConflicts(configDir(), cwd)).not.toThrow();
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
    expect(errorOf(() => assertNoSettingsConflicts(configDir(), cwd)).message).toContain(file);
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
  let configDir: string;
  let originalHome: string | undefined;
  let log: ReturnType<typeof vi.spyOn>;
  let error: ReturnType<typeof vi.spyOn>;
  let output: string[];

  beforeEach(() => {
    workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-claude-run-'));
    fs.mkdirSync(path.join(workDir, '.git'));
    configDir = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-claude-config-'));
    originalHome = process.env.ASTRA_HOME;
    process.env.ASTRA_HOME = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-claude-home-'));
    state.prepared = preparedLaunch();
    state.requests.length = 0;
    output = [];
    log = vi.spyOn(console, 'log').mockImplementation((...args: unknown[]) => {
      output.push(args.map(String).join(' '));
    });
    error = vi.spyOn(console, 'error').mockImplementation((...args: unknown[]) => {
      output.push(args.map(String).join(' '));
    });
  });

  afterEach(() => {
    log.mockRestore();
    error.mockRestore();
    process.exitCode = undefined;
    if (originalHome === undefined) delete process.env.ASTRA_HOME;
    else process.env.ASTRA_HOME = originalHome;
    fs.rmSync(workDir, { recursive: true, force: true });
    fs.rmSync(configDir, { recursive: true, force: true });
  });

  const run = (opts: Parameters<typeof runClaude>[0], spawnImpl: ClaudeSpawn): Promise<void> =>
    runClaude(opts, {
      spawn: spawnImpl,
      env: {
        PATH: '/usr/bin',
        HOME: '/home/u',
        HTTPS_PROXY: 'http://proxy.example:3128',
        CLAUDE_CONFIG_DIR: path.join(workDir, 'inherited-config'),
        ANTHROPIC_AUTH_TOKEN: 'astra-gateway-token',
        OTEL_LOGS_EXPORTER: 'console',
        CLAUDE_CODE_OAUTH_TOKEN: 'inherited-oauth-token',
      },
      cwd: workDir,
      configDir,
    });

  it('runs the official session with the subscription token and no arguments', async () => {
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 3 }));
    await run({}, spawnImpl);

    expect(calls).toHaveLength(1);
    expect(calls[0]?.command).toBe('claude');
    expect(calls[0]?.args).toEqual([]);
    expect(calls[0]?.options.shell).toBe(false);
    expect(calls[0]?.options.stdio).toBe('inherit');
    expect(calls[0]?.options.cwd).toBe(workDir);
    expect(calls[0]?.options.env?.CLAUDE_CODE_OAUTH_TOKEN).toBe(SERVER_TOKEN);
    expect(calls[0]?.options.env?.ANTHROPIC_BASE_URL).toBe('https://api.anthropic.com');
    expect(calls[0]?.options.env?.ANTHROPIC_API_KEY).toBe('');
    expect(calls[0]?.options.env?.HTTPS_PROXY).toBe('http://proxy.example:3128');
    // The inherited gateway token is gone; CLAUDE_CONFIG_DIR is left exactly as the user had it.
    expect(calls[0]?.options.env?.ANTHROPIC_AUTH_TOKEN).toBe('');
    expect(calls[0]?.options.env?.OTEL_LOGS_EXPORTER).toBe('none');
    expect(calls[0]?.options.env?.CLAUDE_CONFIG_DIR).toBe(path.join(workDir, 'inherited-config'));
    expect(process.exitCode).toBe(3);
    expect(state.requests).toEqual([{ path: PREPARE_PATH, method: 'POST', body: {} }]);
  });

  it('prints the account and the token lifetime but never the token', async () => {
    const { spawnImpl } = fakeSpawn(() => ({ code: 0 }));
    await run({}, spawnImpl);

    const text = output.join('\n');
    expect(text).toContain('Work subscription');
    expect(text).toContain('straight to Anthropic');
    expect(text).not.toContain(SERVER_TOKEN);
    expect(text).not.toContain('astra-gateway-token');
  });

  it('warns when the token has less than 30 minutes left', async () => {
    state.prepared = preparedLaunch({ expiresAtUtc: EXPIRES_SOON });
    const { spawnImpl } = fakeSpawn(() => ({ code: 0 }));
    await run({}, spawnImpl);
    expect(output.join('\n')).toContain('less than 30 minutes');
  });

  it('passes --account through to the server', async () => {
    const { spawnImpl } = fakeSpawn(() => ({ code: 0 }));
    await run({ account: ACCOUNT_ID }, spawnImpl);
    expect(state.requests[0]?.body).toEqual({ accountId: ACCOUNT_ID });
  });

  it('refuses a malformed account id before touching the server', async () => {
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 0 }));
    for (const account of ['../etc', 'a'.repeat(65), 'bad id', '', 'a/b']) {
      expect(await rejectionOf(run({ account }, spawnImpl))).toBeInstanceOf(AstraError);
    }
    expect(calls).toEqual([]);
    expect(state.requests).toEqual([]);
  });

  it('blocks a run when project settings would override the subscription', async () => {
    const file = path.join(workDir, '.claude', 'settings.json');
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify({ env: { ANTHROPIC_API_KEY: 'sk-project-secret' } }));
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 0 }));
    const failure = await rejectionOf(run({}, spawnImpl));
    const text = textOf(failure);
    expect(text).toContain(file);
    expect(text).toContain('env.ANTHROPIC_API_KEY');
    expect(text).not.toContain('sk-project-secret');
    expect(text).toContain('Clients → Claude Code');
    expect(calls).toEqual([]);
    expect(state.requests).toEqual([]);
  });

  it('blocks a run when the user-level settings would override the subscription', async () => {
    const file = path.join(configDir, 'settings.json');
    fs.writeFileSync(file, JSON.stringify({ env: { ANTHROPIC_AUTH_TOKEN: 'sk-user-secret' } }));
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 0 }));
    const failure = await rejectionOf(run({}, spawnImpl));
    expect(textOf(failure)).toContain(file);
    expect(textOf(failure)).not.toContain('sk-user-secret');
    expect(calls).toEqual([]);
    expect(state.requests).toEqual([]);
  });

  it('refuses a launch environment that did not come from the server', async () => {
    state.prepared = preparedLaunch({
      env: { CLAUDE_CODE_OAUTH_TOKEN: SERVER_TOKEN, NOT_ALLOWED: 'nope' },
    });
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 0 }));
    const failure = await rejectionOf(run({}, spawnImpl));
    expect(failure).toBeInstanceOf(AstraError);
    expect(failure.message).toContain('NOT_ALLOWED');
    expect(failure.message).not.toContain(SERVER_TOKEN);
    expect(calls).toEqual([]);
    expect(output.join('\n')).not.toContain(SERVER_TOKEN);
  });

  it('reports the server message of a refused prepare', async () => {
    const { ApiError } = await import('../src/lib/http.js');
    state.prepared = new ApiError('Select subscription direct mode first.', 409);
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 0 }));
    const failure = await rejectionOf(run({}, spawnImpl));
    expect(failure).toBeInstanceOf(ApiError);
    expect(failure.message).toContain('Select subscription direct mode first.');
    expect(failure.message).not.toContain(SERVER_TOKEN);
    expect(calls).toEqual([]);
  });

  it('reports a missing official CLI instead of running a session', async () => {
    const enoent = Object.assign(new Error('spawn claude ENOENT'), { code: 'ENOENT' });
    const { spawnImpl } = fakeSpawn(() => ({ code: null, error: enoent }));
    const failure = await rejectionOf(run({}, spawnImpl));
    expect(failure.message).toContain('not found on PATH');
    expect(failure.message).not.toContain(SERVER_TOKEN);
  });

  it('turns a signal death into a failure exit code', async () => {
    const { spawnImpl } = fakeSpawn(() => ({ code: null }));
    await run({}, spawnImpl);
    expect(process.exitCode).toBe(1);
  });

  it('--status reports the account without starting Claude Code', async () => {
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 0 }));
    await run({ status: true }, spawnImpl);

    expect(calls).toEqual([]);
    expect(state.requests).toEqual([{ path: PREPARE_PATH, method: 'POST', body: {} }]);
    const text = output.join('\n');
    expect(text).toContain('Work subscription');
    expect(text).toContain('Statistics (OpenTelemetry): disabled');
    expect(text).toContain('Token valid until:');
    expect(text).not.toContain(SERVER_TOKEN);
    expect(process.exitCode).toBeUndefined();
  });

  it('--status shows enabled statistics without printing any environment value', async () => {
    state.prepared = preparedLaunch({
      env: {
        CLAUDE_CODE_OAUTH_TOKEN: SERVER_TOKEN,
        CLAUDE_CODE_ENABLE_TELEMETRY: '1',
        OTEL_EXPORTER_OTLP_LOGS_HEADERS: 'Authorization=Bearer collector-secret',
      },
    });
    const { spawnImpl, calls } = fakeSpawn(() => ({ code: 0 }));
    await run({ status: true }, spawnImpl);

    expect(calls).toEqual([]);
    const text = output.join('\n');
    expect(text).toContain('Statistics (OpenTelemetry): enabled');
    expect(text).not.toContain(SERVER_TOKEN);
    expect(text).not.toContain('collector-secret');
  });
});
