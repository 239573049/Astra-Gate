// Pure helpers behind the tray panel (web/src/components/tray) and the menu-bar title it computes.

import type { Provider, ProviderAccount, QuotaWindow, StatsSummary, TimeseriesPoint, TopModel } from '../api/types';
import type { TrayTitleMode } from '../shell/bridge';
import { formatTokens, formatUsd } from './format';
import { isWindow, quotaEnabled, usedPercentOf } from './quota';
import { bucketTotals, fillBuckets, rangeBuckets } from './stats';

/** Requests that did not succeed (upstream / gateway errors, cancellations, blocks). */
export function failedRequests(s: Pick<StatsSummary, 'requests' | 'successRate'>): number {
  return Math.max(0, Math.round(s.requests * (1 - s.successRate)));
}

/** Tile-sized cost: "$0", "$<0.01", "$0.37", "$12.40", "$1.2K". */
export function formatCostShort(usd: number): string {
  if (!Number.isFinite(usd) || usd <= 0) return '$0';
  if (usd < 0.01) return '$<0.01';
  if (usd < 1000) return `$${usd.toFixed(2)}`;
  return formatUsd(usd, { compact: true });
}

/** Today's requests per local hour, all 24 hours in order (zero where nothing happened). */
export function hourlyRequests(points: TimeseriesPoint[], now = new Date()): { bucket: string; value: number }[] {
  return fillBuckets(bucketTotals(points, 'requests'), rangeBuckets('today', now));
}

const pad = (n: number) => String(n).padStart(2, '0');

/** Yesterday's tokens from a daily timeseries ("yyyy-MM-dd" local buckets); 0 when absent. */
export function yesterdayTokens(points: TimeseriesPoint[], now = new Date()): number {
  const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
  const key = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  return points.filter((p) => p.bucket === key).reduce((sum, p) => sum + p.tokens, 0);
}

/** Relative change today vs yesterday (0.25 = +25%); null when yesterday had nothing to compare with. */
export function changeRatio(today: number, yesterday: number): number | null {
  if (yesterday <= 0) return null;
  return (today - yesterday) / yesterday;
}

/** The model with the most tokens today and its share of all of today's tokens. */
export function topModelShare(top: TopModel[], totalTokens: number): { model: string; share: number } | null {
  const best = [...top].sort((a, b) => b.tokens - a.tokens)[0];
  if (!best || best.tokens <= 0) return null;
  return { model: best.model, share: totalTokens > 0 ? Math.min(1, best.tokens / totalTokens) : 1 };
}

// ---------- subscription quota windows ----------

export type QuotaKind = 'session' | 'weekly' | 'credits' | 'window' | 'balance';

export interface QuotaRow {
  /** session / weekly / credits: subscription accounts; window / balance: API providers' usage query. */
  kind: QuotaKind;
  /** Window length in minutes (session / weekly / window), for the "5 h" / "7 d" label. */
  windowMinutes: number | null;
  /** Remaining share, 0–100; null for a balance with no known total (it has no meter and is never the "tightest"). */
  remaining: number | null;
  resetsAt: string | null;
  /** Money (or credits) left on a balance row. */
  amount?: { value: number; unit: string | null } | null;
  /** Balance row label (plan / currency name), when the upstream gives one. */
  label?: string | null;
}

export interface QuotaPlan {
  accountId: string;
  providerId: string;
  providerName: string;
  providerIcon: string | null;
  /** Account display name (or email); shown when a provider has several accounts. */
  accountName: string;
  rows: QuotaRow[];
}

const DEFAULT_WINDOW: Record<'session' | 'weekly', number> = { session: 300, weekly: 10_080 };

function remainingOf(used: number | undefined): number | null {
  if (used === undefined || used === null || !Number.isFinite(used)) return null;
  return Math.min(100, Math.max(0, 100 - used));
}

