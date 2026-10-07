import { adminHeaders } from './shared/requestHeaders';
import type { GatewayClient, GatewayProvider } from './shared/types';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly path: string,
  ) {
    super(`${path} failed with HTTP ${status}`);
    this.name = 'ApiError';
  }
}

interface RequestOptions {
  timeoutMs?: number;
  method?: 'GET' | 'POST' | 'PUT';
  body?: unknown;
  headers?: Record<string, string>;
}

async function requestJson<T>(base: string, pathname: string, opts: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { ...(opts.headers ?? {}) };
  if (opts.body !== undefined) headers['content-type'] = 'application/json';
  const res = await fetch(`${base}${pathname}`, {
    method: opts.method ?? 'GET',
    headers,
    body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
    signal: AbortSignal.timeout(opts.timeoutMs ?? 3000),
  });
  if (!res.ok) throw new ApiError(res.status, pathname);
  return (await res.json()) as T;
}

/** GET /api/health — true only when the server answers {status:"ok"}. */
export async function checkHealth(base: string, timeoutMs = 2000): Promise<boolean> {
  try {
    const body = await requestJson<{ status?: unknown }>(base, '/api/health', { timeoutMs });
    return body != null && body.status === 'ok';
  } catch {
    return false;
  }
}

export interface ServerVersionInfo {
  version?: string;
  apiVersion?: string;
}

/** GET /api/version — tolerant: missing fields become undefined. */
export async function fetchVersion(base: string, timeoutMs = 2000): Promise<ServerVersionInfo> {
  const raw = await requestJson<unknown>(base, '/api/version', { timeoutMs });
  const o = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>;
  const out: ServerVersionInfo = {};
  if (typeof o.version === 'string') out.version = o.version;
  if (typeof o.apiVersion === 'string') out.apiVersion = o.apiVersion;
  return out;
}

function asArray(raw: unknown): Record<string, unknown>[] {
  if (!Array.isArray(raw)) return [];
  return raw.filter((item): item is Record<string, unknown> => typeof item === 'object' && item !== null);
}

/** GET /api/clients */
export async function fetchClients(base: string, timeoutMs = 3000): Promise<GatewayClient[]> {
  const raw = await requestJson<unknown>(base, '/api/clients', { timeoutMs });
  return asArray(raw).map((c) => ({
    kind: typeof c.kind === 'string' ? c.kind : '',
    name: typeof c.name === 'string' && c.name !== '' ? c.name : String(c.kind ?? ''),
    enabled: c.enabled === true,
    providerId: typeof c.providerId === 'string' && c.providerId !== '' ? c.providerId : null,
  }));
}

/** GET /api/providers */
export async function fetchProviders(base: string, timeoutMs = 3000): Promise<GatewayProvider[]> {
  const raw = await requestJson<unknown>(base, '/api/providers', { timeoutMs });
  return asArray(raw).map((p) => ({
    id: typeof p.id === 'string' ? p.id : '',
    name: typeof p.name === 'string' && p.name !== '' ? p.name : String(p.id ?? ''),
    enabled: p.enabled === true,
  }));
}

/** PUT /api/clients/{kind}/binding — tray quick-switch of the bound provider. */
export async function putBinding(
  base: string,
  kind: string,
  providerId: string,
  runtimeToken: string | null,
  timeoutMs = 4000,
): Promise<void> {
  await requestJson(base, `/api/clients/${encodeURIComponent(kind)}/binding`, {
    method: 'PUT',
    body: { providerId },
    headers: adminHeaders(runtimeToken),
    timeoutMs,
  });
}

/**
 * POST /api/admin/shutdown with the runtime token. Returns false on any
 * failure (network error, wrong token); callers verify shutdown via health.
 */
export async function requestShutdown(
  base: string,
  runtimeToken: string | null,
  timeoutMs = 3000,
): Promise<boolean> {
  try {
    const res = await fetch(`${base}/api/admin/shutdown`, {
      method: 'POST',
      headers: adminHeaders(runtimeToken),
      signal: AbortSignal.timeout(timeoutMs),
    });
    return res.ok;
  } catch {
    return false;
  }
}
