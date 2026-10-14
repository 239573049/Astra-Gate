import { useLocation, useNavigate, useParams } from 'react-router';

import { RecentRequests, RequestTraceView } from '../components/traces/request';
import { SessionsList, SessionView } from '../components/traces/session';
import { TopologyView } from '../components/traces/topology';
import { Page } from '../components/layout/Page';
import { Segmented } from '../components/ui/controls';
import { useI18n } from '../i18n';

type Tab = 'topology' | 'sessions' | 'requests';

/**
 * 链路追踪 (.plan/observability.md §5): Agent → Astra → provider. Topology (the whole traffic picture), sessions (an agent's
 * turns as a canvas or a timeline) and single requests (the request and response legs through the gateway).
 */
export function TracesPage() {
  const { t } = useI18n();
  const { key, id } = useParams<{ key?: string; id?: string }>();
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const tab: Tab = pathname.startsWith('/traces/sessions') ? 'sessions' : pathname.startsWith('/traces/requests') ? 'requests' : 'topology';

  return (
    <Page
      title={t('nav.traces')}
      subtitle={t('traces.subtitle')}
      actions={
        <Segmented
          ariaLabel={t('traces.tab.label')}
          value={tab}
          onChange={(v) => navigate(v === 'topology' ? '/traces' : `/traces/${v}`)}
          items={[
            { value: 'topology', label: t('traces.tab.topology') },
            { value: 'sessions', label: t('traces.tab.sessions') },
            { value: 'requests', label: t('traces.tab.requests') },
          ]}
        />
      }
      contentClassName="flex flex-col overflow-hidden [mask-image:none] pt-2! pb-3!"
    >
      {tab === 'topology' ? (
        <TopologyView />
      ) : tab === 'sessions' ? (
        key ? (
          <SessionView key={key} sessionKey={key} />
        ) : (
          <div className="min-h-0 flex-1 overflow-auto">
            <SessionsList />
          </div>
        )
      ) : id ? (
        <RequestTraceView key={id} id={id} />
      ) : (
        <div className="min-h-0 flex-1 overflow-auto">
          <RecentRequests />
        </div>
      )}
    </Page>
  );
}
