import { useState } from 'react';

import { useLibraryProjects, useSwitchSkillProject } from '../../api/hooks';
import type { Skill, SkillLibrary } from '../../api/types';
import { useI18n } from '../../i18n';
import { Checkbox } from '../arc/checkbox/checkbox';
import { Badge, Button, EmptyState, Field, Row } from '../ui/controls';
import { errorText, Sheet, useFeedback } from '../ui/overlays';
import { clientName } from './clientNames';

/** Places one Skill inside a project directory: a link in the project's .claude/skills or .agents/skills, recorded with the project. */
export function SkillProjectSheet({ skill, library, onClose }: { skill: Skill | null; library: SkillLibrary; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const projects = useLibraryProjects();
  const place = useSwitchSkillProject();
  const [busy, setBusy] = useState<string | null>(null);
  const dirs = library.projectDirs ?? [];
  const on = (project: string, dir: string) => skill?.placements.some((p) => p.target === 'project' && p.project === project && p.path.endsWith(`${dir}${dir.includes('\\') ? '\\' : '/'}${skill.name}`)) ?? false;

  const toggle = (project: string, dir: string, value: boolean) => {
    if (!skill) return;
    setBusy(project + dir);
    place.mutate(
      { name: skill.name, project, dir, on: value },
      { onError: (e) => toast(errorText(e), 'error'), onSettled: () => setBusy(null) },
    );
  };

  return (
    <Sheet
      open={skill !== null}
      onOpenChange={(o) => !o && onClose()}
      width={560}
      title={t('skills.project.title', { skill: skill?.name ?? '' })}
      description={t('skills.project.hint')}
      footer={<Button onClick={onClose}>{t('common.close')}</Button>}
    >
      {(projects.data ?? []).length === 0 ? (
        <EmptyState title={t('skills.project.none')} />
      ) : (
        <div className="grid gap-3">
          {(projects.data ?? []).map((p) => (
            <Field key={p.path} label={p.name} hint={p.path}>
              {!p.exists && <Badge tone="orange">{t('library.state.project-missing')}</Badge>}
              <div className="grid gap-1">
                {dirs.map((d) => (
                  <Row key={d.dir} label={<span className="font-mono text-[12px]">{d.dir}</span>} detail={d.clients.map(clientName).join(', ')}>
                    <Checkbox
                      aria-label={`${p.name} ${d.dir}`}
                      checked={on(p.path, d.dir)}
                      disabled={busy === p.path + d.dir || (!p.exists && !on(p.path, d.dir))}
                      onCheckedChange={(s) => toggle(p.path, d.dir, s === true)}
                    />
                  </Row>
                ))}
              </div>
            </Field>
          ))}
        </div>
      )}
    </Sheet>
  );
}
