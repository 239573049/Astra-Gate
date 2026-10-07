import { useNavigate, useParams } from 'react-router';

import { PrivacyEventLogs } from '../components/PrivacyEventLogs';
import { PrivacyOverview } from '../components/PrivacyOverview';
import { DryRunTester, PrivacyRulesPanel } from '../components/PrivacyGuardSettings';
import { Page } from '../components/layout/Page';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../components/arc/tabs/tabs';
import { useI18n, type MessageKey } from '../i18n';

const TABS = ['overview', 'logs', 'rules', 'tester'] as const;
type PrivacyTab = (typeof TABS)[number];

/** 隐私护栏：概览 / 拦截日志 / 检测规则 / 试用检测，四个子页面（/privacy/:tab）。 */
export function PrivacyPage() {
  const { t } = useI18n();
  const { tab } = useParams();
  const navigate = useNavigate();
  const active: PrivacyTab = TABS.includes(tab as PrivacyTab) ? (tab as PrivacyTab) : 'overview';

  return (
    <Page title={t('nav.privacy')} subtitle={t('settings.privacy.footer')}>
      <Tabs value={active} onValueChange={(v) => navigate(`/privacy/${v}`, { replace: true })}>
        <TabsList aria-label={t('nav.privacy')}>
          {TABS.map((id) => (
            <TabsTrigger key={id} value={id}>
              {t(`privacy.tab.${id}` as MessageKey)}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="overview" className="pt-5">
          <PrivacyOverview />
        </TabsContent>
        <TabsContent value="logs" className="pt-4">
          <PrivacyEventLogs />
        </TabsContent>
        <TabsContent value="rules" className="pt-5">
          <PrivacyRulesPanel />
        </TabsContent>
        <TabsContent value="tester" className="pt-5 [&>div]:mx-auto [&>div]:max-w-[680px]">
          <DryRunTester />
        </TabsContent>
      </Tabs>
    </Page>
  );
}
