import { existsSync } from 'node:fs';
import { chmod, readFile, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';

import { AstraError } from '../errors.js';
import { printTable, success } from '../ui.js';
import { promptHidden } from '../lib/prompt.js';
import { apiRequest, ApiError } from '../lib/http.js';
import { ensureServerRunning } from '../lib/server-lifecycle.js';
import { CLIENT_KINDS, type ClientKind } from '../lib/platform.js';

interface ClientRow {
  kind: string;
  name?: string;
  enabled?: boolean;
  status?: string;
  providerId?: string;
  selectedModel?: string;
  availability?: string;
  tokenId?: string;
}

interface TokenStatsRow {
  costUsd?: number;
  requests?: number;
  totalTokens?: number;
}

interface TokenRow {
  id: string;
  name?: string;
  isDefault?: boolean;
  enabled?: boolean;
  clients?: string[];
  today?: TokenStatsRow;
  total?: TokenStatsRow;
}

interface ProviderRow {
  id: string;
  name?: string;
  templateId?: string;
  enabled?: boolean;
  modelCount?: number;
}

interface TemplateRow {
  id: string;
  name?: string;
  variants?: { id: string; name?: string }[];
}

interface ApiCallOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
}

async function api<T>(path: string, options: ApiCallOptions = {}): Promise<T> {
  const { base, runtime, started } = await ensureServerRunning();
  if (started) console.log('Server was not running — started it in the background.');
  return apiRequest<T>({
    base,
    path,
    method: options.method ?? 'GET',
    body: options.body,
    runtimeToken: runtime?.runtimeToken,
  });
}

function fmtBool(v: boolean | undefined): string {
  if (v === undefined) return '';
  return v ? 'yes' : 'no';
}

function findProvider(providers: ProviderRow[], idOrName: string): ProviderRow | undefined {
  const needle = idOrName.toLowerCase();
  const byId = providers.find((p) => p.id.toLowerCase() === needle);
  if (byId) return byId;
  const byName = providers.filter((p) => p.name?.toLowerCase() === needle);
  if (byName.length === 1) return byName[0];
  if (byName.length > 1) {
    throw new AstraError(
      `Provider name "${idOrName}" is ambiguous.`,
      `Matching ids: ${byName.map((p) => p.id).join(', ')} — use an id instead.`,
    );
  }
  return undefined;
}

function findToken(tokens: TokenRow[], idOrName: string): TokenRow | undefined {
  const needle = idOrName.toLowerCase();
  const byId = tokens.find((t) => t.id.toLowerCase() === needle);
  if (byId) return byId;
  const byName = tokens.filter((t) => t.name?.toLowerCase() === needle);
  if (byName.length === 1) return byName[0];
  if (byName.length > 1) {
    throw new AstraError(
      `Token name "${idOrName}" is ambiguous.`,
      `Matching ids: ${byName.map((t) => t.id).join(', ')} — use an id instead.`,
    );
  }
  return undefined;
}

function fmtUsd(v: number | undefined): string {
  return v === undefined ? '' : `$${v.toFixed(v !== 0 && Math.abs(v) < 0.01 ? 4 : 2)}`;
}

// ---------- client ----------

export async function runClientList(): Promise<void> {
  const rows = await api<ClientRow[]>('/api/clients');
  printTable(
    rows.map((r) => [
      r.kind,
      r.name ?? '',
      fmtBool(r.enabled),
      r.status ?? '',
      r.providerId ?? '',
      r.selectedModel ?? '',
      r.availability ?? '',
    ]),
    ['KIND', 'NAME', 'ENABLED', 'STATUS', 'PROVIDER', 'MODEL', 'AVAILABILITY'],
  );
}

export async function runClientStatus(kind: ClientKind): Promise<void> {
  const rows = await api<ClientRow[]>('/api/clients');
  const row = rows.find((r) => r.kind === kind);
  if (!row) throw new AstraError(`Unknown client kind: ${kind}.`);
  console.log(`Client: ${row.name ?? row.kind} (${row.kind})`);
  console.log(`  Enabled      ${fmtBool(row.enabled)}`);
  if (row.status) console.log(`  Status       ${row.status}`);
  if (row.availability) console.log(`  Availability ${row.availability}`);
  if (row.providerId) console.log(`  Provider     ${row.providerId}`);
  if (row.selectedModel) console.log(`  Model        ${row.selectedModel}`);
  if (row.tokenId) console.log(`  Token        ${row.tokenId}`);
}

