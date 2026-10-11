import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { spawn, type ChildProcess } from 'node:child_process';
import { AstraError } from '../errors.js';
import { info, warn } from '../ui.js';
import { readTextOrNull } from '../lib/json-file.js';
import { apiRequest } from '../lib/http.js';
import { ensureServerRunning } from '../lib/server-lifecycle.js';

/**
 * `astra claude` runs the official Claude Code CLI with a subscription account Astra already holds. The
 * server (ClaudeDirectService) hands back a short-lived access token plus a small environment overlay, on
 * loopback only; this command spawns a fixed argv with `shell: false`, so no command, script or argument
 * ever comes back from the network. The token is never printed, logged or written to disk.
 */

export interface ClaudeOptions {
  /** Subscription account id; omitted, the server uses the account it has selected. */
  account?: string;
  status?: boolean;
}

const PREPARE_PATH = '/api/clients/claude-code/direct/prepare';
const ACCOUNT_ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;
const OFFICIAL_BASE_URL = 'https://api.anthropic.com';
/** Tokens last hours, not days: warn when the one handed over cannot cover a normal session. */
const TOKEN_LIFETIME_WARNING_MINUTES = 30;

/**
 * Dropped from the inherited environment before the server overlay is merged in: every Anthropic
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

/** The only names the server may set; anything else in its response is refused, never ignored. */
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

/** The credential the session is started with; without it the CLI would fall back to another sign-in. */
const REQUIRED_LAUNCH_ENV_KEY = 'CLAUDE_CODE_OAUTH_TOKEN';

/** Credentials a settings file must not carry into the session. */
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
/** The only `forceLoginMethod` that leaves the subscription in charge. */
const SUBSCRIPTION_LOGIN_METHOD = 'claudeai';

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
  /** Claude Code's user-level configuration directory; defaults to $CLAUDE_CONFIG_DIR or ~/.claude. */
  configDir?: string;
}

const spawnOfficial: ClaudeSpawn = (command, args, options) => spawn(command, [...args], options);

const OFFICIAL_CLI = 'claude';

export interface ClaudeLaunch {
  accountId: string;
  accountName: string;
  expiresAt: Date;
  env: Record<string, string>;
}

export interface SettingsConflict {
  file: string;
  key: string;
}

export async function runClaude(opts: ClaudeOptions, deps: ClaudeDeps = {}): Promise<void> {
  if (opts.account !== undefined && !ACCOUNT_ID_PATTERN.test(opts.account)) {
    throw new AstraError(
      '--account expects an account id of up to 64 letters, digits, "_" or "-".',
      'Pick the subscription account in the Astra web UI (Clients → Claude Code) and copy its id.',
    );
  }

  const env = deps.env ?? process.env;
  const cwd = deps.cwd ?? process.cwd();

  // `--status` reports what Astra would hand over, so it never inspects settings nor starts anything.
  if (opts.status) {
    printStatus(await prepareLaunch(opts.account));
    return;
  }

  // Inspect settings before anything else: a settings file can outrank the token the server is about to issue.
  assertNoSettingsConflicts(claudeConfigDir(env, deps.configDir), cwd);

  const launch = await prepareLaunch(opts.account);
  const childEnv = buildClaudeEnv(env, launch.env);
  printLaunch(launch);

  const spawnImpl = deps.spawn ?? spawnOfficial;
  const result = await runInherit(spawnImpl, [], childEnv, cwd);
  process.exitCode = exitCodeFrom(result.code);
}

function printLaunch(launch: ClaudeLaunch): void {
  info(
    `Using Claude subscription account "${launch.accountName}" (token valid until ${formatLocalTime(launch.expiresAt)}) — ` +
      'requests go straight to Anthropic, not through the Astra gateway.',
  );
  if (launch.expiresAt.getTime() - Date.now() < TOKEN_LIFETIME_WARNING_MINUTES * 60_000) {
    warn(
      `The access token expires in less than ${TOKEN_LIFETIME_WARNING_MINUTES} minutes; restart this command when it does.`,
    );
  }
}

/** Names and lifetime only: no environment value and no token is ever printed. */
function printStatus(launch: ClaudeLaunch): void {
  info(`Account: ${launch.accountName}`);
  info(`Token valid until: ${formatLocalTime(launch.expiresAt)}`);
  info(
    `Statistics (OpenTelemetry): ${launch.env['CLAUDE_CODE_ENABLE_TELEMETRY'] === '1' ? 'enabled' : 'disabled'}`,
  );
}

