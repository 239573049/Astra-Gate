import { ArrowDown, ArrowRightLeft, ArrowUp, Gauge, Globe, Power, RotateCcw, Trash2 } from 'lucide-react';

import type { AccountQuota, ProviderAccount } from '../api/types';
import { Checkbox } from './arc/checkbox/checkbox';
import { Badge, Button } from './ui/controls';
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
 * 紧凑的账户卡片（约 76px 高，网格里一行两三张）：身份 + 当前标记 + 套餐 + 状态点 + ⋯ 菜单；
 * 第二行是类型 · 来源 · 一句话状态；第三行是额度细条（失效时换成「重新登录」）。
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

  return (
    <div
      data-testid="account-card"
      className={cn(
        'group card relative cursor-pointer px-2.5 pb-2 pt-2 transition-colors hover:bg-[var(--fill)]',
        account.isCurrent && 'ring-1 ring-[var(--accent)]',
        selected && 'bg-[var(--accent-subtle)]',
        state === 'disabled' && 'opacity-55',
      )}
      onClick={() => (selecting ? onToggleSelect(account.id) : actions.open(account))}
    >
      {/* 冷却 / 失效：左侧色条，扫一眼就能看出哪几张有问题。 */}
      {(state === 'cooling' || state === 'dead') && (
        <span aria-hidden className="absolute bottom-2 left-0 top-2 w-[2px] rounded-full" style={{ background: STATE_DOT[state] }} />
      )}

      <div className="flex min-w-0 items-center gap-1.5">
        <span
          className={cn('flex size-5 shrink-0 items-center justify-center', !selecting && !selected && 'max-sm:flex sm:hidden sm:group-hover:flex')}
          onClick={(e) => e.stopPropagation()}
        >
          <Checkbox
            checked={selected}
            onCheckedChange={() => onToggleSelect(account.id)}
            aria-label={t('providers.accounts.select', { name })}
          />
        </span>
        {!selecting && !selected && (
          <span
            aria-hidden
            className="hidden size-5 shrink-0 items-center justify-center rounded-md bg-[var(--surface-muted)] text-[10.5px] font-semibold uppercase text-[var(--text-secondary)] sm:flex sm:group-hover:hidden"
          >
            {name.charAt(0)}
          </span>
        )}
        <button
          type="button"
          className="min-w-0 flex-1 truncate text-left text-[12.5px] font-medium outline-none focus-visible:underline"
          title={name}
          onClick={(e) => {
            e.stopPropagation();
            if (selecting) onToggleSelect(account.id);
            else actions.open(account);
          }}
        >
          {name}
        </button>
        {account.isCurrent && <Badge tone="accent">{t('providers.subscription.current')}</Badge>}
        {plan && <Badge tone="accent" className="max-w-24 truncate">{plan}</Badge>}
        {account.httpProxy && (
          <span title={`${t('providers.accounts.proxyBadge')}: ${account.httpProxy}`} className="shrink-0 text-[var(--text-muted)]">
            <Globe className="size-3" aria-label={t('providers.accounts.proxyBadge')} />
          </span>
        )}
        <span
          role="img"
          aria-label={state === 'disabled' ? t('providers.subscription.disabled') : t(STATUS_KEY[account.status])}
          className="size-1.5 shrink-0 rounded-full"
          style={{ background: STATE_DOT[state] }}
        />
        <span className="-my-1 -mr-1 shrink-0" onClick={(e) => e.stopPropagation()}>
          <Menu label={t('common.more')} icon={<span className="text-[15px] leading-none">⋯</span>} items={menu} />
        </span>
      </div>

      <div className="ml-[26px] mt-0.5 flex min-w-0 items-center gap-1 whitespace-nowrap text-[10.5px] text-[var(--text-muted)]">
        <span className="text-[var(--text-secondary)]">{t(isKey ? 'providers.accounts.kind.api_key' : 'providers.accounts.kind.oauth')}</span>
        {account.source && (
          <>
            <span aria-hidden>·</span>
            <span>{t(`providers.accounts.source.${account.source}` as MessageKey)}</span>
          </>
        )}
        {meta && (
          <>
            <span aria-hidden>·</span>
            <span className="truncate" style={{ color: meta.tone }} title={meta.text}>
              {meta.text}
            </span>
          </>
        )}
        <span className="ml-auto pl-1 opacity-0 group-hover:opacity-100">{t('providers.accounts.order', { n: index + 1 })}</span>
      </div>

      {state === 'dead' ? (
        <div className="ml-[26px] mt-1.5 flex items-center gap-2">
          <span className="text-[10.5px] text-[var(--text-muted)]">{t('providers.accounts.signedOut')}</span>
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
  );
}

/** Up to two thin quota bars (session / weekly, else credits) in one row. */
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
    <div className={cn('ml-[26px] mt-1.5 flex gap-3', pending && 'opacity-60')}>
      {bars.slice(0, 2).map((b) => {
        const percent = Math.max(0, Math.min(100, Math.round(b.percent)));
        const tone = percent >= 90 ? 'var(--danger)' : percent >= 70 ? 'var(--warning)' : 'var(--accent)';
        const reset = b.reset ? t('providers.subscription.quotaResets', { when: formatDateTime(b.reset, locale) }) : undefined;
        return (
          <div key={b.key} className="min-w-0 flex-1" title={reset}>
            <div className="flex justify-between text-[10px] text-[var(--text-muted)]">
              <span>{b.label}</span>
              <span className="font-mono tabular-nums text-[var(--text-secondary)]">{percent}%</span>
            </div>
            <div
              role="progressbar"
              aria-label={b.label}
              aria-valuenow={percent}
              aria-valuemin={0}
              aria-valuemax={100}
              className="mt-0.5 h-[3px] overflow-hidden rounded-full bg-[var(--surface-muted)]"
            >
              <div className="h-full rounded-full" style={{ width: `${percent}%`, background: tone }} />
            </div>
          </div>
        );
      })}
    </div>
  );
}
