import { useState } from 'react';

import { ProjectsPanel } from '../components/library/ProjectsPanel';
import { ResourcePanel } from '../components/library/ResourcePanel';
import { Page } from '../components/layout/Page';
import { Segmented } from '../components/ui/controls';
import { useI18n } from '../i18n';
import type { ResourceSegment } from '../api/types';

type Tab = ResourceSegment | 'projects';

/**
 * The resource library beside Skills and MCP (/library): instructions, sub-agents, slash commands, hooks and environment variables, each kept once and
 * written into the clients' own files (plan resources-library; the model is magpie's library, MIT). Everything Astra writes is recorded, so only
 * what is still as Astra wrote it is ever taken back.
 */
export function LibraryPage() {
  const { t } = useI18n();
  const [tab, setTab] = useState<Tab>('instructions');
  return (
    <Page title={t('nav.library')} subtitle={t('library.subtitle')}>
      <div className="mb-3">
        <Segmented<Tab>
          ariaLabel={t('nav.library')}
          value={tab}
          onChange={setTab}
          items={[
            { value: 'instructions', label: t('library.tab.instruction') },
            { value: 'agents', label: t('library.tab.agent') },
            { value: 'commands', label: t('library.tab.command') },
            { value: 'hooks', label: t('library.tab.hook') },
            { value: 'env', label: t('library.tab.env') },
            { value: 'projects', label: t('library.tab.projects') },
          ]}
        />
      </div>
      {tab === 'projects' ? <ProjectsPanel /> : <ResourcePanel key={tab} segment={tab} />}
    </Page>
  );
}
