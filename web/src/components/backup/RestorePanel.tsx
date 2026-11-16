// Settings › Backup & sync › Restore: pick a source (file / local automatic backup / the sync target), preview, then restore.

import { Upload } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';

import { useAutoBackup, usePreviewFullBackup, useRestoreFullBackup } from '../../api/hooks';
import type { FullBackupMode, FullBackupPlan, FullBackupRestore, FullBackupSource, FullBackupSourceKind } from '../../api/types';
import { useI18n } from '../../i18n';
import { Alert } from '../arc/alert/alert';
import { Badge, Button, Group, Input, Segmented } from '../ui/controls';
import { errorText, Select, useFeedback } from '../ui/overlays';
import { PlanSection, SectionPicker, formatTime } from './shared';

export function RestorePanel({ localOnly, autoName }: { localOnly: boolean; autoName?: string | null }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const auto = useAutoBackup(!localOnly);
  const preview = usePreviewFullBackup();
  const restore = useRestoreFullBackup();
  const fileInput = useRef<HTMLInputElement>(null);

  const [source, setSource] = useState<FullBackupSourceKind>('file');
  const [content, setContent] = useState<string | null>(null);
  const [fileName, setFileName] = useState('');
  const [name, setName] = useState('');
  const [pass, setPass] = useState('');
  const [mode, setMode] = useState<FullBackupMode>('merge');
  const [plan, setPlan] = useState<FullBackupPlan | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [result, setResult] = useState<FullBackupRestore | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reset = () => {
    setPlan(null);
    setResult(null);
    setError(null);
  };

  // "Restore from this" in the automatic backups list
  useEffect(() => {
    if (!autoName) return;
    setSource('auto');
    setName(autoName);
    reset();
  }, [autoName]);

  const request = (sections?: string[]): FullBackupSource => ({
    source,
    content: source === 'file' ? (content ?? undefined) : undefined,
    name: source === 'auto' ? name : undefined,
    passphrase: pass || undefined,
    sections,
    mode,
  });

  const ready = source === 'file' ? content !== null : source === 'auto' ? name !== '' : true;

  const runPreview = () => {
    reset();
    preview.mutate(request(), {
      onSuccess: (p) => {
        setPlan(p);
        setPicked(new Set(p.available));
      },
      onError: (e) => setError(errorText(e)),
    });
  };

  const runRestore = async () => {
    if (!plan) return;
    if (mode === 'replace') {
      const ok = await confirm({ title: t('backup.all.restore.replaceConfirm'), detail: t('backup.all.restore.replaceDetail'), confirmLabel: t('backup.all.restore.run'), destructive: true });
      if (!ok) return;
    }
    setError(null);
    restore.mutate(request(plan.legacy ? undefined : [...picked]), {
      onSuccess: (r) => {
        setResult(r);
        setPlan(null);
        setPass('');
        toast(t('backup.all.restore.done', { written: r.written, removed: r.removed }));
      },
      onError: (e) => setError(errorText(e)),
    });
  };

  const choose = async (f: File | undefined) => {
    reset();
    setContent(null);
    setFileName('');
    if (!f) return;
    setContent(await f.text());
    setFileName(f.name);
  };

  const autoFiles = auto.data?.files ?? [];

  return (
    <Group title={t('backup.all.restore.title')} footer={localOnly ? t('backup.all.localOnly') : undefined}>
      <div className="grid gap-3 p-4">
        <Segmented
          value={source}
          onChange={(v: FullBackupSourceKind) => {
            setSource(v);
            reset();
          }}
          items={[
            { value: 'file', label: t('backup.all.restore.source.file') },
            { value: 'auto', label: t('backup.all.restore.source.auto') },
            { value: 'remote', label: t('backup.all.restore.source.remote') },
          ]}
        />
        {source === 'file' && (
          <div className="flex items-center gap-2">
            <input
              ref={fileInput}
              type="file"
              className="hidden"
              aria-label={t('backup.all.restore.choose')}
              onChange={(e) => {
                void choose(e.target.files?.[0]);
                e.target.value = '';
              }}
            />
            <Button icon={<Upload className="size-3.5" />} disabled={localOnly} onClick={() => fileInput.current?.click()}>
              {t('backup.all.restore.choose')}
            </Button>
            {fileName && <span className="truncate font-mono text-[12px]">{fileName}</span>}
          </div>
        )}
        {source === 'auto' && (
          <Select
            className="w-full"
            ariaLabel={t('backup.all.restore.source.auto')}
            placeholder={t('backup.all.restore.pickAuto')}
            value={name || null}
            onChange={(v: string) => {
              setName(v);
              reset();
            }}
            options={autoFiles.map((f) => ({ value: f.name, label: `${f.name} · ${formatTime(f.at)}` }))}
          />
        )}
        {source === 'remote' && <p className="text-[12px] text-[var(--text-muted)]">{t('backup.all.restore.remoteHint')}</p>}
        <Input
          type="password"
          autoComplete="off"
          aria-label={t('backup.all.restore.passphrase')}
          placeholder={t('backup.all.restore.passphrase')}
          value={pass}
          onChange={(e) => setPass(e.target.value)}
        />
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-[12.5px] text-[var(--text-muted)]">{t('backup.all.restore.mode')}</span>
          <Segmented
            value={mode}
            onChange={(v: FullBackupMode) => {
              setMode(v);
              reset();
            }}
            items={[
              { value: 'merge', label: t('backup.all.restore.mode.merge') },
              { value: 'replace', label: t('backup.all.restore.mode.replace') },
            ]}
          />
        </div>
        <p className="text-[11px] text-[var(--text-muted)]">{mode === 'merge' ? t('backup.all.restore.mode.mergeHint') : t('backup.all.restore.mode.replaceHint')}</p>
        <div>
          <Button disabled={localOnly || !ready || preview.isPending} onClick={runPreview}>
            {t('backup.all.restore.preview')}
          </Button>
        </div>
        {error && <Alert tone="danger" title={t('common.error')}>{error}</Alert>}

        {plan && (
          <div className="grid gap-3">
            <div className="flex flex-wrap items-center gap-2 text-[12px] text-[var(--text-muted)]">
              <span>{t('backup.all.restore.created', { time: formatTime(plan.createdAt), version: plan.astraVersion })}</span>
              <Badge tone={plan.hasKeys ? 'orange' : 'neutral'}>{plan.hasKeys ? t('backup.all.restore.hasKeys') : t('backup.all.restore.noKeys')}</Badge>
            </div>
            {plan.notes.map((n) => <p key={n} className="text-[11px] text-[var(--text-muted)]">{n}</p>)}
            {plan.legacy ? (
              <Alert tone="info" title={t('backup.all.restore.legacy')} />
            ) : (
              <>
                <SectionPicker ids={plan.available} picked={picked} onChange={setPicked} />
                <div className="grid gap-3">
                  {plan.sections.filter((s) => picked.has(s.section)).map((s) => <PlanSection key={s.section} plan={s} />)}
                </div>
              </>
            )}
            <div>
              <Button variant="primary" disabled={restore.isPending || (!plan.legacy && picked.size === 0)} onClick={() => void runRestore()}>
                {t('backup.all.restore.run')}
              </Button>
            </div>
          </div>
        )}

        {result && (
          <div className="grid gap-2" data-testid="restore-result">
            <Alert tone={result.errors.length > 0 ? 'warning' : 'success'} title={t('backup.all.restore.done', { written: result.written, removed: result.removed })} />
            {result.safetyBackup && <p className="text-[12px]">{t('backup.all.restore.safety', { name: result.safetyBackup })}</p>}
            {result.skipped.length > 0 && (
              <div className="text-[12px]">
                <div className="font-medium">{t('backup.all.restore.skipped', { count: result.skipped.length })}</div>
                <ul className="list-disc pl-5 text-[var(--text-muted)]">{result.skipped.map((s) => <li key={s}>{s}</li>)}</ul>
              </div>
            )}
            {result.errors.length > 0 && (
              <div className="text-[12px]">
                <div className="font-medium text-[var(--danger)]">{t('backup.all.restore.errors', { count: result.errors.length })}</div>
                <ul className="list-disc pl-5 text-[var(--danger)]">{result.errors.map((s) => <li key={s}>{s}</li>)}</ul>
              </div>
            )}
          </div>
        )}
      </div>
    </Group>
  );
}
