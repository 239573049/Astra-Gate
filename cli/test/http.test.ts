import { describe, expect, it } from 'vitest';
import {
  adminHeaders,
  ApiError,
  apiRequest,
  baseUrlFor,
  checkHealth,
  shutdownRequest,
  type FetchLike,
} from '../src/lib/http';

interface CapturedCall {
  url: string;
  init: RequestInit;
}

function captureFetch(status = 200, body: unknown = {}): [FetchLike, CapturedCall[]] {
  const calls: CapturedCall[] = [];
  const impl: FetchLike = async (input, init) => {
    calls.push({ url: input, init: init ?? {} });
    return new Response(JSON.stringify(body), {
      status,
      headers: { 'content-type': 'application/json' },
    });
  };
  return [impl, calls];
}

function header(init: RequestInit, name: string): string | null {
  return new Headers(init.headers).get(name);
}

describe('admin headers', () => {
  it('adds X-Astra-Admin to mutating requests only', async () => {
    const [fetchImpl, calls] = captureFetch();
    await apiRequest({ base: 'http://127.0.0.1:17321', path: '/api/clients', fetchImpl });
    await apiRequest({
      base: 'http://127.0.0.1:17321',
      path: '/api/clients/codex/disable',
      method: 'POST',
      body: {},
      fetchImpl,
    });
    expect(calls).toHaveLength(2);
    expect(header(calls[0]!.init, 'x-astra-admin')).toBeNull();
    expect(header(calls[1]!.init, 'x-astra-admin')).toBe('1');
  });

  it('sends the runtime token when provided', async () => {
    const [fetchImpl, calls] = captureFetch();
    await apiRequest({
      base: 'http://127.0.0.1:17321',
      path: '/api/clients',
      method: 'POST',
      body: {},
      runtimeToken: 'tok-123',
      fetchImpl,
    });
    expect(header(calls[0]!.init, 'x-astra-runtime-token')).toBe('tok-123');
  });

  it('sends JSON bodies with the right content type', async () => {
    const [fetchImpl, calls] = captureFetch();
    await apiRequest({
      base: 'http://127.0.0.1:17321',
      path: '/api/clients/codex/enable',
      method: 'POST',
      body: { providerId: 'p1', model: 'gpt-x' },
      fetchImpl,
    });
    expect(calls[0]!.init.body).toBe(JSON.stringify({ providerId: 'p1', model: 'gpt-x' }));
    expect(header(calls[0]!.init, 'content-type')).toBe('application/json');
  });

  it('omits X-Astra-Runtime-Token when unknown', () => {
    const headers = adminHeaders(true);
    expect(headers['X-Astra-Admin']).toBe('1');
    expect(headers['X-Astra-Runtime-Token']).toBeUndefined();
  });
});

describe('error handling', () => {
  it('surfaces the server error message and status', async () => {
    const [fetchImpl] = captureFetch(409, { error: 'provider is bound to a client' });
    const err = await apiRequest({
      base: 'http://127.0.0.1:17321',
      path: '/api/providers/p1',
      method: 'DELETE',
      fetchImpl,
    }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(409);
    expect((err as ApiError).message).toBe('provider is bound to a client');
  });

  it('falls back to the HTTP status line when the body is not JSON', async () => {
    const fetchImpl: FetchLike = async () =>
      new Response('nope', { status: 500, statusText: 'Server Error' });
    const err = await apiRequest({ base: 'http://x', path: '/api/providers', fetchImpl }).catch(
      (e: unknown) => e,
    );
    expect((err as ApiError).message).toContain('500');
  });
});

describe('checkHealth', () => {
  it('accepts {"status":"ok"}', async () => {
    const [fetchImpl] = captureFetch(200, { status: 'ok' });
    expect(await checkHealth('http://127.0.0.1:17321', fetchImpl)).toBe(true);
  });

  it('rejects other payloads and network errors', async () => {
    const [fetchImpl] = captureFetch(200, { status: 'degraded' });
    expect(await checkHealth('http://127.0.0.1:17321', fetchImpl)).toBe(false);
    const failing: FetchLike = async () => {
      throw new Error('ECONNREFUSED');
    };
    expect(await checkHealth('http://127.0.0.1:17321', failing)).toBe(false);
  });
});

describe('shutdownRequest', () => {
  it('posts to /api/admin/shutdown with admin + token headers', async () => {
    const [fetchImpl, calls] = captureFetch();
    await shutdownRequest('http://127.0.0.1:17321', 'rt-token', fetchImpl);
    expect(calls[0]!.url).toBe('http://127.0.0.1:17321/api/admin/shutdown');
    expect(calls[0]!.init.method).toBe('POST');
    expect(header(calls[0]!.init, 'x-astra-admin')).toBe('1');
    expect(header(calls[0]!.init, 'x-astra-runtime-token')).toBe('rt-token');
  });
});

describe('baseUrlFor', () => {
  it('maps wildcard hosts to loopback', () => {
    expect(baseUrlFor('127.0.0.1', 17321)).toBe('http://127.0.0.1:17321');
    expect(baseUrlFor('0.0.0.0', 17321)).toBe('http://127.0.0.1:17321');
    expect(baseUrlFor('::', 80)).toBe('http://127.0.0.1:80');
    expect(baseUrlFor('::1', 8080)).toBe('http://[::1]:8080');
  });
});
