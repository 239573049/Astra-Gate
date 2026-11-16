import { Ellipsis, Plus, RefreshCw, Sparkles } from 'lucide-react';
import { useMemo, useState } from 'react';

import { useSkillLibrary, useSkillUpdates } from '../api/hooks';
import type { FoundSkill, Skill, SkillTarget, SkillUpdate } from '../api/types';
import { SkillMarketTab } from '../components/skills/SkillMarketTab';
import { SkillProjectSheet } from '../components/skills/SkillProjectSheet';
import { SkillRouterPanel } from '../components/skills/SkillRouterPanel';
import { SkillMigrateSheet } from '../components/skills/SkillMigrateSheet';
import { SkillInstallSheet } from '../components/skills/SkillInstallSheet';
import { SkillBadges } from '../components/skills/SkillBadges';
import { useSkillActions } from '../components/skills/useSkillActions';
import { Alert } from '../components/arc/alert/alert';
import { Checkbox } from '../components/arc/checkbox/checkbox';
import { Page } from '../components/layout/Page';
import { Badge, Button, EmptyState, Row, SearchField, Segmented, Spinner } from '../components/ui/controls';
import { errorText, Menu, useFeedback } from '../components/ui/overlays';
import { useI18n } from '../i18n';

type Tab = 'managed' | 'market' | 'found';

/** Skills (/skills): the library Astra keeps, and which clients see each Skill through a link (plan resources-library). */
export function SkillsPage() {
  const { t } = useI18n();
  const library = useSkillLibrary();
  const [tab, setTab] = useState<Tab>('managed');
  const [query, setQuery] = useState('');
  const [installOpen, setInstallOpen] = useState(false);
  const [migrateOpen, setMigrateOpen] = useState(false);
  const [preselect, setPreselect] = useState<string[] | undefined>(undefined);
  const actions = useSkillActions();
  const check = useSkillUpdates();
  const { toast } = useFeedback();
  const [projectSkill, setProjectSkill] = useState<Skill | null>(null);
  const updates = useMemo(() => new Map((check.data ?? []).map((u) => [u.name, u] as const)), [check.data]);

  const data = library.data;
  const unmanaged = (data?.found ?? []).filter((f) => f.kind === 'unmanaged');
  const external = (data?.found ?? []).filter((f) => f.kind === 'external');
  const skills = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (data?.skills ?? []).filter((s) => !q || s.name.toLowerCase().includes(q) || s.description.toLowerCase().includes(q));
  }, [data, query]);

  return (
    <Page
      title={t('nav.skills')}
      subtitle={t('skills.subtitle')}
      actions={
        <>
          <Button
            variant="glass"
            icon={<RefreshCw className="size-4" />}
            loading={check.isPending}
            disabled={!data || data.canCreate === false || !data.skills.some((s) => s.sourceKind === 'git')}
            onClick={() =>
              check.mutate(undefined, {
                onSuccess: (r) => toast(r.some((u) => u.behind) ? t('skills.updates.found', { n: r.filter((u) => u.behind).length }) : t('skills.updates.none'), 'success'),
                onError: (e) => toast(errorText(e), 'error'),
              })
            }
          >
            {t('skills.updates.check')}
          </Button>
          <Button variant="glass" onClick={() => void actions.returnAll()} disabled={!data || data.skills.length === 0}>
            {t('skills.returnAll')}
          </Button>
          <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => setInstallOpen(true)} disabled={data?.canCreate === false}>
            {t('skills.install')}
          </Button>
        </>
      }
    >
      {library.isError && <Alert tone="danger" title={`${t('skills.error.load')}: ${errorText(library.error)}`} />}
      {library.isLoading && <Spinner lines={4} />}
      {data && data.canCreate === false && <Alert tone="info" title={t('skills.install.disabled')} />}
      {data && unmanaged.length > 0 && tab === 'managed' && (
        <div className="card mb-3 flex items-center gap-3 border-[var(--warning)] px-4 py-2 text-[12px]">
          <span className="min-w-0 flex-1">{t('skills.foundBanner', { n: unmanaged.length })}</span>
          <Button size="sm" onClick={() => setTab('found')}>
            {t('skills.foundBannerAction')}
          </Button>
        </div>
      )}

      {data && (
        <div className="mb-3 flex flex-wrap items-center gap-2">
          <Segmented<Tab>
            value={tab}
            onChange={setTab}
            items={[
              { value: 'managed', label: `${t('skills.tab.managed')} · ${data.skills.length}` },
              { value: 'market', label: t('skills.tab.market') },
              { value: 'found', label: `${t('skills.tab.found')} · ${data.found.length}` },
            ]}
          />
          {tab === 'managed' && <SearchField className="ml-auto w-56" value={query} onChange={setQuery} placeholder={t('skills.search')} />}
        </div>
      )}

      {data && tab === 'managed' && (
        <>
          {data.skills.length === 0 ? (
            <EmptyState
              icon={<Sparkles className="size-6" />}
              title={t('skills.empty.title')}
              detail={t('skills.empty.detail')}
              action={
                data.canCreate ? (
                  <Button variant="primary" onClick={() => setInstallOpen(true)}>
                    {t('skills.install')}
                  </Button>
                ) : undefined
              }
            />
          ) : (
            <div className="card divide-y divide-[var(--border)]">
              {skills.map((s) => (
                <SkillRow key={s.name} skill={s} targets={data.targets} actions={actions} update={updates.get(s.name)} canPlace={data.canCreate && (data.projectDirs ?? []).length > 0} onPlace={() => setProjectSkill(s)} />
              ))}
            </div>
          )}
          <Legend />
          <SkillRouterPanel canChange={data.canCreate} />
        </>
      )}

      {data && tab === 'market' && <SkillMarketTab library={data} />}

      {data && tab === 'found' && (
        <FoundList
          unmanaged={unmanaged}
          external={external}
          onMigrate={(names) => {
            setPreselect(names);
            setMigrateOpen(true);
          }}
        />
      )}

      {data && <SkillInstallSheet open={installOpen} onOpenChange={setInstallOpen} library={data} />}
      {data && <SkillProjectSheet skill={projectSkill} library={data} onClose={() => setProjectSkill(null)} />}
      {data && <SkillMigrateSheet open={migrateOpen} onOpenChange={setMigrateOpen} names={preselect} libraryDir={data.libraryDir} />}
    </Page>
  );
}

