import { afterEach, describe, expect, it, vi } from 'vitest';

import { api, ApiError, qs } from '../api/client';
import { compactSchedule, emptySchedule, parsePricing } from '../components/PricingEditor';
import { activityGrid, activityLevel, breakdownRows, bucketTotals, fillBuckets, rangeBuckets, rangeStart } from '../lib/stats';

describe('parsePricing / compactSchedule', () => {
  it('validates the document shape', () => {
    expect(parsePricing('')).toEqual([null, null]);
    expect(parsePricing('{')[1]).toBeTruthy();
    expect(parsePricing('[]')[1]).toBe('not an object');
    expect(parsePricing('{"currency":"USD"}')[1]).toBe('"base" is required');
    const [p, err] = parsePricing('{"base":{"input":3}}');
    expect(err).toBeNull();
    expect(p).toEqual({ base: { input: 3 }, currency: 'USD', unit: 'per_1m_tokens' });
  });

  it('drops empty rule lists', () => {
    const p = compactSchedule({ ...emptySchedule(), context_tiers: [], time_windows: [], service_tiers: {}, per_request: null, notes: '' });
    expect(p).toEqual({ currency: 'USD', unit: 'per_1m_tokens', base: { input: null, output: null } });
  });
});

describe('bucketTotals', () => {
  it('sums every series per bucket for the chosen measure, oldest first', () => {
    const pts = [
      { bucket: '10-02', key: 'a', costUsd: 5, requests: 1, tokens: 10 },
      { bucket: '10-01', key: 'a', costUsd: 1, requests: 2, tokens: 20 },
      { bucket: '10-01', key: 'b', costUsd: 2, requests: 3, tokens: 30 },
    ];
    expect(bucketTotals(pts, 'costUsd')).toEqual([
      { bucket: '10-01', value: 3 },
      { bucket: '10-02', value: 5 },
    ]);
    expect(bucketTotals(pts, 'requests').map((p) => p.value)).toEqual([5, 1]);
    expect(bucketTotals([], 'tokens')).toEqual([]);
  });
});

describe('overview time axis', () => {
  const now = new Date(2026, 9, 7, 14, 25); // local Oct 7, 14:25

  it('lists every local bucket of a range, oldest first', () => {
    const today = rangeBuckets('today', now);
    expect(today).toHaveLength(24);
    expect(today[0]).toBe('2026-10-07T00:00');
    expect(today[23]).toBe('2026-10-07T23:00');
    expect(rangeBuckets('7d', now)).toEqual(['2026-10-01', '2026-10-02', '2026-10-03', '2026-10-04', '2026-10-05', '2026-10-06', '2026-10-07']);
    expect(rangeBuckets('90d', now)).toHaveLength(90);
    expect(rangeBuckets('all', now)).toEqual([]);
  });

  it('starts a range at local midnight of its first day', () => {
    expect(rangeStart('today', now)).toEqual(new Date(2026, 9, 7));
    expect(rangeStart('30d', now)).toEqual(new Date(2026, 8, 8));
    expect(rangeStart('all', now)).toBeNull();
  });

  it('fills missing buckets with zero and keeps unknown ranges as-is', () => {
    const totals = [{ bucket: '2026-10-03', value: 4 }];
    expect(fillBuckets(totals, ['2026-10-02', '2026-10-03', '2026-10-04']).map((p) => p.value)).toEqual([0, 4, 0]);
    expect(fillBuckets(totals, [])).toBe(totals);
  });
});

describe('activity grid', () => {
  // Local Oct 8, 2026 is a Thursday.
  const now = new Date(2026, 9, 8, 9, 30);

  it('lays a week out Monday first and pads the ends of the window', () => {
    const grid = activityGrid(7, now);
    expect(grid.days[0]).toBe('2026-09-28'); // padded back to the Monday of the window's first week
    expect(grid.days).toHaveLength(14); // and forward to the Sunday of the current week
    expect(grid.weeks).toHaveLength(2);
    expect(grid.weeks[0]).toHaveLength(7);
    expect(grid.days[grid.weeks[1][6]!]).toBe('2026-10-11');
    expect(grid.days[grid.weeks[1][3]!]).toBe('2026-10-08'); // today (Thursday) in the last column
  });

  it('marks the column that opens a month', () => {
    const grid = activityGrid(365, now);
    expect(grid.months.map((m) => m.label)).toEqual([11, 12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    // Each label sits on the column that holds the first of that month (the window's first month can open mid-column).
    for (const month of grid.months) {
      const label = String(month.label).padStart(2, '0');
      expect(grid.weeks[month.index]!.some((at) => grid.days[at]!.slice(5) === `${label}-01`)).toBe(true);
    }
    expect(grid.months.some((m) => m.index < 2)).toBe(false);
    expect(new Set(grid.days).size).toBe(grid.days.length); // one cell per day, no repeats
  });

  it('scales intensity against the busiest day', () => {
    expect(activityLevel(0, 10)).toBe(0);
    expect(activityLevel(1, 10)).toBe(1);
    expect(activityLevel(5, 10)).toBe(2);
    expect(activityLevel(8, 10)).toBe(4);
    expect(activityLevel(10, 10)).toBe(4);
    expect(activityLevel(3, 0)).toBe(1); // a day counts even when the peak was not reported
  });
});

describe('breakdownRows', () => {
  it('merges keys and shares cost, falling back to requests when nothing was billed', () => {
    const rows = breakdownRows([
      { key: 'a', requests: 1, tokens: 10, costUsd: 1 },
      { key: 'a', requests: 1, tokens: 10, costUsd: 2 },
      { key: 'b', requests: 2, tokens: 5, costUsd: 1 },
    ]);
    expect(rows).toEqual([
      { key: 'a', requests: 2, tokens: 20, costUsd: 3, share: 0.75 },
      { key: 'b', requests: 2, tokens: 5, costUsd: 1, share: 0.25 },
    ]);
    expect(breakdownRows([{ key: 'x', requests: 3, tokens: 0, costUsd: 0 }, { key: 'y', requests: 1, tokens: 0, costUsd: 0 }]).map((r) => r.share)).toEqual([0.75, 0.25]);
  });
});

describe('api client', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('sends the admin header on mutations only and parses JSON', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ ok: true }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    await api('GET', '/api/x');
    await api('POST', '/api/x', { a: 1 });
    const [, getInit] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    const [, postInit] = fetchMock.mock.calls[1] as unknown as [string, RequestInit];
    expect((getInit.headers as Record<string, string>)['X-Astra-Admin']).toBeUndefined();
    expect((postInit.headers as Record<string, string>)['X-Astra-Admin']).toBe('1');
    expect(postInit.credentials).toBe('include');
    expect(postInit.body).toBe('{"a":1}');
  });

  it('turns error bodies into ApiError and 204 into undefined', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ error: 'nope', details: { boundClients: ['codex'] } }), { status: 409 })));
    await expect(api('DELETE', '/api/providers/x')).rejects.toMatchObject({ message: 'nope', status: 409, details: { boundClients: ['codex'] } });
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 204 })));
    await expect(api('DELETE', '/api/x')).resolves.toBeUndefined();
  });

  it('reports network failures with status 0', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('Failed to fetch'))));
    const err = await api('GET', '/api/health').catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).isNetwork).toBe(true);
  });

  it('builds query strings without empty values', () => {
    expect(qs({ a: 1, b: '', c: null, d: undefined, e: 'x y' })).toBe('?a=1&e=x+y');
    expect(qs({})).toBe('');
  });
});