/**
 * Enables a client. `token` (id or name) picks the token written into its config; omitted, the server keeps the
 * client's current token, or uses the default token for a client that never had one.
 */
export async function runClientEnable(
  kind: ClientKind,
  opts: { provider: string; model?: string; token?: string },
): Promise<void> {
  const providers = await api<ProviderRow[]>('/api/providers');
  const provider = findProvider(providers, opts.provider);
  if (!provider) {
    throw new AstraError(
      `No provider matches "${opts.provider}".`,
      'Run `astra provider list` to see available providers.',
    );
  }
  let token: TokenRow | undefined;
  if (opts.token) {
    token = findToken(await api<TokenRow[]>('/api/tokens'), opts.token);
    if (!token) {
      throw new AstraError(
        `No token matches "${opts.token}".`,
        'Run `astra token list` to see available tokens.',
      );
    }
  }
  await api(`/api/clients/${kind}/enable`, {
    method: 'POST',
    body: {
      providerId: provider.id,
      ...(opts.model ? { model: opts.model } : {}),
      ...(token ? { tokenId: token.id } : {}),
    },
  });
  success(
    `Client "${kind}" enabled with provider "${provider.name ?? provider.id}"` +
      `${opts.model ? ` (model: ${opts.model})` : ''}` +
      `${token ? ` (token: ${token.name ?? token.id})` : ''}.`,
  );
  console.log('Restart the client (Codex, Claude Desktop, …) if it does not pick up the change.');
}

export async function runClientDisable(kind: ClientKind): Promise<void> {
  await api(`/api/clients/${kind}/disable`, { method: 'POST', body: {} });
  success(`Client "${kind}" disabled. Its original configuration was restored.`);
}

// ---------- token ----------

/** Lists tokens with today's and lifetime usage; the plaintext token is never printed. */
export async function runTokenList(): Promise<void> {
  const rows = await api<TokenRow[]>('/api/tokens');
  printTable(
    rows.map((r) => [
      r.id,
      r.name ?? '',
      fmtBool(r.isDefault),
      fmtBool(r.enabled),
      (r.clients ?? []).join(','),
      fmtUsd(r.today?.costUsd),
      r.today?.totalTokens === undefined ? '' : String(r.today.totalTokens),
      fmtUsd(r.total?.costUsd),
    ]),
    ['ID', 'NAME', 'DEFAULT', 'ENABLED', 'CLIENTS', 'TODAY COST', 'TODAY TOKENS', 'TOTAL COST'],
  );
}

// ---------- provider ----------

export async function runProviderList(): Promise<void> {
  const rows = await api<ProviderRow[]>('/api/providers');
  printTable(
    rows.map((r) => [
      r.id,
      r.name ?? '',
      r.templateId ?? '',
      fmtBool(r.enabled),
      r.modelCount === undefined ? '' : String(r.modelCount),
    ]),
    ['ID', 'NAME', 'TEMPLATE', 'ENABLED', 'MODELS'],
  );
}

export async function runProviderAdd(opts: {
  template: string;
  variant?: string;
  key: string;
  name?: string;
}): Promise<void> {
  const templates = await api<TemplateRow[]>('/api/provider-templates');
  const needle = opts.template.toLowerCase();
  const template = templates.find(
    (t) => t.id.toLowerCase() === needle || t.name?.toLowerCase() === needle,
  );
  if (!template) {
    throw new AstraError(
      `Unknown provider template "${opts.template}".`,
      `Available templates: ${templates.map((t) => t.id).join(', ') || '(none reported)'}`,
    );
  }
  const variants = template.variants ?? [];
  let variantId: string | undefined = opts.variant;
  if (variants.length > 0 && !variantId) {
    throw new AstraError(
      `Template "${template.id}" has variants — pass --variant.`,
      `Variants: ${variants.map((v) => v.id).join(', ')}`,
    );
  }
  if (variantId && variants.length > 0 && !variants.some((v) => v.id === variantId)) {
    throw new AstraError(
      `Unknown variant "${variantId}" for template "${template.id}".`,
      `Variants: ${variants.map((v) => v.id).join(', ')}`,
    );
  }
  if (variants.length === 0) variantId = undefined;
  const created = await api<ProviderRow>('/api/providers', {
    method: 'POST',
    body: {
      templateId: template.id,
      ...(variantId ? { variantId } : {}),
      ...(opts.name ? { name: opts.name } : {}),
      apiKey: opts.key,
    },
  });
  success(`Provider added: ${created.name ?? created.id} (id: ${created.id}).`);
}

