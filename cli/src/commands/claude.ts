import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { spawn, type ChildProcess } from 'node:child_process';
import { AstraError } from '../errors.js';
import { info, style, success } from '../ui.js';
import { readTextOrNull } from '../lib/json-file.js';
import { apiRequest } from '../lib/http.js';
import { ensureServerRunning } from '../lib/server-lifecycle.js';
import { astraHome, homePaths } from '../lib/paths.js';

/**
 * `astra claude` runs the official Claude Code CLI against a native login profile that Astra created
 * under <astra home>/claude-profiles/<id> (server side: ClaudeDirectService). The server only hands
 * back paths plus a small environment overlay; this command spawns a fixed argv with `shell: false`,
 * so no command, script or argument ever comes back from the network.
 */

export type ClaudeAction = 'login' | 'status' | 'run';

export interface ClaudeOptions {
  /** Native profile id; omitted, the server uses the selected profile. */
  profile?: string;
  login?: boolean;
  status?: boolean;
}

/** Launcher file the server writes next to the profile; the CLI only reads it. */
const LAUNCH_FILE_NAME = 'astra-launch.json';
const PREPARE_PATH = '/api/clients/claude-code/direct/prepare';
const PROFILE_ID_PATTERN = /^[a-f0-9]{32}$/;
/** The login method the launch file pins for a subscription profile. */
const SUBSCRIPTION_LOGIN_METHOD = 'claudeai';
const OFFICIAL_BASE_URL = 'https://api.anthropic.com';
/** The only sign-in the CLI accepts: a Claude subscription, never a key or a gateway token. */
const SUBSCRIPTION_AUTH_METHOD = 'claude.ai';
const AUTH_STATUS_TIMEOUT_MS = 15_000;
const AUTH_STATUS_MAX_BYTES = 64 * 1024;

/**
 * Dropped from the inherited environment before the launch file is merged back in: every Anthropic
 * endpoint/credential (`ANTHROPIC_*`, which already covers `ANTHROPIC_FEDERATION_*`) and every
 * telemetry exporter variable (`OTEL_*`).
 */
const ENV_PREFIX_DENYLIST = ['ANTHROPIC_', 'OTEL_'];

/** Claude Code's telemetry, auth and external-provider switches. Unrelated safety variables stay. */
const ENV_NAME_DENYLIST = [
  'CLAUDE_CODE_OAUTH_TOKEN',
  'CLAUDE_CODE_USE_BEDROCK',
  'CLAUDE_CODE_USE_VERTEX',
  'CLAUDE_CODE_USE_FOUNDRY',
  'CLAUDE_CODE_ENABLE_TELEMETRY',
  'CLAUDE_CODE_ENHANCED_TELEMETRY_BETA',
  'CLAUDE_CODE_OTEL_HEADERS_HELPER',
  'CLAUDE_CODE_OTEL_HEADERS_HELPER_DEBOUNCE_MS',
  'ENABLE_ENHANCED_TELEMETRY_BETA',
  'ENABLE_BETA_TRACING_DETAILED',
  'BETA_TRACING_ENDPOINT',
];

/** The only names the launch file may set; anything else in it is ignored. */
export const LAUNCH_ENV_WHITELIST = [
  'ANTHROPIC_BASE_URL',
  'ANTHROPIC_API_KEY',
  'ANTHROPIC_AUTH_TOKEN',
  'CLAUDE_CODE_OAUTH_TOKEN',
  'CLAUDE_CODE_USE_BEDROCK',
  'CLAUDE_CODE_USE_VERTEX',
  'CLAUDE_CODE_USE_FOUNDRY',
  'CLAUDE_CODE_ENABLE_TELEMETRY',
  'CLAUDE_CODE_ENHANCED_TELEMETRY_BETA',
  'ENABLE_ENHANCED_TELEMETRY_BETA',
  'ENABLE_BETA_TRACING_DETAILED',
  'BETA_TRACING_ENDPOINT',
  'OTEL_LOGS_EXPORTER',
  'OTEL_METRICS_EXPORTER',
  'OTEL_TRACES_EXPORTER',
  'OTEL_EXPORTER_OTLP_LOGS_PROTOCOL',
  'OTEL_EXPORTER_OTLP_LOGS_ENDPOINT',
  'OTEL_EXPORTER_OTLP_LOGS_HEADERS',
  'OTEL_LOG_USER_PROMPTS',
  'OTEL_LOG_ASSISTANT_RESPONSES',
  'OTEL_LOG_TOOL_DETAILS',
  'OTEL_LOG_TOOL_CONTENT',
  'OTEL_LOG_RAW_API_BODIES',
  'OTEL_RESOURCE_ATTRIBUTES',
] as const;

