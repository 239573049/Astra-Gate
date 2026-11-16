import { createHash } from 'node:crypto';
import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChangePlan,
  type EnableContext,
  type JsonObject,
} from '../contracts.js';
import { equalsValue } from '../config-value-codec.js';
import { ClientKinds } from '../domain/kinds.js';
import { JObj, JsoncEditor, compactJson, parseJsonc, toJsonText, type JV } from '../editing/jsonc-editor.js';
import type { ConfigFileFormat } from '../types.js';
import { ConfigFileClientAdapter, jsonEqual, parsePlainObject } from './config-file-adapter.js';

type Obj = Record<string, unknown>;

const PROVIDER_ID = 'astra';
const PLUGIN_ID = 'copilot';
const CHAT_ENABLED_PATH = 'backends.chat.enabledModels';
const DEFAULT_MODEL_PATH = 'defaultModelKey';
const PROVIDER_PATH = `providers.${PROVIDER_ID}`;
const OWNED_PROVIDER_FIELDS = ['providerId', 'providerType', 'baseUrl', 'requiresApiKey'];
const ROW_PREFIX = 'configuredModels.[configuredModelId=';

/** RFC 4122 version 5 UUID of `name` in the namespace `ns`. */
function uuid5(ns: string, name: string): string {
  const nsBytes = Buffer.from(ns.replace(/-/g, ''), 'hex');
  const hash = createHash('sha1').update(nsBytes).update(Buffer.from(name, 'utf8')).digest();
  const bytes = Buffer.from(hash.subarray(0, 16));
  bytes[6] = (bytes[6]! & 0x0f) | 0x50;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = bytes.toString('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** The UUID v5 namespace of Astra's configured model ids (the URL namespace, named for Astra's Obsidian wiring). */
const MODEL_NAMESPACE = uuid5('6ba7b811-9dad-11d1-80b4-00c04fd430c8', 'https://astra-gate.si/obsidian/copilot');

/** Astra's configured model id in Copilot for a model id: a UUID v5, the same on every apply. */
const modelId = (id: string): string => uuid5(MODEL_NAMESPACE, 'astra/' + id);

const rowPath = (id: string): string => 'configuredModels.' + JsoncEditor.selector(['configuredModelId', id]);

/** Astra's ids are UUID v5s of its namespace: recognised by their version digit alone (the user's are the plugin's own random ones). */
const isAstraId = (id: string): boolean => id.length === 36 && id[14] === '5';

const isObj = (v: unknown): v is Obj => v !== null && typeof v === 'object' && !Array.isArray(v);

function parseArray(json: string | null): unknown[] | null {
  if (json === null) return null;
  try {
    const v: unknown = JSON.parse(compactJson(parseJsonc(json).value));
    return Array.isArray(v) ? v : null;
  } catch {
    return null;
  }
}

function isOurs(provider: Obj): boolean {
  return (
    typeof provider.baseUrl === 'string' &&
    provider.baseUrl.replace(/\/+$/, '').endsWith('/v1') &&
    provider.providerType === 'openai-compatible'
  );
}

function providerStillOurs(currentText: string | null, appliedJson: string | null): boolean {
  if (currentText === null || appliedJson === null) return currentText === null && appliedJson === null;
  const current = parsePlainObject(currentText);
  const applied = parsePlainObject(appliedJson);
  if (!current || !applied) return false;
  return OWNED_PROVIDER_FIELDS.every((f) => jsonEqual(applied[f], current[f]));
}

function rowStillOurs(currentText: string | null, appliedJson: string | null): boolean {
  if (currentText === null || appliedJson === null) return currentText === null && appliedJson === null;
  const current = parsePlainObject(currentText);
  const applied = parsePlainObject(appliedJson);
  if (!current || !applied) return false;
  const a = isObj(applied.info) ? applied.info.id : undefined;
  const c = isObj(current.info) ? current.info.id : undefined;
  return jsonEqual(applied.providerId, current.providerId) && jsonEqual(a, c);
}

/** The chat list still holds the user's own ids as Astra saw them: ids of Astra's models may come and go. */
function enabledStillOurs(currentText: string | null, appliedJson: string | null): boolean {
  if (currentText === null || appliedJson === null) return currentText === null && appliedJson === null;
  const current = parseArray(currentText);
  const applied = parseArray(appliedJson);
  if (!current || !applied) return false;
  const foreign = (list: unknown[]): string[] => list.filter((s): s is string => typeof s === 'string' && !isAstraId(s));
  const a = foreign(applied);
  const c = foreign(current);
  return a.length === c.length && a.every((x, i) => x === c[i]);
}

function majorVersion(manifest: string | null): number {
  const root = manifest === null ? null : parsePlainObject(manifest);
  const version = root && typeof root.version === 'string' ? root.version : null;
  const major = version?.split('.')[0];
  return major !== undefined && /^[0-9]+$/.test(major) ? Number(major) : 0;
}

function info(id: string, name: string | null, hints: JsonObject | undefined): Obj {
  const input = ['text'];
  if (ClientAdapterBase.boolHint(hints?.vision) === true) input.push('image');
  const result: Obj = {
    id,
    displayName: name ?? id,
    modalities: { input, output: ['text'] },
  };
  const context = ClientAdapterBase.positiveLong(hints?.contextWindow);
  const output = ClientAdapterBase.positiveLong(hints?.maxOutputTokens);
  if (context !== null || output !== null) {
    const limits: Obj = {};
    if (context !== null) limits.context = context;
    if (output !== null) limits.output = output;
    result.limits = limits;
  }
  if (ClientAdapterBase.boolHint(hints?.reasoning) === true) result.reasoning = true;
  return result;
}

/**
 * Obsidian (Copilot for Obsidian 4+; Coexist): in every vault `obsidian.json` lists that has Copilot, the keyless provider
 * `astra`, one `configuredModels` row per model, the ids in the chat list and the default model, in
 * `.obsidian/plugins/copilot/data.json`. (Port of ObsidianClientAdapter.)
 */
export class ObsidianClientAdapter extends ConfigFileClientAdapter {
  readonly kind = ClientKinds.Obsidian;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private configDirs(): string[] {
    switch (this.env.os) {
      case 'osx':
        return [this.env.combine('Library', 'Application Support', 'obsidian')];
      case 'windows':
        return [path.join(this.appData, 'obsidian')];
      default:
        return [
          path.join(this.xdgConfigHome, 'obsidian'),
          this.env.combine('.var', 'app', 'md.obsidian.Obsidian', 'config', 'obsidian'),
          this.env.combine('snap', 'obsidian', 'current', '.config', 'obsidian'),
        ];
    }
  }

  /** The vault folders the obsidian.json files list that are still there, each once, in the order of their ids. */
  private vaults(): string[] {
    const vaults: string[] = [];
    for (const dir of this.configDirs()) {
      const text = this.env.readTextOrNull(path.join(dir, 'obsidian.json'));
      if (text === null || text.length === 0) continue;
      let root: JV;
      try {
        root = parseJsonc(text, { comments: false, trailingCommas: false }).value;
      } catch {
        continue;
      }
      const list = root instanceof JObj ? root.entries.find(([k]) => k === 'vaults')?.[1] : undefined;
      if (!(list instanceof JObj)) continue;
      const ids = list.entries.map(([k]) => k).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
      for (const id of ids) {
        const vault = list.entries.find(([k]) => k === id)?.[1];
        const p = vault instanceof JObj ? vault.entries.find(([k]) => k === 'path')?.[1] : undefined;
        if (typeof p !== 'string' || p.length === 0 || !this.env.directoryExists(p)) continue;
        const clean = path.resolve(p);
        if (!vaults.includes(clean)) vaults.push(clean);
      }
    }
    return vaults;
  }

  private static pluginDir(vault: string): string {
    return path.join(vault, '.obsidian', 'plugins', PLUGIN_ID);
  }

  private static dataFile(vault: string): string {
    return path.join(ObsidianClientAdapter.pluginDir(vault), 'data.json');
  }

  /** The vaults whose Copilot is installed and version 4 or later, and the vaults that have an older Copilot. */
  private copilotVaults(): { ready: string[]; old: string[] } {
    const ready: string[] = [];
    const old: string[] = [];
    for (const vault of this.vaults()) {
      const manifest = path.join(ObsidianClientAdapter.pluginDir(vault), 'manifest.json');
      if (!this.env.fileExists(manifest)) continue;
      const major = majorVersion(this.env.readTextOrNull(manifest));
      if (major > 0 && major < 4) old.push(vault);
      else ready.push(vault);
    }
    return { ready, old };
  }

  detect(): ClientDetection {
    const { ready, old } = this.copilotVaults();
    if (ready.length + old.length > 0)
      return ClientDetections.found(null, `Copilot for Obsidian in ${(ready.length > 0 ? ready : old)[0]}`);
    return this.store.list(this.kind).length > 0
      ? ClientDetections.found(null, 'vault already configured')
      : ClientDetections.notFound('no Obsidian vault has Copilot for Obsidian');
  }

  configPaths(): string[] {
    return this.copilotVaults().ready.map((v) => ObsidianClientAdapter.dataFile(v));
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    const entries = ClientAdapterBase.modelEntries(ctx);
    if (entries.length === 0) return this.buildPlan(changes);
    const hints = ClientAdapterBase.modelHints(ctx);
    const provider: Obj = {
      providerId: PROVIDER_ID,
      providerType: 'openai-compatible',
      displayName: 'Astra',
      baseUrl: ConfigFileClientAdapter.v1(ctx),
      enableCors: true,
      apiKeyKeychainId: null,
      requiresApiKey: false,
      origin: { kind: 'byok' },
      addedAt: 0,
    };
    const wantedIds = entries.map((e) => modelId(e.id));
    const defaultId = modelId(ctx.model ? ctx.model : entries[0]!.id);

    for (const vault of this.copilotVaults().ready) {
      const file = ObsidianClientAdapter.dataFile(vault);
      const before = this.currentValue(file, 'json', PROVIDER_PATH);
      const recordedProvider = this.store.get(this.kind, file, PROVIDER_PATH) !== null;
      // A provider of the user's own under this name, at another address, is theirs: the vault is left alone.
      const theirs = before !== null && !recordedProvider ? parsePlainObject(before) : null;
      if (theirs && !isOurs(theirs)) continue;
      const next: Obj = JSON.parse(JSON.stringify(provider)) as Obj;
      const current = recordedProvider ? parsePlainObject(before) : null;
      if (current) {
        if (typeof current.displayName === 'string' && current.displayName.trim().length > 0) next.displayName = current.displayName;
        if (typeof current.apiKeyKeychainId === 'string') next.apiKeyKeychainId = current.apiKeyKeychainId;
      }
      changes.push(new ConfigChange(file, 'json', PROVIDER_PATH, before, toJsonText(next), { stillOurs: providerStillOurs }));

      for (const { id, name } of entries) {
        const row = {
          configuredModelId: modelId(id),
          providerId: PROVIDER_ID,
          info: info(id, name, hints.get(id)),
          configuredAt: 0,
        };
        const path_ = rowPath(modelId(id));
        changes.push(
          new ConfigChange(file, 'json', path_, this.currentValue(file, 'json', path_), toJsonText(row), {
            stillOurs: rowStillOurs,
          }),
        );
      }
      // A model that left the list takes its row, and its place in the chat list, with it.
      const gone: string[] = [];
      for (const entry of this.store.list(this.kind)) {
        if (entry.filePath !== file || !entry.keyPath.startsWith(ROW_PREFIX)) continue;
        const rowId = entry.keyPath.slice(ROW_PREFIX.length, -1);
        if (wantedIds.includes(rowId)) continue;
        gone.push(rowId);
        const removal = this.removeKey(file, entry.keyPath);
        if (removal) changes.push(new ConfigChange(file, 'json', entry.keyPath, removal.before, null, { stillOurs: rowStillOurs }));
      }
      this.addEnabledModels(changes, file, wantedIds, gone);

      const defaultBefore = this.currentValue(file, 'json', DEFAULT_MODEL_PATH);
      const recorded = this.store.get(this.kind, file, DEFAULT_MODEL_PATH);
      // The user's default is replaced the first time; after that the model they picked since stays.
      if (recorded === null || equalsValue('json', defaultBefore, recorded.appliedValueJson))
        changes.push(new ConfigChange(file, 'json', DEFAULT_MODEL_PATH, defaultBefore, ClientAdapterBase.jsonString(defaultId)));
    }
    return this.buildPlan(changes);
  }

  private addEnabledModels(changes: ConfigChange[], file: string, wantedIds: string[], goneIds: string[]): void {
    const before = this.currentValue(file, 'json', CHAT_ENABLED_PATH);
    const ids: string[] = [];
    const existing = parseArray(before);
    if (existing)
      for (const node of existing) if (typeof node === 'string' && !goneIds.includes(node)) ids.push(node);
    for (const id of wantedIds) if (!ids.includes(id)) ids.push(id);
    changes.push(new ConfigChange(file, 'json', CHAT_ENABLED_PATH, before, toJsonText(ids), { stillOurs: enabledStillOurs }));
  }

  planDisable(): ConfigChangePlan {
    const changes = this.trackedRemovals().map((c) => {
      const still =
        c.keyPath === PROVIDER_PATH
          ? providerStillOurs
          : c.keyPath.startsWith(ROW_PREFIX)
            ? rowStillOurs
            : c.keyPath === CHAT_ENABLED_PATH
              ? enabledStillOurs
              : null;
      return still ? new ConfigChange(c.file, c.format, c.keyPath, c.before, null, { stillOurs: still }) : c;
    });
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const { ready, old } = this.copilotVaults();
    const enabled = ready.some((v) => this.applied(ObsidianClientAdapter.dataFile(v), PROVIDER_PATH));
    const warnings: string[] = [];
    if (enabled)
      warnings.push(
        "Obsidian holds Copilot's settings while it runs: reload Copilot in Settings, Community plugins (or restart Obsidian) to see the Astra models. The provider is keyless, so the gateway must not require a key from this machine.",
      );
    for (const vault of old)
      warnings.push(`${vault} has Copilot 3: Astra sets Copilot for Obsidian 4 and later, so that vault is left alone.`);
    return this.buildStatus(this.detect(), enabled, warnings);
  }

  protected override valueStillOurs(
    file: string,
    keyPath: string,
    format: ConfigFileFormat,
    current: string | null,
    appliedJson: string | null,
  ): boolean {
    if (keyPath === PROVIDER_PATH) return providerStillOurs(current, appliedJson);
    if (keyPath.startsWith(ROW_PREFIX)) return rowStillOurs(current, appliedJson);
    if (keyPath === CHAT_ENABLED_PATH) return enabledStillOurs(current, appliedJson);
    return super.valueStillOurs(file, keyPath, format, current, appliedJson);
  }
}
