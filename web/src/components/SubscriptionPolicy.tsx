import { Alert } from '../components/arc/alert/alert';
import { useSubscriptionPolicy, useUpdateSubscriptionPolicy } from '../api/hooks';
import type { Provider, SubscriptionPolicy as Policy } from '../api/types';
import { Group, Row, Segmented, Switch } from './ui/controls';
import { errorText, useFeedback } from './ui/overlays';
import { useI18n } from '../i18n';

/**
 * 订阅提供商的使用策略（plan §5.4）：账号切换方式（手动 / 自动故障转移），
 * 以及 Claude 订阅专有的两项——「仅限 Claude Code」客户端限制（默认开启），
 * 与「其他客户端模拟 Claude Code」（默认关闭，见 ClaudeCodeMimicry；开启有封号风险）。
 */
export function SubscriptionPolicy({ provider }: { provider: Provider }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const isSubscription = provider.authScheme === 'oauth-subscription';
  const policy = useSubscriptionPolicy(provider.id, isSubscription);
  const update = useUpdateSubscriptionPolicy(provider.id);

  if (!isSubscription || !policy.data) return null;
  const p = policy.data;
  const save = (patch: Partial<Pick<Policy, 'clientPolicy' | 'switchMode' | 'mimicClaudeCode'>>) =>
    update.mutate(patch, { onError: (e) => toast(errorText(e), 'error') });
  // 非 Claude 订阅只有在被显式设成 claude-code-only 时才显示这一行（否则没有意义）。
  const showClientPolicy = p.claudeSubscription || p.clientPolicy === 'claude-code-only';

  return (
    <Group title={t('providers.subscription.policy.title')} footer={t('providers.subscription.policy.footer')}>
      {showClientPolicy && (
        <Row
          label={t('providers.subscription.policy.claudeCodeOnly')}
          detail={t('providers.subscription.policy.claudeCodeOnlyDetail')}
        >
          <Switch
            label={t('providers.subscription.policy.claudeCodeOnly')}
            checked={p.clientPolicy === 'claude-code-only'}
            disabled={update.isPending}
            onChange={(on) => save({ clientPolicy: on ? 'claude-code-only' : 'any' })}
          />
        </Row>
      )}
      {p.claudeSubscription && (
        <Row
          label={t('providers.subscription.policy.mimic')}
          detail={t('providers.subscription.policy.mimicDetail')}
        >
          <Switch
            label={t('providers.subscription.policy.mimic')}
            checked={p.mimicClaudeCode}
            disabled={update.isPending}
            onChange={(on) => save({ mimicClaudeCode: on })}
          />
        </Row>
      )}
      {showClientPolicy && p.clientPolicy === 'any' && (
        <div className="px-4 py-3">
          {p.mimicClaudeCode
            ? <Alert tone="warning" title={t('providers.subscription.policy.mimicWarning')} />
            : <Alert tone="warning" title={t('providers.subscription.policy.anyWarning')} />}
        </div>
      )}
      <Row
        label={t('providers.subscription.policy.switchMode')}
        detail={t(p.switchMode === 'failover' ? 'providers.subscription.policy.failoverDetail' : 'providers.subscription.policy.manualDetail')}
      >
        <Segmented
          ariaLabel={t('providers.subscription.policy.switchMode')}
          value={p.switchMode}
          onChange={(v) => save({ switchMode: v })}
          items={[
            { value: 'manual', label: t('providers.subscription.policy.manual') },
            { value: 'failover', label: t('providers.subscription.policy.failover') },
          ]}
        />
      </Row>
    </Group>
  );
}