/** Credentials a native subscription profile must not carry. */
const CREDENTIAL_ENV_KEYS = [
  'ANTHROPIC_API_KEY',
  'ANTHROPIC_AUTH_TOKEN',
  'CLAUDE_CODE_OAUTH_TOKEN',
];
/** Switches that would send traffic to Bedrock, Vertex or Foundry instead of the subscription. */
const EXTERNAL_PROVIDER_ENV_KEYS = [
  'CLAUDE_CODE_USE_BEDROCK',
  'CLAUDE_CODE_USE_VERTEX',
  'CLAUDE_CODE_USE_FOUNDRY',
];
/** Settings keys that hand credential handling to an external command. */
const HELPER_SETTING_KEYS = ['apiKeyHelper', 'otelHeadersHelper'];

export interface ClaudeSpawnOptions {
  cwd: string;
  env: NodeJS.ProcessEnv;
  shell: false;
  stdio: 'inherit' | 'pipe';
  windowsHide: true;
}

/** Narrow spawn signature so tests can hand in a fake process factory. */
export type ClaudeSpawn = (
  command: string,
  args: readonly string[],
  options: ClaudeSpawnOptions,
) => ChildProcess;

export interface ClaudeDeps {
  spawn?: ClaudeSpawn;
  env?: NodeJS.ProcessEnv;
  cwd?: string;
}

const spawnOfficial: ClaudeSpawn = (command, args, options) => spawn(command, [...args], options);

const OFFICIAL_CLI = 'claude';

export interface ClaudeLaunch {
  profileId: string;
  configDirectory: string;
  settingsFile: string;
}

export interface ClaudeAuthStatus {
  loggedIn: boolean;
  authMethod: string | null;
}

export interface SettingsConflict {
  file: string;
  key: string;
}

export async function runClaude(opts: ClaudeOptions, deps: ClaudeDeps = {}): Promise<void> {
  if (opts.login && opts.status) {
    throw new AstraError(
      '--login and --status cannot be combined.',
      'Use `astra claude --login` to sign in, or `astra claude --status` to check the sign-in state.',
    );
  }
  if (opts.profile !== undefined && !PROFILE_ID_PATTERN.test(opts.profile)) {
    throw new AstraError(
      '--profile expects a 32-character profile id.',
      'Pick a native profile in the Astra web UI (Clients → Claude Code) and copy its id.',
    );
  }

  const action: ClaudeAction = opts.login ? 'login' : opts.status ? 'status' : 'run';
  const spawnImpl = deps.spawn ?? spawnOfficial;
  const cwd = deps.cwd ?? process.cwd();
  const launch = await prepareLaunch(action, opts.profile);
  const childEnv = buildClaudeEnv(
    deps.env ?? process.env,
    readLaunchEnv(launch.settingsFile),
    launch.configDirectory,
  );

  // Inspect before starting any Claude process: auth status itself may execute a configured helper.
  assertNoSettingsConflicts(launch.configDirectory, cwd);
  if (action === 'run') {
    assertSubscriptionAuth(await probeAuthStatus(spawnImpl, childEnv, cwd));
  }

  const args =
    action === 'login'
      ? ['auth', 'login']
      : action === 'status'
        ? ['auth', 'status']
        : ['--settings', launch.settingsFile];
  if (action === 'login') {
    info(style.dim(`Signing in the official Claude Code CLI (profile ${launch.profileId}).`));
  }

  const result = await runInherit(spawnImpl, args, childEnv, cwd);
  process.exitCode = exitCodeFrom(result.code);

  if (action === 'login' && result.code === 0) {
    // A successful `auth login` is not proof by itself: confirm the subscription sign-in took effect.
    assertSubscriptionAuth(await probeAuthStatus(spawnImpl, childEnv, cwd));
    success('Signed in — the Claude subscription profile is ready.');
    info(style.dim(launch.configDirectory));
  }
}

