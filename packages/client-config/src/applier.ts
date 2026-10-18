import fs from 'node:fs';
import path from 'node:path';
import { parse as parseJsoncValue, type ParseError } from 'jsonc-parser';
import { parseTOML, getStaticTOMLValue } from 'toml-eslint-parser';
import { decodeToSource, encode, equalsValue, isJsonValued } from './config-value-codec.js';
import { createOps, type ConfigEditorOps } from './config-editor-ops.js';
import {
  ConfigChange,
  ConfigChangePlan,
  type ApplyReport,
  type AppliedKey,
  type BackupFileEntry,
  type BackupInfo,
  type BackupManifest,
  type FileDiff,
  type IClientAdapter,
  type IClientConfigStateStore,
  type RestoreReport,
  type RestoredKey,
} from './contracts.js';
import type { ClientEnvironment } from './env.js';
import { DiffTool } from './editing/diff-tool.js';
import { EditorException } from './editing/errors.js';
import { JsoncEditor } from './editing/jsonc-editor.js';
import { isStrict, repair as repairStrictJson } from './editing/strict-json.js';
import { isBlank } from './editing/text-tool.js';
import { YamlEditor } from './editing/yaml-editor.js';
import type { ConfigFileFormat } from './types.js';

const MAX_ROLLING_BACKUPS = 20;

/** The JSON document as it was before Astra's first write; `known` is false when that cannot be told. */
interface OriginalJsonDocument {
  known: boolean;
  root: unknown;
}

/**
 * Executes ConfigChangePlans against real files: first-write and rolling backups, state tracking (original vs
 * applied per key), atomic writes and drift-aware restores. Port of Astra.Clients ClientConfigApplier.
 */
export class ClientConfigApplier {
  /**
   * @param store Persistence for per-key original/applied values.
   * @param env Environment (file IO, home dir).
   * @param backupsRoot Where the per-client backups live (`~/.astra/backups/client-configs`).
   */
  constructor(
    private readonly store: IClientConfigStateStore,
    private readonly env: ClientEnvironment,
    private readonly backupsRoot: string,
  ) {}

  // ---------------------------------------------------------------- apply (enable / key rotation)

  /** Simulates a combined set of changes without writing files, backups or state. */
  preview(clientKind: string, changes: readonly ConfigChange[]): ConfigChangePlan {
    const diffs: FileDiff[] = [];
    for (const [file, group] of new ConfigChangePlan(clientKind, changes, []).byFile()) {
      const before = this.env.readTextOrNull(file) ?? '';
      const ops = createOps(group[0]!.format, before);
      const warnings: string[] = [];
      for (const change of group) ops.apply(change, warnings);
      if (ops.text !== before) diffs.push({ file, unifiedDiff: DiffTool.unified(file, before, ops.text) });
    }
    return new ConfigChangePlan(clientKind, changes, diffs);
  }

  /** Applies every change in the plan, taking first-write and rolling backups and recording state. */
  apply(plan: ConfigChangePlan): ApplyReport {
    const applied: AppliedKey[] = [];
    const warnings: string[] = [];
    let rollingDir: string | null = null;

    for (const [file, group] of plan.byFile()) {
      const format = group[0]!.format;
      const existed = this.env.fileExists(file);
      const read = this.env.readTextWithBom(file) ?? { text: '', hadBom: false };
      this.ensureFirstWriteBackup(plan.clientKind, file, existed);

      const ops = createOps(format, read.text);
      let originalDocument: OriginalJsonDocument | null = null;
      for (const change of group) {
        const before = ops.read(change);
        ops.apply(change, warnings);
        const after = ops.read(change);
        applied.push({
          filePath: file,
          keyPath: change.keyPath,
          wasAbsent: before === null,
          beforeJson: encode(format, before),
          afterJson: encode(format, after),
        });
        // Taking out a key Astra added (a model the provider no longer offers, or one that moved to another
        // protocol's list) puts it back to its original absence, like a restore: no state is left behind, and the
        // containers Astra created for it go too once empty.
        if (after === null && this.store.get(plan.clientKind, file, change.keyPath)?.originalAbsent) {
          if (isJsonValued(format))
            this.pruneEmptyParents(
              ops,
              change,
              (originalDocument ??= this.originalJson(
                plan.clientKind,
                file,
                format,
                this.readFirstWriteEntry(plan.clientKind, file),
              )),
            );
          this.store.delete(plan.clientKind, file, change.keyPath);
          continue;
        }
        this.upsertState(plan.clientKind, change, before, after);
      }

      rollingDir ??= this.createRollingBackupDir(plan.clientKind);
      this.addRollingBackup(rollingDir, file, existed);
      this.env.writeTextAtomic(file, ops.text, read.hadBom);
    }

    return { clientKind: plan.clientKind, applied, warnings };
  }

