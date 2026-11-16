// Settings › Backup & sync › Sync: keep several machines in step through a folder, WebDAV, S3 or a private GitHub repository.
// The secret field is write-only (the server never returns it); the passphrase must be the same on every machine.

import { useEffect, useState } from 'react';

import {
  useClearSyncConflicts, useForgetSync, useSaveSyncConfig, useSyncNow, useSyncStatus, useTestSync, useUploadRemoteBackup,
} from '../../api/hooks';
import type { SyncConfigInput, SyncKind, SyncRun, SyncStatus, SyncTargetInput } from '../../api/types';
import { useI18n, type MessageKey } from '../../i18n';
import { Alert } from '../arc/alert/alert';
import { Checkbox } from '../arc/checkbox/checkbox';
import { Badge, Button, Group, Input, Row, Switch } from '../ui/controls';
import { errorText, Select, useFeedback } from '../ui/overlays';
import { BACKUP_SECTIONS, ItemList, MIN_PASSPHRASE, SectionPicker, formatTime } from './shared';

const SYNC_SECTIONS = BACKUP_SECTIONS.filter((s) => s !== 'usage');
const KINDS: SyncKind[] = ['folder', 'webdav', 's3', 'github'];

export function SyncPanel({ localOnly }: { localOnly: boolean }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const status = useSyncStatus();
  const s = status.data;
  const save = useSaveSyncConfig();
  const test = useTestSync();
  const now = useSyncNow();
  const forget = useForgetSync();
  const clear = useClearSyncConflicts();
  const upload = useUploadRemoteBackup();

  const [kind, setKind] = useState<SyncKind>('folder');
  const [url, setUrl] = useState('');
  const [username, setUsername] = useState('');
  const [secret, setSecret] = useState('');
  const [folder, setFolder] = useState('');
  const [bucket, setBucket] = useState('');
  const [region, setRegion] = useState('');
  const [pathStyle, setPathStyle] = useState(false);
  const [branch, setBranch] = useState('');
  const [pass, setPass] = useState('');
  const [interval, setIntervalMin] = useState('15');
  const [sections, setSections] = useState<Set<string>>(new Set(SYNC_SECTIONS));
  const [includeKeys, setIncludeKeys] = useState(false);
  const [includeSignins, setIncludeSignins] = useState(false);
  const [run, setRun] = useState<SyncRun | null>(null);
  const [error, setError] = useState<string | null>(null);

  // load the saved settings into the form once (and again after Forget)
  const configured = s?.configured;
  useEffect(() => {
    if (!s) return;
    const g = s.target;
    setKind((g.kind || 'folder') as SyncKind);
    setUrl(g.url);
    setUsername(g.username);
    setFolder(g.folder);
    setBucket(g.bucket);
    setRegion(g.region);
    setPathStyle(g.pathStyle);
    setBranch(g.branch);
    setIntervalMin(String(s.intervalMinutes || 15));
    setSections(new Set(s.sections.length > 0 ? s.sections : SYNC_SECTIONS));
    setIncludeKeys(s.includeKeys);
    setIncludeSignins(s.includeSignins);
    setSecret('');
    setPass('');
  }, [s === undefined, configured]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!s) return null;

  const fail = (e: unknown) => setError(errorText(e));

  const target = (): SyncTargetInput => ({
    kind,
    url: kind === 'webdav' || kind === 's3' || kind === 'github' ? url.trim() : undefined,
    username: kind === 'webdav' || kind === 's3' ? username.trim() : undefined,
    secret: secret || undefined,
    folder: folder.trim(),
    bucket: kind === 's3' ? bucket.trim() : undefined,
    region: kind === 's3' ? region.trim() : undefined,
    pathStyle: kind === 's3' ? pathStyle : undefined,
    branch: kind === 'github' ? branch.trim() : undefined,
  });

  const config = (): SyncConfigInput => ({
    target: target(),
    passphrase: pass || undefined,
    intervalMinutes: Math.max(1, Number(interval) || 15),
    sections: [...SYNC_SECTIONS.filter((x) => sections.has(x))],
    includeKeys,
    includeSignins,
  });

  const doSave = (then?: () => void) => {
    setError(null);
    save.mutate(config(), {
      onSuccess: () => {
        setSecret('');
        setPass('');
        then?.();
      },
      onError: fail,
    });
  };

  const doTest = () =>
    doSave(() =>
      test.mutate(undefined, { onSuccess: () => toast(t('sync.test.ok')), onError: fail }),
    );

  const doNow = (dryRun: boolean) => {
    setError(null);
    setRun(null);
    now.mutate(dryRun, {
      onSuccess: (r) => {
        setRun(r);
        if (!dryRun) toast(t('sync.run.done', { pushed: r.pushed, pulled: r.pulled }));
      },
      onError: fail,
    });
  };

  const doForget = async () => {
    if (!(await confirm({ title: t('sync.forget.confirm'), detail: t('sync.forget.detail'), destructive: true, confirmLabel: t('sync.forget') }))) return;
    forget.mutate(undefined, { onSuccess: () => setRun(null), onError: fail });
  };

  const doUpload = () => {
    setError(null);
    upload.mutate(
      { sections: [...SYNC_SECTIONS.filter((x) => sections.has(x))], includeKeys, passphrase: pass || undefined },
      { onSuccess: (r) => toast(t('sync.upload.done', { name: r.name })), onError: fail },
    );
  };

  const busy = save.isPending || test.isPending || now.isPending || s.running;
  const secretPlaceholder = s.target.hasSecret && s.target.kind === kind ? t('sync.secret.saved') : t(`sync.secret.${kind}` as MessageKey);
  const passOk = pass.length === 0 ? s.hasPassphrase : pass.length >= MIN_PASSPHRASE;

  return (
    <Group title={t('sync.title')} footer={localOnly ? t('backup.all.localOnly') : t('sync.hint')}>
      <div className="grid gap-3 p-4">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-[12.5px] text-[var(--text-muted)]">{t('sync.kind')}</span>
          <Select
            className="w-56"
            ariaLabel={t('sync.kind')}
            value={kind}
            disabled={localOnly}
            onChange={(v: SyncKind) => setKind(v)}
            options={KINDS.map((k) => ({ value: k, label: t(`sync.kind.${k}` as MessageKey) }))}
          />
        </div>

        {(kind === 'webdav' || kind === 's3' || kind === 'github') && (
          <Input aria-label={t(`sync.url.${kind}` as MessageKey)} placeholder={t(`sync.url.${kind}` as MessageKey)} value={url} disabled={localOnly} onChange={(e) => setUrl(e.target.value)} />
        )}
        {kind === 's3' && (
          <div className="grid gap-3 sm:grid-cols-2">
            <Input aria-label={t('sync.bucket')} placeholder={t('sync.bucket')} value={bucket} disabled={localOnly} onChange={(e) => setBucket(e.target.value)} />
            <Input aria-label={t('sync.region')} placeholder={t('sync.region')} value={region} disabled={localOnly} onChange={(e) => setRegion(e.target.value)} />
          </div>
        )}
        {(kind === 'webdav' || kind === 's3') && (
          <Input
            aria-label={kind === 's3' ? t('sync.accessKey') : t('sync.username')}
            placeholder={kind === 's3' ? t('sync.accessKey') : t('sync.username')}
            value={username}
            disabled={localOnly}
            onChange={(e) => setUsername(e.target.value)}
          />
        )}
        {kind !== 'folder' && (
          <Input
            type="password"
            autoComplete="new-password"
            aria-label={t('sync.secret')}
            placeholder={secretPlaceholder}
            value={secret}
            disabled={localOnly}
            onChange={(e) => setSecret(e.target.value)}
          />
        )}
        {kind === 'github' && (
          <>
            <Input aria-label={t('sync.branch')} placeholder={t('sync.branch')} value={branch} disabled={localOnly} onChange={(e) => setBranch(e.target.value)} />
            <p className="text-[11px] text-[var(--text-muted)]">{t('sync.github.private')}</p>
          </>
        )}
        {kind === 's3' && (
          <label className="flex items-center gap-2 text-[12.5px]">
            <Checkbox checked={pathStyle} disabled={localOnly} aria-label={t('sync.pathStyle')} onCheckedChange={(v) => setPathStyle(v === true)} />
            <span>{t('sync.pathStyle')}</span>
          </label>
        )}
        <Input aria-label={t('sync.folder')} placeholder={t(`sync.folder.${kind}` as MessageKey)} value={folder} disabled={localOnly} onChange={(e) => setFolder(e.target.value)} />

        <Input
          type="password"
          autoComplete="new-password"
          aria-label={t('sync.passphrase')}
          placeholder={s.hasPassphrase ? t('sync.passphrase.saved') : t('sync.passphrase')}
          value={pass}
          disabled={localOnly}
          onChange={(e) => setPass(e.target.value)}
        />
        <Alert tone="warning" title={t('sync.passphrase.warn')} />
        {pass.length > 0 && pass.length < MIN_PASSPHRASE && <p className="text-[11px] text-[var(--danger)]">{t('backup.all.passphrase.short', { min: MIN_PASSPHRASE })}</p>}

        <label className="flex items-center gap-2 text-[12.5px]">
          {t('sync.interval')}
          <Input className="w-20" type="number" min={1} aria-label={t('sync.interval')} value={interval} disabled={localOnly} onChange={(e) => setIntervalMin(e.target.value)} />
          {t('sync.minutes')}
        </label>
        <SectionPicker ids={SYNC_SECTIONS} picked={sections} onChange={setSections} disabled={localOnly} />
        <label className="flex items-center gap-2 text-[12.5px]">
          <Checkbox checked={includeSignins} disabled={localOnly} aria-label={t('sync.includeSignins')} onCheckedChange={(v) => setIncludeSignins(v === true)} />
          <span>{t('sync.includeSignins')}</span>
        </label>
        <label className="flex items-center gap-2 text-[12.5px]">
          <Checkbox checked={includeKeys} disabled={localOnly} aria-label={t('sync.includeKeys')} onCheckedChange={(v) => setIncludeKeys(v === true)} />
          <span>{t('sync.includeKeys')}</span>
        </label>
        <p className="text-[11px] text-[var(--text-muted)]">{t('sync.includeKeys.hint')}</p>

        {error && <Alert tone="danger" title={t('common.error')}>{error}</Alert>}
        <div className="flex flex-wrap gap-2">
          <Button disabled={localOnly || busy} onClick={doTest}>{t('sync.test')}</Button>
          <Button variant="primary" disabled={localOnly || busy || !passOk} onClick={() => doSave(() => toast(t('sync.saved')))}>{t('common.save')}</Button>
          <Button disabled={localOnly || busy || !s.configured} onClick={() => doNow(false)}>{t('sync.now')}</Button>
          <Button disabled={localOnly || busy || !s.configured} onClick={() => doNow(true)}>{t('sync.preview')}</Button>
          <Button disabled={localOnly || busy || !s.configured} onClick={doUpload}>{t('sync.upload')}</Button>
          <Button disabled={localOnly || forget.isPending || !s.configured} onClick={() => void doForget()}>{t('sync.forget')}</Button>
        </div>
      </div>

      <Row label={t('sync.enabled')} detail={s.configured ? undefined : t('sync.notConfigured')}>
        <Switch
          checked={s.enabled}
          disabled={localOnly || !s.configured || save.isPending}
          label={t('sync.enabled')}
          onChange={(v) => save.mutate({ enabled: v }, { onError: fail })}
        />
      </Row>

      <StatusBlock s={s} />

      {run && (
        <div className="grid gap-2 p-4" data-testid="sync-run">
          <div className="text-[12.5px] font-medium">
            {run.dryRun ? t('sync.run.preview', { pushed: run.pushed, pulled: run.pulled }) : t('sync.run.done', { pushed: run.pushed, pulled: run.pulled })}
          </div>
          {run.firstSync && <p className="text-[11px] text-[var(--text-muted)]">{t('sync.run.first')}</p>}
          {run.items.length > 0 && <div className="card"><ItemList items={run.items} /></div>}
          {run.errors.map((e) => <Alert key={e} tone="danger" title={e} />)}
        </div>
      )}

      {s.conflicts.length > 0 && (
        <div className="grid gap-2 p-4">
          <div className="flex items-center gap-2">
            <span className="text-[12.5px] font-medium">{t('sync.conflicts', { count: s.conflicts.length })}</span>
            <Button size="sm" disabled={localOnly || clear.isPending} onClick={() => clear.mutate(undefined, { onError: fail })}>{t('sync.conflicts.clear')}</Button>
          </div>
          <ul className="card divide-y divide-[var(--border)] text-[12px]">
            {s.conflicts.map((c) => (
              <li key={`${c.section}/${c.id}/${c.at}`} className="flex flex-wrap items-center gap-2 px-3 py-1.5">
                <Badge>{t(`backup.all.section.${c.section}` as MessageKey)}</Badge>
                <span className="min-w-0 flex-1 truncate">{c.label || c.id}</span>
                <Badge tone={c.winner === 'local' ? 'green' : 'orange'}>{c.winner === 'local' ? t('sync.winner.local') : t('sync.winner.remote')}</Badge>
                <span className="text-[11px] text-[var(--text-muted)]">{formatTime(c.at)} · {c.device}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </Group>
  );
}

function StatusBlock({ s }: { s: SyncStatus }) {
  const { t } = useI18n();
  return (
    <div className="grid gap-1 p-4 text-[12px]" data-testid="sync-status">
      <div>{t('sync.status.last')}: {s.lastSyncAt ? formatTime(s.lastSyncAt) : t('sync.status.never')}{s.running && ` · ${t('sync.status.running')}`}</div>
      <div>{t('sync.status.counts', { pushed: s.lastPushed, pulled: s.lastPulled, tracked: s.tracked })}</div>
      <div className="text-[var(--text-muted)]">{t('sync.status.device')}: <span className="font-mono">{s.deviceId}</span></div>
      {s.lastError && <Alert tone="danger" title={t('sync.status.error')}>{s.lastError}</Alert>}
    </div>
  );
}
