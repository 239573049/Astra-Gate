// The WebDAV copy of the provider backup (issue #33): set-up, test, "back up now" and "restore from the cloud". Restore does not
// import anything itself: it hands the downloaded file to the sheet's ordinary preview, so nothing is applied unseen.
// Plaintext keys are never uploaded (the server refuses it), so only "no keys" and "passphrase" are offered here.

import { CloudDownload, CloudUpload } from 'lucide-react';
import { useEffect, useState } from 'react';

import { useForgetWebDav, useRestoreWebDav, useSaveWebDav, useTestWebDav, useUploadWebDav, useWebDav } from '../api/hooks';
import type { BackupFile, BackupSecrets } from '../api/types';
import { useI18n } from '../i18n';
import { Alert } from './arc/alert/alert';
import { Button, Input } from './ui/controls';
import { errorText, Select, useFeedback } from './ui/overlays';

export function WebDavBackupSection({ onRestored }: { onRestored: (file: BackupFile) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const status = useWebDav();
  const save = useSaveWebDav();
  const forget = useForgetWebDav();
  const test = useTestWebDav();
  const upload = useUploadWebDav();
  const restore = useRestoreWebDav();

  const [url, setUrl] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [folder, setFolder] = useState('astra');
  const [secrets, setSecrets] = useState<Exclude<BackupSecrets, 'plaintext'>>('none');
  const [passphrase, setPassphrase] = useState('');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!status.data) return;
    setUrl(status.data.url);
    setUsername(status.data.username);
    setFolder(status.data.folder);
  }, [status.data]);

  const fail = (e: unknown) => setError(errorText(e));
  const ready = status.data?.configured === true;
  const busy = save.isPending || test.isPending || upload.isPending || restore.isPending || forget.isPending;

  const doSave = () => {
    setError(null);
    save.mutate(
      { url, username, folder, password: password || undefined },
      {
        onSuccess: () => {
          setPassword('');
          toast(t('backup.webdav.saved'));
        },
        onError: fail,
      },
    );
  };

  return (
    <section className="grid gap-3" aria-label={t('backup.webdav.title')}>
      <h3 className="text-[13px] font-medium">{t('backup.webdav.title')}</h3>
      <p className="text-[11px] text-[var(--text-muted)]">{t('backup.webdav.hint')}</p>
      <Input aria-label={t('backup.webdav.url')} placeholder="https://dav.example.com/remote.php/dav/files/me" mono value={url} onChange={(e) => setUrl(e.target.value)} />
      <div className="grid grid-cols-2 gap-2">
        <Input aria-label={t('backup.webdav.username')} placeholder={t('backup.webdav.username')} autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} />
        <Input
          type="password"
          autoComplete="new-password"
          aria-label={t('backup.webdav.password')}
          placeholder={status.data?.hasPassword ? t('backup.webdav.passwordKept') : t('backup.webdav.password')}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
      </div>
      <Input aria-label={t('backup.webdav.folder')} placeholder={t('backup.webdav.folder')} mono value={folder} onChange={(e) => setFolder(e.target.value)} />
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="primary" disabled={busy || url.trim().length === 0} onClick={doSave}>
          {t('backup.webdav.save')}
        </Button>
        <Button
          disabled={busy || !ready}
          onClick={() => {
            setError(null);
            test.mutate(undefined, { onSuccess: () => toast(t('backup.webdav.testOk')), onError: fail });
          }}
        >
          {t('backup.webdav.test')}
        </Button>
        {ready && (
          <Button
            disabled={busy}
            onClick={() => {
              setError(null);
              forget.mutate(undefined, { onSuccess: () => { setUrl(''); setUsername(''); setPassword(''); }, onError: fail });
            }}
          >
            {t('backup.webdav.forget')}
          </Button>
        )}
      </div>

      {ready && (
        <>
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[12.5px] text-[var(--text-muted)]">{t('backup.export.secrets')}</span>
            <Select
              className="w-64"
              ariaLabel={t('backup.export.secrets')}
              value={secrets}
              onChange={(v: Exclude<BackupSecrets, 'plaintext'>) => setSecrets(v)}
              options={[
                { value: 'none', label: t('backup.secrets.none') },
                { value: 'passphrase', label: t('backup.secrets.passphrase') },
              ]}
            />
          </div>
          {secrets === 'passphrase' && (
            <Input
              type="password"
              autoComplete="new-password"
              aria-label={`${t('backup.webdav.title')} ${t('backup.passphrase')}`}
              placeholder={t('backup.passphrase')}
              value={passphrase}
              onChange={(e) => setPassphrase(e.target.value)}
            />
          )}
          <div className="flex flex-wrap items-center gap-2">
            <Button
              variant="primary"
              icon={<CloudUpload className="size-3.5" />}
              disabled={busy || (secrets === 'passphrase' && passphrase.length < 8)}
              onClick={() => {
                setError(null);
                upload.mutate(
                  { secrets, passphrase: secrets === 'passphrase' ? passphrase : undefined },
                  { onSuccess: (r) => toast(t('backup.webdav.uploadDone', { count: r.uploaded })), onError: fail },
                );
              }}
            >
              {t('backup.webdav.upload')}
            </Button>
            <Button
              icon={<CloudDownload className="size-3.5" />}
              disabled={busy}
              onClick={() => {
                setError(null);
                restore.mutate(undefined, { onSuccess: onRestored, onError: fail });
              }}
            >
              {t('backup.webdav.restore')}
            </Button>
          </div>
        </>
      )}
      {error && <Alert tone="danger" title={t('common.error')}>{error}</Alert>}
    </section>
  );
}
