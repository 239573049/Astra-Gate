import type { Skill, SkillClient, SkillTarget } from '../../api/types';
import { useI18n } from '../../i18n';
import { cn } from '../../lib/cn';
import { clientName } from './clientNames';
import type { useSkillActions } from './useSkillActions';

type Look = 'on' | 'shared' | 'locked' | 'copy' | 'off' | 'disabled';

/** How one client's badge looks and behaves, from what the server says that client sees (state: own | via-shared | via-<kind> | off). */
export function badgeLook(view: SkillClient, target: SkillTarget | undefined): Look {
  if (!target?.supported || !target.detected) return view.state === 'off' ? 'disabled' : lookOf(view);
  return lookOf(view);
}

function lookOf(view: SkillClient): Look {
  if (view.state === 'off') return 'off';
  if (view.how === 'copy') return 'copy';
  if (view.state === 'own') return 'on';
  if (view.state === 'via-shared') return 'shared';
  return 'locked';
}

const LOOK: Record<Look, string> = {
  on: 'border-[var(--accent)] bg-[var(--accent-subtle)] text-[var(--accent)]',
  shared: 'border-[var(--accent)] bg-[var(--accent-subtle)] text-[var(--accent)]',
  locked: 'cursor-help border-dashed border-[var(--accent)] bg-transparent text-[var(--accent)]',
  copy: 'border-[var(--warning)] bg-transparent text-[var(--warning)]',
  off: 'border-[var(--border)] text-[var(--text-muted)] hover:border-[var(--text-muted)] hover:text-[var(--text-secondary)]',
  disabled: 'cursor-not-allowed border-[var(--border)] text-[var(--text-muted)] line-through opacity-40',
};

/** One chip per supported client: click to switch the Skill on or off for it. */
export function SkillBadges({ skill, targets, actions }: { skill: Skill; targets: SkillTarget[]; actions: ReturnType<typeof useSkillActions> }) {
  const { t } = useI18n();
  return (
    <div className="flex max-w-[380px] flex-wrap justify-end gap-1.5">
      {skill.clients.map((view) => {
        const target = targets.find((x) => x.kind === view.kind);
        const look = badgeLook(view, target);
        const name = clientName(view.kind);
        const via = view.state.startsWith('via-') && view.state !== 'via-shared' ? clientName(view.state.slice(4)) : '';
        const hint =
          look === 'shared' ? t('skills.badge.sharedHint')
          : look === 'locked' ? t('skills.badge.viaHint', { client: via })
          : look === 'copy' ? t('skills.badge.copyHint')
          : look === 'disabled' ? t(target?.supported ? 'skills.badge.notDetected' : 'skills.badge.unsupported')
          : undefined;
        const interactive = look === 'on' || look === 'shared' || look === 'copy' || look === 'off';
        return (
          <button
            key={view.kind}
            type="button"
            title={hint}
            disabled={look === 'disabled'}
            aria-pressed={look !== 'off' && look !== 'disabled'}
            className={cn('inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11.5px] transition-colors', LOOK[look])}
            onClick={interactive ? () => void actions.toggle(skill.name, view.kind, look === 'off') : undefined}
          >
            {name}
            {look === 'shared' && <i className="text-[10px] not-italic opacity-80">{t('skills.badge.shared')}</i>}
            {look === 'copy' && <i className="text-[10px] not-italic opacity-80">{view.modified ? `${t('skills.badge.copy')}·${t('skills.badge.modified')}` : t('skills.badge.copy')}</i>}
          </button>
        );
      })}
    </div>
  );
}