function windowRow(kind: 'session' | 'weekly', w: QuotaWindow | null | undefined): QuotaRow | null {
  const remaining = remainingOf(w?.usedPercent);
  if (remaining === null) return null;
  return { kind, windowMinutes: w?.windowMinutes ?? DEFAULT_WINDOW[kind], remaining, resetsAt: w?.resetsAtUtc ?? null };
}

/** Quota windows of the active subscription accounts that have a quota snapshot. */
export function quotaPlans(
  accounts: { provider: { id: string; name: string; icon?: string | null }; account: ProviderAccount }[],
): QuotaPlan[] {
  const plans: QuotaPlan[] = [];
  for (const { provider, account } of accounts) {
    if (account.status !== 'active' || !account.quota) continue;
    const q = account.quota;
    const rows = [windowRow('session', q.session), windowRow('weekly', q.weekly)].filter((r): r is QuotaRow => r !== null);
    const credits = remainingOf(q.credits?.usedPercent);
    if (credits !== null) rows.push({ kind: 'credits', windowMinutes: null, remaining: credits, resetsAt: null });
    if (rows.length === 0) continue;
    plans.push({
      accountId: account.id,
      providerId: provider.id,
      providerName: q.planLabel || provider.name,
      providerIcon: provider.icon ?? null,
      accountName: account.displayName || account.accountEmail || account.id,
      rows,
    });
  }
  return plans;
}

/**
 * Rows of API-key providers whose usage query is on: plan windows become meters, balances show their
 * amount (with a meter only when a total is known). A failed query keeps showing the last good numbers.
 */
export function providerQuotaPlans(providers: Provider[]): QuotaPlan[] {
  const plans: QuotaPlan[] = [];
  for (const p of providers) {
    if (!p.enabled || !quotaEnabled(p) || p.quota?.isValid === false) continue;
    const rows: QuotaRow[] = [];
    for (const plan of p.quota?.plans ?? []) {
      const used = usedPercentOf(plan);
      if (isWindow(plan)) {
        rows.push({ kind: 'window', windowMinutes: plan.windowMinutes ?? null, remaining: 100 - (used ?? 0), resetsAt: plan.resetsAtUtc ?? null });
      } else if (plan.remaining != null || used != null) {
        rows.push({
          kind: 'balance',
          windowMinutes: null,
          remaining: used == null ? null : 100 - used,
          resetsAt: plan.resetsAtUtc ?? null,
          amount: plan.remaining == null ? null : { value: plan.remaining, unit: plan.unit ?? null },
          label: plan.name ?? null,
        });
      }
    }
    if (rows.length === 0) continue;
    plans.push({ accountId: `provider:${p.id}`, providerId: p.id, providerName: p.name, providerIcon: p.icon ?? null, accountName: '', rows });
  }
  return plans;
}

/** The tightest window across every plan (the one closest to running out); rows without a share are skipped. */
export function tightestQuota(plans: QuotaPlan[]): { plan: QuotaPlan; row: QuotaRow & { remaining: number } } | null {
  let best: { plan: QuotaPlan; row: QuotaRow & { remaining: number } } | null = null;
  for (const plan of plans)
    for (const row of plan.rows)
      if (row.remaining !== null && (!best || row.remaining < best.row.remaining)) best = { plan, row: { ...row, remaining: row.remaining } };
  return best;
}

/** Remaining below this share counts as running low (orange), below the next as critical (red). */
export const QUOTA_LOW = 30;
export const QUOTA_CRITICAL = 10;

export function quotaTone(remaining: number): 'green' | 'orange' | 'red' {
  return remaining < QUOTA_CRITICAL ? 'red' : remaining < QUOTA_LOW ? 'orange' : 'green';
}

/** "5 h" / "7 d" style window length; days when the window is whole days. */
export function windowLength(minutes: number, locale: string): string {
  const unit = (value: number, u: 'hour' | 'day' | 'minute') =>
    new Intl.NumberFormat(locale === 'zh' ? 'zh-CN' : 'en-US', { style: 'unit', unit: u, unitDisplay: locale === 'zh' ? 'long' : 'narrow' }).format(value);
  if (minutes >= 1440 && minutes % 1440 === 0) return unit(minutes / 1440, 'day');
  if (minutes >= 60) return unit(Math.round(minutes / 60), 'hour');
  return unit(minutes, 'minute');
}

