// Provider backup (issue #33): export providers and their models to one JSON file, or restore from such a file.
// A file with keys is only made on this machine (the server refuses it elsewhere); previews show masks, never keys.

import { Download, Upload } from 'lucide-react';
import { useRef, useState } from 'react';

import { useExportProviderBackup, useImportProviderBackup, usePreviewProviderBackup } from '../api/hooks';
import type { BackupFile, BackupItemStatus, BackupPreview, BackupPreviewItem, BackupSecrets } from '../api/types';
import { useI18n, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';
import { Alert } from './arc/alert/alert';
import { Checkbox } from './arc/checkbox/checkbox';
import { Badge, Button, Input } from './ui/controls';
import { errorText, Select, Sheet, useFeedback } from './ui/overlays';
import { WebDavBackupSection } from './WebDavBackupSection';

const STATUS_TONE = { new: 'green', sameHost: 'orange', same: 'neutral', invalid: 'red' } as const satisfies Record<BackupItemStatus, string>;
const selectable = (i: BackupPreviewItem) => i.status === 'new' || i.status === 'sameHost';

/** Hands a JSON document to the browser as a file download. */
function download(name: string, data: unknown) {
  const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}

function isBackupFile(v: unknown): v is BackupFile {
  return typeof v === 'object' && v !== null && (v as BackupFile).format === 'astra-providers' && Array.isArray((v as BackupFile).providers);
}

export function ProviderBackupSheet({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const exportBackup = useExportProviderBackup();
  const previewBackup = usePreviewProviderBackup();
  const importBackup = useImportProviderBackup();
  const fileInput = useRef<HTMLInputElement>(null);

  const [secrets, setSecrets] = useState<BackupSecrets>('none');
  const [exportPass, setExportPass] = useState('');
  const [exportError, setExportError] = useState<string | null>(null);

  const [file, setFile] = useState<BackupFile | null>(null);
  const [importPass, setImportPass] = useState('');
  const [preview, setPreview] = useState<BackupPreview | null>(null);
  const [picked, setPicked] = useState<Set<number>>(new Set());
  const [importError, setImportError] = useState<string | null>(null);

  const runExport = () => {
    setExportError(null);
    exportBackup.mutate(
      { secrets, passphrase: secrets === 'passphrase' ? exportPass : undefined },
      {
        onSuccess: (data) => {
          download(`astra-providers-${new Date().toISOString().slice(0, 10)}.json`, data);
          toast(t('backup.export.done', { count: data.providers.length }));
          setExportPass('');
        },
        onError: (e) => setExportError(errorText(e)),
      },
    );
  };

  const runPreview = (target: BackupFile, passphrase: string) => {
    setImportError(null);
    previewBackup.mutate(
      { file: target, passphrase: target.secrets === 'passphrase' ? passphrase : undefined },
      {
        onSuccess: (p) => {
          setPreview(p);
          setPicked(new Set(p.items.filter((i) => i.status === 'new').map((i) => i.index)));
        },
        onError: (e) => {
          setPreview(null);
          setImportError(errorText(e));
        },
      },
    );
  };

  /** A file from somewhere other than the file chooser (the WebDAV restore): previewed exactly like a chosen one. */
  const adopt = (parsed: BackupFile) => {
    setPreview(null);
    setImportError(null);
    setFile(parsed);
    if (parsed.secrets !== 'passphrase') runPreview(parsed, '');
  };

  const choose = async (f: File | undefined) => {
    setPreview(null);
    setFile(null);
    setImportError(null);
    if (!f) return;
    try {
      const parsed: unknown = JSON.parse(await f.text());
      if (!isBackupFile(parsed)) throw new Error('not a backup');
      setFile(parsed);
      // an encrypted file waits for its passphrase; any other previews straight away
      if (parsed.secrets !== 'passphrase') runPreview(parsed, '');
    } catch {
      setImportError(t('backup.import.badFile'));
    }
  };

  const runImport = () => {
    if (!file) return;
    setImportError(null);
    importBackup.mutate(
      { file, passphrase: file.secrets === 'passphrase' ? importPass : undefined, indexes: [...picked] },
      {
        onSuccess: (r) => {
          toast(r.skipped.length > 0 ? t('backup.import.doneSkipped', { count: r.added.length, skipped: r.skipped.length }) : t('backup.import.done', { count: r.added.length }));
          setFile(null);
          setPreview(null);
          setImportPass('');
          onOpenChange(false);
        },
        onError: (e) => setImportError(errorText(e)),
      },
    );
  };

  const toggle = (index: number, on: boolean) =>
    setPicked((prev) => {
      const next = new Set(prev);
      if (on) next.add(index);
      else next.delete(index);
      return next;
    });

  const needsPass = file?.secrets === 'passphrase' && !preview;
  const exportBlocked = exportBackup.isPending || (secrets === 'passphrase' && exportPass.length < 8);

  return (
    <Sheet open={open} onOpenChange={onOpenChange} width={640} title={t('backup.title')} description={t('backup.detail')}>
      <div className="grid gap-6">
        <section className="grid gap-3" aria-label={t('backup.export.title')}>
          <h3 className="text-[13px] font-medium">{t('backup.export.title')}</h3>
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[12.5px] text-[var(--text-muted)]">{t('backup.export.secrets')}</span>
            <Select
              className="w-64"
              ariaLabel={t('backup.export.secrets')}
              value={secrets}
              onChange={(v: BackupSecrets) => setSecrets(v)}
              options={[
                { value: 'none', label: t('backup.secrets.none') },
                { value: 'passphrase', label: t('backup.secrets.passphrase') },
                { value: 'plaintext', label: t('backup.secrets.plaintext') },
              ]}
            />
          </div>
          {secrets !== 'none' && <p className="text-[11px] text-[var(--text-muted)]">{t('backup.secrets.hint')}</p>}
          {secrets === 'passphrase' && (
            <Input
              type="password"
              autoComplete="new-password"
              aria-label={t('backup.passphrase')}
              placeholder={t('backup.passphrase')}
              value={exportPass}
              onChange={(e) => setExportPass(e.target.value)}
            />
          )}
          {secrets === 'passphrase' && <p className="text-[11px] text-[var(--text-muted)]">{t('backup.passphrase.hint')}</p>}
          {secrets === 'plaintext' && <Alert tone="warning" title={t('backup.plaintext.warn')} />}
          {exportError && <Alert tone="danger" title={t('common.error')}>{exportError}</Alert>}
          <div>
            <Button variant="primary" icon={<Download className="size-3.5" />} disabled={exportBlocked} onClick={runExport}>
              {t('backup.export.run')}
            </Button>
          </div>
        </section>

        <section className="grid gap-3" aria-label={t('backup.import.title')}>
          <h3 className="text-[13px] font-medium">{t('backup.import.title')}</h3>
          <div>
            <input
              ref={fileInput}
              type="file"
              accept=".json,application/json"
              className="hidden"
              aria-label={t('backup.import.choose')}
              onChange={(e) => {
                void choose(e.target.files?.[0]);
                e.target.value = '';
              }}
            />
            <Button icon={<Upload className="size-3.5" />} onClick={() => fileInput.current?.click()}>
              {t('backup.import.choose')}
            </Button>
          </div>
          {needsPass && (
            <>
              <p className="text-[12px] text-[var(--text-muted)]">{t('backup.import.encrypted')}</p>
              <div className="flex gap-2">
                <Input
                  className="flex-1"
                  type="password"
                  autoComplete="off"
                  aria-label={t('backup.passphrase')}
                  placeholder={t('backup.passphrase')}
                  value={importPass}
                  onChange={(e) => setImportPass(e.target.value)}
                />
                <Button disabled={importPass.length === 0 || previewBackup.isPending} onClick={() => file && runPreview(file, importPass)}>
                  {t('backup.import.preview')}
                </Button>
              </div>
            </>
          )}
          {importError && <Alert tone="danger" title={t('common.error')}>{importError}</Alert>}
          {preview && (
            <>
              {preview.secrets === 'none' && <p className="text-[11px] text-[var(--text-muted)]">{t('backup.import.noKeys')}</p>}
              <ul className="card max-h-[320px] divide-y divide-[var(--border)] overflow-auto">
                {preview.items.map((i) => (
                  <PreviewRow key={i.index} item={i} checked={picked.has(i.index)} onToggle={(on) => toggle(i.index, on)} />
                ))}
              </ul>
              <div>
                <Button variant="primary" disabled={picked.size === 0 || importBackup.isPending} onClick={runImport}>
                  {t('backup.import.run', { count: picked.size })}
                </Button>
              </div>
            </>
          )}
        </section>
        <WebDavBackupSection onRestored={adopt} />
      </div>
    </Sheet>
  );
}

function PreviewRow({ item, checked, onToggle }: { item: BackupPreviewItem; checked: boolean; onToggle: (on: boolean) => void }) {
  const { t } = useI18n();
  const enabled = selectable(item);
  return (
    <li className={cn('flex items-start gap-3 px-4 py-3', !enabled && 'opacity-60')}>
      <Checkbox checked={checked && enabled} disabled={!enabled} onCheckedChange={(state) => onToggle(state === true)} aria-label={item.name} />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="min-w-0 truncate text-[13px] font-medium" title={item.name}>{item.name}</span>
          <Badge tone={STATUS_TONE[item.status]}>{t(`backup.status.${item.status}` as MessageKey)}</Badge>
        </div>
        {item.endpoints && item.endpoints.length > 0 && (
          <div className="mt-0.5 truncate font-mono text-[11px] text-[var(--text-muted)]">{item.endpoints.map((e) => e.baseUrl).join(' / ')}</div>
        )}
        <div className="mt-0.5 flex flex-wrap gap-x-3 text-[11px] text-[var(--text-muted)]">
          {item.hasKey && <span className="font-mono">{item.keyMasked ?? '••••'}</span>}
          <span>{t('backup.models', { count: item.modelCount })}</span>
        </div>
        {item.existing && item.status !== 'new' && <div className="mt-1 text-[11px] text-[var(--text-muted)]">{item.existing.name}</div>}
        {item.error && <div className="mt-1 text-[11px] text-[var(--danger)]">{item.error}</div>}
        {item.ignored.length > 0 && <div className="mt-1 text-[11px] text-[var(--warning)]">{t('import.ignored', { fields: item.ignored.join(', ') })}</div>}
      </div>
    </li>
  );
}