function SkillRow({
  skill,
  targets,
  actions,
  update,
  canPlace,
  onPlace,
}: {
  skill: Skill;
  targets: SkillTarget[];
  actions: ReturnType<typeof useSkillActions>;
  update?: SkillUpdate;
  canPlace: boolean;
  onPlace: () => void;
}) {
  const { t } = useI18n();
  const modified = skill.placements.some((p) => p.modified);
  const sourceTone = skill.sourceKind === 'git' ? 'green' : 'neutral';
  const source = skill.sourceJson ? sourceText(skill.sourceJson) : '';
  const inProjects = skill.placements.filter((p) => p.target === 'project');
  return (
    <Row
      label={
        <span className="flex items-center gap-2">
          <span className="font-medium">{skill.name}</span>
          <Badge tone={sourceTone}>{t(`skills.source.${skill.sourceKind}` as const)}</Badge>
          {modified && <Badge tone="orange">{t('skills.copyModified')}</Badge>}
          {update?.behind && <Badge tone="orange">{t('skills.updates.behind')}</Badge>}
          {update && !update.behind && !update.error && <Badge tone="green">{t('skills.updates.current')}</Badge>}
          {update?.error && <Badge tone="red" title={update.error}>{t('skills.updates.error')}</Badge>}
        </span>
      }
      detail={
        <>
          <span>{skill.description || '—'}</span>
          {source && <span className="ml-2 text-[var(--text-muted)]">{source}</span>}
          {skill.placements.length === 0 && <span className="ml-2 text-[var(--text-muted)]">· {t('skills.noPlacements')}</span>}
          {inProjects.map((p) => (
            <span key={p.path} className="ml-2 text-[var(--text-muted)]" title={p.path}>
              · {t('skills.badge.project', { name: (p.project ?? '').split(/[\\/]/).filter(Boolean).pop() ?? '' })}{p.projectMissing ? ` (${t('library.state.project-missing')})` : ''}
            </span>
          ))}
        </>
      }
      className="items-start py-3"
    >
      <SkillBadges skill={skill} targets={targets} actions={actions} />
      <Menu
        label=""
        iconOnly
        icon={<Ellipsis className="size-4" />}
        items={[
          ...(update?.behind ? [{ id: 'update', label: t('skills.updates.update'), onSelect: () => void actions.update(skill.name, update.edited) }] : []),
          ...(canPlace ? [{ id: 'project', label: t('skills.project.menu'), onSelect: onPlace }] : []),
          ...(modified
            ? [
                { id: 'take', label: t('skills.menu.take'), onSelect: () => void actions.takeEdits(skill.name) },
                { id: 'over', label: t('skills.menu.overwrite'), onSelect: () => void actions.overwriteCopy(skill.name) },
              ]
            : []),
          { id: 'return', label: t('skills.menu.return'), disabled: skill.placements.length === 0, separatorBefore: modified, onSelect: () => void actions.giveBack(skill.name) },
          { id: 'delete', label: t('skills.menu.delete'), destructive: true, onSelect: () => void actions.remove(skill.name) },
        ]}
      />
    </Row>
  );
}

