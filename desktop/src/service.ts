import { spawn, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as fsp from 'node:fs/promises';

import { checkHealth, fetchVersion, requestShutdown, type ServerVersionInfo } from './api';
import { parseConfigJson, type ServerConfig } from './shared/config';
import { parseInstallJson, serverPathFromInstall } from './shared/install';
import { type AstraPaths } from './shared/paths';
import { isPidAlive } from './shared/pid';
import { apiBase } from './shared/port';
import { resolveServerBinary, shouldReplaceRunningServer, type ServerBinary } from './shared/resolveServerBinary';
import { isRuntimeStale, parseRuntimeJson } from './shared/runtime';
import { STARTED_BY, type InstallInfo, type RuntimeInfo } from './shared/types';
import { EXPECTED_API_MAJOR, isApiVersionCompatible } from './shared/version';

const START_TIMEOUT_MS = 20_000;
const START_POLL_INTERVAL_MS = 300;
const SHUTDOWN_HTTP_GRACE_MS = 5_000;
const KILL_GRACE_MS = 3_000;

export class StartupError extends Error {
  constructor(
    message: string,
    readonly logPath: string | null,
  ) {
    super(message);
    this.name = 'StartupError';
  }
}

export interface ProbeResult {
  running: boolean;
  port: number | null;
  runtime: RuntimeInfo | null;
  runtimeStale: boolean;
  apiVersion: string | null;
  /** Server version from /api/version (same call as apiVersion); null when it could not be read. */
  version: string | null;
}

export interface RunningService {
  port: number;
  apiBase: string;
  weStarted: boolean;
  apiVersion: string | null;
  apiVersionMismatch: boolean;
}

export type StopResult = 'stopped' | 'notRunning' | 'failed';

export interface ServiceManagerOptions {
  paths: AstraPaths;
  repoRoot: string;
  /** Server shipped inside the packaged app (standalone installers); null in dev. */
  bundledServer?: { path: string; version: string } | null;
  platform?: NodeJS.Platform;
  env?: NodeJS.ProcessEnv;
  expectedApiMajor?: number;
  log?: (line: string) => void;
}

/**
 * Owns the Astra server lifecycle for this desktop session:
 * probe -> take over a version-matching server or replace a mismatching one
 * (and spawn with --started-by desktop when none runs) -> wait for health,
 * and shutdown-on-exit only when we spawned it ourselves.
 */
export class ServiceManager {
  /** True only when THIS app run spawned the currently-known server. */
  weStarted = false;

  private child: ChildProcess | null = null;
  private readonly paths: AstraPaths;
  private readonly repoRoot: string;
  private readonly bundledServer: { path: string; version: string } | null;
  private readonly platform: NodeJS.Platform;
  private readonly env: NodeJS.ProcessEnv;
  private readonly expectedApiMajor: number;
  private readonly log: (line: string) => void;

  constructor(opts: ServiceManagerOptions) {
    this.paths = opts.paths;
    this.repoRoot = opts.repoRoot;
    this.bundledServer = opts.bundledServer ?? null;
    this.platform = opts.platform ?? process.platform;
    this.env = opts.env ?? process.env;
    this.expectedApiMajor = opts.expectedApiMajor ?? EXPECTED_API_MAJOR;
    this.log = opts.log ?? ((line) => console.log(`[astra] ${line}`));
  }

  get dataPaths(): AstraPaths {
    return this.paths;
  }

  async readRuntime(): Promise<RuntimeInfo | null> {
    try {
      return parseRuntimeJson(await fsp.readFile(this.paths.runtimeFile, 'utf8'));
    } catch {
      return null;
    }
  }

  async readConfig(): Promise<ServerConfig> {
    try {
      return parseConfigJson(await fsp.readFile(this.paths.configFile, 'utf8'));
    } catch {
      return parseConfigJson(null);
    }
  }

  computeMismatch(apiVersion: string | null): boolean {
    if (!apiVersion) return false;
    return !isApiVersionCompatible(apiVersion, this.expectedApiMajor);
  }

  /** Health-check whatever we believe the server is (runtime port or config port). */
  async probe(healthTimeoutMs = 1500): Promise<ProbeResult> {
    const runtime = await this.readRuntime();
    const stale = runtime !== null && isRuntimeStale(runtime, isPidAlive);
    const config = await this.readConfig();
    const port = runtime !== null && !stale ? runtime.port : config.port;
    const base = apiBase(port);
    const running = await checkHealth(base, healthTimeoutMs);
    const info = running ? await this.fetchServerInfo(base) : null;
    return {
      running,
      port,
      runtime,
      runtimeStale: stale,
      apiVersion: info?.apiVersion ?? null,
      version: info?.version ?? null,
    };
  }

  /**
   * Ensures a healthy server running the binary this app resolved: probes
   * first and adopts a running server whose version matches the target;
   * a mismatching one (an older `astra serve`, a previous install left
   * running) is stopped and replaced. Otherwise spawns the resolved binary
   * and waits up to 20s for health. Throws StartupError with the log path
   * on failure.
   */
  async ensureRunning(): Promise<RunningService> {
    const probed = await this.probe();
    if (probed.running && probed.port !== null) {
      const target = await this.resolveBinary();
      // target === null (nothing installed) keeps the running server: it beats failing.
      if (target === null || !shouldReplaceRunningServer(probed.version, target)) {
        return {
          port: probed.port,
          apiBase: apiBase(probed.port),
          weStarted: this.weStarted,
          apiVersion: probed.apiVersion,
          apiVersionMismatch: this.computeMismatch(probed.apiVersion),
        };
      }
      this.log(
        `replacing running server ${probed.version} with the resolved ${target.version} (${target.source} build)`,
      );
      await this.stopService();
    }
    return this.startServer();
  }

  /**
   * Stops the service: POST /api/admin/shutdown with the runtime token, wait
   * up to 5s, then fall back to SIGTERM (taskkill on Windows).
   */
  async stopService(): Promise<StopResult> {
    const probed = await this.probe(1200);
    if (!probed.running || probed.port === null) return 'notRunning';
    const base = apiBase(probed.port);
    const token = probed.runtime?.runtimeToken ?? null;
    if (token) await requestShutdown(base, token, 3000);

    const httpDeadline = Date.now() + SHUTDOWN_HTTP_GRACE_MS;
    while (Date.now() < httpDeadline) {
      if (!(await checkHealth(base, 800))) return 'stopped';
      await delay(250);
    }

    const pid = probed.runtime?.pid ?? this.child?.pid ?? null;
    if (pid === null || !Number.isInteger(pid) || pid <= 0) return 'failed';
    this.killTree(pid);

    const killDeadline = Date.now() + KILL_GRACE_MS;
    while (Date.now() < killDeadline) {
      if (!(await checkHealth(base, 600))) return 'stopped';
      await delay(250);
    }
    return 'failed';
  }

  /** Quit-time hook: only stop the server when this session spawned it. */
  async stopIfOurs(): Promise<void> {
    if (!this.weStarted) {
      this.log('not stopping server on exit: it was not started by the desktop app');
      return;
    }
    const result = await this.stopService();
    this.log(`stop service on exit: ${result}`);
  }

  private async startServer(): Promise<RunningService> {
    const config = await this.readConfig();
    const port = config.port;
    const todayLog = this.paths.logFile(new Date());

    const binary = await this.resolveBinary();
    if (!binary) {
      throw new StartupError(
        'Astra server binary not found. Looked at install.json serverPath, ' +
          'ASTRA_SERVER_BIN, the server bundled with this app, and the dev build path ' +
          '(<repo>/src/Astra.Server/bin/Debug/net10.0/astra-server). ' +
          'Reinstall the desktop app from the download page, run "astra update", or build the server first.',
        todayLog,
      );
    }

    await fsp.mkdir(this.paths.logsDir, { recursive: true });
    const out = fs.openSync(todayLog, 'a');
    let child: ChildProcess;
    try {
      child = spawn(binary.path, ['serve', '--started-by', STARTED_BY, '--port', String(port)], {
        detached: true,
        stdio: ['ignore', out, out],
        env: this.env,
        windowsHide: true,
      });
    } finally {
      fs.closeSync(out);
    }
    this.child = child;
    child.unref();
    this.log(`starting server: ${binary.path} serve --started-by ${STARTED_BY} --port ${port} (source: ${binary.source})`);

    // Detached, but tracked: watch for a crash or a failed launch (ENOENT & co
    // arrive asynchronously via 'error').
    let earlyExit: string | null = null;
    child.on('error', (err) => {
      earlyExit = `failed to launch ${binary.path}: ${err.message}`;
    });
    child.on('exit', (code, signal) => {
      earlyExit = `server process exited before becoming healthy (code=${code ?? 'null'} signal=${signal ?? 'null'})`;
    });

    // The server moves to the next free port when the configured one is taken, so follow runtime.json.
    const deadline = Date.now() + START_TIMEOUT_MS;
    while (Date.now() < deadline) {
      if (earlyExit !== null) throw new StartupError(earlyExit, todayLog);
      await delay(START_POLL_INTERVAL_MS);
      if (earlyExit !== null) throw new StartupError(earlyExit, todayLog);
      const runtime = await this.readRuntime();
      const actualPort = runtime !== null && !isRuntimeStale(runtime, isPidAlive) ? runtime.port : port;
      const base = apiBase(actualPort);
      if (await checkHealth(base, 1000)) {
        this.weStarted = true;
        const info = await this.fetchServerInfo(base);
        const apiVersion = info?.apiVersion ?? null;
        this.log(
          `server is healthy on port ${actualPort}${actualPort !== port ? ` (port ${port} was busy)` : ''} (pid ${child.pid ?? '?'})`,
        );
        return {
          port: actualPort,
          apiBase: base,
          weStarted: true,
          apiVersion,
          apiVersionMismatch: this.computeMismatch(apiVersion),
        };
      }
    }
    throw new StartupError(
      `Server did not become healthy within ${START_TIMEOUT_MS / 1000}s on port ${port}.`,
      todayLog,
    );
  }

  /** The binary this app would serve with (install.json > ASTRA_SERVER_BIN > bundled > dev). */
  private async resolveBinary(): Promise<ServerBinary | null> {
    const install = await this.readInstall();
    return resolveServerBinary({
      installServerPath: serverPathFromInstall(install),
      installServerVersion: install?.serverVersion ?? null,
      envServerBin: this.env.ASTRA_SERVER_BIN ?? null,
      bundled: this.bundledServer,
      repoRoot: this.repoRoot,
      platform: this.platform,
      exists: (p) => {
        try {
          return fs.statSync(p).isFile();
        } catch {
          return false;
        }
      },
    });
  }

  private async fetchServerInfo(base: string): Promise<ServerVersionInfo | null> {
    try {
      return await fetchVersion(base, 2000);
    } catch {
      return null;
    }
  }

  private async readInstall(): Promise<InstallInfo | null> {
    try {
      return parseInstallJson(await fsp.readFile(this.paths.installFile, 'utf8'));
    } catch {
      return null;
    }
  }

  private killTree(pid: number): void {
    this.log(`sending kill to server pid ${pid}`);
    if (this.platform === 'win32') {
      spawn('taskkill', ['/PID', String(pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
      return;
    }
    try {
      process.kill(pid, 'SIGTERM');
    } catch {
      // already gone
    }
  }
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