export async function runProviderRemove(id: string): Promise<void> {
  try {
    await api(`/api/providers/${encodeURIComponent(id)}`, { method: 'DELETE' });
  } catch (err) {
    if (err instanceof ApiError && err.status === 409) {
      throw new AstraError(
        `Provider "${id}" is still bound to one or more clients.`,
        'Disable the client or bind it to another provider first (`astra client disable <kind>`).',
      );
    }
    throw err;
  }
  success(`Provider "${id}" removed.`);
}

// ---------- provider import ----------

export const IMPORT_SOURCES = ['cc-switch', 'alma', 'claude-code', 'codex', 'magpie'] as const;

interface ImportCandidateRow {
  ref: string;
  source: string;
  name: string;
  endpoints: { protocol: string; baseUrl: string }[];
  hasKey: boolean;
  keyMasked?: string | null;
  models: string[];
  status: 'new' | 'same' | 'sameHost' | 'off' | 'skip';
  existing?: { name: string } | null;
  skipReason?: string | null;
  ignoredFields: string[];
}

interface ImportSourceRow {
  id: string;
  name: string;
  path?: string | null;
  found: boolean;
  error?: string | null;
  items: ImportCandidateRow[];
}

interface ImportResultRow {
  added: { id: string; name: string }[];
  skipped: { ref: string; reason: string }[];
}

const SKIP_TEXT: Record<string, string> = {
  'official-login': 'official login (not importable)',
  'no-base-url': 'no base URL',
  'unsupported-protocol': 'unsupported protocol',
  'points-to-astra': 'points at Astra itself',
  'invalid-base-url': 'invalid base URL',
  'no-inline-key': 'key comes from an environment variable',
};

function importNote(c: ImportCandidateRow): string {
  const notes: string[] = [];
  if (c.skipReason) notes.push(SKIP_TEXT[c.skipReason] ?? c.skipReason);
  if (c.status === 'same' && c.existing) notes.push(`already added as "${c.existing.name}"`);
  if (c.status === 'sameHost' && c.existing) notes.push(`same address as "${c.existing.name}"`);
  if (c.ignoredFields.length > 0) notes.push(`ignores ${c.ignoredFields.join(', ')}`);
  return notes.join('; ');
}

/**
 * Lists providers found in other apps (read-only), or with `yes` imports them. The server re-reads the sources on
 * import, so no key ever passes through the CLI; selection is by `ref`, and by default only status `new` entries.
 */
export async function runProviderImport(opts: { from?: string; yes?: boolean; only?: string[] }): Promise<void> {
  if (opts.from && !(IMPORT_SOURCES as readonly string[]).includes(opts.from)) {
    throw new AstraError(`Unknown import source "${opts.from}".`, `Sources: ${IMPORT_SOURCES.join(', ')}`);
  }
  if (opts.only?.length && !opts.yes) {
    throw new AstraError('--only needs --yes.', 'Run without options first to see the refs, then add --yes.');
  }
  const query = opts.from ? `?source=${encodeURIComponent(opts.from)}` : '';
  const sources = await api<ImportSourceRow[]>(`/api/providers/import/sources${query}`);
  const all = sources.flatMap((s) => s.items);

  if (!opts.yes) {
    for (const s of sources) {
      const state = s.error ? `error: ${s.error}` : s.found ? `${s.items.length} found` : 'not found';
      console.log(`${s.name}${s.path ? `  (${s.path})` : ''} — ${state}`);
      if (s.items.length > 0) {
        printTable(
          s.items.map((c) => [
            c.ref,
            c.status,
            c.name,
            c.endpoints.map((e) => `${e.protocol} ${e.baseUrl}`).join(' | '),
            c.hasKey ? (c.keyMasked ?? '••••') : '-',
            String(c.models.length),
            importNote(c),
          ]),
          ['REF', 'STATUS', 'NAME', 'ENDPOINTS', 'KEY', 'MODELS', 'NOTE'],
        );
      }
      console.log('');
    }
    const fresh = all.filter((c) => c.status === 'new').length;
    console.log(
      fresh > 0
        ? `Nothing was changed (sources are only read). Run again with --yes to import the ${fresh} new entr${fresh === 1 ? 'y' : 'ies'}, or --yes --only <ref>… to pick.`
        : 'Nothing new to import.',
    );
    return;
  }

  let chosen: ImportCandidateRow[];
  if (opts.only?.length) {
    chosen = [];
    for (const ref of opts.only) {
      const found = all.find((c) => c.ref === ref);
      if (!found) throw new AstraError(`No importable entry "${ref}".`, 'Run `astra provider import` to see the refs.');
      if (found.status === 'skip' || found.status === 'same') {
        throw new AstraError(`"${ref}" cannot be imported (${found.status === 'same' ? 'already added' : importNote(found)}).`);
      }
      chosen.push(found);
    }
  } else {
    chosen = all.filter((c) => c.status === 'new');
  }
  if (chosen.length === 0) {
    console.log('Nothing to import.');
    return;
  }
  const result = await api<ImportResultRow>('/api/providers/import', {
    method: 'POST',
    body: chosen.map((c) => ({ source: c.source, ref: c.ref })),
  });
  for (const a of result.added) success(`Imported: ${a.name} (id: ${a.id}).`);
  for (const k of result.skipped) console.log(`Skipped ${k.ref}: ${SKIP_TEXT[k.reason] ?? k.reason}`);
}