/** Asks the server for the profile paths and refuses anything outside this machine's Astra data dir. */
async function prepareLaunch(action: ClaudeAction, profileId?: string): Promise<ClaudeLaunch> {
  const { base, runtime, started } = await ensureServerRunning();
  if (started) console.log('Server was not running — started it in the background.');
  const response = await apiRequest<unknown>({
    base,
    path: PREPARE_PATH,
    method: 'POST',
    body: { action, ...(profileId ? { profileId } : {}) },
    runtimeToken: runtime?.runtimeToken,
  });
  return validateLaunchResponse(response, astraHome());
}

/**
 * The server may only point at <astra home>/claude-profiles/<id>/astra-launch.json: profiles are
 * per-machine, so a path from anywhere else is refused rather than run.
 */
export function validateLaunchResponse(raw: unknown, home: string): ClaudeLaunch {
  const expectedDirectory = path.resolve(homePaths(home).claudeProfilesDir);
  if (!isPlainObject(raw)) throw invalidLaunch();
  const { profileId, configDirectory, settingsFile } = raw;
  if (typeof profileId !== 'string' || !PROFILE_ID_PATTERN.test(profileId)) throw invalidLaunch();
  if (typeof configDirectory !== 'string' || typeof settingsFile !== 'string')
    throw invalidLaunch();
  if (path.resolve(configDirectory) !== path.join(expectedDirectory, profileId))
    throw invalidLaunch();
  const expectedSettings = path.join(expectedDirectory, profileId, LAUNCH_FILE_NAME);
  if (path.resolve(settingsFile) !== expectedSettings) throw invalidLaunch();
  return {
    profileId,
    configDirectory: path.dirname(expectedSettings),
    settingsFile: expectedSettings,
  };
}

function invalidLaunch(): AstraError {
  return new AstraError(
    "The Astra server returned a Claude profile outside this machine's Astra data directory.",
    'Astra only runs profiles it created under <astra home>/claude-profiles. Restart the server and try again.',
  );
}

/** Reads the `env` overlay out of the launch file; its content is never printed. */
export function readLaunchEnv(settingsFile: string): Record<string, unknown> {
  for (const candidate of [settingsFile, path.dirname(settingsFile), path.dirname(path.dirname(settingsFile))]) {
    try {
      const stat = fs.lstatSync(candidate);
      if (stat.isSymbolicLink() || (candidate === settingsFile && (!stat.isFile() || stat.size > 64 * 1024))) {
        throw new AstraError('The native Claude launch path must be a regular, bounded local file.');
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
    }
  }
  const text = readTextOrNull(settingsFile);
  if (text === null) {
    throw new AstraError(
      `The Astra launch settings file is missing: ${settingsFile}`,
      'Run the command again so the Astra server can recreate it.',
    );
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new AstraError(`The Astra launch settings file is not valid JSON: ${settingsFile}`);
  }
  if (!isPlainObject(parsed)) {
    throw new AstraError(`The Astra launch settings file is not a JSON object: ${settingsFile}`);
  }
  if (parsed.forceLoginMethod !== SUBSCRIPTION_LOGIN_METHOD || !isPlainObject(parsed.env) ||
    Object.keys(parsed).some((key) => key !== 'forceLoginMethod' && key !== 'env') ||
    Object.entries(parsed.env).some(([key, value]) =>
      !(LAUNCH_ENV_WHITELIST as readonly string[]).includes(key) || typeof value !== 'string')) {
    throw new AstraError(`The Astra launch settings file contains unexpected settings: ${settingsFile}`);
  }
  return parsed.env;
}

/** Builds the child environment: the inherited one without Claude's endpoint, credential and telemetry
 * variables, plus only the whitelisted names the launch file supplies, then the absolute profile dir. */
export function buildClaudeEnv(
  baseEnv: NodeJS.ProcessEnv,
  launchEnv: Record<string, unknown>,
  configDirectory: string,
): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {};
  for (const [key, value] of Object.entries(baseEnv)) {
    if (ENV_PREFIX_DENYLIST.some((prefix) => key.startsWith(prefix))) continue;
    if ((ENV_NAME_DENYLIST as readonly string[]).includes(key)) continue;
    env[key] = value;
  }
  for (const key of LAUNCH_ENV_WHITELIST) {
    const value = launchEnv[key];
    if (typeof value === 'string') env[key] = value;
  }
  env.CLAUDE_CONFIG_DIR = path.resolve(configDirectory);
  return env;
}

