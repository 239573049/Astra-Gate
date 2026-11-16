import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { ClientKinds } from '../domain/kinds.js';
import { JNum, JObj, JsoncEditor, jsonDeepEquals, parseJsonc, toJsonText, type JV } from '../editing/jsonc-editor.js';
import type { ConfigFileFormat } from '../types.js';
import { ConfigFileClientAdapter } from './config-file-adapter.js';

const AGENT_ID = 'astra';
const MAX_MODELS = 64;
const OWNED_FIELDS = ['id', 'preset', 'kind', 'api_key', 'model', 'models', 'base_url'];
const ROW_PATH = 'builtin_agents.' + JsoncEditor.selector(['id', AGENT_ID]);

const member = (o: JObj, key: string): JV | undefined => o.entries.find(([k]) => k === key)?.[1];

function parseObject(json: string | null): JObj | null {
  if (json === null) return null;
  try {
    const v = parseJsonc(json).value;
    return v instanceof JObj ? v : null;
  } catch {
    return null;
  }
}

/** The raw text of the document's `version` member when it is a JSON number, else null. */
function versionOf(text: string): string | null {
  const root = parseObject(text);
  const v = root ? member(root, 'version') : undefined;
  return v instanceof JNum ? v.raw : null;
}

function ownedPartsEqual(currentText: string | null, appliedJson: string | null): boolean {
  if (currentText === null || appliedJson === null) return currentText === null && appliedJson === null;
  const current = parseObject(currentText);
  const applied = parseObject(appliedJson);
  if (!current || !applied) return false;
  for (const field of OWNED_FIELDS) {
    const a = member(applied, field);
    const c = member(current, field);
    if ((a === undefined) !== (c === undefined)) return false;
    if (a !== undefined && c !== undefined && !jsonDeepEquals(a, c)) return false;
  }
  return true;
}

/**
 * OpenPencil (`openpencil-desktop`; Coexist): one `builtin_agents` entry `astra` in `settings.json` of the platform's config
 * folder (`<config>/openpencil`), `version: 1` written in a file Astra creates. (Port of OpenPencilClientAdapter.)
 */
export class OpenPencilClientAdapter extends ConfigFileClientAdapter {
  readonly kind = ClientKinds.OpenPencil;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dir(): string {
    return this.appSupportDir('openpencil');
  }

  private get settingsFile(): string {
    return path.join(this.dir, 'settings.json');
  }

  detect(): ClientDetection {
    if (this.env.directoryExists(this.dir)) return ClientDetections.found(null, `config directory ${this.dir}`);
    const legacy = this.env.combine('.openpencil');
    if (this.env.directoryExists(legacy)) return ClientDetections.found(null, `config directory ${legacy}`);
    const app = this.findApp('OpenPencil.app');
    return app ? ClientDetections.found(null, `application ${app}`) : ClientDetections.notFound(`no ${this.dir}`);
  }

  configPaths(): string[] {
    return [this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.settingsFile;
    const text = this.env.readTextOrNull(file);
    let hasVersion = false;
    if (text !== null && text.trim().length > 0) {
      // A file OpenPencil would not read, or whose version it does not know, is left alone.
      if (versionOf(text) !== '1') return this.buildPlan([]);
      hasVersion = true;
    }

    const models: string[] = [];
    if (ctx.model) models.push(ctx.model);
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) {
      if (id.length === 0 || models.includes(id) || id.length > 256) continue;
      models.push(id);
      if (models.length === MAX_MODELS) break;
    }
    const row: Record<string, unknown> = {
      id: AGENT_ID,
      preset: 'custom',
      display_name: 'Astra',
      kind: 'openai-compat',
      api_key: ctx.localKey,
      model: models.length > 0 ? models[0] : '',
    };
    if (models.length > 1) row.models = models;
    row.base_url = ConfigFileClientAdapter.v1(ctx);
    row.enabled = true;

    const before = this.currentValue(file, 'json', ROW_PATH);
    // The name and the on/off the user gave the entry in OpenPencil's settings stay.
    const current = this.store.get(this.kind, file, ROW_PATH) !== null ? parseObject(before) : null;
    if (current) {
      const name = member(current, 'display_name');
      if (typeof name === 'string') row.display_name = name;
      const on = member(current, 'enabled');
      if (typeof on === 'boolean') row.enabled = on;
    }
    const changes: ConfigChange[] = [];
    if (!hasVersion) changes.push(this.setKey(file, 'version', '1'));
    changes.push(new ConfigChange(file, 'json', ROW_PATH, before, toJsonText(row), { stillOurs: ownedPartsEqual }));
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes = this.trackedRemovals().map((c) =>
      c.keyPath === ROW_PATH ? new ConfigChange(c.file, c.format, ROW_PATH, c.before, null, { stillOurs: ownedPartsEqual }) : c,
    );
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const enabled = this.applied(this.settingsFile, ROW_PATH);
    const warnings: string[] = [];
    if (enabled)
      warnings.push(
        "OpenPencil reads its settings when it starts and writes them all back when it quits: quit OpenPencil before changing this, then open it and pick an Astra model in its chat's model picker.",
      );
    const text = this.env.readTextOrNull(this.settingsFile);
    if (text !== null && text.trim().length > 0 && versionOf(text) !== '1')
      warnings.push(`${this.settingsFile} does not say version 1, so Astra leaves it alone.`);
    return this.buildStatus(this.detect(), enabled, warnings);
  }

  protected override valueStillOurs(
    file: string,
    keyPath: string,
    format: ConfigFileFormat,
    current: string | null,
    appliedJson: string | null,
  ): boolean {
    return keyPath === ROW_PATH
      ? ownedPartsEqual(current, appliedJson)
      : super.valueStillOurs(file, keyPath, format, current, appliedJson);
  }
}
