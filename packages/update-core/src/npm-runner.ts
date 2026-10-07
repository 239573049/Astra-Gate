import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

/**
 * Locates and drives the system npm. Shared by the CLI and the desktop app;
 * from Electron the main process executable is NOT node, so prefix-based
 * discovery is skipped and PATH (plus well-known prefixes) is used instead.
 */
export interface NpmCommand {
  command: string;
  args: string[];
}

export interface LocateNpmOptions {
  env?: NodeJS.ProcessEnv;
  platform?: string;
  execPath?: string;
  /** Resolve through the user's login shell when PATH lookups fail (GUI processes). */
  allowShellResolve?: boolean;
}

export function locateNpm(o: LocateNpmOptions = {}): NpmCommand {
  const env = o.env ?? process.env;
  const platform = o.platform ?? process.platform;
  const execPath = o.execPath ?? process.execPath;
  const isNode = /^node(\.exe)?$/i.test(path.basename(execPath));

  if (isNode) {
    const found = npmBesideNode(execPath, platform);
    if (found) return found;
  }

  // Well-known absolute locations cover GUI processes whose PATH is minimal.
  const home = env.HOME ?? '';
  const candidates =
    platform === 'win32'
      ? [path.join(process.env.APPDATA ?? path.join(home, 'AppData', 'Roaming'), 'npm', 'npm.cmd')]
      : ['/opt/homebrew/bin/npm', '/usr/local/bin/npm', '/usr/bin/npm'];
  for (const candidate of candidates) {
    if (candidate && fs.existsSync(candidate)) return { command: candidate, args: [] };
  }

  return { command: platform === 'win32' ? 'npm.cmd' : 'npm', args: [] };
}

function npmBesideNode(execPath: string, platform: string): NpmCommand | null {
  const execDir = path.dirname(execPath);
  const prefix = platform === 'win32' ? execDir : path.basename(execDir) === 'bin' ? path.dirname(execDir) : execDir;
  const cli = path.join(prefix, 'lib', 'node_modules', 'npm', 'bin', 'npm-cli.js');
  if (fs.existsSync(cli)) return { command: execPath, args: [cli] };
  return null;
}

function usesShell(command: string, platform: string): boolean {
  return platform === 'win32' && command.endsWith('.cmd');
}

/**
 * Settings a parent npm / npx exports to its children as npm_config_* and that
 * break a nested `npm install --prefix`: npm 12 reads env config like CLI
 * flags, and refuses `allow-scripts` in project-scoped installs (EALLOWSCRIPTS
 * whenever the user has allowScripts configured and runs us through npx).
 * `call` / `package` only describe the npx invocation itself.
 */
const PARENT_NPM_KEYS = /^npm_config_(allow[-_]scripts|call|package)$/i;

/** The child npm's environment: inherited, minus the parent-npm keys above. */
export function npmChildEnv(env: NodeJS.ProcessEnv = process.env): NodeJS.ProcessEnv {
  return Object.fromEntries(Object.entries(env).filter(([key]) => !PARENT_NPM_KEYS.test(key)));
}

export interface RunCaptureOptions {
  timeoutMs?: number;
  env?: NodeJS.ProcessEnv;
}

interface RunResult {
  code: number | null;
  stdout: string;
  stderr: string;
}

function runCapture(command: string, args: readonly string[], o: RunCaptureOptions & { platform?: string } = {}): Promise<RunResult> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, [...args], {
      env: o.env,
      windowsHide: true,
      shell: usesShell(command, o.platform ?? process.platform),
    });
    let stdout = '';
    let stderr = '';
    let settled = false;
    const timer = o.timeoutMs
      ? setTimeout(() => {
          settled = true;
          try {
            child.kill('SIGKILL');
          } catch {
            /* ignore */
          }
          reject(new Error(`Command timed out after ${o.timeoutMs}ms: ${command} ${args.join(' ')}`));
        }, o.timeoutMs)
      : null;
    child.stdout?.on('data', (d: Buffer) => {
      stdout += d.toString();
    });
    child.stderr?.on('data', (d: Buffer) => {
      stderr += d.toString();
    });
    child.on('error', (err) => {
      if (timer) clearTimeout(timer);
      if (!settled) reject(err);
    });
    child.on('close', (code) => {
      if (timer) clearTimeout(timer);
      if (!settled) resolve({ code, stdout, stderr });
    });
  });
}

export interface NpmRunnerOptions {
  env?: NodeJS.ProcessEnv;
  platform?: string;
  execPath?: string;
  npm?: NpmCommand;
  /** Default 10 minutes — platform packages are 80-120 MB. */
  installTimeoutMs?: number;
}

function npmCommand(o: NpmRunnerOptions): NpmCommand {
  return o.npm ?? locateNpm(o);
}

/** `npm install --prefix <prefix> <spec>` — the primitive behind staging and desktop installs. */
export async function npmInstallIntoPrefix(prefix: string, spec: string, o: NpmRunnerOptions = {}): Promise<void> {
  const npm = npmCommand(o);
  const r = await runCapture(
    npm.command,
    [...npm.args, 'install', '--prefix', prefix, '--no-audit', '--no-fund', '--loglevel', 'error', spec],
    { env: npmChildEnv(o.env), platform: o.platform, timeoutMs: o.installTimeoutMs ?? 600_000 },
  );
  if (r.code !== 0) {
    throw new Error(`Failed to install ${spec}: ${r.stderr.trim() || `npm exited with code ${r.code}`}`);
  }
}

/** Best-effort `npm view <pkg> <field>`; throws on failure. */
export async function npmView(pkg: string, field: string, o: NpmRunnerOptions = {}): Promise<string> {
  const npm = npmCommand(o);
  const r = await runCapture(npm.command, [...npm.args, 'view', pkg, field, '--loglevel', 'error'], {
    env: npmChildEnv(o.env),
    platform: o.platform,
    timeoutMs: 60_000,
  });
  if (r.code !== 0) throw new Error(r.stderr.trim() || 'npm view failed');
  return r.stdout.trim();
}
