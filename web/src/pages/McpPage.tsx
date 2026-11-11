import { McpGateway } from '../components/McpGateway';
import { Page } from '../components/layout/Page';
import { useI18n } from '../i18n';

/** MCP 网关：把多个 MCP 服务器聚合成一个地址，按令牌授权（/mcp，GitHub issue #29）。 */
export function McpPage() {
  const { t } = useI18n();
  return (
    <Page title={t('nav.mcp')} subtitle={t('mcp.subtitle')}>
      <McpGateway />
    </Page>
  );
}
