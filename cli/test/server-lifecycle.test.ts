import { describe, expect, it } from 'vitest';
import type { FetchLike } from '../src/lib/http';
import type { RuntimeInfo } from '../src/lib/runtime';
import { waitForStartedServer } from '../src/lib/server-lifecycle';

/** A fake network where only the listed bases answer /api/health. */
function healthyAt(...bases: string[]): [FetchLike, string[]] {
  const seen: string[] = [];
  const impl: FetchLike = async (url) => {
    seen.push(url);
    const ok = bases.some((b) => url === `${b}/api/health`);
    return new Response(JSON.stringify({ status: ok ? 'ok' : 'down' }), { status: ok ? 200 : 503 });
  };
  return [impl, seen];
}

const runtime = (port: number): RuntimeInfo => ({ pid: 4242, port, host: '127.0.0.1' });

describe('waitForStartedServer', () => {
  it('follows runtime.json when the server moved to the next free port', async () => {
    const [fetchImpl, seen] = healthyAt('http://127.0.0.1:17322');
    const result = await waitForStartedServer({
      fallbackBase: 'http://127.0.0.1:17321',
      timeoutMs: 2_000,
      pollMs: 5,
      readRuntime: () => runtime(17322),
      isAlive: () => true,
      fetchImpl,
    });
    expect(result?.base).toBe('http://127.0.0.1:17322');
    expect(result?.runtime?.port).toBe(17322);
    expect(seen).not.toContain('http://127.0.0.1:17321/api/health');
  });

  it('waits until runtime.json appears', async () => {
    const [fetchImpl] = healthyAt('http://127.0.0.1:17330');
    let reads = 0;
    const result = await waitForStartedServer({
      fallbackBase: 'http://127.0.0.1:17321',
      timeoutMs: 2_000,
      pollMs: 5,
      readRuntime: () => (++reads < 3 ? null : runtime(17330)),
      isAlive: () => true,
      fetchImpl,
    });
    expect(result?.base).toBe('http://127.0.0.1:17330');
    expect(reads).toBeGreaterThanOrEqual(3);
  });

  it('ignores a runtime.json whose process is gone and falls back to the requested address', async () => {
    const [fetchImpl] = healthyAt('http://127.0.0.1:17321');
    const result = await waitForStartedServer({
      fallbackBase: 'http://127.0.0.1:17321',
      timeoutMs: 100,
      pollMs: 5,
      readRuntime: () => runtime(17399),
      isAlive: () => false,
      fetchImpl,
    });
    expect(result).toBeNull(); // a stale runtime.json never wins, and we do not guess past it

    const fallback = await waitForStartedServer({
      fallbackBase: 'http://127.0.0.1:17321',
      timeoutMs: 1_000,
      pollMs: 5,
      readRuntime: () => null,
      fetchImpl,
    });
    expect(fallback).toEqual({ base: 'http://127.0.0.1:17321', runtime: null });
  });

  it('stops as soon as the child exits', async () => {
    const [fetchImpl] = healthyAt();
    const started = Date.now();
    const result = await waitForStartedServer({
      fallbackBase: 'http://127.0.0.1:17321',
      timeoutMs: 5_000,
      pollMs: 5,
      aborted: () => Date.now() - started > 30,
      readRuntime: () => null,
      fetchImpl,
    });
    expect(result).toBeNull();
    expect(Date.now() - started).toBeLessThan(2_000);
  });
});
