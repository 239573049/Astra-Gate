import { Gift, Ticket } from 'lucide-react';

import { ApiError } from '../api/client';
import { useConsumeResetCredit, useResetCredits } from '../api/hooks';
import type { ProviderAccount, ResetCredit } from '../api/types';
import { Badge, Button, Group, Row, Spinner } from './ui/controls';
import { errorText, useFeedback } from './ui/overlays';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';

const STATUS_TONE = { available: 'green', redeemed: 'neutral', expired: 'orange' } as const;

/**
 * codex 的额度重置卡：上游 <c>/wham/rate-limit-reset-credits</c> 给出账号手上的重置卡
 * （status = available / redeemed / expired）。卡用完一张少一张，且有过期时间，
 * 所以每次进页面实时拉一次；用卡前弹确认，用完后端会顺手刷新额度。
 */
export function SubscriptionResetCredits({ account, providerId }: { account: ProviderAccount; providerId: string }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const credits = useResetCredits(account.id, account.status === 'active');
  const consume = useConsumeResetCredit(providerId);

  const list = credits.data?.credits ?? [];
  const available = credits.data?.available_count ?? list.filter((c) => c.status === 'available').length;

  const spend = async (card: ResetCredit) => {
    const ok = await confirm({
      title: t('providers.subscription.credits.consumeConfirm', { title: card.title ?? card.reset_type ?? '' }),
      detail: t('providers.subscription.credits.consumeDetail'),
      confirmLabel: t('providers.subscription.credits.consume'),
      destructive: true,
    });
    if (!ok) return;
    consume.mutate(
      { accountId: account.id, creditId: card.id },
      {
        onSuccess: () => toast(t('providers.subscription.credits.consumed'), 'success'),
        onError: (e) =>
          toast(e instanceof ApiError && e.status === 409 ? errorText(e) : `${t('providers.subscription.credits.consumeFailed')}：${errorText(e)}`, 'error'),
      },
    );
  };

  return (
    <Group
      title={t('providers.subscription.credits.title')}
      footer={t('providers.subscription.credits.footer')}
    >
      {credits.isLoading && <Spinner className="my-4" />}
      {credits.isError && (
        <div className="px-4 py-3 text-[12px] text-[var(--text-secondary)]">
          {t('providers.subscription.credits.unavailable')}
        </div>
      )}
      {credits.data && list.length === 0 && (
        <div className="px-4 py-5 text-center text-[12px] text-[var(--text-secondary)]">
          {t('providers.subscription.credits.none')}
        </div>
      )}
      {list.map((card) => {
        const spent = card.status !== 'available';
        return (
          <Row
            key={card.id}
            className={cn(spent && 'opacity-55')}
            label={
              <span className="flex items-center gap-2">
                <Gift className="size-3.5 text-[var(--text-secondary)]" />
                {card.title ?? card.reset_type ?? card.id}
              </span>
            }
            detail={[
              card.description,
              card.expires_at ? t('providers.subscription.credits.expires', { when: new Date(card.expires_at).toLocaleString() }) : null,
              card.redeemed_at ? t('providers.subscription.credits.redeemedAt', { when: new Date(card.redeemed_at).toLocaleString() }) : null,
              card.is_supported_by_plan === false ? t('providers.subscription.credits.notInPlan') : null,
            ]
              .filter(Boolean)
              .join(' · ')}
          >
            <Badge tone={STATUS_TONE[card.status as keyof typeof STATUS_TONE] ?? 'neutral'}>
              {t((`providers.subscription.credits.status.${card.status}`) as 'providers.subscription.credits.status.available')}
            </Badge>
            <Button
              size="sm"
              variant="plain"
              icon={<Ticket className="size-3.5" />}
              disabled={spent}
              loading={consume.isPending && consume.variables?.creditId === card.id}
              onClick={() => void spend(card)}
            >
              {t('providers.subscription.credits.consume')}
            </Button>
          </Row>
        );
      })}
      {credits.data && list.length > 0 && (
        <div className="px-4 py-2 text-[11px] text-[var(--text-muted)]">
          {t('providers.subscription.credits.availableCount', { count: available })}
        </div>
      )}
    </Group>
  );
}
