import { useEffect, useState } from 'react';

import type { AccountQuota, QuotaWindow } from '../api/types';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';

/**
 * 订阅账号的额度卡片：每个限流窗口一条进度条（已用百分比 + 重置时间 + 窗口时长）。
 * 没有 usedPercent 的项（例如纯余额套餐）不画条，改由文字行展示。
 */
export function SubscriptionQuotaCard({ quota, className }: { quota: AccountQuota; className?: string }) {
  const { t, locale } = useI18n();

  const resetText = (iso?: string | null) => {
    if (!iso) return null;
    const at = new Date(iso);
    return Number.isNaN(at.getTime()) ? null : t('providers.subscription.quotaResets', { when: at.toLocaleString(locale) });
  };

  const bars: { key: string; label: string; window: QuotaWindow }[] = [];
  if (quota.session?.usedPercent != null)
    bars.push({ key: 'session', label: t('providers.subscription.quotaWindowSession'), window: quota.session });
  if (quota.weekly?.usedPercent != null)
    bars.push({ key: 'weekly', label: t('providers.subscription.quotaWindowWeekly'), window: quota.weekly });
  if (quota.credits?.usedPercent != null)
    bars.push({
      key: 'credits',
      // Copilot 的 credits 是"高级请求"额度，换个标签更准确。
      label: quota.entitlement != null ? t('providers.subscription.quotaWindowPremium') : t('providers.subscription.quotaWindowCredits'),
      window: { usedPercent: quota.credits.usedPercent, resetsAtUtc: quota.credits.resetsAtUtc ?? null },
    });

  const balance =
    quota.credits?.prepaidBalance != null
      ? t('providers.subscription.quotaBalance', { balance: quota.credits.prepaidBalance })
      : null;

  // Copilot：把"已用 / 总额"和超出额的用量摆在条下面。
  const counts =
    quota.entitlement != null && quota.entitlement >= 0
      ? [
          t('providers.subscription.quotaPremiumCount', {
            used: Math.max(0, (quota.entitlement ?? 0) - (quota.remaining ?? 0)),
            total: quota.entitlement,
          }),
          quota.overageUsed ? t('providers.subscription.quotaOverage', { count: quota.overageUsed }) : null,
          quota.overagePermitted === false ? t('providers.subscription.quotaOverageOff') : null,
        ].filter(Boolean).join(' · ')
      : null;

  const caption = quota.planLabel ?? (quota.fetchedAtUtc ? t('providers.subscription.quotaFetchedAt', { when: new Date(quota.fetchedAtUtc).toLocaleString(locale) }) : null);

  return (
    <div className={cn('px-4 py-1', className)}>
      <div className="card flex flex-col gap-3 p-3">
        {(caption || balance) && (
          <div className="flex flex-wrap items-center gap-3 text-[11px] text-[var(--text-secondary)]">
            {caption && <span>{caption}</span>}
            {balance && <span>{balance}</span>}
          </div>
        )}
        {bars.length === 0 ? (
          <div className="text-[12px] text-[var(--text-muted)]">{t('providers.subscription.quotaNoWindows')}</div>
        ) : (
          bars.map((b) => <QuotaBar key={b.key} label={b.label} window={b.window} reset={resetText(b.window.resetsAtUtc)} />)
        )}
        {counts && <div className="text-[10.5px] text-[var(--text-muted)]">{counts}</div>}
      </div>
    </div>
  );
}

function QuotaBar({ label, window, reset }: { label: string; window: QuotaWindow; reset: string | null }) {
  const percent = Math.max(0, Math.min(100, Math.round(window.usedPercent ?? 0)));
  const [width, setWidth] = useState(0);

  // 入场时把条从 0 推到目标宽度；跟随系统"减少动态效果"设置。
  useEffect(() => {
    const reduce = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (reduce) {
      setWidth(percent);
      return;
    }
    const id = requestAnimationFrame(() => setWidth(percent));
    return () => cancelAnimationFrame(id);
  }, [percent]);

  // 越高越接近限流：>=90% 红、>=70% 橙、其余用强调色。
  const tone = percent >= 90 ? 'var(--danger)' : percent >= 70 ? 'var(--warning)' : 'var(--accent)';
  const hours = window.windowMinutes ? Math.round(window.windowMinutes / 60) : null;

  return (
    <div>
      <div className="mb-1 flex items-baseline justify-between gap-2 text-[11.5px]">
        <span className="text-[var(--text-secondary)]">
          {label}
          {hours != null && <span className="text-[var(--text-muted)]"> · {hours}h</span>}
        </span>
        <span className="font-mono tabular-nums">{percent}%</span>
      </div>
      <div
        role="progressbar"
        aria-valuenow={percent}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-label={label}
        className="h-1.5 w-full overflow-hidden rounded-full bg-[var(--surface-muted)]"
      >
        <div className="h-full rounded-full transition-[width] duration-500 ease-out" style={{ width: `${width}%`, background: tone }} />
      </div>
      {reset && <div className="mt-1 text-[10.5px] text-[var(--text-muted)]">{reset}</div>}
    </div>
  );
}
