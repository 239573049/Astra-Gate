// The request log's context-window view: how much of the model's window a request's prompt filled (the idea is magpie's
// per-request context usage, MIT). The server sends prompt tokens, the window and an estimate of what the prompt was made of.

import type { ContextUsage } from '../api/types';

export type FillTone = 'ok' | 'warn' | 'danger';

/** Below 70% is comfortable, up to 90% getting full, above that the window is nearly gone. */
export function fillTone(fill: number): FillTone {
  if (fill >= 0.9) return 'danger';
  if (fill >= 0.7) return 'warn';
  return 'ok';
}

/** 0.0123 → "1.2%", 0.5 → "50%", 0.0004 → "<0.1%". */
export function formatFill(fill: number | null | undefined): string {
  if (fill == null || !Number.isFinite(fill)) return '—';
  const pct = fill * 100;
  if (pct > 0 && pct < 0.1) return '<0.1%';
  return `${pct.toFixed(pct >= 10 ? 0 : 1)}%`;
}

export interface WindowSegment { key: 'cacheRead' | 'fresh' | 'output'; tokens: number }

/** What fills the window bar, left to right: cached prompt, fresh prompt, output. Empty without a window. */
export function windowSegments(c: ContextUsage): WindowSegment[] {
  if (!c.window || c.window <= 0) return [];
  const cached = Math.min(Math.max(0, c.cacheReadTokens), c.promptTokens);
  return [
    { key: 'cacheRead', tokens: cached },
    { key: 'fresh', tokens: Math.max(0, c.promptTokens - cached) },
    { key: 'output', tokens: Math.max(0, c.outputTokens) },
  ];
}

/** Width of `tokens` against the window as a CSS percentage, capped so overflowing segments stay inside the bar. */
export function widthPercent(tokens: number, window: number): number {
  if (window <= 0) return 0;
  return Math.min(100, (tokens / window) * 100);
}