// ---------- provider backup file (issue #33, .plan/provider-backup.md) ----------

/** Where a backup's passphrase may come from besides stdin; never argv. */
const BACKUP_PASSPHRASE_ENV = 'ASTRA_BACKUP_PASSPHRASE';
/** A backup is a few hundred KB at most; refuse anything absurd before parsing it. */
const BACKUP_MAX_BYTES = 32 * 1024 * 1024;

interface BackupFileDoc {
  format?: string;
  secrets?: string;
  providers?: unknown[];
  [key: string]: unknown;
}

interface BackupPreviewRow {
  index: number;
  name: string;
  status: 'new' | 'sameHost' | 'same' | 'invalid';
  modelCount: number;
  endpoints?: { protocol: string; baseUrl: string }[];
  hasKey: boolean;
  keyMasked?: string | null;
  existing?: { name: string };
  error?: string;
  ignored: string[];
}

/** The passphrase from the environment, else read from stdin (a hidden prompt on a terminal). Never from argv. */
async function readBackupPassphrase(question: string): Promise<string> {
  const fromEnv = process.env[BACKUP_PASSPHRASE_ENV];
  if (fromEnv) return fromEnv;
  const entered = await promptHidden(question);
  if (!entered) throw new AstraError('No passphrase given.', `Pipe it on stdin or set ${BACKUP_PASSPHRASE_ENV}.`);
  return entered;
}

function backupNote(r: BackupPreviewRow): string {
  const notes: string[] = [];
  if (r.error) notes.push(r.error);
  if (r.status === 'same' && r.existing) notes.push(`already added as "${r.existing.name}"`);
  if (r.status === 'sameHost' && r.existing) notes.push(`same address as "${r.existing.name}"`);
  if (r.ignored.length > 0) notes.push(`ignores ${r.ignored.join(', ')}`);
  return notes.join('; ');
}

/**
 * Exports providers and their models to a file. No API keys unless asked: a passphrase (stdin or
 * ASTRA_BACKUP_PASSPHRASE) seals them, `--include-keys` writes them in the clear. Neither ever goes on argv, and the file is
 * created owner-only (0600) and never replaced without `--force`.
 */
export async function runProviderExport(opts: {
  out?: string;
  force?: boolean;
  includeKeys?: boolean;
  passphraseStdin?: boolean;
  id?: string[];
}): Promise<void> {
  const sealed = Boolean(opts.passphraseStdin) || Boolean(process.env[BACKUP_PASSPHRASE_ENV]);
  if (sealed && opts.includeKeys) {
    throw new AstraError('--include-keys and a passphrase exclude each other.', 'Choose plaintext keys or an encrypted file, not both.');
  }
  const out = path.resolve(opts.out ?? `astra-providers-${new Date().toISOString().slice(0, 10)}.json`);
  if (!opts.force && existsSync(out)) {
    throw new AstraError(`${out} already exists.`, 'Pass --force to replace it, or choose another file with --out.');
  }
  const secrets = sealed ? 'passphrase' : opts.includeKeys ? 'plaintext' : 'none';
  const passphrase = sealed ? await readBackupPassphrase('Passphrase for the backup: ') : undefined;

  const file = await api<BackupFileDoc>('/api/providers/backup/export', {
    method: 'POST',
    body: { secrets, passphrase, ids: opts.id?.length ? opts.id : undefined },
  });
  await writeFile(out, `${JSON.stringify(file, null, 2)}\n`, { mode: 0o600 });
  await chmod(out, 0o600); // an existing file (--force) keeps its old mode otherwise
  const count = file.providers?.length ?? 0;
  success(`Exported ${count} provider${count === 1 ? '' : 's'} to ${out}.`);
  if (secrets === 'none') console.log('The file has no API keys; enter them again after importing.');
  else if (secrets === 'passphrase') console.log('API keys are encrypted with your passphrase; it cannot be recovered if lost.');
  else console.log('Warning: API keys are in the file in plain text. Keep it where only you can read it.');
}

