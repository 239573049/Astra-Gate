// Pure helpers behind the overview charts and breakdown tables.

import type { Range, TimeseriesPoint } from '../api/types';

export type Measure = 'costUsd' | 'requests' | 'tokens';

/** Sums every series per time bucket for one measure, oldest bucket first. */
export function bucketTotals(points: TimeseriesPoint[], measure: Measure) {
  const totals = new Map<string, number>();
  for (const p of points) totals.set(p.bucket, (totals.get(p.bucket) ?? 0) + p[measure]);
  return [...totals.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([bucket, value]) => ({ bucket, value }));
}

const pad = (n: number) => String(n).padStart(2, '0');
const localDay = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const RANGE_DAYS: Partial<Record<Range, number>> = { '7d': 7, '30d': 30, '90d': 90 };

/** Local midnight of a range's first day (the server's own range start); null for "all". */
export function rangeStart(range: Range, now = new Date()): Date | null {
  if (range === 'today') return new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const days = RANGE_DAYS[range];
  return days ? new Date(now.getFullYear(), now.getMonth(), now.getDate() - (days - 1)) : null;
}

/**
 * Every local bucket key of a range, oldest first, in the server's bucket format: the 24 hours of today
 * ("yyyy-MM-ddTHH:00") or one "yyyy-MM-dd" per day. Empty for "all", whose length is open-ended.
 */
export function rangeBuckets(range: Range, now = new Date()): string[] {
  if (range === 'today') {
    const day = localDay(now);
    return Array.from({ length: 24 }, (_, h) => `${day}T${pad(h)}:00`);
  }
  const start = rangeStart(range, now);
  const days = RANGE_DAYS[range] ?? 0;
  if (!start) return [];
  return Array.from({ length: days }, (_, i) => localDay(new Date(start.getFullYear(), start.getMonth(), start.getDate() + i)));
}

/** Puts bucket totals on a continuous axis: every expected bucket in order, zero where nothing happened. */
export function fillBuckets(totals: { bucket: string; value: number }[], buckets: string[]) {
  if (buckets.length === 0) return totals;
  const byBucket = new Map(totals.map((p) => [p.bucket, p.value]));
  return buckets.map((bucket) => ({ bucket, value: byBucket.get(bucket) ?? 0 }));
}

const monthDay = (b: string) => `${b.slice(5, 7)}/${b.slice(8, 10)}`;
/** Short label under a bar: "14:00" for an hour bucket, "10/07" for a day. */
export const bucketAxisLabel = (b: string) => (b.includes('T') ? b.slice(11, 16) : monthDay(b));
/** Headline label while a bar is scrubbed: "10/07 14:00" or "10/07". */
export const bucketFullLabel = (b: string) => (b.includes('T') ? `${monthDay(b)} ${b.slice(11, 16)}` : monthDay(b));

export type BreakdownRow = { key: string; requests: number; tokens: number; costUsd: number; share: number };

/** Sums rows per key; share is of cost, or of requests when nothing was billed. */
export function breakdownRows(points: Omit<BreakdownRow, 'share'>[]): BreakdownRow[] {
  const byKey = new Map<string, Omit<BreakdownRow, 'share'>>();
  for (const p of points) {
    const row = byKey.get(p.key) ?? { key: p.key, requests: 0, tokens: 0, costUsd: 0 };
    row.requests += p.requests;
    row.tokens += p.tokens;
    row.costUsd += p.costUsd;
    byKey.set(p.key, row);
  }
  const rows = [...byKey.values()];
  const totalCost = rows.reduce((sum, r) => sum + r.costUsd, 0);
  const totalRequests = rows.reduce((sum, r) => sum + r.requests, 0);
  return rows.map((r) => ({
    ...r,
    share: totalCost > 0 ? r.costUsd / totalCost : totalRequests > 0 ? r.requests / totalRequests : 0,
  }));
}
