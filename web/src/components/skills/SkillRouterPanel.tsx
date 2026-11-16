import { useSetSkillRouter, useSkillRouter } from '../../api/hooks';
import { useI18n, type MessageKey } from '../../i18n';
import { Badge, Row, Switch } from '../ui/controls';
import { errorText, useFeedback } from '../ui/overlays';
import { clientName } from './clientNames';

/**
 * The skill router, per client and off by default: a client with it on gets one Skill, astra-skills, listing the library's Skills it has no link for
 * and opening one only when a task fits, so its prompt carries one description however many Skills there are.
 */
export function SkillRouterPanel({ canChange }: { canChange: boolean }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const router = useSkillRouter();
  const set = useSetSkillRouter();
  const clients = (router.data ?? []).filter((c) => c.supported || c.enabled);
  if (clients.length === 0) return null;
  return (
    <div className="mt-4">
      <div className="mb-1 px-1 text-[12px] font-medium">{t('skills.router.title')}</div>
      <p className="mb-2 px-1 text-[11px] text-[var(--text-muted)]">{t('skills.router.hint')}</p>
      <div className="card divide-y divide-[var(--border)]">
        {clients.map((c) => (
          <Row
            key={c.kind}
            label={
              <span className="flex items-center gap-2">
                {clientName(c.kind)}
                {c.enabled && <Badge tone="green">{t('skills.router.listed', { n: c.listed })}</Badge>}
              </span>
            }
            detail={c.entry ? <span className="font-mono text-[11px]">{c.entry}</span> : undefined}
          >
            <Switch
              checked={c.enabled}
              label={clientName(c.kind)}
              disabled={!canChange || set.isPending}
              onChange={(on) => set.mutate({ client: c.kind, on }, { onError: (e) => toast(errorText(e), 'error') })}
            />
          </Row>
        ))}
        {(router.data ?? [])
          .filter((c) => !c.supported && !c.enabled)
          .map((c) => (
            <Row key={c.kind} className="opacity-60" label={clientName(c.kind)} detail={c.why ? t(`skills.router.why.${c.why}` as MessageKey) : undefined} />
          ))}
      </div>
    </div>
  );
}