/** "in 1 hour" / "1小时后"; null when unknown or already past. */
export function resetsIn(iso: string | null, locale: string, now = Date.now()): string | null {
  if (!iso) return null;
  const ms = new Date(iso).getTime() - now;
  if (!Number.isFinite(ms) || ms <= 0) return null;
  const rtf = new Intl.RelativeTimeFormat(locale === 'zh' ? 'zh-CN' : 'en-US', { numeric: 'auto' });
  const hours = ms / 3_600_000;
  if (hours >= 48) return rtf.format(Math.round(hours / 24), 'day');
  if (hours >= 1) return rtf.format(Math.round(hours), 'hour');
  return rtf.format(Math.max(1, Math.round(ms / 60_000)), 'minute');
}

/** `justNow` under a minute, then "3 minutes ago" / "3分钟前"; null when never fetched. */
export function updatedAgo(at: number, locale: string, justNow: string, now = Date.now()): string | null {
  if (!at) return null;
  const seconds = Math.max(0, (now - at) / 1000);
  const rtf = new Intl.RelativeTimeFormat(locale === 'zh' ? 'zh-CN' : 'en-US', { numeric: 'auto' });
  if (seconds < 60) return justNow;
  if (seconds < 3600) return rtf.format(-Math.round(seconds / 60), 'minute');
  return rtf.format(-Math.round(seconds / 3600), 'hour');
}

// ---------- menu-bar title ----------

export interface TrayTitleInput {
  running: boolean;
  summary: StatsSummary | undefined;
  plans: QuotaPlan[];
}

export interface TrayTitleLabels {
  requests: (count: string) => string;
  tokens: (count: string) => string;
  cost: (amount: string) => string;
  quota: (plan: string, window: string, remaining: string) => string;
  failures: (count: number) => string;
  stopped: string;
  windowName: (row: QuotaRow) => string;
}

/**
 * What the menu bar shows next to the icon (`title`, macOS) and the extra tooltip line (`detail`, every
 * platform) for one "menu bar text" choice. Empty strings when there is nothing to show (no data yet, no
 * quota, or — in "alerts" mode — nothing wrong).
 */
export function trayTitle(mode: TrayTitleMode, input: TrayTitleInput, labels: TrayTitleLabels): { title: string; detail: string } {
  const none = { title: '', detail: '' };
  if (mode === 'none') return none;
  if (mode === 'alerts') {
    if (!input.running) return { title: '!', detail: labels.stopped };
    const failed = input.summary ? failedRequests(input.summary) : 0;
    if (failed > 0) return { title: `⚠ ${failed}`, detail: labels.failures(failed) };
    const tight = tightestQuota(input.plans);
    if (tight && tight.row.remaining < QUOTA_CRITICAL) {
      const pct = `${Math.round(tight.row.remaining)}%`;
      return { title: `⚠ ${pct}`, detail: labels.quota(tight.plan.providerName, labels.windowName(tight.row), pct) };
    }
    return none;
  }
  if (mode === 'quota') {
    const tight = tightestQuota(input.plans);
    if (!tight) return none;
    const pct = `${Math.round(tight.row.remaining)}%`;
    return { title: pct, detail: labels.quota(tight.plan.providerName, labels.windowName(tight.row), pct) };
  }
  const s = input.summary;
  if (!s || !input.running) return none;
  switch (mode) {
    case 'requests': {
      const v = formatTokens(s.requests, true);
      return { title: v, detail: labels.requests(v) };
    }
    case 'tokens': {
      const v = formatTokens(s.inputTokens + s.outputTokens, true);
      return { title: v, detail: labels.tokens(v) };
    }
    case 'cost': {
      const v = formatCostShort(s.costUsd);
      return { title: v, detail: labels.cost(v) };
    }
  }
}
