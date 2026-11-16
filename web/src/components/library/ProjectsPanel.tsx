// The project directories the library can place things into (instructions, sub-agents, commands, Skills, MCP servers). A project that no longer
// exists is shown as missing — what Astra wrote there is reported, never a failure — and it can be forgotten once nothing of Astra's is placed in it.

import { FolderOpen, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';

import { useAddLibraryProject, useLibraryProjects, useRemoveLibraryProject } from '../../api/hooks';
import { useI18n } from '../../i18n';
import { Alert } from '../arc/alert/alert';
import { Badge, Button, EmptyState, Input, Row, Spinner } from '../ui/controls';
import { errorText, useFeedback } from '../ui/overlays';

export function ProjectsPanel() {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const projects = useLibraryProjects();
  const add = useAddLibraryProject();
  const remove = useRemoveLibraryProject();
  const [path, setPath] = useState('');

  const submit = () =>
    add.mutate(
      { path: path.trim() },
      {
        onSuccess: (p) => {
          setPath('');
          toast(t('library.projects.added', { name: p.name }), 'success');
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );

  const forget = async (p: string, name: string) => {
    if (!(await confirm({ title: t('library.projects.forget.title', { name }), detail: t('library.projects.forget.detail'), confirmLabel: t('library.projects.forget') }))) return;
    remove.mutate(p, { onError: (e) => toast(errorText(e), 'error') });
  };

  return (
    <>
      <p className="mb-3 text-[12px] text-[var(--text-secondary)]">{t('library.projects.hint')}</p>
      <div className="mb-3 flex items-center gap-2">
        <Input mono className="flex-1" value={path} placeholder="/Users/me/code/app" onChange={(e) => setPath(e.target.value)} aria-label={t('library.projects.path')} />
        <Button variant="primary" icon={<Plus className="size-4" />} disabled={path.trim() === ''} loading={add.isPending} onClick={submit}>
          {t('library.projects.add')}
        </Button>
      </div>
      {projects.isError && <Alert tone="danger" title={errorText(projects.error)} />}
      {projects.isLoading && <Spinner lines={2} />}
      {projects.data && projects.data.length === 0 && <EmptyState icon={<FolderOpen className="size-6" />} title={t('library.projects.empty')} />}
      {projects.data && projects.data.length > 0 && (
        <div className="card divide-y divide-[var(--border)]">
          {projects.data.map((p) => (
            <Row
              key={p.path}
              label={
                <span className="flex items-center gap-2">
                  <span className="font-medium">{p.name}</span>
                  {!p.exists && <Badge tone="orange">{t('library.state.project-missing')}</Badge>}
                </span>
              }
              detail={<span className="font-mono text-[11px]">{p.path}</span>}
            >
              <Button size="sm" icon={<Trash2 className="size-3.5" />} aria-label={t('library.projects.forget')} onClick={() => void forget(p.path, p.name)} />
            </Row>
          ))}
        </div>
      )}
    </>
  );
}
