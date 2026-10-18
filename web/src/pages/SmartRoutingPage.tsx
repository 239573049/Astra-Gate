import { Page } from '../components/layout/Page';
import { SmartRoutingSettings } from '../components/SmartRoutingSettings';
import { useI18n } from '../i18n';

/** 智能路由：判定模型 / 档位与模型 / 路由规则（/smart-routing）。 */
export function SmartRoutingPage() {
  const { t } = useI18n();
  return (
    <Page title={t('nav.smartRouting')} subtitle={t('smartRouting.subtitle')}>
      <SmartRoutingSettings />
    </Page>
  );
}
