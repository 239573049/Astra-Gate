import { getBridge } from '../shell/bridge';

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly details?: unknown,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /** True when the server could not be reached at all. */
  get isNetwork(): boolean {
    return this.status === 0;
  }
}

/**
 * API origin. Same-origin when the UI is served by astra-server or the Vite dev proxy;
 * the desktop app (app://astra) talks to the server origin injected by the preload.
 */
export function apiBase(): string {
  if (import.meta.env.DEV) return '';
  return getBridge()?.apiBase.replace(/\/+$/, '') ?? '';
}

type Method = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

export async function api<T>(method: Method, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (method !== 'GET') headers['X-Astra-Admin'] = '1';
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  let res: Response;
  try {
    res = await fetch(apiBase() + path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      credentials: 'include',
      signal,
    });
  } catch (err) {
    if ((err as Error)?.name === 'AbortError') throw err;
    throw new ApiError((err as Error)?.message || 'Network error', 0);
  }

  if (res.status === 204) return undefined as T;
  const text = await res.text();
  let data: unknown = undefined;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = text;
    }
  }
  if (!res.ok) {
    const obj = (data && typeof data === 'object' ? data : {}) as { error?: string; details?: unknown };
    throw new ApiError(obj.error || (typeof data === 'string' && data) || `HTTP ${res.status}`, res.status, obj.details);
  }
  return data as T;
}

export function qs(params: Record<string, string | number | boolean | null | undefined>): string {
  const s = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue;
    s.set(k, String(v));
  }
  const out = s.toString();
  return out ? `?${out}` : '';
}

export const enc = encodeURIComponent;

// ---------- server-sent events ----------

export interface SseMessage {
  /** The `event:` field; "" when the frame carried none. */
  event: string;
  /** The `data:` payload, parsed as JSON (the raw text when it is not JSON). */
  data: unknown;
}

/** Splits an SSE text stream into frames (WHATWG rules: blank line dispatches, `data:` lines joined with "\n"). */
export function createSseParser(): (chunk: string) => SseMessage[] {
  let buffer = '';
  return (chunk: string) => {
    buffer += chunk;
    const messages: SseMessage[] = [];
    for (;;) {
      const match = /\r\n\r\n|\n\n|\r\r/.exec(buffer);
      if (!match) return messages;
      const frame = buffer.slice(0, match.index);
      buffer = buffer.slice(match.index + match[0].length);
      let event = '';
      const data: string[] = [];
      for (const line of frame.split(/\r\n|\n|\r/)) {
        if (line.startsWith(':')) continue;
        const colon = line.indexOf(':');
        const field = colon < 0 ? line : line.slice(0, colon);
        let value = colon < 0 ? '' : line.slice(colon + 1);
        if (value.startsWith(' ')) value = value.slice(1);
        if (field === 'event') event = value;
        else if (field === 'data') data.push(value);
      }
      if (data.length === 0) continue;
      const text = data.join('\n');
      let parsed: unknown = text;
      try {
        parsed = JSON.parse(text);
      } catch {
        // Not JSON: pass the raw text on rather than dropping the frame.
      }
      messages.push({ event, data: parsed });
    }
  };
}

/**
 * POSTs a body and reads the answer as an event stream. Failures before the stream starts (validation, auth) arrive
 * as JSON and are reported exactly like {@link api} reports them.
 */
export async function apiStream(
  path: string,
  body: unknown,
  onEvent: (message: SseMessage) => void,
  signal?: AbortSignal,
): Promise<void> {
  return readStream('POST', path, body, onEvent, signal);
}

/**
 * GETs an event stream (e.g. the live request log) and reads it until the server closes it or `signal` aborts.
 * `onOpen` fires once the server accepted the stream, before any event.
 */
export async function apiEventStream(
  path: string,
  onEvent: (message: SseMessage) => void,
  signal?: AbortSignal,
  onOpen?: () => void,
): Promise<void> {
  return readStream('GET', path, undefined, onEvent, signal, onOpen);
}

async function readStream(
  method: 'GET' | 'POST',
  path: string,
  body: unknown,
  onEvent: (message: SseMessage) => void,
  signal?: AbortSignal,
  onOpen?: () => void,
): Promise<void> {
  let res: Response;
  try {
    res = await fetch(apiBase() + path, {
      method,
      headers:
        method === 'POST'
          ? { Accept: 'text/event-stream', 'Content-Type': 'application/json', 'X-Astra-Admin': '1' }
          : { Accept: 'text/event-stream' },
      body: method === 'POST' ? JSON.stringify(body) : undefined,
      credentials: 'include',
      signal,
    });
  } catch (err) {
    if ((err as Error)?.name === 'AbortError') throw err;
    throw new ApiError((err as Error)?.message || 'Network error', 0);
  }

  if (!res.ok || !res.body) {
    const text = await res.text().catch(() => '');
    let data: unknown = undefined;
    if (text) {
      try {
        data = JSON.parse(text);
      } catch {
        data = text;
      }
    }
    const obj = (data && typeof data === 'object' ? data : {}) as { error?: string; details?: unknown };
    throw new ApiError(obj.error || (typeof data === 'string' && data) || `HTTP ${res.status}`, res.status, obj.details);
  }

  onOpen?.();
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  const parse = createSseParser();
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    for (const message of parse(decoder.decode(value, { stream: true }))) onEvent(message);
  }
  for (const message of parse(decoder.decode())) onEvent(message);
}
