import { ArrowRightLeft, Gauge, RotateCcw, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';

import { useUpdateProviderAccount } from '../api/hooks';
import type { Provider, ProviderAccount, ProviderAccountUsage } from '../api/types';
import { AccountUsageStats, ProviderUsageHint } from './AccountUsageStats';
import { Alert } from './arc/alert/alert';
import { Badge, Button, Input, KV } from './ui/controls';
import { errorText, Sheet, useFeedback } from './ui/overlays';
import { accountState, type AccountActions } from './SubscriptionAccountCard';
import { SubscriptionQuotaCard } from './SubscriptionQuotaCard';
import { SubscriptionResetCredits } from './SubscriptionResetCredits';
import { useI18n, type MessageKey } from '../i18n';
import { formatDateTime } from '../lib/format';

const STATUS_KEY: Record<ProviderAccount['status'], MessageKey> = {
  active: 'providers.subscription.status.active',
  expired: 'providers.subscription.status.expired',
  revoked: 'providers.subscription.status.revoked',
};

/**
 * 账户详情（点卡片打开）：完整额度、codex 重置卡、凭据信息，以及名称与账户级代理的编辑。
 * 动作与卡片菜单相同，由账户列表统一执行。
 */
export function SubscriptionAccountDetail({
  provider,
  account,
  index,
  actions,
  onClose,
  usage,
  usageTimeZone,
}: {
  provider: Provider;
  account: ProviderAccount | null;
  index: number;
  actions: AccountActions;
  onClose: () => void;
  usage?: ProviderAccountUsage;
  usageTimeZone?: string;
}) {
  const { t, locale } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateProviderAccount(provider.id);
  const [name, setName] = useState('');
  const [proxy, setProxy] = useState('');

  useEffect(() => {
    setName(account?.displayName ?? '');
    setProxy(account?.httpProxy ?? '');
  }, [account?.id, account?.displayName, account?.httpProxy]);

  if (!account) return <Sheet open={false} onOpenChange={onClose} title="" />;
  const state = accountState(account);
  const isKey = account.kind === 'api_key';
  const isCodex = provider.templateId === 'openai-subscription';
  const fallback = provider.httpProxy || t('providers.accounts.proxyFallbackSystem');
  const when = (iso?: string | null) => (iso ? formatDateTime(iso, locale) : '—');

  const save = (patch: { displayName?: string; httpProxy?: string }, ok: MessageKey) =>
    update.mutate({ id: account.id, ...patch }, { onSuccess: () => toast(t(ok), 'success'), onError: (e) => toast(errorText(e), 'error') });

  return (
    <Sheet
      open
      onOpenChange={(o) => !o && onClose()}
      title={account.displayName || account.keyMasked || account.id}
      description={[account.plan, t(isKey ? 'providers.accounts.kind.api_key' : 'providers.accounts.kind.oauth')].filter(Boolean).join(' · ')}
      width={520}
    >
      <div className="flex flex-col gap-4">
        <div className="flex flex-wrap gap-1.5">
          <Button size="sm" icon={<ArrowRightLeft className="size-3.5" />} disabled={account.isCurrent || state === 'dead'} onClick={() => actions.activate(account)}>
            {t('providers.subscription.activate')}
          </Button>
          <Button size="sm" icon={<Gauge className="size-3.5" />} disabled={state === 'dead'} onClick={() => actions.refreshQuota(account)}>
            {t('providers.subscription.quotaRefresh')}
          </Button>
          {!isKey && (
            <Button size="sm" icon={<RotateCcw className="size-3.5" />} disabled={account.status === 'revoked'} onClick={() => actions.refreshToken(account)}>
              {t('providers.subscription.refreshToken')}
            </Button>
          )}
          {!isKey && account.status !== 'active' && (
            <Button size="sm" variant="primary" onClick={() => actions.relogin(account)}>
              {t('providers.subscription.relogin')}
            </Button>
          )}
          <Button size="sm" variant="plain" className="ml-auto text-[var(--danger)]" icon={<Trash2 className="size-3.5" />} onClick={() => actions.remove(account)}>
            {t('providers.subscription.logout')}
          </Button>
        </div>

        {state === 'dead' && account.lastError && <Alert tone="danger" title={account.lastError} />}

        <section className="flex flex-col gap-2">
          <h4 className="text-[11px] font-medium text-[var(--text-secondary)]">{t('providers.usage.title')}</h4>
          <div className="card px-3 py-2"><AccountUsageStats usage={usage} /></div>
          <ProviderUsageHint timeZone={usageTimeZone} />
        </section>

        <section className="flex flex-col gap-2">
          <h4 className="text-[11px] font-medium text-[var(--text-secondary)]">{t('providers.accounts.detail.identity')}</h4>
          <div className="flex items-end gap-2">
            <Input className="flex-1" label={t('providers.accounts.detail.name')} value={name} onChange={(e) => setName(e.target.value)} />
            <Button
              disabled={name.trim().length === 0 || name.trim() === account.displayName}
              loading={update.isPending && update.variables?.displayName !== undefined}
              onClick={() => save({ displayName: name.trim() }, 'providers.accounts.renamed')}
            >
              {t('common.save')}
            </Button>
          </div>
          <div className="flex items-end gap-2">
            <Input
              className="flex-1"
              mono
              label={t('providers.accounts.detail.proxy')}
              placeholder={fallback}
              value={proxy}
              onChange={(e) => setProxy(e.target.value)}
            />
            <Button
              disabled={proxy.trim() === (account.httpProxy ?? '')}
              loading={update.isPending && update.variables?.httpProxy !== undefined}
              onClick={() => save({ httpProxy: proxy.trim() }, 'providers.accounts.proxySaved')}
            >
              {t('common.save')}
            </Button>
          </div>
          <p className="text-[11px] text-[var(--text-muted)]">{t('providers.accounts.detail.proxyHint', { fallback })}</p>
        </section>

        {account.quota && (
          <section className="-mx-4 flex flex-col gap-1">
            <h4 className="px-4 text-[11px] font-medium text-[var(--text-secondary)]">{t('providers.accounts.detail.quota')}</h4>
            <SubscriptionQuotaCard quota={account.quota} />
          </section>
        )}
        {isCodex && state !== 'dead' && (
          <div className="-mx-4">
            <SubscriptionResetCredits account={account} providerId={provider.id} />
          </div>
        )}

        <section className="flex flex-col gap-1">
          <h4 className="text-[11px] font-medium text-[var(--text-secondary)]">{t('providers.accounts.detail.credentials')}</h4>
          <div className="card divide-y divide-[var(--border)] px-3">
            <KV label={t('providers.accounts.detail.status')}>
              <Badge tone={state === 'usable' ? 'green' : state === 'cooling' ? 'orange' : state === 'dead' ? 'red' : 'neutral'}>
                {state === 'disabled' ? t('providers.subscription.disabled') : t(STATUS_KEY[account.status])}
              </Badge>
            </KV>
            {account.source && <KV label={t('providers.accounts.detail.source')}>{t(`providers.accounts.source.${account.source}` as MessageKey)}</KV>}
            {isKey ? (
              <KV label={t('providers.accounts.detail.key')} mono>{account.keyMasked ?? '••••'}</KV>
            ) : (
              <>
                <KV label={t('providers.accounts.detail.expires')}>{when(account.expiresAtUtc)}</KV>
                <KV label={t('providers.accounts.detail.lastRefresh')}>{when(account.lastRefreshAtUtc)}</KV>
              </>
            )}
            {state === 'cooling' && (
              <KV label={t('providers.accounts.filter.cooling')}>{when(account.cooldownUntilUtc)}</KV>
            )}
            <KV label={t('providers.accounts.detail.order')}>{t('providers.accounts.order', { n: index + 1 })}</KV>
            <KV label={t('providers.accounts.detail.created')}>{when(account.createdAt)}</KV>
            {account.lastError && state !== 'dead' && <KV label={t('providers.accounts.detail.lastError')}>{account.lastError}</KV>}
          </div>
        </section>
      </div>
    </Sheet>
  );
}
