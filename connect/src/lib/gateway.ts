import { msg } from '../messages.js';

export type GatewayErrorKind = 'unauthorized' | 'unreachable' | 'outdated' | 'disabled' | 'unavailable' | 'server';

export class GatewayError extends Error {
  constructor(
    readonly kind: GatewayErrorKind,
    message: string,
    readonly status?: number,
  ) {
    super(message);
    this.name = 'GatewayError';
  }
}

/** The gateway origin the clients will use: scheme + host (+ port / path prefix), no trailing slash or /v1. */
export function normalizeGatewayUrl(input: string): string {
  const text = input.trim();
  let url: URL;
  try {
    url = new URL(/^[a-z][a-z0-9+.-]*:\/\//i.test(text) ? text : `https://${text}`);
  } catch {
    throw new Error(`不是有效的地址：${input}`);
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error('网关地址必须是 http:// 或 https://');
  let pathname = url.pathname.replace(/\/+$/, '');
  if (pathname.endsWith('/v1')) pathname = pathname.slice(0, -3);
  return `${url.origin}${pathname}`;
}

export const isInsecure = (gatewayUrl: string): boolean => {
  const url = new URL(gatewayUrl);
  return url.protocol === 'http:' && !['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
};

export interface BudgetStatus {
  metric: string;
  period: string;
  unit: string;
  limit: number;
  used: number;
  remaining: number;
  usedPercent: number;
  exhausted: boolean;
  resetsAt: string | null;
}

export interface Tally {
  requests: number;
  successRequests: number;
  costUsd: number;
  inputTokens: number;
  outputTokens: number;
}

/** `GET /v1/astra/key`: what the token's holder may see about the token. */
export interface KeyInfo {
  name: string;
  enabled: boolean;
  limits: BudgetStatus[];
  usage?: { today: Tally; week: Tally; month: Tally; total: Tally };
}

export interface ContextModel {
  id: string;
  name: string;
  contextWindow?: number;
  maxOutputTokens?: number;
  vision?: boolean;
  reasoning?: boolean;
  pdf?: boolean;
  protocols?: string[];
}

/** `GET /v1/astra/connect-context`. */
export interface ConnectContext {
  name: string;
  models: ContextModel[];
}

type Fetch = typeof fetch;

async function getJson<T>(baseUrl: string, apiKey: string, route: string, fetchImpl: Fetch): Promise<T> {
  let response: Response;
  try {
    response = await fetchImpl(`${baseUrl}${route}`, {
      headers: { Authorization: `Bearer ${apiKey}`, Accept: 'application/json' },
      signal: AbortSignal.timeout(10_000),
    });
  } catch (e) {
    const cause = (e as { cause?: { code?: string; message?: string } }).cause;
    throw new GatewayError('unreachable', msg.unreachable(baseUrl, cause?.code ?? cause?.message ?? (e as Error).message));
  }
  if (response.ok) return (await response.json()) as T;
  const text = await response.text().catch(() => '');
  let detail = '';
  try {
    detail = (JSON.parse(text) as { error?: { message?: string } }).error?.message ?? '';
  } catch {
    // not JSON
  }
  switch (response.status) {
    case 401:
      throw new GatewayError('unauthorized', msg.keyRejected, 401);
    case 403:
      throw new GatewayError('disabled', detail || msg.tokenDisabled, 403);
    case 404:
      throw new GatewayError('outdated', msg.outdated, 404);
    case 503:
      throw new GatewayError('unavailable', msg.noProviderForKey(detail || '服务端没有给它配置提供商'), 503);
    default:
      throw new GatewayError('server', detail || `网关返回 ${response.status}`, response.status);
  }
}

export const fetchKeyInfo = (baseUrl: string, apiKey: string, fetchImpl: Fetch = fetch): Promise<KeyInfo> =>
  getJson<KeyInfo>(baseUrl, apiKey, '/v1/astra/key', fetchImpl);

export const fetchConnectContext = (
  baseUrl: string,
  apiKey: string,
  fetchImpl: Fetch = fetch,
): Promise<ConnectContext> => getJson<ConnectContext>(baseUrl, apiKey, '/v1/astra/connect-context', fetchImpl);
