// Settings › Backup & sync: whole-installation backup, restore, automatic backups and multi-machine sync.
// Everything but a keyless export is for the machine running Astra; a session from elsewhere sees it read-only.

import { useState } from 'react';

import { useAuthStatus } from '../../api/hooks';
import { useI18n } from '../../i18n';
import { Alert } from '../arc/alert/alert';
import { AutoBackupPanel } from './AutoBackupPanel';
import { BackupPanel } from './BackupPanel';
import { RestorePanel } from './RestorePanel';
import { SyncPanel } from './SyncPanel';

export function BackupSyncSettings() {
  const { t } = useI18n();
  const auth = useAuthStatus();
  const localOnly = auth.data?.required === true;
  const [autoName, setAutoName] = useState<string | null>(null);

  return (
    <div className="grid gap-1">
      {localOnly && <Alert tone="info" title={t('backup.all.localOnly')} />}
      <BackupPanel localOnly={localOnly} />
      <RestorePanel localOnly={localOnly} autoName={autoName} />
      <AutoBackupPanel localOnly={localOnly} onRestore={setAutoName} />
      <SyncPanel localOnly={localOnly} />
    </div>
  );
}
