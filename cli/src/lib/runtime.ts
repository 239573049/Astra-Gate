import { readTextOrNull } from './json-file.js';

export const STARTED_BY_VALUES = ['cli', 'desktop', 'autostart'] as const;
export type StartedBy = (typeof STARTED_BY_VALUES)[number];

/** Shape of runtime.json, written by the server when it starts listening. */
export interface RuntimeInfo {
  pid: number;
  port: number;
  host: string;
  version?: string;
  apiVersion?: string;
  startedBy?: StartedBy;
  startedAt?: string;
  runtimeToken?: string;
}

export function parseRuntimeJson(text: string): RuntimeInfo | null {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return null;
  }
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const obj = raw as Record<string, unknown>;
  const { pid, port, host } = obj;
  if (typeof pid !== 'number' || !Number.isInteger(pid) || pid <= 0) return null;
  if (typeof port !== 'number' || !Number.isInteger(port) || port <= 0 || port > 65535) return null;
  if (typeof host !== 'string' || host.trim() === '') return null;

  const info: RuntimeInfo = { pid, port, host };
  if (typeof obj.version === 'string') info.version = obj.version;
  if (typeof obj.apiVersion === 'string') info.apiVersion = obj.apiVersion;
  if (
    typeof obj.startedBy === 'string' &&
    (STARTED_BY_VALUES as readonly string[]).includes(obj.startedBy)
  ) {
    info.startedBy = obj.startedBy as StartedBy;
  }
  if (typeof obj.startedAt === 'string') info.startedAt = obj.startedAt;
  if (typeof obj.runtimeToken === 'string') info.runtimeToken = obj.runtimeToken;
  return info;
}

export function readRuntimeInfo(runtimeFile: string): RuntimeInfo | null {
  const text = readTextOrNull(runtimeFile);
  return text === null ? null : parseRuntimeJson(text);
}

export function isRuntimeCurrent(
  info: RuntimeInfo,
  isAlive: (pid: number) => boolean,
): boolean {
  return isAlive(info.pid);
}