  // ---------------------------------------------------------------- repair

  /**
   * Rewrites JSON files the client reads strictly as strict JSON: their comments and trailing commas go, nothing
   * else changes. Takes the same first-write and rolling backups as any other write; no value changes, so no key
   * state is recorded. Files that are missing, already strict or not even JSONC are left alone.
   */
  repair(clientKind: string, files: Iterable<string>): string[] {
    const repaired: string[] = [];
    let rollingDir: string | null = null;
    for (const file of new Set(files)) {
      const read = this.env.readTextWithBom(file);
      const strict = read ? repairStrictJson(read.text) : null;
      if (!read || strict === null) continue;
      this.ensureFirstWriteBackup(clientKind, file, true);
      rollingDir ??= this.createRollingBackupDir(clientKind);
      this.addRollingBackup(rollingDir, file, true);
      this.env.writeTextAtomic(file, strict, read.hadBom);
      repaired.push(file);
    }
    return repaired;
  }

  // ---------------------------------------------------------------- restore (disable / force / restore-all)

  /**
   * Restores every key in the plan: when the current value still equals what Astra wrote, the original value (or
   * absence) is restored; drifted keys are skipped and reported.
   */
  disable(plan: ConfigChangePlan): RestoreReport {
    return this.restore(plan, false);
  }

  /** Like `disable`, but restores even drifted keys. */
  forceRestore(plan: ConfigChangePlan): RestoreReport {
    return this.restore(plan, true);
  }

  /** Offline restore: disables every listed client (with purge plans when `purge` is set). */
  restoreAll(adapters: readonly IClientAdapter[], purge: boolean): RestoreReport[] {
    return adapters.map((a) => this.disable(purge ? a.planPurge() : a.planDisable()));
  }

  private restore(plan: ConfigChangePlan, force: boolean): RestoreReport {
    const keys: RestoredKey[] = [];
    const warnings: string[] = [];
    const deletedFiles: string[] = [];

    for (const [file, group] of plan.byFile()) {
      const format = group[0]!.format;
      const firstWriteEntry = this.readFirstWriteEntry(plan.clientKind, file);
      const existedOriginally = firstWriteEntry?.existed ?? this.env.fileExists(file);

      const read = this.env.readTextWithBom(file) ?? { text: '', hadBom: false };
      const ops = createOps(format, read.text);
      let fileChanged = false;
      let fileHasDrift = false;
      let originalDocument: OriginalJsonDocument | null = null;

      for (const change of group) {
        const state = this.store.get(plan.clientKind, file, change.keyPath);
        if (state === null) {
          keys.push({ filePath: file, keyPath: change.keyPath, outcome: 'notTracked' });
          continue;
        }

        if (!this.env.fileExists(file)) {
          if (state.originalAbsent) {
            this.store.delete(plan.clientKind, file, change.keyPath);
            keys.push({ filePath: file, keyPath: change.keyPath, outcome: 'restored', detail: 'file no longer exists' });
          } else {
            fileHasDrift = true;
            keys.push({ filePath: file, keyPath: change.keyPath, outcome: 'drifted', detail: 'file no longer exists' });
          }
          continue;
        }

        const current = ops.read(change);
        const drifted =
          change.kind === 'table'
            ? current !== 'true'
            : !(change.stillOurs?.(current, state.appliedValueJson) ?? equalsValue(format, current, state.appliedValueJson));

        if (drifted && !force) {
          fileHasDrift = true;
          keys.push({
            filePath: file,
            keyPath: change.keyPath,
            outcome: 'drifted',
            detail: 'current value differs from what Astra wrote',
          });
          continue;
        }

        if (state.originalAbsent) {
          ops.remove(change);
          if (isJsonValued(format))
            this.pruneEmptyParents(
              ops,
              change,
              (originalDocument ??= this.originalJson(plan.clientKind, file, format, firstWriteEntry)),
            );
        } else if (change.kind !== 'table') {
          const originalSource = decodeToSource(format, state.originalValueJson);
          if (originalSource === null) {
            warnings.push(`State entry for '${change.keyPath}' has no original value; skipped.`);
            continue;
          }
          ops.restoreSource(change, originalSource);
        }
        this.store.delete(plan.clientKind, file, change.keyPath);
        keys.push({ filePath: file, keyPath: change.keyPath, outcome: 'restored' });
        fileChanged = true;
      }

      if (fileChanged || ops.text !== read.text) {
        // A file we created ourselves that no longer holds any key/value content is deleted.
        if (!existedOriginally && hasNoUserContent(format, ops.text)) {
          this.env.deleteFile(file);
          deletedFiles.push(file);
          continue;
        }
        this.env.writeTextAtomic(file, ops.text, read.hadBom);
      }

      if (!fileHasDrift && fileChanged && existedOriginally) this.reconcileToOriginal(plan.clientKind, file, format);
    }

    return { clientKind: plan.clientKind, keys, deletedFiles, warnings };
  }

