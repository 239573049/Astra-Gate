import type { RequestSummary } from '../api/types';

export type UsageCounts = Pick<RequestSummary,
  'totalInputTokens' | 'totalOutputTokens' | 'cacheReadTokens' | 'cacheWriteTokens' | 'reasoningTokens' | 'usageSource'>;

/** Prompt-cache hits as a share of ALL input tokens (including cache reads and writes). */
export function cacheHitRatio(cacheRead: number, totalInput: number): number | null {
  if (!Number.isFinite(totalInput) || totalInput <= 0 || !Number.isFinite(cacheRead) || cacheRead < 0) return null;
  return Math.min(cacheRead / totalInput, 1);
}

const count = (n: number): number => Number.isFinite(n) ? Math.max(0, n) : 0;

/** Disjoint counters for display only: cached input and reasoning output are already in the totals. */
export function splitUsage(usage: UsageCounts) {
  const input = count(usage.totalInputTokens);
  const output = count(usage.totalOutputTokens);
  const read = count(usage.cacheReadTokens);
  const write = count(usage.cacheWriteTokens);
  const reasoning = count(usage.reasoningTokens);
  return {
    reported: usage.usageSource === 'reported',
    input,
    output,
    read,
    write,
    reasoning,
    uncachedInput: Math.max(0, input - read - write),
    regularOutput: Math.max(0, output - reasoning),
    hitRatio: usage.usageSource === 'reported' ? cacheHitRatio(read, input) : null,
  };
}
