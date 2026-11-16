// Settings › Backup & sync › Automatic backups: a schedule, how many to keep, and the files in the local backup folder.

import { Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';

import { useAutoBackup, useDeleteAutoBackup, useRunAutoBackup, useSaveAutoBackup } from '../../api/hooks';
import { useI18n, type MessageKey } from '../../i18n';
import { Badge, Button, Group, Input, Row, Switch } from '../ui/controls';
import { errorText, useFeedback } from '../ui/overlays';
import { MIN_PASSPHRASE, formatSize, formatTime } from './shared';

export function AutoBackupPanel({ localOnly, onRestore }: { localOnly: boolean; onRestore: (name: string) => void }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const auto = useAutoBackup();
  const save = useSaveAutoBackup();
  const run = useRunAutoBackup();
  const del = useDeleteAutoBackup();
  const d = auto.data;

  const [hours, setHours] = useState('24');
  const [keep, setKeep] = useState('7');
  const [pass, setPass] = useState('');
  useEffect(() => {
    if (!d) return;
    setHours(String(d.hours));
    setKeep(String(d.keep));
  }, [d?.hours, d?.keep]); // eslint-disable-line react-hooks/exhaustive-deps

  const apply = (input: Parameters<typeof save.mutate>[0], done?: () => void) =>
    save.mutate(input, { onSuccess: done, onError: (e) => toast(errorText(e), 'error') });

  if (!d) return null;
  const hoursN = Number(hours);
  const keepN = Number(keep);
  const valid = Number.isInteger(hoursN) && hoursN >= 1 && Number.isInteger(keepN) && keepN >= 1;

  return (
    <Group title={t('backup.all.auto.title')} footer={localOnly ? t('backup.all.localOnly') : t('backup.all.auto.hint', { dir: d.dir })}>
      <Row label={t('backup.all.auto.enabled')}>
        <Switch checked={d.enabled} disabled={localOnly || save.isPending} label={t('backup.all.auto.enabled')} onChange={(v) => apply({ enabled: v })} />
      </Row>
      <div className="grid gap-3 p-4">
        <div className="flex flex-wrap items-center gap-3 text-[12.5px]">
          <label className="flex items-center gap-2">
            {t('backup.all.auto.every')}
            <Input className="w-20" type="number" min={1} aria-label={t('backup.all.auto.every')} value={hours} onChange={(e) => setHours(e.target.value)} />
            {t('backup.all.auto.hours')}
          </label>
          <label className="flex items-center gap-2">
            {t('backup.all.auto.keep')}
            <Input className="w-20" type="number" min={1} aria-label={t('backup.all.auto.keep')} value={keep} onChange={(e) => setKeep(e.target.value)} />
          </label>
          <Button size="sm" disabled={localOnly || !valid || save.isPending || (hoursN === d.hours && keepN === d.keep)} onClick={() => apply({ hours: hoursN, keep: keepN })}>
            {t('common.save')}
          </Button>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Input
            className="w-64"
            type="password"
            autoComplete="new-password"
            aria-label={t('backup.all.auto.passphrase')}
            placeholder={d.hasPassphrase ? t('backup.all.auto.passphraseSet') : t('backup.all.auto.passphrase')}
            value={pass}
            disabled={localOnly}
            onChange={(e) => setPass(e.target.value)}
          />
          <Button size="sm" disabled={localOnly || pass.length < MIN_PASSPHRASE || save.isPending} onClick={() => apply({ passphrase: pass }, () => setPass(''))}>
            {t('backup.all.auto.setPassphrase')}
          </Button>
          {d.hasPassphrase && (
            <Button size="sm" disabled={localOnly || save.isPending} onClick={() => apply({ clearPassphrase: true })}>
              {t('backup.all.auto.clearPassphrase')}
            </Button>
          )}
        </div>
        <p className="text-[11px] text-[var(--text-muted)]">{t('backup.all.auto.passphraseHint')}</p>
        <div>
          <Button
            variant="primary"
            disabled={localOnly || run.isPending}
            onClick={() => run.mutate(undefined, { onSuccess: () => toast(t('backup.all.auto.ran')), onError: (e) => toast(errorText(e), 'error') })}
          >
            {t('backup.all.auto.run')}
          </Button>
        </div>
      </div>
      {d.files.length === 0 ? (
        <p className="px-4 py-3 text-[12px] text-[var(--text-muted)]">{t('backup.all.auto.empty')}</p>
      ) : (
        d.files.map((f) => (
          <div key={f.name} className="flex flex-wrap items-center gap-2 px-4 py-2.5 text-[12.5px]">
            <span className="min-w-0 flex-1 truncate font-mono" title={f.name}>{f.name}</span>
            <Badge tone={f.kind === 'auto' ? 'neutral' : 'accent'}>{t(`backup.all.auto.kind.${f.kind}` as MessageKey)}</Badge>
            {f.encrypted && <Badge tone="orange">{t('backup.all.auto.encrypted')}</Badge>}
            <span className="text-[11px] text-[var(--text-muted)]">{formatTime(f.at)} · {formatSize(f.size)}</span>
            <Button size="sm" disabled={localOnly} onClick={() => onRestore(f.name)}>{t('backup.all.auto.restoreFrom')}</Button>
            <Button
              size="sm"
              icon={<Trash2 className="size-3.5" />}
              aria-label={t('common.delete')}
              disabled={localOnly || del.isPending}
              onClick={async () => {
                if (await confirm({ title: t('backup.all.auto.deleteConfirm', { name: f.name }), destructive: true, confirmLabel: t('common.delete') })) {
                  del.mutate(f.name, { onError: (e) => toast(errorText(e), 'error') });
                }
              }}
            />
          </div>
        ))
      )}
    </Group>
  );
}
