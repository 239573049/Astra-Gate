import fs from 'node:fs';
import type { ChildProcess } from 'node:child_process';
import { spawn } from 'node:child_process';
import { AstraError } from '../errors.js';
import { homePaths, astraHome } from './paths.js';
import {
  readConfig,
  writeConfig,
  normalizePort,
  normalizeHost,
} from './config-file.js';
import { readRuntimeInfo, type RuntimeInfo } from './runtime.js';
import { resolveServerBinary, type ServerCommand } from './binary.js';
import { baseUrlFor, checkHealth, shutdownRequest, type FetchLike } from './http.js';
import {
  delay,
  exitCodeOf,
  isPidAlive as pidAliveFn,
  killPid,
  runCapture,
  spawnDetached,
  terminatePid,
  waitForPidExit,
} from './process-utils.js';
import { applyServerInfo, readInstallJson, writeInstallJson } from './install-json.js';

export interface StartOptions {
  port?: number | string;
  host?: string;
  foreground?: boolean;
  open?: boolean;
}

export interface StartResult {
  base: string;
  host: string;
  port: number;
  alreadyRunning: boolean;
  pid?: number;
  command: ServerCommand;
  logFile: string;
}

export function currentRuntime(home: string): RuntimeInfo | null {
  return readRuntimeInfo(homePaths(home).runtimeFile);
}

function clearRuntimeFile(home: string): void {
  try {
    fs.rmSync(homePaths(home).runtimeFile, { force: true });
  } catch {
    /* ignore */
  }
}

/** The server is running when runtime.json exists, its pid is alive, and /api/health says ok. */
export async function probeRunning(
  home: string,
  fetchImpl?: FetchLike,
): Promise<{ runtime: RuntimeInfo; base: string } | null> {
  const rt = currentRuntime(home);
  if (!rt) return null;
  if (!pidAliveFn(rt.pid)) return null;
  const base = baseUrlFor(rt.host, rt.port);
  if (await checkHealth(base, fetchImpl)) return { runtime: rt, base };
  return null;
}

/** Best-effort `astra-server version` probe. */
export async function getServerVersion(command: ServerCommand): Promise<string | undefined> {
  try {
    const r = await runCapture(command.command, [...command.args, 'version'], { timeoutMs: 15_000 });
    if (r.code !== 0) return undefined;
    const parsed = JSON.parse(r.stdout.trim()) as { version?: unknown };
    return typeof parsed.version === 'string' ? parsed.version : undefined;
  } catch {
    return undefined;
  }
}

export async function waitForHealthy(
  base: string,
  timeoutMs: number,
  aborted?: () => boolean,
  fetchImpl?: FetchLike,
): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (aborted?.()) return false;
    if (await checkHealth(base, fetchImpl)) return true;
    await delay(400);
  }
  return false;
}

export interface StartedServerProbe {
  /** Address we asked for; used when the server has not written runtime.json (older servers). */
  fallbackBase: string;
  timeoutMs: number;
  aborted?: () => boolean;
  readRuntime: () => RuntimeInfo | null;
  isAlive?: (pid: number) => boolean;
  fetchImpl?: FetchLike;
  pollMs?: number;
}

/**
 * Waits for a freshly spawned server. The server moves to the next free port when the configured one is taken
 * (plan §2), so its real address comes from runtime.json, not from the port we passed.
 */
export async function waitForStartedServer(o: StartedServerProbe): Promise<{ base: string; runtime: RuntimeInfo | null } | null> {
  const isAlive = o.isAlive ?? pidAliveFn;
  const deadline = Date.now() + o.timeoutMs;
  while (Date.now() < deadline) {
    if (o.aborted?.()) return null;
    const rt = o.readRuntime();
    if (rt && isAlive(rt.pid)) {
      const base = baseUrlFor(rt.host, rt.port);
      if (await checkHealth(base, o.fetchImpl)) return { base, runtime: rt };
    } else if (!rt && (await checkHealth(o.fallbackBase, o.fetchImpl))) {
      return { base: o.fallbackBase, runtime: null };
    }
    await delay(o.pollMs ?? 400);
  }
  return null;
}

function openLogFds(home: string): { out: number; err: number; logFile: string } {
  const p = homePaths(home);
  fs.mkdirSync(p.logsDir, { recursive: true });
  const fd = fs.openSync(p.serverStdoutLog, 'a');
  return { out: fd, err: fd, logFile: p.serverStdoutLog };
}

