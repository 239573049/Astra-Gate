import { Layers, MoreHorizontal } from 'lucide-react';
import { useState } from 'react';

import { useApplyProfile, useDeleteProfile, useProfilePreview, useProfiles, useSaveProfile, useUpdateProfile } from '../api/hooks';
import type { Profile, ProfileAction, ProfileDrop, ProfileStep } from '../api/types';
import { useI18n } from '../i18n';
import { Alert } from './arc/alert/alert';
import { Badge, Button, Field, Input, Row, Spinner } from './ui/controls';
import { errorText, Menu, Select, Sheet, useFeedback } from './ui/overlays';

const ACTION_TONE: Record<ProfileAction, 'accent' | 'green' | 'orange' | 'red' | 'neutral'> = {
  enable: 'green', update: 'accent', disable: 'orange', store: 'neutral', unchanged: 'neutral', skipped: 'orange', failed: 'red',
};

/**
 * The profile switcher on the Clients page: a profile is a named snapshot of every configured client; applying one
 * switches them all through the regular enable / disable path, after a preview.
 */
export function ProfilesBar() {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const profiles = useProfiles();
  const save = useSaveProfile();
  const update = useUpdateProfile();
  const remove = useDeleteProfile();
  const list = profiles.data ?? [];
  const active = list.find((p) => p.active);
  const [picked, setPicked] = useState<string>('');
  const selected = list.find((p) => p.id === picked) ?? active ?? list[0];
  /** The name sheet: saving a new profile (id null) or renaming one. */
  const [naming, setNaming] = useState<{ id: string | null; name: string } | null>(null);
  const [previewId, setPreviewId] = useState<string | null>(null);

  const submitName = () => {
    if (!naming) return;
    const name = naming.name.trim();
    if (!name) return;
    const done = (p: Profile) => {
      setNaming(null);
      setPicked(p.id);
      toast(t('profiles.saved', { name: p.name }), 'success');
    };
    if (naming.id === null) save.mutate(name, { onSuccess: done, onError: (e) => toast(errorText(e), 'error') });
    else update.mutate({ id: naming.id, name }, { onSuccess: done, onError: (e) => toast(errorText(e), 'error') });
  };

  const overwrite = async (p: Profile) => {
    if (!(await confirm({ title: t('profiles.overwriteConfirm', { name: p.name }), confirmLabel: t('profiles.overwrite') }))) return;
    update.mutate({ id: p.id, overwrite: true }, { onSuccess: () => toast(t('profiles.saved', { name: p.name }), 'success'), onError: (e) => toast(errorText(e), 'error') });
  };

  const del = async (p: Profile) => {
    if (!(await confirm({ title: t('profiles.deleteConfirm', { name: p.name }), confirmLabel: t('profiles.delete'), destructive: true }))) return;
    remove.mutate(p.id, { onSuccess: () => setPicked(''), onError: (e) => toast(errorText(e), 'error') });
  };

  return (
    <div className="card mb-4 flex flex-wrap items-center gap-3 px-4 py-2.5">
      <Layers className="size-4 text-[var(--text-secondary)]" aria-hidden />
      <span className="text-[13px] font-medium">{t('profiles.title')}</span>
      {list.length === 0 ? (
        <span className="text-[12px] text-[var(--text-secondary)]">{t('profiles.none')}</span>
      ) : (
        <>
          <Select
            className="w-[220px]"
            ariaLabel={t('profiles.pick')}
            value={selected?.id ?? ''}
            onChange={setPicked}
            options={list.map((p) => ({ value: p.id, label: p.name, detail: t('profiles.clientCount', { count: p.clients.length }) }))}
          />
          {selected?.active && !selected.drifted && <Badge tone="green">{t('profiles.active')}</Badge>}
          {selected?.active && selected.drifted && (
            <Badge tone="orange" title={t('profiles.driftedHint')}>
              {t('profiles.active')} · {t('profiles.drifted')}
            </Badge>
          )}
        </>
      )}
      <div className="ml-auto flex items-center gap-2">
        {selected && (
          <Button variant="primary" size="sm" onClick={() => setPreviewId(selected.id)}>
            {t('profiles.apply')}
          </Button>
        )}
        <Button size="sm" onClick={() => setNaming({ id: null, name: '' })}>
          {t('profiles.save')}
        </Button>
        {selected && (
          <Menu
            label={t('profiles.title')}
            icon={<MoreHorizontal className="size-4" />}
            iconOnly
            items={[
              { id: 'overwrite', label: t('profiles.overwrite'), onSelect: () => void overwrite(selected) },
              { id: 'rename', label: t('profiles.rename'), onSelect: () => setNaming({ id: selected.id, name: selected.name }) },
              { id: 'delete', label: t('profiles.delete'), destructive: true, separatorBefore: true, onSelect: () => void del(selected) },
            ]}
          />
        )}
      </div>

      <Sheet
        open={naming !== null}
        onOpenChange={(o) => !o && setNaming(null)}
        width={420}
        title={naming?.id === null ? t('profiles.saveTitle') : t('profiles.rename')}
        description={naming?.id === null ? t('profiles.subtitle') : undefined}
        footer={
          <>
            <Button onClick={() => setNaming(null)}>{t('common.cancel')}</Button>
            <Button variant="primary" disabled={!naming?.name.trim()} loading={save.isPending || update.isPending} onClick={submitName}>
              {t('common.save')}
            </Button>
          </>
        }
      >
        <Field label={t('profiles.name')}>
          <Input
            autoFocus
            maxLength={64}
            value={naming?.name ?? ''}
            onChange={(e) => setNaming((cur) => (cur ? { ...cur, name: e.target.value } : cur))}
            onKeyDown={(e) => e.key === 'Enter' && submitName()}
          />
        </Field>
      </Sheet>
      <ProfilePreviewSheet profile={list.find((p) => p.id === previewId) ?? null} onClose={() => setPreviewId(null)} />
    </div>
  );
}