/** Refuses to run when project or profile settings would override the profile's own sign-in. */
export function assertNoSettingsConflicts(configDirectory: string, cwd: string): void {
  const conflicts: SettingsConflict[] = [];
  for (const file of settingsFilesToCheck(configDirectory, cwd)) {
    const text = readTextOrNull(file);
    if (text === null) continue;
    conflicts.push(...settingsConflicts(file, parseSettingsJson(file, text)));
  }
  if (conflicts.length === 0) return;
  const detail = conflicts.map((c) => `  ${c.key}  ${c.file}`).join('\n');
  throw new AstraError(
    `Claude settings on this machine override the native profile (${conflicts.length} conflict${conflicts.length === 1 ? '' : 's'}):`,
    `${detail}\n  Astra never edits your settings — remove or rename these entries yourself.`,
  );
}

/** The profile's own settings plus the project settings Claude Code reads from cwd up to the git root. */
export function settingsFilesToCheck(configDirectory: string, cwd: string): string[] {
  const files = [path.join(configDirectory, 'settings.json')];
  const root = findGitRoot(cwd);
  let dir = path.resolve(cwd);
  for (;;) {
    files.push(path.join(dir, '.claude', 'settings.json'));
    files.push(path.join(dir, '.claude', 'settings.local.json'));
    if (root === null || dir === root) break;
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return [...new Set(files)];
}

function findGitRoot(startDir: string): string | null {
  let dir = path.resolve(startDir);
  for (;;) {
    if (fs.existsSync(path.join(dir, '.git'))) return dir;
    const parent = path.dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

/** An unknown or malformed settings file is refused, never silently ignored. */
export function parseSettingsJson(file: string, text: string): Record<string, unknown> {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new AstraError(
      `Claude settings are not valid JSON: ${file}`,
      'Fix or remove the file yourself — Astra never edits your settings.',
    );
  }
  if (!isPlainObject(parsed)) {
    throw new AstraError(
      `Claude settings are not a JSON object: ${file}`,
      'Fix or remove the file yourself — Astra never edits your settings.',
    );
  }
  return parsed;
}

/** Conflicts carry the file path and the key name only — never the configured value. */
export function settingsConflicts(
  file: string,
  settings: Record<string, unknown>,
): SettingsConflict[] {
  const conflicts: SettingsConflict[] = [];
  if (settings.env !== undefined && !isPlainObject(settings.env)) {
    conflicts.push({ file, key: 'env' });
  }
  const env = isPlainObject(settings.env) ? settings.env : {};
  for (const [key, value] of Object.entries(env)) {
    // Settings may reintroduce variables scrubbed from the parent shell, including named profiles.
    if ((key.startsWith('OTEL_') || key.startsWith('ANTHROPIC_FEDERATION_') ||
      key === 'ANTHROPIC_PROFILE' || key === 'ANTHROPIC_ORGANIZATION_ID' ||
      key === 'CLAUDE_CONFIG_DIR' || key === 'BETA_TRACING_ENDPOINT') && isNonEmptyValue(value)) {
      conflicts.push({ file, key: `env.${key}` });
    }
  }
  for (const key of CREDENTIAL_ENV_KEYS) {
    if (isNonEmptyText(env[key])) conflicts.push({ file, key: `env.${key}` });
  }
  for (const key of EXTERNAL_PROVIDER_ENV_KEYS) {
    if (isEnabledFlag(env[key])) conflicts.push({ file, key: `env.${key}` });
  }
  if (
    isNonEmptyText(env.ANTHROPIC_BASE_URL) &&
    String(env.ANTHROPIC_BASE_URL).trim() !== OFFICIAL_BASE_URL
  ) {
    conflicts.push({ file, key: 'env.ANTHROPIC_BASE_URL' });
  }
  for (const key of HELPER_SETTING_KEYS) {
    if (isNonEmptyValue(settings[key])) conflicts.push({ file, key });
  }
  if (
    isNonEmptyText(settings.forceLoginMethod) &&
    String(settings.forceLoginMethod).trim() !== SUBSCRIPTION_LOGIN_METHOD
  ) {
    conflicts.push({ file, key: 'forceLoginMethod' });
  }
  if (isNonEmptyValue(settings.forceLoginGatewayUrl))
    conflicts.push({ file, key: 'forceLoginGatewayUrl' });
  return conflicts;
}

/** Runs `claude auth status`, bounded in both time and output, and parses the JSON it prints. */
async function probeAuthStatus(
  spawnImpl: ClaudeSpawn,
  env: NodeJS.ProcessEnv,
  cwd: string,
): Promise<ClaudeAuthStatus> {
  const result = await capture(spawnImpl, ['auth', 'status'], env, cwd);
  if (result.code !== 0) {
    throw new AstraError(
      'The official Claude Code CLI could not report the sign-in state of this profile.',
      'Run `astra claude --status` to see the official output.',
    );
  }
  return parseAuthStatus(result.stdout);
}

/** Only a Claude subscription (`claude.ai`) is accepted; an API key or OAuth token is not. */
export function assertSubscriptionAuth(status: ClaudeAuthStatus): void {
  if (status.loggedIn && status.authMethod === SUBSCRIPTION_AUTH_METHOD) return;
  if (!status.loggedIn) {
    throw new AstraError(
      'This Claude profile is not signed in.',
      'Run `astra claude --login` to sign in with a Claude subscription.',
    );
  }
  throw new AstraError(
    `This Claude profile is signed in with "${status.authMethod ?? 'an unknown method'}", not a Claude subscription.`,
    'Sign in with a subscription: run `astra claude --login` after clearing the other credential.',
  );
}

/** Parses `auth status` output; the raw JSON never reaches an error message. */
export function parseAuthStatus(text: string): ClaudeAuthStatus {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text.trim());
  } catch {
    throw unreadableAuthStatus();
  }
  if (!isPlainObject(parsed) || typeof parsed.loggedIn !== 'boolean') throw unreadableAuthStatus();
  return {
    loggedIn: parsed.loggedIn,
    authMethod: typeof parsed.authMethod === 'string' ? parsed.authMethod : null,
  };
}