export async function startServer(options: StartOptions = {}): Promise<StartResult> {
  const home = astraHome();
  const p = homePaths(home);
  const cfg = readConfig(home);
  const port = normalizePort(options.port ?? cfg.port);
  const host = normalizeHost(options.host ?? cfg.host);
  // Persist the effective port/host so autostart entries and the desktop app agree.
  if (
    options.port !== undefined ||
    options.host !== undefined ||
    cfg.port === undefined ||
    cfg.host === undefined
  ) {
    writeConfig(home, { port, host });
  }

  const existing = await probeRunning(home);
  if (existing) {
    return {
      base: existing.base,
      host: existing.runtime.host,
      port: existing.runtime.port,
      alreadyRunning: true,
      pid: existing.runtime.pid,
      command: resolveServerBinary({ installServerPath: readInstallJson(home)?.serverPath ?? null }),
      logFile: p.serverStdoutLog,
    };
  }
  // runtime.json exists but the process is gone — clean up before starting.
  if (currentRuntime(home)) clearRuntimeFile(home);

  const command = resolveServerBinary({ installServerPath: readInstallJson(home)?.serverPath ?? null });
  const version = await getServerVersion(command);
  writeInstallJson(
    home,
    applyServerInfo(readInstallJson(home), {
      serverPath: command.serverPath,
      serverVersion: version,
    }),
  );

  const args = [...command.args, 'serve', '--port', String(port), '--host', host, '--started-by', 'cli'];
  const base = baseUrlFor(host, port);

  if (options.foreground) {
    const child = spawn(command.command, args, { stdio: 'inherit', windowsHide: true });
    void (async () => {
      if (await waitForHealthy(base, 20_000, () => child.exitCode !== null)) {
        // Health polling side effect only; the command layer opens the browser.
      }
    })();
    const code = await exitCodeOf(child);
    if (code !== 0) {
      throw new AstraError(
        `The server exited with code ${code}.`,
        `Check the logs in ${p.logsDir}`,
      );
    }
    return { base, host, port, alreadyRunning: false, command, logFile: p.serverStdoutLog };
  }

  const fds = openLogFds(home);
  let child: ChildProcess;
  try {
    child = spawnDetached(command.command, args, ['ignore', fds.out, fds.err]);
  } catch (err) {
    throw new AstraError(
      `Failed to start the server binary: ${(err as Error).message}`,
      `Binary: ${command.command}`,
    );
  } finally {
    try {
      fs.closeSync(fds.out);
    } catch {
      /* ignore */
    }
  }

  const started = await waitForStartedServer({
    fallbackBase: base,
    timeoutMs: 20_000,
    aborted: () => child.exitCode !== null,
    readRuntime: () => currentRuntime(home),
  });
  if (!started) {
    if (child.exitCode !== null && child.exitCode !== 0) {
      throw new AstraError(
        `The server exited with code ${child.exitCode} before becoming healthy.`,
        `Check the log: ${p.serverStdoutLog}`,
      );
    }
    throw new AstraError(
      `The server did not become healthy at ${base} within 20 seconds.`,
      `Check the log: ${p.serverStdoutLog}`,
    );
  }
  const rt = started.runtime;
  return {
    base: started.base,
    host: rt?.host ?? host,
    port: rt?.port ?? port,
    alreadyRunning: false,
    pid: rt?.pid ?? child.pid,
    command,
    logFile: p.serverStdoutLog,
  };
}

export interface StopResult {
  stopped: boolean;
  pid?: number;
}

export async function stopServer(): Promise<StopResult> {
  const home = astraHome();
  const rt = currentRuntime(home);
  if (!rt) return { stopped: false };
  if (!pidAliveFn(rt.pid)) {
    clearRuntimeFile(home);
    return { stopped: false };
  }
  const base = baseUrlFor(rt.host, rt.port);
  if (rt.runtimeToken) {
    try {
      await shutdownRequest(base, rt.runtimeToken);
    } catch {
      /* fall through to signals */
    }
  }
  if (!(await waitForPidExit(rt.pid, 8_000))) {
    await terminatePid(rt.pid);
    if (!(await waitForPidExit(rt.pid, 3_000))) {
      await killPid(rt.pid);
      await waitForPidExit(rt.pid, 2_000);
    }
  }
  clearRuntimeFile(home);
  return { stopped: true, pid: rt.pid };
}

export interface ServerStatus {
  running: boolean;
  base?: string;
  runtime?: RuntimeInfo;
  staleRuntime: boolean;
  configPort: number;
  configHost: string;
}

export async function serverStatus(fetchImpl?: FetchLike): Promise<ServerStatus> {
  const home = astraHome();
  const cfg = readConfig(home);
  const configPort = normalizePort(cfg.port);
  const configHost = normalizeHost(cfg.host);
  const rt = currentRuntime(home);
  if (!rt) return { running: false, staleRuntime: false, configPort, configHost };
  if (!pidAliveFn(rt.pid)) {
    clearRuntimeFile(home);
    return { running: false, staleRuntime: true, configPort, configHost };
  }
  const base = baseUrlFor(rt.host, rt.port);
  const healthy = await checkHealth(base, fetchImpl);
  return { running: healthy, base, runtime: rt, staleRuntime: false, configPort, configHost };
}

export interface EnsuredServer {
  base: string;
  runtime?: RuntimeInfo;
  started: boolean;
}

/** For admin subcommands: use the running server or start one in the background. */
export async function ensureServerRunning(): Promise<EnsuredServer> {
  const home = astraHome();
  const existing = await probeRunning(home);
  if (existing) return { base: existing.base, runtime: existing.runtime, started: false };
  const result = await startServer({});
  return { base: result.base, runtime: currentRuntime(home) ?? undefined, started: true };
}