function stepDetail(step: ProfileStep, t: ReturnType<typeof useI18n>['t']): string {
  const parts: string[] = [];
  if (step.reason) {
    parts.push(step.action === 'failed' ? step.reason : t(`profiles.reason.${step.reason}` as 'profiles.reason.no-provider'));
  }
  if (step.changes.length > 0) parts.push(step.changes.map((c) => c.keyPath).join(', '));
  for (const n of step.notes) parts.push(n.startsWith('drifted:') ? t('profiles.drift', { key: n.slice('drifted:'.length) }) : n);
  return parts.join(' · ');
}

function dropText(d: ProfileDrop, t: ReturnType<typeof useI18n>['t']): string {
  const text = d.what === 'provider' ? t(`profiles.drop.provider.${d.reason}` as 'profiles.drop.provider.missing', { id: d.id }) : t(`profiles.drop.${d.what}` as 'profiles.drop.account', { id: d.id });
  return `${d.kind}: ${text}`;
}

/** What applying the profile would do, client by client (read-only), with the apply button; the outcome replaces it. */
function ProfilePreviewSheet({ profile, onClose }: { profile: Profile | null; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const preview = useProfilePreview(profile?.id, profile !== null);
  const apply = useApplyProfile();
  const steps = preview.data?.steps ?? [];
  const changing = steps.filter((s) => s.action !== 'unchanged');

  const run = () => {
    if (!profile) return;
    apply.mutate(profile.id, {
      onSuccess: (r) => {
        const skipped = r.steps.filter((s) => s.action === 'skipped' || s.action === 'failed').length;
        toast(skipped > 0 ? t('profiles.appliedPartial', { name: profile.name, count: skipped }) : t('profiles.applied', { name: profile.name }), skipped > 0 ? undefined : 'success');
        onClose();
      },
      onError: (e) => toast(errorText(e), 'error'),
    });
  };

  return (
    <Sheet
      open={profile !== null}
      onOpenChange={(o) => !o && onClose()}
      width={620}
      title={t('profiles.previewTitle', { name: profile?.name ?? '' })}
      description={t('profiles.previewDetail')}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!preview.data} loading={apply.isPending} onClick={run}>
            {t('profiles.apply')}
          </Button>
        </>
      }
    >
      {preview.isPending && <Spinner lines={4} />}
      {preview.isError && <Alert tone="danger" title={t('common.error')}>{errorText(preview.error)}</Alert>}
      {preview.data && (
        <div className="flex flex-col gap-4">
          {changing.length === 0 && <Alert tone="info" title={t('profiles.nothing')} />}
          <div className="card divide-y divide-[var(--border)]">
            {changing.map((s) => (
              <Row key={s.kind} label={s.name} detail={stepDetail(s, t) || undefined}>
                <Badge tone={ACTION_TONE[s.action]}>{t(`profiles.action.${s.action}` as 'profiles.action.enable')}</Badge>
              </Row>
            ))}
          </div>
          {preview.data.dropped.length > 0 && (
            <Alert tone="warning" title={t('profiles.dropped')}>
              {preview.data.dropped.map((d, i) => (
                <span key={i} className="block break-all">
                  {dropText(d, t)}
                </span>
              ))}
            </Alert>
          )}
        </div>
      )}
    </Sheet>
  );
}
