import { ArrowDown, ArrowRightLeft, ArrowUp, Ellipsis, Gauge, Globe, Power, RotateCcw, Trash2 } from 'lucide-react';

import type { AccountQuota, ProviderAccount, ProviderAccountUsage } from '../api/types';
import { AccountUsageStats } from './AccountUsageStats';
import { Checkbox } from './arc/checkbox/checkbox';
import { Button } from './ui/controls';
import { Menu, type MenuItem } from './ui/overlays';
import { useI18n, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';
import { formatDateTime } from '../lib/format';

/** Where an account stands for the gateway right now (drives the card's accent and the status filter). */
export type AccountState = 'usable' | 'cooling' | 'dead' | 'disabled';

export function accountState(a: ProviderAccount, now = Date.now()): AccountState {
  if (!a.enabled) return 'disabled';
  if (a.status !== 'active') return 'dead';
  if (a.cooldownUntilUtc && new Date(a.cooldownUntilUtc).getTime() > now) return 'cooling';
  return 'usable';
}

const STATE_DOT: Record<AccountState, string> = {
  usable: 'var(--success)',
  cooling: 'var(--warning)',
  dead: 'var(--danger)',
  disabled: 'var(--text-muted)',
};

const STATUS_KEY: Record<ProviderAccount['status'], MessageKey> = {
  active: 'providers.subscription.status.active',
  expired: 'providers.subscription.status.expired',
  revoked: 'providers.subscription.status.revoked',
};

/**
 * 顶部色块的颜色按套餐家族区分（半透明渐变，深浅主题都能用）：
 * max / ultra = 蓝，pro / plus = 绿，team / business / enterprise = 紫，API Key = 灰，其余 = 灰。
 */
function planGradient(plan: string | undefined, isKey: boolean): string {
  const p = (plan ?? '').toLowerCase();
  const rgb = isKey
    ? '110,115,135'
    : /max|ultra/.test(p)
      ? '47,124,246'
      : /pro|plus|premium/.test(p)
        ? '52,195,143'
        : /team|business|enterprise|allegro|vivace/.test(p)
          ? '130,100,230'
          : '110,115,135';
  return `linear-gradient(135deg, rgba(${rgb},.34), rgba(${rgb},.08))`;
}

/** Per-account actions; the card only renders them, the account list owns the mutations. */
export interface AccountActions {
  open: (a: ProviderAccount) => void;
  activate: (a: ProviderAccount) => void;
  toggleEnabled: (a: ProviderAccount) => void;
  refreshQuota: (a: ProviderAccount) => void;
  refreshToken: (a: ProviderAccount) => void;
  relogin: (a: ProviderAccount) => void;
  remove: (a: ProviderAccount) => void;
}

/**
 * 账户卡片：身份、状态、今日 / 累计请求用量与费用，底部保留上游额度细条（失效时换成「重新登录」）。
 * 点击卡片打开详情；悬停或多选时左上角出现复选框。
 */
export function SubscriptionAccountCard({
  account,
  index,
  actions,
  selected,
  selecting,
  onToggleSelect,
  onMove,
  quotaPending,
  usage,
}: {
  account: ProviderAccount;
  /** Position in the failover order (0-based). */
  index: number;
  actions: AccountActions;
  selected: boolean;
  /** Some card is selected: checkboxes stay visible and a click toggles the selection. */
  selecting: boolean;
  onToggleSelect: (id: string) => void;
  /** Moves the account one place up / down in the failover order; undefined at that edge. */
  onMove?: { up?: () => void; down?: () => void };
  quotaPending?: boolean;
  usage?: ProviderAccountUsage;
}) {
  const { t, locale } = useI18n();
  const state = accountState(account);
  const name = account.displayName || account.accountEmail || account.keyMasked || account.id;
  const isKey = account.kind === 'api_key';
  const plan = account.plan?.trim();

  // 第二行的一句话状态：问题优先（冷却 / 失效），其次是令牌到期或 Key 掩码。
  const meta =
    state === 'cooling'
      ? { text: t('providers.subscription.cooldown', { when: formatDateTime(account.cooldownUntilUtc!, locale) }), tone: 'var(--warning)' }
      : state === 'dead'
        ? { text: account.lastError || t(STATUS_KEY[account.status]), tone: 'var(--danger)' }
        : isKey
          ? { text: account.keyMasked ?? t('providers.accounts.neverExpires'), tone: undefined }
          : account.expiresAtUtc
            ? { text: t('providers.accounts.expiresAt', { when: formatDateTime(account.expiresAtUtc, locale) }), tone: undefined }
            : null;

  const menu: MenuItem[] = [
    {
      id: 'activate',
      label: t('providers.subscription.activate'),
      icon: <ArrowRightLeft className="size-3.5" />,
      disabled: account.isCurrent || state === 'dead',
      onSelect: () => actions.activate(account),
    },
    {
      id: 'toggle',
      label: t(account.enabled ? 'providers.subscription.disable' : 'providers.subscription.enable'),
      icon: <Power className="size-3.5" />,
      onSelect: () => actions.toggleEnabled(account),
    },
    ...(onMove
      ? [
          { id: 'up', label: t('providers.subscription.moveUp'), icon: <ArrowUp className="size-3.5" />, disabled: !onMove.up, onSelect: () => onMove.up?.() },
          { id: 'down', label: t('providers.subscription.moveDown'), icon: <ArrowDown className="size-3.5" />, disabled: !onMove.down, onSelect: () => onMove.down?.() },
        ]
      : []),
    {
      id: 'quota',
      separatorBefore: true,
      label: t('providers.subscription.quotaRefresh'),
      icon: <Gauge className="size-3.5" />,
      disabled: state === 'dead',
      onSelect: () => actions.refreshQuota(account),
    },
    ...(isKey
      ? []
      : [
          {
            id: 'refresh',
            label: t('providers.subscription.refreshToken'),
            icon: <RotateCcw className="size-3.5" />,
            disabled: account.status === 'revoked',
            onSelect: () => actions.refreshToken(account),
          },
          { id: 'relogin', label: t('providers.subscription.relogin'), disabled: account.status === 'active', onSelect: () => actions.relogin(account) },
        ]),
    {
      id: 'remove',
      label: t('providers.subscription.logout'),
      icon: <Trash2 className="size-3.5" />,
      destructive: true,
      separatorBefore: true,
      onSelect: () => actions.remove(account),
    },
  ];

  const statusText = state === 'disabled' ? t('providers.subscription.disabled') : t(STATUS_KEY[account.status]);
  const kindText = t(isKey ? 'providers.accounts.kind.api_key' : 'providers.accounts.kind.oauth');
  const hasQuota = Boolean(account.quota?.session?.usedPercent != null || account.quota?.weekly?.usedPercent != null || account.quota?.credits?.usedPercent != null);

  return (
    <div
      data-testid="account-card"
      className={cn(
        'group card relative flex min-h-[292px] min-w-0 cursor-pointer flex-col overflow-hidden transition-colors hover:bg-[var(--fill)]',
        account.isCurrent && 'ring-1 ring-[var(--accent)]',
        selected && 'bg-[var(--accent-subtle)]',
        state === 'disabled' && 'opacity-55',
      )}
      onClick={() => (selecting ? onToggleSelect(account.id) : actions.open(account))}
    >
      {/* 顶部色块：状态 + 套餐名（颜色按套餐家族）；冷却 / 失效时在左侧再加一条色边。 */}
      <div className="relative shrink-0 px-3 pb-3 pt-2.5" style={{ background: planGradient(plan, isKey) }}>
        {(state === 'cooling' || state === 'dead') && (
          <span aria-hidden className="absolute inset-y-0 left-0 w-[3px]" style={{ background: STATE_DOT[state] }} />
        )}
        <div className="flex items-center gap-1.5">
          <span className={cn('flex size-5 items-center justify-center', !selecting && !selected && 'hidden group-hover:flex')} onClick={(e) => e.stopPropagation()}>
            <Checkbox checked={selected} onCheckedChange={() => onToggleSelect(account.id)} aria-label={t('providers.accounts.select', { name })} />
          </span>
          <span className="flex items-center gap-1 text-[11px] text-[var(--text-secondary)]">
            <span role="img" aria-label={statusText} className="size-1.5 shrink-0 rounded-full" style={{ background: STATE_DOT[state] }} />
            <span aria-hidden>{statusText}</span>
          </span>
          {account.httpProxy && (
            <span title={`${t('providers.accounts.proxyBadge')}: ${account.httpProxy}`} className="shrink-0 text-[var(--text-muted)]">
              <Globe className="size-3.5" aria-label={t('providers.accounts.proxyBadge')} />
            </span>
          )}
          <span className="-mr-1.5 ml-auto shrink-0" onClick={(e) => e.stopPropagation()}>
            <Menu iconOnly label={t('common.more')} icon={<Ellipsis className="size-4" />} items={menu} />
          </span>
        </div>
        <div className="mt-1.5 truncate text-[17px] font-bold tracking-[.01em]" title={plan || kindText}>
          {plan || kindText}
        </div>
      </div>

      <div className="flex min-h-0 flex-1 flex-col px-3 pb-3 pt-2.5">
        <button
          type="button"
          className="block max-w-full truncate text-left text-[13px] font-medium outline-none focus-visible:underline"
          title={name}
          onClick={(e) => {
            e.stopPropagation();
            if (selecting) onToggleSelect(account.id);
            else actions.open(account);
          }}
        >
          {name}
        </button>
        <div className="mt-0.5 flex min-w-0 items-center gap-1 whitespace-nowrap text-[11px] text-[var(--text-muted)]">
          {account.isCurrent && (
            <span className="flex shrink-0 items-center gap-1 text-[var(--success)]">
              <span aria-hidden>●</span>
              <span>{t('providers.subscription.current')}</span>
            </span>
          )}
          {account.isCurrent && <span aria-hidden>·</span>}
          <span className="shrink-0">{kindText}</span>
          {account.source && (
            <>
              <span aria-hidden>·</span>
              <span className="truncate">{t(`providers.accounts.source.${account.source}` as MessageKey)}</span>
            </>
          )}
          <span className="ml-auto shrink-0 pl-1 opacity-0 group-hover:opacity-100">{t('providers.accounts.order', { n: index + 1 })}</span>
        </div>

        {/* 冷却 / 失效 / 没有额度的账户：把一句话状态放在这里（到期时间、冷却至、失效原因）。 */}
        <div className="mt-1.5 min-h-[28px]">
          {meta && (state === 'cooling' || state === 'dead' || !hasQuota) && (
            <div className="line-clamp-2 text-[11px] leading-snug" style={{ color: meta.tone ?? 'var(--text-muted)' }} title={meta.text}>
              {meta.text}
            </div>
          )}
        </div>

        <div className="mt-3 shrink-0 border-t border-[var(--border-subtle)] pt-2">
          <AccountUsageStats usage={usage} compact />
        </div>

        {state === 'dead' ? (
          <div className="mt-auto flex items-center gap-2 pt-2">
            <span className="text-[11px] text-[var(--text-muted)]">{t('providers.accounts.signedOut')}</span>
            {!isKey && (
              <Button
                size="sm"
                className="ml-auto"
                onClick={(e) => {
                  e.stopPropagation();
                  actions.relogin(account);
                }}
              >
                {t('providers.subscription.relogin')}
              </Button>
            )}
          </div>
        ) : (
          <QuotaStrip quota={account.quota ?? null} pending={quotaPending} />
        )}
      </div>
    </div>
  );
}

const SEGMENTS = 10;

/** Up to two segmented quota bars (session / weekly, else credits) at the card's bottom; the reset time is the tooltip. */
function QuotaStrip({ quota, pending }: { quota: AccountQuota | null; pending?: boolean }) {
  const { t, locale } = useI18n();
  const bars: { key: string; label: string; percent: number; reset?: string | null }[] = [];
  if (quota?.session?.usedPercent != null)
    bars.push({
      key: 'session',
      label: quota.session.windowMinutes ? `${Math.round(quota.session.windowMinutes / 60)}h` : t('providers.accounts.quotaShortSession'),
      percent: quota.session.usedPercent,
      reset: quota.session.resetsAtUtc,
    });
  if (quota?.weekly?.usedPercent != null)
    bars.push({ key: 'weekly', label: t('providers.accounts.quotaShortWeekly'), percent: quota.weekly.usedPercent, reset: quota.weekly.resetsAtUtc });
  if (bars.length < 2 && quota?.credits?.usedPercent != null)
    bars.push({
      key: 'credits',
      label: t(quota.entitlement != null ? 'providers.accounts.quotaShortPremium' : 'providers.accounts.quotaShortCredits'),
      percent: quota.credits.usedPercent,
      reset: quota.credits.resetsAtUtc,
    });
  if (bars.length === 0) return null;

  return (
    <div className={cn('mt-auto flex flex-col gap-2 pt-2', pending && 'opacity-60')}>
      {bars.slice(0, 2).map((b) => {
        const percent = Math.max(0, Math.min(100, Math.round(b.percent)));
        const tone = percent >= 90 ? 'var(--danger)' : percent >= 70 ? 'var(--warning)' : 'var(--accent)';
        const lit = Math.round((percent / 100) * SEGMENTS);
        const reset = b.reset ? t('providers.subscription.quotaResets', { when: formatDateTime(b.reset, locale) }) : undefined;
        return (
          <div key={b.key} className="min-w-0" title={reset}>
            <div className="flex justify-between text-[11px] text-[var(--text-secondary)]">
              <span>{b.label}</span>
              <span className="font-mono tabular-nums">{percent}%</span>
            </div>
            <div
              role="progressbar"
              aria-label={b.label}
              aria-valuenow={percent}
              aria-valuemin={0}
              aria-valuemax={100}
              className="mt-0.5 flex gap-[3px]"
            >
              {Array.from({ length: SEGMENTS }, (_, i) => (
                <i key={i} className="h-[5px] flex-1 rounded-[2px] bg-[var(--surface-muted)]" style={i < lit ? { background: tone } : undefined} />
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}
