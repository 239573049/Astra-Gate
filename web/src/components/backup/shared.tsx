// Shared pieces of the whole-installation "Backup & sync" settings area.

import type { ReactNode } from 'react';

import type { FullBackupItem, FullBackupSectionPlan } from '../../api/types';
import { useI18n, type MessageKey } from '../../i18n';
import { Checkbox } from '../arc/checkbox/checkbox';
import { Badge } from '../ui/controls';

/** Section ids a backup holds, in display order (`usage` is opt-in everywhere). */
export const BACKUP_SECTIONS = [
  'settings', 'prices', 'providers', 'signins', 'tokens', 'routing', 'privacy', 'tools', 'profiles', 'mcp', 'alerts', 'plugins', 'library', 'clients', 'usage',
] as const;
export const SECRET_SECTIONS = new Set(['signins']);
export const MIN_PASSPHRASE = 8;

export const sectionLabel = (t: (k: MessageKey) => string, id: string) => {
  const key = `backup.all.section.${id}` as MessageKey;
  const v = t(key);
  return v === key ? id : v;
};

/** Hands text to the browser as a file download. */
export function saveText(name: string, content: string) {
  const url = URL.createObjectURL(new Blob([content], { type: 'application/json' }));
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}

export function formatSize(n: number) {
  return n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`;
}

export function formatTime(iso?: string | null) {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

/** Checkboxes for sections. `counts` (from /info) shows how many documents each holds on this machine. */
export function SectionPicker({
  ids, picked, onChange, counts, disabled,
}: { ids: readonly string[]; picked: ReadonlySet<string>; onChange: (next: Set<string>) => void; counts?: Record<string, number>; disabled?: boolean }) {
  const { t } = useI18n();
  return (
    <div className="grid gap-x-4 gap-y-1.5 sm:grid-cols-2 md:grid-cols-3" role="group" aria-label={t('backup.all.sections')}>
      {ids.map((id) => (
        <label key={id} className="flex items-center gap-2 text-[12.5px]">
          <Checkbox
            checked={picked.has(id)}
            disabled={disabled}
            aria-label={sectionLabel(t, id)}
            onCheckedChange={(state) => {
              const next = new Set(picked);
              if (state === true) next.add(id);
              else next.delete(id);
              onChange(next);
            }}
          />
          <span>{sectionLabel(t, id)}</span>
          {counts && id in counts && <span className="text-[11px] text-[var(--text-muted)]">{counts[id]}</span>}
        </label>
      ))}
    </div>
  );
}

const ITEM_TONE = { added: 'green', changed: 'orange', removed: 'red', unchanged: 'neutral' } as const;

export function ItemList({ items, truncated }: { items: FullBackupItem[]; truncated?: boolean }) {
  const { t } = useI18n();
  return (
    <ul className="max-h-[220px] divide-y divide-[var(--border)] overflow-auto text-[12px]">
      {items.map((i) => (
        <li key={`${i.id}-${i.status}`} className="flex items-center gap-2 px-3 py-1.5">
          <Badge tone={ITEM_TONE[i.status] ?? 'neutral'}>{t(`backup.all.status.${i.status}` as MessageKey)}</Badge>
          <span className="min-w-0 truncate" title={i.id}>{i.label || i.id}</span>
        </li>
      ))}
      {truncated && <li className="px-3 py-1.5 text-[11px] text-[var(--text-muted)]">{t('backup.all.truncated')}</li>}
    </ul>
  );
}

export function PlanSection({ plan }: { plan: FullBackupSectionPlan }) {
  const { t } = useI18n();
  return (
    <div className="grid gap-1">
      <div className="flex flex-wrap items-center gap-2 text-[12.5px]">
        <span className="font-medium">{sectionLabel(t, plan.section)}</span>
        <span className="text-[var(--text-muted)]" data-testid={`plan-${plan.section}`}>
          {t('backup.all.counts', { added: plan.added, changed: plan.changed, removed: plan.removed, unchanged: plan.unchanged })}
        </span>
      </div>
      {plan.items.length > 0 && (
        <div className="card">
          <ItemList items={plan.items} truncated={plan.truncated} />
        </div>
      )}
    </div>
  );
}

export function SubHeading({ children }: { children: ReactNode }) {
  return <h4 className="text-[13px] font-medium">{children}</h4>;
}
