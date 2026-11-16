import { Page } from '../components/layout/Page';
import { ToolsSettings } from '../components/ToolsSettings';
import { useI18n } from '../i18n';

/** 工具：联网搜索、网页读取、图片生成和插件工具（/tools）。 */
export function ToolsPage() {
  const { t } = useI18n();
  return (
    <Page title={t('nav.tools')} subtitle={t('tools.subtitle')}>
      <ToolsSettings />
    </Page>
  );
}