/**
 * Previews a backup file against this Astra, or with `yes` creates its providers. Only masks are printed; a file whose keys are
 * encrypted needs its passphrase (stdin or ASTRA_BACKUP_PASSPHRASE). `--only` takes the row numbers of the preview.
 */
export async function runProviderImportFile(
  file: string,
  opts: { yes?: boolean; only?: string[] },
): Promise<void> {
  const only = opts.only?.map((n) => Number(n));
  if (only?.length && !opts.yes) {
    throw new AstraError('--only needs --yes.', 'Run without options first to see the row numbers, then add --yes.');
  }
  if (only?.some((n) => !Number.isInteger(n) || n < 0)) {
    throw new AstraError('--only takes the row numbers shown by the preview.');
  }
  const abs = path.resolve(file);
  let doc: BackupFileDoc;
  try {
    if ((await stat(abs)).size > BACKUP_MAX_BYTES) throw new AstraError(`${abs} is too large to be a provider backup.`);
    doc = JSON.parse(await readFile(abs, 'utf8')) as BackupFileDoc;
  } catch (e) {
    if (e instanceof AstraError) throw e;
    throw new AstraError(`Cannot read ${abs} as a backup file.`, e instanceof Error ? e.message : undefined);
  }
  if (doc.format !== 'astra-providers' || !Array.isArray(doc.providers)) {
    throw new AstraError(`${abs} is not an Astra provider backup file.`);
  }
  const passphrase = doc.secrets === 'passphrase' ? await readBackupPassphrase('Passphrase of the backup: ') : undefined;

  const preview = await api<{ items: BackupPreviewRow[] }>('/api/providers/backup/preview', {
    method: 'POST',
    body: { file: doc, passphrase },
  });
  printTable(
    preview.items.map((r) => [
      String(r.index),
      r.status,
      r.name,
      (r.endpoints ?? []).map((e) => `${e.protocol} ${e.baseUrl}`).join(' | '),
      r.hasKey ? (r.keyMasked ?? '••••') : '-',
      String(r.modelCount),
      backupNote(r),
    ]),
    ['ROW', 'STATUS', 'NAME', 'ENDPOINTS', 'KEY', 'MODELS', 'NOTE'],
  );

  const importable = (r: BackupPreviewRow) => r.status === 'new' || r.status === 'sameHost';
  if (!opts.yes) {
    const fresh = preview.items.filter((r) => r.status === 'new').length;
    console.log('');
    console.log(
      fresh > 0
        ? `Nothing was changed. Run again with --yes to import the ${fresh} new entr${fresh === 1 ? 'y' : 'ies'}, or --yes --only <row>… to pick.`
        : 'Nothing new to import.',
    );
    return;
  }

  let indexes: number[];
  if (only?.length) {
    indexes = [];
    for (const n of only) {
      const row = preview.items.find((r) => r.index === n);
      if (!row) throw new AstraError(`No row ${n} in this backup.`);
      if (!importable(row)) throw new AstraError(`Row ${n} cannot be imported (${row.status === 'same' ? 'already added' : backupNote(row) || row.status}).`);
      indexes.push(n);
    }
  } else {
    indexes = preview.items.filter((r) => r.status === 'new').map((r) => r.index);
  }
  if (indexes.length === 0) {
    console.log('Nothing to import.');
    return;
  }
  const result = await api<{ added: { id: string; name: string }[]; skipped: { index: number; reason: string }[] }>(
    '/api/providers/backup/import',
    { method: 'POST', body: { file: doc, passphrase, indexes } },
  );
  for (const a of result.added) success(`Imported: ${a.name} (id: ${a.id}).`);
  for (const k of result.skipped) console.log(`Skipped row ${k.index}: ${k.reason}`);
}

export const KNOWN_CLIENT_KINDS: readonly string[] = CLIENT_KINDS;
