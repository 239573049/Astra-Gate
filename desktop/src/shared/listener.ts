/**
 * Finds the Astra server listening on a port when nothing on disk names it — a
 * server whose runtime.json entry was overwritten by a later start (which then
 * moved to the next free port) keeps the configured port, answers health
 * checks, and cannot be shut down by token or by a recorded pid.
 */

/** Runs a command and returns its stdout, or null when it fails or cannot be run. */
export type RunCommand = (command: string, args: string[]) => Promise<string | null>;

/** `lsof -t` prints one pid per line. */
export function parseLsofPids(stdout: string): number[] {
  return stdout
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => /^\d+$/.test(line))
    .map(Number)
    .filter((pid) => pid > 0);
}

/**
 * `netstat -ano -p TCP` rows: proto, local, foreign, state, pid. The state
 * column is localized on some Windows languages, so a listener is recognised
 * by its unconnected foreign address (`0.0.0.0:0`, `[::]:0`) instead.
 */
export function parseNetstatListenerPids(stdout: string, port: number): number[] {
  const pids: number[] = [];
  for (const line of stdout.split(/\r?\n/)) {
    const cols = line.trim().split(/\s+/);
    if (cols.length < 5 || cols[0]!.toUpperCase() !== 'TCP') continue;
    const local = cols[1]!;
    const foreign = cols[2]!;
    if (!local.endsWith(`:${port}`) || !foreign.endsWith(':0')) continue;
    const pid = Number(cols[cols.length - 1]);
    if (Number.isInteger(pid) && pid > 0 && !pids.includes(pid)) pids.push(pid);
  }
  return pids;
}

/** `tasklist /FO CSV /NH` row: the image name is the first quoted field. */
export function parseTasklistImage(stdout: string): string | null {
  const match = /^"([^"]+)"/.exec(stdout.trim());
  return match ? match[1]! : null;
}

/**
 * True for the server executables the desktop and the updater launch:
 * `astra-server` (bundled / dev build) and `astra-server-<version>` (managed),
 * with `.exe` on Windows. A path is reduced to its file name first.
 */
export function isAstraServerExecutable(nameOrPath: string): boolean {
  const name = nameOrPath.trim().split(/[\\/]/).pop() ?? '';
  return /^astra-server(-[^\\/]*)?(\.exe)?$/i.test(name);
}

/**
 * The pid of the Astra server listening on `port`, or null when no listener is
 * found, the lookup tool is missing, or the listener is not an astra-server
 * executable (a proxy or `dotnet run` is never a candidate for a kill).
 */
export async function findServerListenerPid(
  port: number,
  platform: NodeJS.Platform,
  run: RunCommand,
): Promise<number | null> {
  if (platform === 'win32') {
    const netstat = await run('netstat', ['-ano', '-p', 'TCP']);
    const pids = netstat === null ? [] : parseNetstatListenerPids(netstat, port);
    if (pids.length !== 1) return null;
    const pid = pids[0]!;
    const row = await run('tasklist', ['/FI', `PID eq ${pid}`, '/FO', 'CSV', '/NH']);
    const image = row === null ? null : parseTasklistImage(row);
    return image !== null && isAstraServerExecutable(image) ? pid : null;
  }
  const lsof = platform === 'darwin' ? '/usr/sbin/lsof' : 'lsof';
  const out = await run(lsof, ['-nP', '-t', `-iTCP:${port}`, '-sTCP:LISTEN']);
  const pids = out === null ? [] : parseLsofPids(out);
  if (pids.length !== 1) return null;
  const pid = pids[0]!;
  const comm = await run('ps', ['-o', 'comm=', '-p', String(pid)]);
  return comm !== null && isAstraServerExecutable(comm) ? pid : null;
}
