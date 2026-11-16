// Settings › Backup & sync › Back up: export the whole installation as one file. Without a passphrase the file holds no secret at all.

import { Download } from 'lucide-react';
import { useState } from 'react';

import { useExportFullBackup, useFullBackupInfo } from '../../api/hooks';
import { useI18n } from '../../i18n';
import { Alert } from '../arc/alert/alert';
import { Checkbox } from '../arc/checkbox/checkbox';
import { Button, Group, Input } from '../ui/controls';
import { errorText, useFeedback } from '../ui/overlays';
import { BACKUP_SECTIONS, MIN_PASSPHRASE, saveText, SectionPicker } from './shared';

export function BackupPanel({ localOnly }: { localOnly: boolean }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const info = useFullBackupInfo();
  const exportBackup = useExportFullBackup();
  const [picked, setPicked] = useState<Set<string>>(() => new Set(BACKUP_SECTIONS.filter((s) => s !== 'usage')));
  const [keys, setKeys] = useState(false);
  const [pass, setPass] = useState('');
  const [error, setError] = useState<string | null>(null);

  const withKeys = keys && !localOnly;
  const blocked = exportBackup.isPending || picked.size === 0 || (withKeys && pass.length < MIN_PASSPHRASE);

  const run = () => {
    setError(null);
    exportBackup.mutate(
      {
        sections: BACKUP_SECTIONS.filter((s) => picked.has(s) && s !== 'usage'),
        includeUsage: picked.has('usage'),
        includeKeys: withKeys,
        passphrase: withKeys ? pass : undefined,
      },
      {
        onSuccess: (r) => {
          saveText(r.name, r.content);
          toast(t('backup.all.export.done', { count: r.documents }));
          setPass('');
        },
        onError: (e) => setError(errorText(e)),
      },
    );
  };

  return (
    <Group title={t('backup.all.export.title')} footer={t('backup.all.export.hint')}>
      <div className="grid gap-3 p-4">
        <SectionPicker ids={BACKUP_SECTIONS} picked={picked} onChange={setPicked} counts={info.data?.sections} />
        <label className="flex items-center gap-2 text-[12.5px]">
          <Checkbox checked={withKeys} disabled={localOnly} aria-label={t('backup.all.export.keys')} onCheckedChange={(s) => setKeys(s === true)} />
          <span>{t('backup.all.export.keys')}</span>
        </label>
        {localOnly && <p className="text-[11px] text-[var(--text-muted)]">{t('backup.all.localOnly.keys')}</p>}
        {withKeys && (
          <>
            <Input
              type="password"
              autoComplete="new-password"
              aria-label={t('backup.all.passphrase')}
              placeholder={t('backup.all.passphrase')}
              value={pass}
              onChange={(e) => setPass(e.target.value)}
            />
            <Alert tone="warning" title={t('backup.all.passphrase.warn')} />
            {pass.length > 0 && pass.length < MIN_PASSPHRASE && (
              <p className="text-[11px] text-[var(--danger)]">{t('backup.all.passphrase.short', { min: MIN_PASSPHRASE })}</p>
            )}
          </>
        )}
        {!withKeys && <p className="text-[11px] text-[var(--text-muted)]">{t('backup.all.export.noKeys')}</p>}
        {error && <Alert tone="danger" title={t('common.error')}>{error}</Alert>}
        <div>
          <Button variant="primary" icon={<Download className="size-3.5" />} disabled={blocked} onClick={run}>
            {t('backup.all.export.run')}
          </Button>
        </div>
      </div>
    </Group>
  );
}