function unreadableAuthStatus(): AstraError {
  return new AstraError(
    'Could not read the sign-in state of the official Claude Code CLI.',
    'Run `astra claude --status` to see the official output.',
  );
}

/** A child that died from a signal has a null exit code and must still count as a failure. */
export function exitCodeFrom(code: number | null): number {
  return code ?? 1;
}

interface CapturedRun {
  code: number | null;
  stdout: string;
}

function capture(
  spawnImpl: ClaudeSpawn,
  args: readonly string[],
  env: NodeJS.ProcessEnv,
  cwd: string,
): Promise<CapturedRun> {
  return new Promise((resolve, reject) => {
    let child: ChildProcess;
    try {
      child = spawnImpl(OFFICIAL_CLI, args, {
        cwd,
        env,
        shell: false,
        stdio: 'pipe',
        windowsHide: true,
      });
    } catch (err) {
      reject(spawnFailure(err));
      return;
    }
    let stdout = '';
    let bytes = 0;
    let settled = false;
    const finish = (action: () => void): void => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      action();
    };
    const timer = setTimeout(() => {
      kill(child);
      finish(() =>
        reject(
          new AstraError(
            `The official Claude Code CLI did not answer within ${AUTH_STATUS_TIMEOUT_MS / 1000} seconds.`,
          ),
        ),
      );
    }, AUTH_STATUS_TIMEOUT_MS);

    child.stdout?.on('data', (chunk: Buffer | string) => {
      if (settled) return;
      bytes += Buffer.byteLength(chunk);
      if (bytes > AUTH_STATUS_MAX_BYTES) {
        kill(child);
        finish(() =>
          reject(new AstraError('The official Claude Code CLI printed more than expected.')),
        );
        return;
      }
      stdout += chunk.toString();
    });
    // Drain stderr so the child cannot block on a full pipe; its content is never kept.
    child.stderr?.on('data', () => {
      /* discarded on purpose */
    });
    child.stdin?.end();
    child.on('error', (err) => finish(() => reject(spawnFailure(err))));
    child.on('close', (code) => finish(() => resolve({ code, stdout })));
  });
}

