import { AstraError } from '../errors.js';

export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

export class ApiError extends Error {
  readonly status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

/** Build the HTTP base URL, mapping wildcard hosts to loopback. */
export function baseUrlFor(host: string, port: number): string {
  let h = host.trim();
  if (!h || h === '0.0.0.0' || h === '*' || h === '::') h = '127.0.0.1';
  if (h.startsWith('[') && h.endsWith(']')) h = h.slice(1, -1);
  return `http://${h.includes(':') ? `[${h}]` : h}:${port}`;
}

/**
 * Headers for Astra management API calls.
 * Every mutating /api/* request must carry X-Astra-Admin: 1;
 * privileged calls (shutdown) additionally carry the runtime token.
 */
export function adminHeaders(mutating: boolean, runtimeToken?: string): Record<string, string> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (mutating) headers['X-Astra-Admin'] = '1';
  if (runtimeToken) headers['X-Astra-Runtime-Token'] = runtimeToken;
  return headers;
}

export interface ApiRequestOptions {
  base: string;
  path: string;
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
  runtimeToken?: string;
  timeoutMs?: number;
  fetchImpl?: FetchLike;
}

export async function apiRequest<T>(options: ApiRequestOptions): Promise<T> {
  const { base, path, method = 'GET', body, runtimeToken, timeoutMs = 10_000 } = options;
  const doFetch = options.fetchImpl ?? globalThis.fetch;
  if (!doFetch) {
    throw new AstraError('fetch is unavailable. The Astra CLI requires Node.js 18+.');
  }
  const mutating = method !== 'GET';
  const headers = adminHeaders(mutating, runtimeToken);
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  let response: Response;
  try {
    response = await doFetch(base + path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch (err) {
    throw new AstraError(
      `Cannot reach the Astra server at ${base} (${describeFetchError(err)}).`,
      'Start it with `astra start`.',
    );
  }

  const text = await response.text();
  let payload: unknown;
  if (text.trim()) {
    try {
      payload = JSON.parse(text);
    } catch {
      payload = undefined;
    }
  }
  if (!response.ok) {
    const serverMessage = readErrorMessage(payload);
    throw new ApiError(
      serverMessage ?? `${response.status} ${response.statusText || 'request failed'}`,
      response.status,
    );
  }
  return payload as T;
}

function readErrorMessage(payload: unknown): string | undefined {
  if (payload && typeof payload === 'object' && 'error' in payload) {
    const e = (payload as { error: unknown }).error;
    if (typeof e === 'string' && e) return e;
  }
  return undefined;
}

function describeFetchError(err: unknown): string {
  if (err instanceof Error) {
    if (err.name === 'TimeoutError' || err.name === 'AbortError') return 'timed out';
    return err.message;
  }
  return String(err);
}

export async function checkHealth(
  base: string,
  fetchImpl?: FetchLike,
  timeoutMs = 2_500,
): Promise<boolean> {
  const doFetch = fetchImpl ?? globalThis.fetch;
  if (!doFetch) return false;
  try {
    const res = await doFetch(`${base}/api/health`, { signal: AbortSignal.timeout(timeoutMs) });
    if (!res.ok) return false;
    const payload = (await res.json()) as { status?: unknown };
    return payload.status === 'ok';
  } catch {
    return false;
  }
}

export async function shutdownRequest(
  base: string,
  runtimeToken: string,
  fetchImpl?: FetchLike,
): Promise<void> {
  await apiRequest<void>({
    base,
    path: '/api/admin/shutdown',
    method: 'POST',
    runtimeToken,
    timeoutMs: 5_000,
    fetchImpl,
  });
}
