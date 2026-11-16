import { Page } from '../components/layout/Page';
import { RoutingGroups } from '../components/RoutingGroups';
import { useI18n } from '../i18n';

/** 路由分组：把几个模型合成一个 group/<名称>，按额度、顺序、轮换、权重等在成员之间分配（/routing-groups）。 */
export function RoutingGroupsPage() {
  const { t } = useI18n();
  return (
    <Page title={t('nav.routingGroups')} subtitle={t('routingGroups.subtitle')}>
      <RoutingGroups />
    </Page>
  );
}