interface InheritResult {
  code: number | null;
}

/** Runs the official CLI on this terminal, forwarding SIGINT/SIGTERM so no child is left behind. */
function runInherit(
  spawnImpl: ClaudeSpawn,
  args: readonly string[],
  env: NodeJS.ProcessEnv,
  cwd: string,
): Promise<InheritResult> {
  return new Promise((resolve, reject) => {
    let child: ChildProcess;
    try {
      child = spawnImpl(OFFICIAL_CLI, args, {
        cwd,
        env,
        shell: false,
        stdio: 'inherit',
        windowsHide: true,
      });
    } catch (err) {
      reject(spawnFailure(err));
      return;
    }
    const forward = (signal: NodeJS.Signals): void => {
      try {
        child.kill(signal);
      } catch {
        /* already gone */
      }
    };
    const onInterrupt = (): void => forward('SIGINT');
    const onTerminate = (): void => forward('SIGTERM');
    const cleanup = (): void => {
      process.removeListener('SIGINT', onInterrupt);
      process.removeListener('SIGTERM', onTerminate);
    };
    process.on('SIGINT', onInterrupt);
    process.on('SIGTERM', onTerminate);
    child.on('error', (err) => {
      cleanup();
      reject(spawnFailure(err));
    });
    child.on('close', (code) => {
      cleanup();
      resolve({ code });
    });
  });
}

function kill(child: ChildProcess): void {
  try {
    child.kill('SIGKILL');
  } catch {
    /* already gone */
  }
}

function spawnFailure(err: unknown): AstraError {
  const code = (err as NodeJS.ErrnoException).code;
  if (code === 'ENOENT') {
    return new AstraError(
      'The official Claude Code CLI was not found on PATH.',
      'Install it with `npm install -g @anthropic-ai/claude-code`.',
    );
  }
  return new AstraError(
    `Could not start the official Claude Code CLI: ${code ?? (err instanceof Error ? err.message : 'unknown error')}`,
  );
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNonEmptyText(value: unknown): boolean {
  return typeof value === 'string' && value.trim() !== '';
}

/** For provider switches, "0"/"false" means off — the launch file itself writes 0 to disable them. */
function isEnabledFlag(value: unknown): boolean {
  if (typeof value === 'boolean') return value;
  if (typeof value === 'number') return value !== 0;
  if (typeof value === 'string')
    return !['', '0', 'false', 'no', 'off'].includes(value.trim().toLowerCase());
  return false;
}

function isNonEmptyValue(value: unknown): boolean {
  if (typeof value === 'string') return value.trim() !== '';
  if (Array.isArray(value)) return value.length > 0;
  if (isPlainObject(value)) return Object.keys(value).length > 0;
  if (typeof value === 'number') return true;
  return value === true;
}