  private originalJson(
    clientKind: string,
    file: string,
    format: ConfigFileFormat,
    firstWrite: BackupFileEntry | null,
  ): OriginalJsonDocument {
    if (firstWrite === null) return { known: false, root: null };
    if (!firstWrite.existed) return { known: true, root: {} }; // we created the file
    if (firstWrite.backupFile === null) return { known: false, root: null };
    const bytes = this.env.readAllBytesOrNull(this.firstWriteFile(clientKind, firstWrite.backupFile));
    if (bytes === null) return { known: false, root: null };
    try {
      const text = stripBom(bytes).toString('utf8');
      return { known: true, root: format === 'yaml' ? YamlEditor.parseToJson(text) : parseTolerantJson(text) };
    } catch (e) {
      if (e instanceof EditorException || e instanceof SyntaxError) return { known: false, root: null };
      throw e;
    }
  }

  /**
   * After removing a key Astra added, also removes the objects (or arrays) Astra had to create to hold it (e.g.
   * "provider" for "provider.astra") once they are empty — but only when they did not exist before Astra's first write.
   */
  private pruneEmptyParents(ops: ConfigEditorOps, change: ConfigChange, original: OriginalJsonDocument): void {
    if (!original.known) return;
    const segments = JsoncEditor.splitKeyPath(change.keyPath);
    for (let depth = segments.length - 1; depth >= 1; depth--) {
      const parent = new ConfigChange(change.file, change.format, segments.slice(0, depth).join('.'), null, null);
      const text = ops.read(parent);
      if (text === null) return;
      const node: unknown = JSON.parse(text);
      const empty = Array.isArray(node) ? node.length === 0 : isPlainObject(node) && Object.keys(node).length === 0;
      if (!empty) return;
      if (existsIn(original.root, segments.slice(0, depth))) return;
      ops.remove(parent);
    }
  }

  /**
   * After a clean restore the file is semantically what it was, but our splices can leave cosmetic differences (a
   * comma kept before '}', indentation of a re-inserted block, trailing newlines). When the current content still
   * parses to the same document as the original, restore the original bytes wholesale — byte-identical output for
   * untouched user keys.
   */
  private reconcileToOriginal(clientKind: string, file: string, format: ConfigFileFormat): void {
    const entry = this.readFirstWriteEntry(clientKind, file);
    if (entry?.backupFile == null) return;
    const originalBytes = this.env.readAllBytesOrNull(this.firstWriteFile(clientKind, entry.backupFile));
    const currentBytes = this.env.readAllBytesOrNull(file);
    if (originalBytes === null || currentBytes === null) return;
    const originalText = stripBom(originalBytes).toString('utf8');
    const currentText = stripBom(currentBytes).toString('utf8');
    if (currentText === originalText) return;
    // Never undo a Repair: Astra's own edits keep a file's comments and trailing commas, so a strict file whose
    // original was not strict has been repaired, and its original bytes are what the client could not read.
    if (format === 'json' && isStrict(currentText) && !isStrict(originalText)) return;
    if (semanticallyEqual(format, currentText, originalText) || onlyTrailingNewlinesDiffer(currentText, originalText))
      this.env.writeAllBytes(file, originalBytes);
  }

  // ---------------------------------------------------------------- backups

  /** Lists the backups of one client, oldest first ("first-write" plus up to 20 rolling backups). */
  listBackups(clientKind: string): BackupInfo[] {
    const clientDir = path.join(this.backupsRoot, clientKind);
    if (!this.env.directoryExists(clientDir)) return [];
    const result: BackupInfo[] = [];
    for (const name of fs.readdirSync(clientDir).sort(ordinal)) {
      const dir = path.join(clientDir, name);
      if (!this.env.directoryExists(dir)) continue;
      const manifest = this.readManifest(dir);
      if (manifest === null) continue;
      result.push({
        clientKind,
        backupId: name,
        createdAt: manifest.createdAt,
        isFirstWrite: name === 'first-write',
        files: [...manifest.entries],
      });
    }
    return result;
  }

