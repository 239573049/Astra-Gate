import { useEffect, useState } from 'react';

import { useMigrateSkills, useMigrateSkillsPreview } from '../../api/hooks';
import type { SkillMigratePreviewItem } from '../../api/types';
import { useI18n } from '../../i18n';
import { formatDateTime } from '../../lib/format';
import { Badge, Button, Field, Spinner } from '../ui/controls';
import { errorText, Sheet, useFeedback } from '../ui/overlays';

/** Previews what moving Skills into Astra will do (nothing changes until the button), and asks which copy to keep when they differ. */
export function SkillMigrateSheet({ open, onOpenChange, names, libraryDir }: { open: boolean; onOpenChange: (open: boolean) => void; names?: string[]; libraryDir: string }) {
  const { t, locale } = useI18n();
  const { toast } = useFeedback();
  const preview = useMigrateSkillsPreview();
  const migrate = useMigrateSkills();
  const [items, setItems] = useState<SkillMigratePreviewItem[]>([]);
  const [keep, setKeep] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setError(null);
    setKeep({});
    preview.mutate(names, { onSuccess: (list) => setItems(list), onError: (err) => setError(errorText(err)) });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, names]);

  // A conflict (copies that differ) must be settled before the Skill can move; a Skill the library already has is never a conflict.
  const unresolved = items.filter((i) => i.conflict && !keep[i.name]);
  const ready = items.filter((i) => !i.conflict || keep[i.name]);

  const submit = async () => {
    try {
      const outcomes = await migrate.mutateAsync(ready.map((i) => ({ name: i.name, keep: i.inLibrary ? 'library' : (keep[i.name] ?? null) })));
      const failed = outcomes.filter((o) => !o.ok);
      if (failed.length === 0) toast(t('skills.toast.migrated', { n: outcomes.length }), 'success');
      else toast(t('skills.toast.partial', { ok: outcomes.length - failed.length, failed: failed.length, reason: failed[0]?.reason ?? '' }), 'error');
      onOpenChange(false);
    } catch (err) {
      setError(errorText(err));
    }
  };

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('skills.migrate.title')}
      description={t('skills.migrate.lead')}
      width={560}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={ready.length === 0 || unresolved.length > 0 || migrate.isPending} onClick={() => void submit()}>
            {t('skills.migrate.submit', { n: ready.length })}
          </Button>
        </>
      }
    >
      {preview.isPending && <Spinner lines={3} />}
      {error && <p className="mb-3 text-[12px] text-[var(--danger)]">{error}</p>}
      <div className="flex flex-col gap-4">
        {items.map((item) => (
          <Field key={item.name} label={item.name}>
            <div className="card divide-y divide-[var(--border)]">
              {item.conflict ? (
                <>
                  <div className="px-3 py-2 text-[12px] text-[var(--text-secondary)]">{t('skills.migrate.keep')}</div>
                  {item.locations.map((l) => (
                    <label key={l.path} className="flex cursor-default items-start gap-3 px-3 py-2">
                      <input type="radio" name={`keep-${item.name}`} checked={keep[item.name] === l.path} onChange={() => setKeep((k) => ({ ...k, [item.name]: l.path }))} />
                      <span className="min-w-0">
                        <span className="block truncate font-mono text-[12px]">{l.path}</span>
                        <span className="text-[11px] text-[var(--text-muted)]">{t('skills.migrate.modified', { time: formatDateTime(l.modified, locale) })}</span>
                      </span>
                    </label>
                  ))}
                  <div className="px-3 py-2 text-[11px] text-[var(--text-muted)]">{t('skills.migrate.keepHint')}</div>
                </>
              ) : (
                item.locations.map((l) => (
                  <div key={l.path} className="flex items-center gap-3 px-3 py-2">
                    <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{l.path}</span>
                    <Badge tone="green">{item.inLibrary ? t('skills.found.inLibrary') : t('skills.migrate.noConflict')}</Badge>
                  </div>
                ))
              )}
              {item.inLibrary && <div className="px-3 py-2 text-[11px] text-[var(--text-muted)]">{t('skills.migrate.inLibrary')}</div>}
            </div>
          </Field>
        ))}
        {items.length > 0 && <p className="text-[11px] text-[var(--text-muted)]">{t('skills.migrate.backup', { dir: libraryDir.replace(/library[\\/]skills$/, 'backups/library') })}</p>}
      </div>
    </Sheet>
  );
}
