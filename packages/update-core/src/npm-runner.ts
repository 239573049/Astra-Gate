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
  /** Test seam — replaces the login-shell PATH lookup used when `node` is not on PATH. */
  loginShellPath?: () => Promise<string | null>;
  /** Test seam — replaces the well-known prefixes probed for node. */
  wellKnownBinDirs?: string[];
}

function npmCommand(o: NpmRunnerOptions): NpmCommand {
  return o.npm ?? locateNpm(o);
}

function dirHasExecutable(dir: string, name: string): boolean {
  try {
    fs.accessSync(path.join(dir, name), fs.constants.X_OK);
    return true;
  } catch {
    return false;
  }
}

const NODE_BIN_DIRS = ['/opt/homebrew/bin', '/usr/local/bin'];
const SHELL_PATH_MARKER = /__ASTRA_PATH_START__([\s\S]*?)__ASTRA_PATH_END__/;
let cachedLoginShellPath: Promise<string | null> | null = null;

/** Asks the user's login shell for its PATH (nvm / volta / fnm / Homebrew live there). */
function readLoginShellPath(env: NodeJS.ProcessEnv, timeoutMs = 5_000): Promise<string | null> {
  return new Promise((resolve) => {
    let out = '';
    let done = false;
    let timer: NodeJS.Timeout | null = null;
    const finish = (value: string | null): void => {
      if (done) return;
      done = true;
      if (timer) clearTimeout(timer);
      resolve(value);
    };
    let child: ReturnType<typeof spawn>;
    try {
      child = spawn(env.SHELL || '/bin/zsh', ['-ilc', 'printf "__ASTRA_PATH_START__%s__ASTRA_PATH_END__" "$PATH"'], {
        env,
        stdio: ['ignore', 'pipe', 'ignore'],
      });
    } catch {
      finish(null);
      return;
    }
    timer = setTimeout(() => {
      try {
        child.kill('SIGKILL');
      } catch {
        /* ignore */
      }
      finish(null);
    }, timeoutMs);
    child.stdout?.on('data', (d: Buffer) => {
      out += d.toString();
    });
    child.on('error', () => finish(null));
    child.on('close', () => finish(SHELL_PATH_MARKER.exec(out)?.[1] || null));
  });
}

/**
 * GUI processes (Finder / Dock launched Electron) start with launchd's minimal
 * PATH (/usr/bin:/bin:/usr/sbin:/sbin), where npm's `#!/usr/bin/env node`
 * shebang cannot find node and nvm / Homebrew installs are invisible. When node
 * is not on the child's PATH, append the well-known prefixes and then the login
 * shell's PATH. Existing entries keep precedence; a terminal launch (node
 * already on PATH) is returned untouched. Windows GUI processes inherit the
 * user PATH, so nothing is done there.
 */
export async function withNodeOnPath(
  env: NodeJS.ProcessEnv,
  npm: NpmCommand,
  o: Pick<NpmRunnerOptions, 'platform' | 'loginShellPath' | 'wellKnownBinDirs'> = {},
): Promise<NodeJS.ProcessEnv> {
  if ((o.platform ?? process.platform) === 'win32') return env;
  const current = (env.PATH ?? '').split(path.delimiter).filter(Boolean);
  const hasNode = (dirs: string[]): boolean => dirs.some((d) => dirHasExecutable(d, 'node'));
  if (hasNode(current)) return env;

  const extra = [...(path.isAbsolute(npm.command) ? [path.dirname(npm.command)] : []), ...(o.wellKnownBinDirs ?? NODE_BIN_DIRS)];
  let merged = [...current, ...extra.filter((d) => !current.includes(d))];
  if (!hasNode(merged)) {
    const lookup = o.loginShellPath ?? (() => (cachedLoginShellPath ??= readLoginShellPath(env)));
    const shellPath = await lookup();
    if (shellPath) {
      const shellDirs = shellPath.split(path.delimiter).filter(Boolean);
      merged = [...merged, ...shellDirs.filter((d) => !merged.includes(d))];
    }
  }
  return { ...env, PATH: merged.join(path.delimiter) };
}

/** `npm install --prefix <prefix> <spec>` — the primitive behind staging and desktop installs. */
export async function npmInstallIntoPrefix(prefix: string, spec: string, o: NpmRunnerOptions = {}): Promise<void> {
  const npm = npmCommand(o);
  const env = await withNodeOnPath(npmChildEnv(o.env), npm, o);
  const r = await runCapture(
    npm.command,
    [...npm.args, 'install', '--prefix', prefix, '--no-audit', '--no-fund', '--loglevel', 'error', spec],
    { env, platform: o.platform, timeoutMs: o.installTimeoutMs ?? 600_000 },
  );
  if (r.code !== 0) {
    throw new Error(`Failed to install ${spec}: ${r.stderr.trim() || `npm exited with code ${r.code}`}`);
  }
}

/** Best-effort `npm view <pkg> <field>`; throws on failure. */
export async function npmView(pkg: string, field: string, o: NpmRunnerOptions = {}): Promise<string> {
  const npm = npmCommand(o);
  const env = await withNodeOnPath(npmChildEnv(o.env), npm, o);
  const r = await runCapture(npm.command, [...npm.args, 'view', pkg, field, '--loglevel', 'error'], {
    env,
    platform: o.platform,
    timeoutMs: 60_000,
  });
  if (r.code !== 0) throw new Error(r.stderr.trim() || 'npm view failed');
  return r.stdout.trim();
}
