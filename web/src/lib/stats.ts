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

/** The GitHub-style grid: weeks as columns (Monday first), 7 rows per week, plus the month each column opens. */
export type ActivityGrid = {
  /** One local "yyyy-MM-dd" per cell, oldest first, padded at both ends to whole weeks. */
  days: string[];
  /** Cell indexes per column, oldest column first: weeks[column][0..6] is Monday to Sunday. */
  weeks: number[][];
  /** One entry per column that opens a month, labelled 1-12. */
  months: { index: number; label: number }[];
  /** The month every column belongs to, for labelling a column the window opens inside of. */
  monthLabels: { index: number; month: number; year: number }[];
};

/**
 * Lays the last `days` local days out as calendar weeks, oldest first, padded at both ends to whole weeks (so
 * the newest day is never alone in its own column). Day keys are local "yyyy-MM-dd"; which of them have data is
 * the caller's lookup. A column carries a month label when it holds the first of a month, and `monthLabels`
 * names the month every column belongs to — which is what a leading spacer column has to be labelled by.
 */
export function activityGrid(days = 365, now = new Date()): ActivityGrid {
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const first = new Date(today.getFullYear(), today.getMonth(), today.getDate() - (days - 1));
  // Weeks start on Monday (ISO), so a day's row is 0-6 with Sunday last; the grid backs up to the Monday of the window's first week.
  const start = new Date(first.getFullYear(), first.getMonth(), first.getDate() - ((first.getDay() + 6) % 7));
  // …and runs on to the Sunday of the current week.
  const last = new Date(today.getFullYear(), today.getMonth(), today.getDate() + (6 - ((today.getDay() + 6) % 7)));
  const keys: string[] = [];
  for (let d = start; d <= last; d = new Date(d.getFullYear(), d.getMonth(), d.getDate() + 1)) keys.push(localDay(d));
  const weeks: number[][] = [];
  const monthLabels: { index: number; month: number; year: number }[] = [];
  for (let i = 0; i < keys.length; i += 7) {
    weeks.push(Array.from({ length: 7 }, (_, row) => i + row));
    // Columns are padded to whole weeks on both sides, so a column's own first day is the day it opens with.
    monthLabels.push({ index: weeks.length - 1, month: Number(keys[i]!.slice(5, 7)), year: Number(keys[i]!.slice(0, 4)) });
  }
  // A column is named by the month that *starts* in it, which is not the month it opens with when the 1st lands mid-week.
  const months = weeks.flatMap((week, index) => {
    const opens = week.find((at) => keys[at]!.slice(8) === '01');
    return opens === undefined ? [] : [{ index, label: Number(keys[opens]!.slice(5, 7)) }];
  });
  return { days: keys, weeks, months, monthLabels };
}

/** Intensity of one heatmap cell, 0 (no activity) to 4 (the busiest day), from a day's count against the window's busiest. */
export function activityLevel(count: number, peak: number): 0 | 1 | 2 | 3 | 4 {
  if (count <= 0) return 0;
  if (peak <= 0) return 1;
  return Math.min(4, Math.max(1, Math.ceil((count / peak) * 4))) as 1 | 2 | 3 | 4;
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
