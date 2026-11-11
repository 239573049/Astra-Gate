import { useDeleteSkill, useReturnSkills, useSkillCopyAction, useSwitchSkill } from '../../api/hooks';
import type { SkillOutcome } from '../../api/types';
import { useI18n } from '../../i18n';
import { errorText, useFeedback } from '../ui/overlays';
import { clientName } from './clientNames';

/** What the Skills page does to a Skill, with the confirmations and toasts each one needs. */
export function useSkillActions() {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const switchSkill = useSwitchSkill();
  const giveBackMut = useReturnSkills();
  const removeMut = useDeleteSkill();
  const copyAction = useSkillCopyAction();

  const report = (outcomes: SkillOutcome[], successKey: 'skills.toast.returned' | 'skills.toast.done' = 'skills.toast.returned') => {
    const failed = outcomes.filter((o) => !o.ok);
    if (failed.length === 0) toast(t(successKey), 'success');
    else toast(t('skills.toast.partial', { ok: outcomes.length - failed.length, failed: failed.length, reason: failed[0]?.reason ?? '' }), 'error');
  };

  const attempt = async (run: () => Promise<unknown>) => {
    try {
      await run();
    } catch (err) {
      toast(errorText(err), 'error');
    }
  };

  return {
    /** Switches a client on or off; switching off a link the client shares with others asks first. */
    async toggle(skill: string, client: string, on: boolean) {
      await attempt(async () => {
        let result = await switchSkill.mutateAsync({ name: skill, client, on });
        if (result.needsConfirm) {
          const ok = await confirm({
            title: t('skills.confirm.sharedOff.title', { client: clientName(client) }),
            detail: t('skills.confirm.sharedOff.detail', { client: clientName(client), skill, others: result.affected.map(clientName).join('、') }),
            confirmLabel: t('skills.confirm.sharedOff.confirm'),
            destructive: true,
          });
          if (!ok) return;
          result = await switchSkill.mutateAsync({ name: skill, client, on, confirm: true });
        }
      });
    },
    async giveBack(name: string) {
      if (!(await confirm({ title: t('skills.confirm.return.title', { skill: name }), detail: t('skills.confirm.return.detail'), confirmLabel: t('skills.menu.return') }))) return;
      await attempt(async () => report(await giveBackMut.mutateAsync(name)));
    },
    async returnAll() {
      if (!(await confirm({ title: t('skills.confirm.returnAll.title'), detail: t('skills.confirm.returnAll.detail'), confirmLabel: t('skills.returnAll') }))) return;
      await attempt(async () => report(await giveBackMut.mutateAsync(null)));
    },
    async remove(name: string) {
      if (!(await confirm({ title: t('skills.confirm.delete.title', { skill: name }), detail: t('skills.confirm.delete.detail'), confirmLabel: t('common.delete'), destructive: true }))) return;
      await attempt(async () => {
        await removeMut.mutateAsync(name);
        toast(t('skills.toast.deleted'), 'success');
      });
    },
    async takeEdits(name: string) {
      if (!(await confirm({ title: t('skills.confirm.take.title'), detail: t('skills.confirm.take.detail'), confirmLabel: t('skills.menu.take') }))) return;
      await attempt(async () => {
        await copyAction.mutateAsync({ name, action: 'take-edits' });
        toast(t('skills.toast.done'), 'success');
      });
    },
    async overwriteCopy(name: string) {
      if (!(await confirm({ title: t('skills.confirm.overwrite.title'), detail: t('skills.confirm.overwrite.detail'), confirmLabel: t('skills.menu.overwrite') }))) return;
      await attempt(async () => {
        await copyAction.mutateAsync({ name, action: 'overwrite-copy' });
        toast(t('skills.toast.done'), 'success');
      });
    },
  };
}
