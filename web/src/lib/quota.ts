import type { Provider, ProviderQuotaPlan, ProviderQuotaSnapshot, QuotaErrorCode } from '../api/types';
import type { MessageKey, TFunction } from '../i18n';
import { resetsIn, windowLength } from './tray';

// Balance / quota snapshots of API-key providers (the "usage query"), shared by the provider card,
// the detail page and the tray. Snapshots never contain made-up numbers: a missing field stays missing.

export const QUOTA_ERROR_KEY: Record<QuotaErrorCode, MessageKey> = {
  config: 'providers.quota.error.config',
  no_key: 'providers.quota.error.no_key',
  unauthorized: 'providers.quota.error.unauthorized',
  no_endpoint: 'providers.quota.error.no_endpoint',
  rate_limited: 'providers.quota.error.rate_limited',
  upstream: 'providers.quota.error.upstream',
  timeout: 'providers.quota.error.timeout',
  network: 'providers.quota.error.network',
  not_json: 'providers.quota.error.not_json',
  too_large: 'providers.quota.error.too_large',
  empty: 'providers.quota.error.empty',
};

const CURRENCY = /^[A-Z]{3}$/;

/** "¥110.00" / "$0.0042" for ISO currencies, "1,200 credits" otherwise. */
export function formatAmount(value: number, unit: string | null | undefined, locale: string): string {
  const tag = locale === 'zh' ? 'zh-CN' : 'en-US';
  if (unit && CURRENCY.test(unit)) {
    try {
      const small = value !== 0 && Math.abs(value) < 1;
      return new Intl.NumberFormat(tag, { style: 'currency', currency: unit, minimumFractionDigits: 2, maximumFractionDigits: small ? 4 : 2 }).format(value);
    } catch {
      // Not a currency Intl knows: fall through to the plain form.
    }
  }
  const n = new Intl.NumberFormat(tag, { maximumFractionDigits: 2 }).format(value);
  return unit ? `${n} ${unit}` : n;
}

/** A usage window of a coding plan (as opposed to money left). */
export const isWindow = (p: ProviderQuotaPlan) => p.windowMinutes != null && p.usedPercent != null;

/** Share used, 0–100: reported directly, or derived from used / total. Null when unknown. */
export function usedPercentOf(p: ProviderQuotaPlan): number | null {
  if (p.usedPercent != null && Number.isFinite(p.usedPercent)) return Math.min(100, Math.max(0, p.usedPercent));
  if (p.used != null && p.total != null && p.total > 0) return Math.min(100, Math.max(0, (p.used / p.total) * 100));
  return null;
}

/** The failure to show: only when it is newer than the last good snapshot (otherwise it was already recovered). */
export function quotaFailure(s: ProviderQuotaSnapshot | null | undefined): { code: QuotaErrorCode | null; message: string } | null {
  if (!s?.error) return null;
  if (s.fetchedAtUtc && s.errorAtUtc && new Date(s.errorAtUtc).getTime() < new Date(s.fetchedAtUtc).getTime()) return null;
  return { code: s.errorCode ?? null, message: s.error };
}

export function failureText(f: { code: QuotaErrorCode | null; message: string }, t: TFunction): string {
  return f.code ? t(QUOTA_ERROR_KEY[f.code]) : f.message;
}

/** One short phrase per plan: "余额 ¥110.00", "5 小时已用 12%", "已用 30%". */
export function planPhrase(p: ProviderQuotaPlan, t: TFunction, locale: string): string | null {
  const used = usedPercentOf(p);
  if (isWindow(p)) return t('providers.quota.window', { window: windowLength(p.windowMinutes!, locale), percent: Math.round(used ?? 0) });
  if (p.remaining != null) {
    const amount = t('providers.quota.balance', { amount: formatAmount(p.remaining, p.unit, locale) });
    return used != null ? `${amount} · ${t('providers.quota.usedPercent', { percent: Math.round(used) })}` : amount;
  }
  if (used != null) return t('providers.quota.usedPercent', { percent: Math.round(used) });
  if (p.used != null && p.total != null)
    return t('providers.quota.totalUsed', { used: formatAmount(p.used, p.unit, locale), total: formatAmount(p.total, p.unit, locale) });
  return null;
}

/** When the window resets ("1小时后重置"), or null. */
export function planResets(p: ProviderQuotaPlan, t: TFunction, locale: string): string | null {
  const when = resetsIn(p.resetsAtUtc ?? null, locale);
  return when ? t('providers.quota.resets', { time: when }) : null;
}

/** The card line: every plan's phrase, or null when there is nothing to show yet. */
export function quotaSummary(s: ProviderQuotaSnapshot | null | undefined, t: TFunction, locale: string): string | null {
  const parts = (s?.plans ?? []).map((p) => planPhrase(p, t, locale)).filter((x): x is string => Boolean(x));
  if (s?.isValid === false) parts.unshift(t('providers.quota.invalid'));
  return parts.length ? parts.join(' · ') : null;
}

/** API-key provider whose usage query is on (subscriptions show their quota per account instead). */
export const quotaEnabled = (p: Provider) => p.authScheme !== 'oauth-subscription' && Boolean(p.quotaConfig?.enabled);
