import { chmod, readFile, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import readline from 'node:readline';

import { AstraError } from '../errors.js';
import { printTable, success, warn } from '../ui.js';
import { confirmPrompt, promptHidden } from '../lib/prompt.js';
import { apiRequest, ApiError } from '../lib/http.js';
import { ensureServerRunning } from '../lib/server-lifecycle.js';

// Whole-installation backup, restore and multi-machine sync (/api/backup/*, /api/sync/*).
// Passphrases and target secrets never go on argv: env, --…-stdin, or a masked prompt.

export const BACKUP_PASSPHRASE_ENV = 'ASTRA_BACKUP_PASSPHRASE';
export const SYNC_PASSPHRASE_ENV = 'ASTRA_SYNC_PASSPHRASE';
export const SYNC_SECRET_ENV = 'ASTRA_SYNC_SECRET';
/** A backup file may carry usage history; refuse anything beyond this before reading it. */
export const BACKUP_FILE_MAX_BYTES = 96 * 1024 * 1024;
export const SYNC_KINDS = ['folder', 'webdav', 's3', 'github'] as const;
export const REPLACE_CONFIRM_TEXT = 'replace';

// ---------- shared plumbing ----------

async function api<T>(apiPath: string, method: 'GET' | 'POST' | 'PUT' | 'DELETE' = 'GET', body?: unknown): Promise<T> {
  const { base, runtime, started } = await ensureServerRunning();
  if (started) console.log('Server was not running — started it in the background.');
  try {
    return await apiRequest<T>({ base, path: apiPath, method, body, runtimeToken: runtime?.runtimeToken });
  } catch (e) {
    throw mapApiError(e);
  }
}

/** The server's `{ error }` message as an AstraError; a 403 says the feature is for this machine only. */
export function mapApiError(e: unknown): unknown {
  if (!(e instanceof ApiError)) return e;
  if (e.status === 403) {
    return new AstraError(e.message || 'Forbidden.', 'This is only available on the machine Astra runs on (a local request).');
  }
  return new AstraError(e.message, `HTTP ${e.status}`);
}

let stdinLines: string[] | undefined;

/** One secret line: the next line of piped stdin, or a masked prompt on a terminal. */
async function readStdinLine(question: string): Promise<string> {
  if (process.stdin.isTTY) return promptHidden(question);
  if (stdinLines === undefined) stdinLines = (await promptHidden(question)).split(/\r?\n/);
  return stdinLines.shift() ?? '';
}

/** Test hook: forget stdin already read. */
export function resetStdinForTests(): void {
  stdinLines = undefined;
}

/**
 * A passphrase or secret from `env`, else stdin (piped) or a masked prompt (asked twice on a terminal when `confirm`).
 * Never from argv.
 */
export async function readSecret(opts: { env: string; question: string; confirm?: boolean; what?: string }): Promise<string> {
  const what = opts.what ?? 'passphrase';
  const fromEnv = process.env[opts.env];
  if (fromEnv) return fromEnv;
  const value = await readStdinLine(opts.question);
  if (!value) throw new AstraError(`No ${what} given.`, `Pipe it on stdin, set ${opts.env}, or type it at the prompt.`);
  if (opts.confirm && process.stdin.isTTY) {
    const again = await promptHidden('Repeat it: ');
    if (again !== value) throw new AstraError(`The two ${what}s differ.`);
  }
  return value;
}

export function parseSections(v: string | undefined): string[] | undefined {
  if (v === undefined) return undefined;
  const list = v.split(',').map((s) => s.trim()).filter(Boolean);
  if (list.length === 0) throw new AstraError('--sections needs at least one section id.', 'Example: --sections providers,tokens');
  for (const s of list) if (!/^[a-z0-9][a-z0-9_.-]*$/i.test(s)) throw new AstraError(`Invalid section id "${s}".`);
  return list;
}

export function parsePositiveInt(v: string | undefined, flag: string): number | undefined {
  if (v === undefined) return undefined;
  if (!/^\d+$/.test(v) || Number(v) < 1) throw new AstraError(`${flag} takes a positive whole number.`);
  return Number(v);
}

// ---------- backup create ----------

interface ExportResult {
  name: string;
  content: string;
  encrypted: boolean;
  hasKeys: boolean;
  documents: number;
  warnings: string[];
}

export async function runBackupCreate(opts: {
  keys?: boolean; // commander's --no-keys gives keys: false
  out?: string;
  sections?: string;
  includeUsage?: boolean;
  passphraseStdin?: boolean;
}): Promise<void> {
  const withKeys = opts.keys !== false;
  const sections = parseSections(opts.sections);
  const passphrase = withKeys
    ? await readSecret({ env: BACKUP_PASSPHRASE_ENV, question: 'Passphrase for the backup: ', confirm: !opts.passphraseStdin })
    : undefined;
  const res = await api<ExportResult>('/api/backup/export', 'POST', {
    sections,
    includeKeys: withKeys,
    includeUsage: Boolean(opts.includeUsage),
    passphrase,
  });
  const out = path.resolve(opts.out ?? res.name);
  await writeFile(out, res.content, { mode: 0o600 });
  await chmod(out, 0o600); // an existing file keeps its old mode otherwise
  success(`Backup written to ${out}.`);
  console.log(
    `${res.documents} document${res.documents === 1 ? '' : 's'}${sections ? ` in sections: ${sections.join(', ')}` : ''}; ` +
      `${res.encrypted ? 'encrypted' : 'not encrypted'}, ${res.hasKeys ? 'includes API keys' : 'no API keys'}${opts.includeUsage ? ', with usage history' : ''}.`,
  );
  for (const w of res.warnings ?? []) warn(w);
  if (res.encrypted) console.log('The passphrase cannot be recovered; keep it safe.');
}

// ---------- backup restore ----------

interface PlanItem {
  id: string;
  label: string;
  status: string;
}
interface PlanSection {
  section: string;
  added: number;
  changed: number;
  removed: number;
  unchanged: number;
  items: PlanItem[];
  truncated: boolean;
}
export interface BackupPlan {
  createdAt: string;
  astraVersion: string;
  hasKeys: boolean;
  mode: string;
  available: string[];
  sections: PlanSection[];
  notes: string[];
  legacy: boolean;
}
interface RestoreResult {
  plan: BackupPlan;
  written: number;
  removed: number;
  skipped: string[];
  errors: string[];
  safetyBackup?: string | null;
}

function renderTable(rows: string[][], headers: string[]): string[] {
  const all = [headers, ...rows];
  const widths = headers.map((_, i) => Math.max(...all.map((r) => (r[i] ?? '').length)));
  return all.map((r) => r.map((c, i) => c.padEnd(widths[i] ?? 0)).join('  ').trimEnd());
}

/** The per-section count table and the item lines of a preview. */
export function formatPlan(plan: BackupPlan): string[] {
  const lines: string[] = [];
  lines.push(
    `Backup from ${plan.createdAt} (Astra ${plan.astraVersion}), ${plan.hasKeys ? 'with API keys' : 'no API keys'}, mode: ${plan.mode}${plan.legacy ? ' (providers-only file)' : ''}.`,
  );
  const rows = plan.sections.map((s) => [s.section, String(s.added), String(s.changed), String(s.removed), String(s.unchanged)]);
  lines.push(...renderTable(rows, ['SECTION', 'ADDED', 'CHANGED', 'REMOVED', 'UNCHANGED']));
  const sign: Record<string, string> = { added: '+', changed: '~', removed: '-' };
  for (const s of plan.sections) {
    const items = s.items.filter((i) => i.status !== 'unchanged');
    if (items.length === 0) continue;
    lines.push('', `${s.section}:`);
    for (const i of items) lines.push(`  ${sign[i.status] ?? '?'} ${i.label || i.id}`);
    if (s.truncated) lines.push('  … (list truncated)');
  }
  for (const n of plan.notes ?? []) lines.push('', `Note: ${n}`);
  return lines;
}

/** Whether a backup file's text is passphrase-sealed. */
export function isEncryptedBackup(text: string): boolean {
  return /"kdf"\s*:\s*"pbkdf2-sha256"/.test(text);
}

function askLine(question: string): Promise<string> {
  return new Promise((resolve) => {
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    rl.question(question, (a) => {
      rl.close();
      resolve(a);
    });
  });
}

export async function runBackupRestore(
  file: string,
  opts: { sections?: string; replace?: boolean; dryRun?: boolean; yes?: boolean },
): Promise<void> {
  const sections = parseSections(opts.sections);
  const abs = path.resolve(file);
  let content: string;
  try {
    if ((await stat(abs)).size > BACKUP_FILE_MAX_BYTES) throw new AstraError(`${abs} is too large to be an Astra backup.`);
    content = await readFile(abs, 'utf8');
  } catch (e) {
    if (e instanceof AstraError) throw e;
    throw new AstraError(`Cannot read ${abs}.`, e instanceof Error ? e.message : undefined);
  }
  const passphrase = isEncryptedBackup(content)
    ? await readSecret({ env: BACKUP_PASSPHRASE_ENV, question: 'Passphrase of the backup: ' })
    : undefined;
  const body = { source: 'file', content, passphrase, sections, mode: opts.replace ? 'replace' : 'merge' };

  const plan = await api<BackupPlan>('/api/backup/preview', 'POST', body);
  for (const l of formatPlan(plan)) console.log(l);
  if (opts.dryRun) {
    console.log('\nDry run: nothing was changed.');
    return;
  }

  if (!opts.yes) {
    console.log('');
    if (opts.replace) {
      warn('--replace makes this Astra match the backup: what the backup does not have is removed.');
      const typed = await askLine(`Type "${REPLACE_CONFIRM_TEXT}" to continue: `);
      if (typed.trim().toLowerCase() !== REPLACE_CONFIRM_TEXT) throw new AstraError('Cancelled; nothing was changed.');
    }
    if (!(await confirmPrompt('Restore this backup now? A safety copy of the current setup is kept first. [y/N] '))) {
      throw new AstraError('Cancelled; nothing was changed.');
    }
  }

  const res = await api<RestoreResult>('/api/backup/restore', 'POST', body);
  success(`Restored: ${res.written} written, ${res.removed} removed.`);
  if (res.safetyBackup) console.log(`Safety backup of the previous setup: ${res.safetyBackup}`);
  if (res.skipped?.length) {
    console.log(`Skipped (${res.skipped.length}):`);
    for (const s of res.skipped) console.log(`  ${s}`);
  }
  if (res.errors?.length) {
    for (const e of res.errors) warn(e);
    process.exitCode = 1;
  }
}

// ---------- backup list / auto ----------

interface AutoInfo {
  enabled: boolean;
  hours: number;
  keep: number;
  hasPassphrase: boolean;
  dir: string;
  files: { name: string; kind: string; at: string; size: number; encrypted: boolean }[];
}

function printAuto(a: AutoInfo): void {
  console.log(
    `Automatic backups: ${a.enabled ? 'on' : 'off'}, every ${a.hours} h, keeping ${a.keep}; ${a.hasPassphrase ? 'encrypted with keys' : 'plain, no keys'}.`,
  );
  console.log(`Folder: ${a.dir}`);
}

export async function runBackupList(): Promise<void> {
  const a = await api<AutoInfo>('/api/backup/auto');
  printAuto(a);
  if (a.files.length === 0) {
    console.log('No backups yet.');
    return;
  }
  printTable(
    a.files.map((f) => [f.name, f.kind, f.at, `${Math.ceil(f.size / 1024)} KB`, f.encrypted ? 'yes' : 'no']),
    ['NAME', 'KIND', 'WHEN', 'SIZE', 'ENCRYPTED'],
  );
}

export async function runBackupAuto(opts: { on?: boolean; off?: boolean; hours?: string; keep?: string; run?: boolean }): Promise<void> {
  if (opts.on && opts.off) throw new AstraError('--on and --off exclude each other.');
  const hours = parsePositiveInt(opts.hours, '--hours');
  const keep = parsePositiveInt(opts.keep, '--keep');
  let a: AutoInfo | undefined;
  if (opts.on || opts.off || hours !== undefined || keep !== undefined) {
    a = await api<AutoInfo>('/api/backup/auto', 'PUT', { enabled: opts.on ? true : opts.off ? false : undefined, hours, keep });
  }
  if (opts.run) {
    a = await api<AutoInfo>('/api/backup/auto/run', 'POST');
    success('Backup made.');
  }
  printAuto(a ?? (await api<AutoInfo>('/api/backup/auto')));
}

// ---------- sync ----------

interface SyncConflict {
  section: string;
  id: string;
  label: string;
  winner: string;
  at: string;
  device: string;
}
interface SyncStatus {
  enabled: boolean;
  configured: boolean;
  target: { kind: string; url: string; username: string; hasSecret: boolean; folder: string; bucket: string; region: string; pathStyle: boolean; branch: string };
  hasPassphrase: boolean;
  intervalMinutes: number;
  sections: string[];
  includeKeys: boolean;
  includeSignins: boolean;
  deviceId: string;
  lastSyncAt?: string | null;
  lastError?: string | null;
  lastPushed: number;
  lastPulled: number;
  running: boolean;
  conflicts: SyncConflict[];
  tracked: number;
}
interface SyncRun {
  pushed: number;
  pulled: number;
  dryRun: boolean;
  firstSync: boolean;
  conflicts: SyncConflict[];
  items: PlanItem[];
  errors: string[];
}

export const PASSPHRASE_NOTE =
  'The sync passphrase cannot be recovered. Use the same passphrase on every machine; without it the synced data cannot be read.';

export function formatSyncStatus(s: SyncStatus): string[] {
  const t = s.target;
  const lines = [
    `Sync: ${s.enabled ? 'on' : 'off'}${s.configured ? '' : ' (not configured)'}${s.running ? ', running now' : ''}`,
    `Target: ${t.kind || '-'}${t.url ? ` ${t.url}` : ''}${t.bucket ? ` bucket ${t.bucket}` : ''}${t.region ? ` (${t.region})` : ''}${t.branch ? ` branch ${t.branch}` : ''}${t.folder ? ` folder ${t.folder}` : ''}`,
    `Secret: ${t.hasSecret ? 'set' : 'not set'}; passphrase: ${s.hasPassphrase ? 'set' : 'not set'}`,
    `Every ${s.intervalMinutes} min; sections: ${s.sections.length ? s.sections.join(', ') : 'all'}; API keys ${s.includeKeys ? 'included' : 'not included'}; sign-ins ${s.includeSignins ? 'included' : 'not included'}`,
    `Device: ${s.deviceId}; tracked documents: ${s.tracked}`,
    `Last sync: ${s.lastSyncAt ?? 'never'} (pushed ${s.lastPushed}, pulled ${s.lastPulled})`,
  ];
  if (s.lastError) lines.push(`Last error: ${s.lastError}`);
  if (s.conflicts.length) {
    lines.push('', `Conflicts (${s.conflicts.length}), the newer side was kept:`);
    for (const c of s.conflicts) lines.push(`  ${c.section}/${c.label || c.id}: kept ${c.winner} (${c.at}, ${c.device})`);
  }
  return lines;
}

export async function runSyncStatus(): Promise<void> {
  for (const l of formatSyncStatus(await api<SyncStatus>('/api/sync/status'))) console.log(l);
}

export async function runSyncNow(opts: { dryRun?: boolean }): Promise<void> {
  const r = await api<SyncRun>(`/api/sync/now${opts.dryRun ? '?dryRun=true' : ''}`, 'POST');
  console.log(
    `${r.dryRun ? 'Dry run: would push' : 'Pushed'} ${r.pushed}, ${r.dryRun ? 'would pull' : 'pulled'} ${r.pulled}${r.firstSync ? ' (first sync)' : ''}.`,
  );
  for (const i of r.items) console.log(`  ${i.status}: ${i.label || i.id}`);
  for (const c of r.conflicts) console.log(`  conflict ${c.section}/${c.label || c.id}: kept ${c.winner}`);
  for (const e of r.errors) warn(e);
  if (r.errors.length) process.exitCode = 1;
  else if (!r.dryRun) success('Sync finished.');
}

export async function runSyncEnable(enabled: boolean): Promise<void> {
  const s = await api<SyncStatus>('/api/sync/config', 'PUT', { enabled });
  success(`Sync is ${s.enabled ? 'on' : 'off'}.`);
}

export async function runSyncTest(): Promise<void> {
  await api<{ ok: boolean }>('/api/sync/test', 'POST');
  success('The sync target is reachable.');
}

export async function runSyncClearConflicts(): Promise<void> {
  await api<SyncStatus>('/api/sync/conflicts', 'DELETE');
  success('Conflict list cleared.');
}

export interface SyncSetupOptions {
  kind?: string;
  url?: string;
  username?: string;
  folder?: string;
  bucket?: string;
  region?: string;
  pathStyle?: boolean;
  branch?: string;
  interval?: string;
  keys?: boolean; // --no-keys gives false
  signins?: boolean;
  sections?: string;
  secretStdin?: boolean;
  passphraseStdin?: boolean;
}

/** Validates the flags and builds the target part of the PUT /api/sync/config body (without the secret). */
export function buildSyncTarget(opts: SyncSetupOptions): {
  kind: string;
  url?: string;
  username?: string;
  folder?: string;
  bucket?: string;
  region?: string;
  pathStyle?: boolean;
  branch?: string;
} {
  if (!opts.kind || !(SYNC_KINDS as readonly string[]).includes(opts.kind)) {
    throw new AstraError(`--kind must be one of ${SYNC_KINDS.join(', ')}.`);
  }
  if (opts.kind !== 's3' && !opts.url) {
    const hint: Record<string, string> = {
      folder: 'The folder path to sync through.',
      webdav: 'The WebDAV address.',
      github: 'The repository, e.g. https://github.com/me/astra-sync.',
    };
    throw new AstraError(`--url is required for ${opts.kind}.`, hint[opts.kind]);
  }
  if (opts.kind === 's3' && !opts.bucket) throw new AstraError('--bucket is required for s3.');
  return {
    kind: opts.kind,
    url: opts.url,
    username: opts.username,
    folder: opts.folder,
    bucket: opts.bucket,
    region: opts.region,
    pathStyle: opts.pathStyle,
    branch: opts.branch,
  };
}

export async function runSyncSetup(opts: SyncSetupOptions): Promise<void> {
  const target = buildSyncTarget(opts);
  const interval = parsePositiveInt(opts.interval, '--interval');
  const sections = parseSections(opts.sections);
  // A folder needs no secret; WebDAV password, S3 secret key or GitHub token are optional (the stored one stays when omitted).
  let secret: string | undefined;
  if (target.kind !== 'folder') {
    if (process.env[SYNC_SECRET_ENV] || opts.secretStdin) {
      secret = await readSecret({ env: SYNC_SECRET_ENV, question: 'Target password / token: ', what: 'secret' });
    } else if (process.stdin.isTTY) {
      secret = (await promptHidden('Target password / token (Enter to keep the stored one): ')) || undefined;
    }
  }
  const passphrase = await readSecret({
    env: SYNC_PASSPHRASE_ENV,
    question: 'Sync passphrase: ',
    confirm: !opts.passphraseStdin,
  });
  const s = await api<SyncStatus>('/api/sync/config', 'PUT', {
    target: { ...target, secret },
    passphrase,
    intervalMinutes: interval,
    sections,
    includeKeys: opts.keys === false ? false : undefined,
    includeSignins: opts.signins ? true : undefined,
  });
  success(`Sync target saved (${s.target.kind}). Run \`astra sync test\`, then \`astra sync enable\`.`);
  warn(PASSPHRASE_NOTE);
}