  /** Restores every file of one backup to its original location (deleting files that did not exist). */
  restoreFromBackup(clientKind: string, backupId: string): void {
    const dir = path.join(this.backupsRoot, clientKind, backupId);
    const manifest = this.readManifest(dir);
    if (manifest === null) throw new Error(`Backup '${clientKind}/${backupId}' not found.`);
    for (const e of manifest.entries) {
      if (!e.existed || e.backupFile === null) {
        this.env.deleteFile(e.originalPath);
        continue;
      }
      const bytes = this.env.readAllBytesOrNull(path.join(dir, e.backupFile));
      if (bytes === null) this.env.deleteFile(e.originalPath);
      else this.env.writeAllBytes(e.originalPath, bytes);
    }
  }

  private readFirstWriteEntry(clientKind: string, file: string): BackupFileEntry | null {
    return this.readManifest(this.firstWriteDir(clientKind))?.entries.find((e) => e.originalPath === file) ?? null;
  }

  private firstWriteDir(clientKind: string): string {
    return path.join(this.backupsRoot, clientKind, 'first-write');
  }

  private firstWriteFile(clientKind: string, file: string): string {
    return path.join(this.firstWriteDir(clientKind), file);
  }

  private readManifest(dir: string): BackupManifest | null {
    const manifestPath = path.join(dir, 'manifest.json');
    if (!this.env.fileExists(manifestPath)) return null;
    try {
      const raw = JSON.parse(this.env.readTextOrNull(manifestPath) ?? '') as Partial<BackupManifest>;
      return { createdAt: raw.createdAt ?? new Date().toISOString(), entries: raw.entries ?? [] };
    } catch (e) {
      if (e instanceof SyntaxError) return null;
      throw e;
    }
  }

  /** Keeps, forever, one full copy of each file before Astra's very first write to it. */
  private ensureFirstWriteBackup(clientKind: string, file: string, existed: boolean): void {
    const dir = this.firstWriteDir(clientKind);
    const manifest = this.readManifest(dir) ?? { createdAt: new Date().toISOString(), entries: [] };
    if (manifest.entries.some((e) => e.originalPath === file)) return;
    const backupFile = existed ? this.writeBackupCopy(dir, file) : null;
    manifest.entries.push({ originalPath: file, existed, backupFile });
    this.writeManifest(dir, manifest);
  }

  private createRollingBackupDir(clientKind: string): string {
    const clientDir = path.join(this.backupsRoot, clientKind);
    fs.mkdirSync(clientDir, { recursive: true });
    const baseName = timestamp(new Date());
    let dir = path.join(clientDir, baseName);
    for (let i = 0; this.env.directoryExists(dir); i++) dir = path.join(clientDir, `${baseName}-${i}`);
    fs.mkdirSync(dir, { recursive: true });
    this.pruneRollingBackups(clientDir);
    return dir;
  }

  private addRollingBackup(dir: string, file: string, existed: boolean): void {
    const manifest = this.readManifest(dir) ?? { createdAt: new Date().toISOString(), entries: [] };
    const backupFile = existed ? this.writeBackupCopy(dir, file) : null;
    manifest.entries.push({ originalPath: file, existed, backupFile });
    this.writeManifest(dir, manifest);
  }

  private pruneRollingBackups(clientDir: string): void {
    const dirs = fs
      .readdirSync(clientDir)
      .filter((n) => n !== 'first-write')
      .sort(ordinal)
      .reverse();
    for (let i = MAX_ROLLING_BACKUPS; i < dirs.length; i++) {
      try {
        fs.rmSync(path.join(clientDir, dirs[i]!), { recursive: true, force: true });
      } catch {
        // best effort
      }
    }
  }

  private writeManifest(dir: string, manifest: BackupManifest): void {
    fs.mkdirSync(dir, { recursive: true });
    this.env.writeTextAtomic(path.join(dir, 'manifest.json'), JSON.stringify(manifest, null, 2), false);
  }

  /** Writes the backup copy of `file` into `dir` and returns the unique file name used. */
  private writeBackupCopy(dir: string, file: string): string {
    const bytes = this.env.readAllBytesOrNull(file) ?? Buffer.alloc(0);
    const fileName = sanitizeFileName(path.basename(file));
    let candidate = fileName;
    const ext = path.extname(fileName);
    const stem = ext ? fileName.slice(0, -ext.length) : fileName;
    for (let i = 0; this.env.fileExists(path.join(dir, candidate)); ) candidate = `${stem}_${++i}${ext}`;
    this.env.writeAllBytes(path.join(dir, candidate), bytes);
    return candidate;
  }