/** A short, human reading of a Skill's source: the repository (and commit), or the folder it was copied from. */
function sourceText(json: string): string {
  try {
    const o = JSON.parse(json) as { url?: string; commit?: string; path?: string; from?: string };
    if (o.url) return `${o.url.replace(/^https?:\/\//, '').replace(/\.git$/, '')}${o.commit ? ` · ${o.commit.slice(0, 7)}` : ''}`;
    return o.path ?? o.from ?? '';
  } catch {
    return '';
  }
}

function Legend() {
  const { t } = useI18n();
  return (
    <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 px-1 text-[11px] text-[var(--text-muted)]">
      <span>{t('skills.legend.on')}</span>
      <span>{t('skills.legend.shared')}</span>
      <span>{t('skills.legend.copy')}</span>
      <span>{t('skills.legend.off')}</span>
    </div>
  );
}

function FoundList({ unmanaged, external, onMigrate }: { unmanaged: FoundSkill[]; external: FoundSkill[]; onMigrate: (names: string[]) => void }) {
  const { t } = useI18n();
  // A Skill the skills CLI manages is not selected by default: migrating it would let `skills update` write into Astra's copy.
  const [selected, setSelected] = useState<Set<string>>(() => new Set(unmanaged.filter((f) => !f.skillsCli).map((f) => f.name)));
  const toggle = (name: string, on: boolean) =>
    setSelected((cur) => {
      const next = new Set(cur);
      if (on) next.add(name);
      else next.delete(name);
      return next;
    });

  if (unmanaged.length === 0 && external.length === 0) return <EmptyState title={t('skills.found.empty')} />;
  return (
    <>
      <div className="mb-2 flex items-center gap-2 px-1">
        <span className="text-[12px] text-[var(--text-secondary)]">{t('skills.found.selected', { n: selected.size, total: unmanaged.length })}</span>
        <Button className="ml-auto" variant="primary" disabled={selected.size === 0} onClick={() => onMigrate([...selected])}>
          {t('skills.found.migrate')}
        </Button>
      </div>
      <div className="card divide-y divide-[var(--border)]">
        {unmanaged.map((f) => (
          <Row
            key={f.name}
            icon={<Checkbox checked={selected.has(f.name)} onCheckedChange={(s) => toggle(f.name, s === true)} aria-label={f.name} />}
            label={
              <span className="flex items-center gap-2">
                <span className="font-medium">{f.name}</span>
                <Badge>{t('skills.found.kind.unmanaged')}</Badge>
                {f.conflict && <Badge tone="orange">{t('skills.found.differs')}</Badge>}
                {f.inLibrary && <Badge tone="green">{t('skills.found.inLibrary')}</Badge>}
                {f.skillsCli && <Badge tone="orange" title={t('skills.found.skillsCliHint')}>{t('skills.found.skillsCli')}</Badge>}
              </span>
            }
            detail={<span className="font-mono">{f.locations.map((l) => l.path).join(' · ')}</span>}
          >
            <span className="text-[11px] text-[var(--text-muted)]">{t('skills.found.places', { n: f.locations.length })}</span>
          </Row>
        ))}
        {external.map((f) => (
          <Row
            key={f.name + f.locations[0]?.path}
            className="opacity-70"
            label={
              <span className="flex items-center gap-2">
                <span className="font-medium">{f.name}</span>
                <Badge>{t('skills.found.kind.external')}</Badge>
              </span>
            }
            detail={
              <>
                <span className="font-mono">{f.locations[0]?.path} → {f.linkTarget}</span>
                <span className="ml-2">{t('skills.found.externalHint')}</span>
              </>
            }
          />
        ))}
      </div>
    </>
  );
}
