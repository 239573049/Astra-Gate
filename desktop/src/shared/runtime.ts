import { DEFAULT_PORT, normalizePort } from './port';
import type { RuntimeInfo } from './types';

export type PidAlive = (pid: number) => boolean;

const STRING_FIELDS = [
  'host',
  'version',
  'apiVersion',
  'startedBy',
  'startedAt',
  'runtimeToken',
] as const;

/**
 * Parses the contents of runtime.json. Returns null for missing/invalid
 * content; requires at least a positive integer `pid`. Unknown extra fields
 * are ignored.
 */
export function parseRuntimeJson(text: string | null | undefined): RuntimeInfo | null {
  if (text == null) return null;
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return null;
  }
  if (typeof raw !== 'object' || raw === null) return null;
  const o = raw as Record<string, unknown>;
  const pid = o.pid;
  if (typeof pid !== 'number' || !Number.isInteger(pid) || pid <= 0) return null;
  const runtime: RuntimeInfo = { pid, port: normalizePort(o.port, DEFAULT_PORT) };
  for (const key of STRING_FIELDS) {
    const v = o[key];
    if (typeof v === 'string' && v.length > 0) runtime[key] = v;
  }
  return runtime;
}

/** A runtime.json is stale when the pid it names is no longer alive. */
export function isRuntimeStale(runtime: RuntimeInfo, pidAlive: PidAlive): boolean {
  return !pidAlive(runtime.pid);
}

/**
 * True when runtime.json was written by the server spawned as `pid` — or, for
 * a wrapper (ASTRA_SERVER_BIN) that runs the server under another pid, by a
 * server that started no earlier than the spawn. An entry left by a server
 * that was already running is neither.
 */
export function isRuntimeFrom(runtime: RuntimeInfo, pid: number | undefined, spawnedAtMs: number): boolean {
  if (pid !== undefined && runtime.pid === pid) return true;
  const startedAt = runtime.startedAt ? Date.parse(runtime.startedAt) : Number.NaN;
  return startedAt >= spawnedAtMs;
}
