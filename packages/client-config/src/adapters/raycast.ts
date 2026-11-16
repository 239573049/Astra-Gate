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
import { JsoncEditor, toJsonText } from '../editing/jsonc-editor.js';
import type { ConfigFileFormat } from '../types.js';
import { ConfigFileClientAdapter, jsonEqual, parsePlainObject } from './config-file-adapter.js';

type Obj = Record<string, unknown>;

const PROVIDER_ID = 'astra';
const OWNED_FIELDS = ['id', 'base_url', 'api_keys', 'models'];
const OWNED_MODEL_FIELDS = ['id', 'name', 'context', 'abilities'];
const ROW_PATH = 'providers.' + JsoncEditor.selector(['id', PROVIDER_ID]);

const isObj = (v: unknown): v is Obj => v !== null && typeof v === 'object' && !Array.isArray(v);
const supported = (value: boolean): Obj => ({ supported: value });

/** Whether the entry still holds what Astra wrote in the fields Astra owns; keys the user added are not compared. */
function ownedPartsEqual(currentText: string | null, appliedJson: string | null): boolean {
  if (currentText === null || appliedJson === null) return currentText === null && appliedJson === null;
  const current = parsePlainObject(currentText);
  const applied = parsePlainObject(appliedJson);
  if (!current || !applied) return false;
  for (const field of OWNED_FIELDS) {
    if (field === 'models') continue;
    if (!jsonEqual(applied[field], current[field])) return false;
  }
  const appliedModels = Array.isArray(applied.models) ? applied.models : [];
  const currentModels = Array.isArray(current.models) ? current.models : [];
  if (appliedModels.length !== currentModels.length) return false;
  for (let i = 0; i < appliedModels.length; i++) {
    const written = appliedModels[i];
    const now = currentModels[i];
    if (!isObj(written) || !isObj(now)) return false;
    for (const field of OWNED_MODEL_FIELDS)
      if (field in written && !jsonEqual(written[field], now[field])) return false;
  }
  return true;
}

/**
 * Raycast AI (Custom Providers; Coexist): one `providers` entry `astra` in `~/.config/raycast/ai/providers.yaml`, addressed
 * by `providers.[id=astra]`; keys Astra does not write stay. (Port of RaycastClientAdapter.)
 */
export class RaycastClientAdapter extends ConfigFileClientAdapter {
  readonly kind = ClientKinds.Raycast;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dir(): string {
    return this.env.combine('.config', 'raycast');
  }

  private get providersFile(): string {
    return path.join(this.dir, 'ai', 'providers.yaml');
  }

  detect(): ClientDetection {
    const app = this.findApp('Raycast.app');
    if (app) return ClientDetections.found(null, `application ${app}`);
    return this.env.directoryExists(this.dir)
      ? ClientDetections.found(null, `config directory ${this.dir}`)
      : ClientDetections.notFound(`no ${this.dir}`);
  }

  configPaths(): string[] {
    return [this.providersFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.providersFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Obj[] = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const model: Obj = { id, name: name ?? id };
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) model.context = context;
      model.abilities = {
        temperature: supported(true),
        vision: supported(ClientAdapterBase.boolHint(info?.vision) ?? false),
        system_message: supported(true),
        tools: supported(true),
        reasoning_effort: supported(ClientAdapterBase.boolHint(info?.reasoning) ?? false),
      };
      models.push(model);
    }
    const row: Obj = {
      id: PROVIDER_ID,
      name: 'Astra',
      base_url: ConfigFileClientAdapter.v1(ctx),
      api_keys: { [PROVIDER_ID]: ctx.localKey },
      models,
    };
    const before = this.currentValue(file, 'yaml', ROW_PATH);
    // Keys Astra does not write in its entry (and in a model) are the user's and stay.
    const current = parsePlainObject(before);
    if (current) this.carryOver(current, row);
    return this.buildPlan([new ConfigChange(file, 'yaml', ROW_PATH, before, toJsonText(row), { stillOurs: ownedPartsEqual })]);
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
    return this.buildStatus(this.detect(), this.applied(this.providersFile, ROW_PATH), [
      'Custom Providers need Raycast Pro; Raycast picks the file up without a restart.',
    ]);
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

  private carryOver(current: Obj, row: Obj): void {
    for (const [key, value] of Object.entries(current))
      if (!OWNED_FIELDS.includes(key) && !(key in row)) row[key] = value;
    if (!Array.isArray(current.models) || !Array.isArray(row.models)) return;
    for (const model of row.models as Obj[]) {
      const match = current.models.find((m) => isObj(m) && m.id === model.id);
      if (!isObj(match)) continue;
      for (const [key, value] of Object.entries(match))
        if (!OWNED_MODEL_FIELDS.includes(key) && !(key in model)) model[key] = value;
    }
  }
}