function formatLocalTime(date: Date): string {
  return date.toLocaleString();
}

/** Asks the server for the launch environment of the selected (or the named) subscription account. */
async function prepareLaunch(accountId?: string): Promise<ClaudeLaunch> {
  const { base, runtime, started } = await ensureServerRunning();
  if (started) console.log('Server was not running — started it in the background.');
  const response = await apiRequest<unknown>({
    base,
    path: PREPARE_PATH,
    method: 'POST',
    body: accountId === undefined ? {} : { accountId },
    runtimeToken: runtime?.runtimeToken,
  });
  return validateLaunchResponse(response);
}

/** Claude Code's user-level configuration directory: $CLAUDE_CONFIG_DIR when set, else ~/.claude. */
export function claudeConfigDir(env: NodeJS.ProcessEnv, override?: string): string {
  if (override) return path.resolve(override);
  const configured = env.CLAUDE_CONFIG_DIR?.trim();
  return configured ? configured : path.join(os.homedir(), '.claude');
}

/**
 * The server hands back environment variables only, and only the ones this command knows how to pass on:
 * anything else — an unknown name, a value that is not a string, a missing token — is refused, never
 * silently dropped. Errors name the offending key and never its value.
 */
export function validateLaunchResponse(raw: unknown): ClaudeLaunch {
  if (!isPlainObject(raw)) throw invalidLaunch();
  const { accountId, accountName, expiresAtUtc, env } = raw;
  if (typeof accountId !== 'string' || typeof accountName !== 'string' || !isPlainObject(env))
    throw invalidLaunch();
  const expiresAt = parseExpiry(expiresAtUtc);
  for (const [key, value] of Object.entries(env)) {
    if (!isAllowedEnvKey(key))
      throw invalidLaunch(`The Astra server returned an unknown Claude launch variable: ${key}`);
    if (typeof value !== 'string')
      throw invalidLaunch(`The Astra server returned a non-text Claude launch variable: ${key}`);
  }
  if (typeof env[REQUIRED_LAUNCH_ENV_KEY] !== 'string' || env[REQUIRED_LAUNCH_ENV_KEY] === '') {
    throw new AstraError(
      'The Astra server returned no Claude subscription token.',
      'Select the subscription account in the Astra web UI (Clients → Claude Code) and try again.',
    );
  }
  return { accountId, accountName, expiresAt, env: { ...env } as Record<string, string> };
}

function parseExpiry(value: unknown): Date {
  if (typeof value !== 'string') throw invalidLaunch();
  const expiresAt = new Date(value);
  if (Number.isNaN(expiresAt.getTime())) throw invalidLaunch();
  return expiresAt;
}

function isAllowedEnvKey(key: string): boolean {
  return (LAUNCH_ENV_WHITELIST as readonly string[]).includes(key);
}

function invalidLaunch(detail?: string): AstraError {
  return new AstraError(
    detail ?? 'The Astra server returned an unexpected Claude launch environment.',
    'Restart the server (`astra restart`) and try again with an up-to-date Astra.',
  );
}

/**
 * Builds the child environment: the inherited one without Claude's endpoint, credential and telemetry
 * variables, plus the variables the server supplied. Proxy and locale variables (HTTPS_PROXY, NO_PROXY,
 * PATH, HOME, CLAUDE_CONFIG_DIR, …) stay, and CLAUDE_CONFIG_DIR is never pointed anywhere new.
 */
export function buildClaudeEnv(
  baseEnv: NodeJS.ProcessEnv,
  launchEnv: Record<string, unknown>,
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
  return env;
}

/** Refuses to run when user-level or project settings would override the subscription sign-in. */
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
    `Claude settings on this machine override the subscription connection (${conflicts.length} conflict${conflicts.length === 1 ? '' : 's'}):`,
    `${detail}\n  Astra never edits your settings — remove or rename these entries yourself.\n  If a conflict comes from the Astra gateway takeover (ANTHROPIC_BASE_URL / ANTHROPIC_AUTH_TOKEN pointing at Astra), disable it first in the Astra web UI under Clients → Claude Code.`,
  );
}

/** The user-level settings Claude Code reads, plus the project settings from cwd up to the git root. */
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

/** A child that died from a signal has a null exit code and must still count as a failure. */
export function exitCodeFrom(code: number | null): number {
  return code ?? 1;
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

/** For provider switches, "0"/"false" means off — the server writes 0 to disable them. */
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
