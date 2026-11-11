import { useEffect, useState } from 'react';

import { useInstallSkills, useProbeSkills } from '../../api/hooks';
import type { SkillCandidate, SkillLibrary, SkillProbeResult } from '../../api/types';
import { useI18n } from '../../i18n';
import { cn } from '../../lib/cn';
import { Checkbox } from '../arc/checkbox/checkbox';
import { Badge, Button, Field, Input, Segmented } from '../ui/controls';
import { errorText, Sheet, useFeedback } from '../ui/overlays';
import { clientName } from './clientNames';

type SourceKind = 'git' | 'local';

/** Installs Skills from a Git repository or a folder: look into the source first, pick, then install and optionally switch clients on. */
export function SkillInstallSheet({ open, onOpenChange, library }: { open: boolean; onOpenChange: (open: boolean) => void; library: SkillLibrary }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const probe = useProbeSkills();
  const install = useInstallSkills();
  const [kind, setKind] = useState<SourceKind>('git');
  const [source, setSource] = useState('');
  const [ref, setRef] = useState('');
  const [found, setFound] = useState<SkillProbeResult | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [clients, setClients] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (open) return;
    setFound(null);
    setPicked(new Set());
    setClients(new Set());
    setError(null);
  }, [open]);

  const installable = (c: SkillCandidate) => c.status === 'new' || c.status === 'exists';
  const detected = library.targets.filter((x) => x.supported && x.detected);

  const look = async () => {
    setError(null);
    setFound(null);
    try {
      const result = await probe.mutateAsync({ kind, source: source.trim(), ref: kind === 'git' && ref.trim() ? ref.trim() : undefined });
      setFound(result);
      setPicked(new Set(result.skills.filter((c) => c.status === 'new').map((c) => c.name)));
    } catch (err) {
      setError(errorText(err));
    }
  };

  const submit = async () => {
    if (!found) return;
    try {
      const outcomes = await install.mutateAsync({ probeId: found.probeId, names: [...picked], clients: [...clients] });
      const failed = outcomes.filter((o) => !o.ok);
      if (failed.length === 0) toast(t('skills.toast.installed', { n: outcomes.length }), 'success');
      else toast(t('skills.toast.partial', { ok: outcomes.length - failed.length, failed: failed.length, reason: failed[0]?.reason ?? '' }), 'error');
      onOpenChange(false);
    } catch (err) {
      setError(errorText(err));
    }
  };

  const toggle = (set: Set<string>, name: string, on: boolean) => {
    const next = new Set(set);
    if (on) next.add(name);
    else next.delete(name);
    return next;
  };

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('skills.install.title')}
      description={t('skills.install.localOnly')}
      width={520}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!found || picked.size === 0 || install.isPending} onClick={() => void submit()}>
            {t('skills.install.submit', { n: picked.size })}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <Segmented<SourceKind>
          value={kind}
          onChange={(k) => {
            setKind(k);
            setFound(null);
          }}
          items={[
            { value: 'git', label: t('skills.install.kind.git') },
            { value: 'local', label: t('skills.install.kind.local') },
          ]}
        />
        <Field label={t(kind === 'git' ? 'skills.install.repo' : 'skills.install.dir')} hint={kind === 'git' ? t('skills.install.repoHint') : undefined} error={error ?? undefined}>
          <Input mono value={source} onChange={(e) => setSource(e.target.value)} placeholder={kind === 'git' ? 'https://github.com/owner/skills' : '/Users/me/skills'} />
        </Field>
        {kind === 'git' && (
          <Field label={t('skills.install.ref')}>
            <Input value={ref} onChange={(e) => setRef(e.target.value)} placeholder={t('skills.install.refPlaceholder')} />
          </Field>
        )}
        <div>
          <Button onClick={() => void look()} disabled={source.trim() === '' || probe.isPending}>
            {t('skills.install.probe')}
          </Button>
        </div>

        {found && (
          <>
            <Field label={t('skills.install.found', { n: found.skills.length })}>
              <div className="card divide-y divide-[var(--border)]">
                {found.skills.map((c) => (
                  <div key={c.name} className={cn('flex items-start gap-3 px-3 py-2', !installable(c) && 'opacity-60')}>
                    <Checkbox
                      checked={picked.has(c.name)}
                      disabled={!installable(c)}
                      onCheckedChange={(s) => setPicked((cur) => toggle(cur, c.name, s === true))}
                      aria-label={c.name}
                    />
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2 text-[13px] font-medium">
                        {c.name}
                        {c.status !== 'new' && (
                          <Badge tone={c.status === 'exists' ? 'green' : 'orange'}>
                            {t(`skills.status.${c.status}` as const)}
                            {c.reason ? ` · ${t(`skills.reason.${c.reason === 'size' ? 'size' : 'name'}` as const)}` : ''}
                          </Badge>
                        )}
                      </div>
                      <div className="text-[11px] text-[var(--text-secondary)]">{c.description}</div>
                    </div>
                  </div>
                ))}
              </div>
            </Field>
            <Field label={t('skills.install.apply')}>
              <div className="flex flex-wrap gap-x-4 gap-y-2">
                {detected.map((x) => (
                  <Checkbox
                    key={x.kind}
                    label={clientName(x.kind) + (x.readsShared ? ` · ${t('skills.badge.shared')}` : '')}
                    checked={clients.has(x.kind)}
                    onCheckedChange={(s) => setClients((cur) => toggle(cur, x.kind, s === true))}
                  />
                ))}
              </div>
            </Field>
          </>
        )}
      </div>
    </Sheet>
  );
}
