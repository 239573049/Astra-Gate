import { Gauge, RotateCcw, Trash2 } from 'lucide-react';
import { useEffect, useRef } from 'react';

import { ApiError } from '../api/client';
import { useDeleteProviderAccount, useFetchProviderAccountQuota, useRefreshProviderAccount } from '../api/hooks';
import type { Provider, ProviderAccount } from '../api/types';
import { Badge, Button, Group } from './ui/controls';
import { errorText, Menu, useFeedback } from './ui/overlays';
import { SubscriptionQuotaCard } from './SubscriptionQuotaCard';
import { SubscriptionResetCredits } from './SubscriptionResetCredits';
import { useI18n, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';

const STATUS_TONE = { active: 'green', expired: 'orange', revoked: 'red' } as const;
const STATUS_KEY: Record<ProviderAccount['status'], MessageKey> = {
  active: 'providers.subscription.status.active',
  expired: 'providers.subscription.status.expired',
  revoked: 'providers.subscription.status.revoked',
};

/** Quota snapshots older than this are refreshed automatically when the card mounts. */
const QUOTA_FRESH_MS = 5 * 60_000;

/**
 * 单个订阅账号的卡片：账号身份（邮箱 + 套餐等级 + 状态）、额度进度条、以及各类操作。
 * 每个卡片自己负责这个账号的额度/令牌刷新与删除，登录仍由父级统一发起（需要共享登录弹窗）。
 */
export function SubscriptionAccountCard({
  provider,
  account,
  onRelogin,
}: {
  provider: Provider;
  account: ProviderAccount;
  onRelogin: (accountId: string) => void;
}) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const quota = useFetchProviderAccountQuota(provider.id);
  const refresh = useRefreshProviderAccount(provider.id);
  const remove = useDeleteProviderAccount(provider.id);
  const attempted = useRef(false);

  const dead = account.status !== 'active';
  const isCodex = provider.templateId === 'openai-subscription';

  // 进页面时拉一次额度（缺失或快照超过 5 分钟）；失败保持静默，用户可手动重试。
  useEffect(() => {
    if (dead || attempted.current) return;
    const at = account.quota?.fetchedAtUtc ? new Date(account.quota.fetchedAtUtc).getTime() : 0;
    if (Number.isFinite(at) && at > 0 && Date.now() - at < QUOTA_FRESH_MS) return;
    attempted.current = true;
    quota.mutate(account.id, { onError: () => {} });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [account.id, dead, account.quota?.fetchedAtUtc]);

  const refreshQuota = () =>
    quota.mutate(account.id, {
      onError: (e) => {
        if (e instanceof ApiError && e.status === 501) toast(t('providers.subscription.quotaUnsupported'), 'error');
        else toast(errorText(e), 'error');
      },
    });

  const refreshToken = () =>
    refresh.mutate(account.id, {
      onSuccess: () => toast(t('providers.subscription.tokenRefreshed'), 'success'),
      onError: (e) => toast(errorText(e), 'error'),
    });

  const removeAccount = async () => {
    if (
      !(await confirm({
        title: t('providers.subscription.logoutConfirm', { name: account.displayName || account.id }),
        destructive: true,
        confirmLabel: t('providers.subscription.logout'),
      }))
    )
      return;
    remove.mutate(account.id, { onError: (e) => toast(errorText(e), 'error') });
  };

  const plan = account.plan?.trim();

  return (
    <Group className={cn('overflow-hidden', dead && 'opacity-60')}>
      {/* 头部：身份 + 状态 + 操作（提供商 logo 在页面标题上，这里不重复） */}
      <div className="flex flex-wrap items-center gap-3 px-4 py-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="truncate text-[13.5px] font-medium">{account.displayName || account.id}</span>
            {plan && <Badge tone="accent">{plan}</Badge>}
            <Badge tone={STATUS_TONE[account.status]}>{t(STATUS_KEY[account.status])}</Badge>
          </div>
          <div className="mt-0.5 flex flex-wrap items-center gap-x-2 text-[11px] text-[var(--text-secondary)]">
            {account.accountEmail && account.accountEmail !== account.displayName && <span>{account.accountEmail}</span>}
            {account.expiresAtUtc && (
              <span>
                {t('providers.subscription.expires')} {new Date(account.expiresAtUtc).toLocaleString()}
              </span>
            )}
          </div>
        </div>
        <div className="flex items-center gap-1">
          <Button
            size="sm"
            variant="plain"
            icon={<Gauge className="size-3.5" />}
            aria-label={t('providers.subscription.quotaRefresh')}
            loading={quota.isPending && quota.variables === account.id}
            disabled={dead}
            onClick={refreshQuota}
          />
          <Menu
            label={t('common.more')}
            icon={<span className="text-[15px] leading-none">⋯</span>}
            items={[
              {
                id: 'refresh',
                label: t('providers.subscription.refreshToken'),
                icon: <RotateCcw className="size-3.5" />,
                disabled: account.status === 'revoked',
                onSelect: refreshToken,
              },
              {
                id: 'relogin',
                label: t('providers.subscription.relogin'),
                disabled: !dead,
                onSelect: () => onRelogin(account.id),
              },
              {
                id: 'remove',
                label: t('providers.subscription.logout'),
                icon: <Trash2 className="size-3.5" />,
                destructive: true,
                separatorBefore: true,
                onSelect: () => void removeAccount(),
              },
            ]}
          />
        </div>
      </div>

      {/* 额度进度条 */}
      {account.quota ? (
        <SubscriptionQuotaCard quota={account.quota} />
      ) : (
        <div className="px-4 pb-3 text-[11.5px] text-[var(--text-muted)]">
          {dead ? t('providers.subscription.quotaNoAccount') : t('providers.subscription.quotaLoading')}
        </div>
      )}

      {/* codex 专属：额度重置卡 */}
      {isCodex && !dead && <SubscriptionResetCredits account={account} providerId={provider.id} />}
    </Group>
  );
}