  private upsertState(clientKind: string, change: ConfigChange, before: string | null, after: string | null): void {
    const existing = this.store.get(clientKind, change.file, change.keyPath);
    const entry = existing ?? {
      clientKind,
      filePath: change.file,
      keyPath: change.keyPath,
      originalAbsent: before === null,
      originalValueJson: encode(change.format, before),
      appliedValueJson: null as string | null,
      appliedAt: '',
    };
    entry.appliedValueJson = encode(change.format, after);
    entry.appliedAt = new Date().toISOString();
    this.store.upsert(entry);
  }
}

// ---------------------------------------------------------------- helpers

const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

const isPlainObject = (v: unknown): v is Record<string, unknown> =>
  v !== null && typeof v === 'object' && !Array.isArray(v);

const stripBom = (bytes: Buffer): Buffer =>
  bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf ? bytes.subarray(3) : bytes;

/** `yyyyMMdd-HHmmss-fff` in UTC, the rolling backup directory name. */
function timestamp(d: Date): string {
  const p = (n: number, w = 2) => String(n).padStart(w, '0');
  return `${d.getUTCFullYear()}${p(d.getUTCMonth() + 1)}${p(d.getUTCDate())}-${p(d.getUTCHours())}${p(d.getUTCMinutes())}${p(d.getUTCSeconds())}-${p(d.getUTCMilliseconds(), 3)}`;
}

const sanitizeFileName = (name: string): string => name.replace(/[^A-Za-z0-9._-]/g, '_');

function parseTolerantJson(text: string): unknown {
  const errors: ParseError[] = [];
  const value: unknown = parseJsoncValue(text, errors, { allowTrailingComma: true });
  if (errors.length > 0) throw new SyntaxError('Invalid JSON');
  return value;
}

function existsIn(root: unknown, segments: string[]): boolean {
  let node = root;
  for (const segment of segments) {
    if (JsoncEditor.isSelector(segment)) {
      if (!Array.isArray(node)) return false;
      const match = node.find((e) => JsoncEditor.elementMatches(e, segment));
      if (match === undefined) return false;
      node = match;
      continue;
    }
    if (!isPlainObject(node) || !Object.hasOwn(node, segment)) return false;
    node = node[segment];
  }
  return true;
}

function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a instanceof Date && b instanceof Date) return a.getTime() === b.getTime();
  if (Array.isArray(a) || Array.isArray(b))
    return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((v, i) => deepEqual(v, b[i]));
  if (isPlainObject(a) && isPlainObject(b)) {
    const ka = Object.keys(a);
    return ka.length === Object.keys(b).length && ka.every((k) => Object.hasOwn(b, k) && deepEqual(a[k], b[k]));
  }
  return Number.isNaN(a) && Number.isNaN(b);
}

const tomlValue = (text: string): unknown => getStaticTOMLValue(parseTOML(text));

/** True when both texts parse to the same document (format-aware). */
function semanticallyEqual(format: ConfigFileFormat, a: string, b: string): boolean {
  if (a === b) return true;
  try {
    switch (format) {
      case 'json':
        return deepEqual(parseTolerantJson(a), parseTolerantJson(b));
      case 'yaml':
        return deepEqual(YamlEditor.parseToJson(a), YamlEditor.parseToJson(b));
      case 'toml':
        return deepEqual(tomlValue(a), tomlValue(b));
      default:
        return false;
    }
  } catch {
    return false;
  }
}

const onlyTrailingNewlinesDiffer = (current: string, original: string): boolean =>
  current.replace(/[\r\n]+$/, '') === original.replace(/[\r\n]+$/, '');

/** True when the text has no key/value content left — only empty scaffolding such as `[models]` headers. */
function hasNoUserContent(format: ConfigFileFormat, text: string): boolean {
  if (isBlank(text)) return true;
  try {
    switch (format) {
      case 'toml':
        return tomlHasNoPairs(text);
      case 'json':
        return jsonIsEmpty(parseTolerantJson(text));
      case 'yaml':
        return jsonIsEmpty(YamlEditor.parseToJson(text));
      default:
        return false;
    }
  } catch {
    return false;
  }
}

function tomlHasNoPairs(text: string): boolean {
  const body = parseTOML(text).body[0]!.body;
  for (const node of body) {
    if (node.type === 'TOMLKeyValue') return false;
    if (node.type === 'TOMLTable' && node.body.length > 0) return false;
  }
  return true;
}

function jsonIsEmpty(node: unknown): boolean {
  if (node === null || node === undefined) return true;
  if (Array.isArray(node)) return node.length === 0;
  if (isPlainObject(node)) return Object.values(node).every(jsonIsEmpty);
  return false;
}
