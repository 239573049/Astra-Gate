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
import { JsoncEditor, toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { listState } from './shell-rc.js';

const PROVIDER_ID = 'astra';
const FILE_NAME = 'openbot-custom-providers-v1.json';
/** How many models OpenBot takes for one endpoint. */
const MAX_MODELS = 64;
const MODEL_ID = /^[A-Za-z0-9._:/[\],=-]+$/;

/**
 * OpenBot (nightly-labs/openbot; Coexist): `openbot-custom-providers-v1.json` in Electron's userData folder. Owns the
 * element `id = astra` of `providers` (keyless: the entry's key is sealed with the system keychain) and `version` when the file
 * is new. (Port of OpenBotClientAdapter.)
 */
export class OpenBotClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.OpenBot;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dir(): string {
    if (this.env.os === 'osx') return this.env.combine('Library', 'Application Support', 'OpenBot');
    if (this.env.os === 'windows') {
      const app = this.env.getEnvironmentVariable('APPDATA');
      return app ? path.join(app, 'OpenBot') : this.env.combine('AppData', 'Roaming', 'OpenBot');
    }
    const xdg = this.env.getEnvironmentVariable('XDG_CONFIG_HOME');
    return xdg ? path.join(xdg, 'OpenBot') : this.env.combine('.config', 'OpenBot');
  }

  private get configFile(): string {
    return path.join(this.dir, FILE_NAME);
  }

  private static get entryPath(): string {
    return 'providers.' + JsoncEditor.selector(['id', PROVIDER_ID]);
  }

  detect(): ClientDetection {
    const dir = this.dir;
    return this.env.directoryExists(dir)
      ? ClientDetections.found(null, `data folder ${dir}`)
      : ClientDetections.notFound(`no ${dir}`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      if (id.length > 128 || PROVIDER_ID.length + 1 + id.length > 160 || !MODEL_ID.test(id)) continue;
      const label = name === null || name.length > 160 ? id : name;
      const model: Record<string, unknown> = { id, name: label };
      const window = ClientAdapterBase.positiveLong(hints.get(id)?.contextWindow);
      if (window !== null && window >= 1024 && window <= 100_000_000) model.contextWindow = window;
      models.push(model);
      if (models.length === MAX_MODELS) break;
    }
    const entry: Record<string, unknown> = {
      id: PROVIDER_ID,
      name: 'Astra',
      baseUrl: ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1',
      models,
      secret: null,
    };
    // What the user gave the entry inside OpenBot (its name, harness and sealed secret) stays.
    const existing = this.currentValue(file, 'json', OpenBotClientAdapter.entryPath);
    if (existing !== null) {
      const old = JSON.parse(existing) as Record<string, unknown> | null;
      if (old && typeof old === 'object') {
        if (typeof old.name === 'string' && old.name !== '') entry.name = old.name;
        if (typeof old.harness === 'string') entry.harness = old.harness;
        if (typeof old.secret === 'string') entry.secret = old.secret;
      }
    }
    const changes: ConfigChange[] = [];
    if (this.currentValue(file, 'json', 'version') === null) changes.push(new ConfigChange(file, 'json', 'version', null, '1'));
    changes.push(
      new ConfigChange(file, 'json', OpenBotClientAdapter.entryPath, this.currentValue(file, 'json', OpenBotClientAdapter.entryPath), toJsonText(entry)),
    );
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    for (const entry of listState(this.store, this.kind)) {
      const current = this.currentValue(entry.filePath, 'json', entry.keyPath);
      if (current !== null) changes.push(new ConfigChange(entry.filePath, 'json', entry.keyPath, current, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.stillOurs(this.configFile, 'json', OpenBotClientAdapter.entryPath);
    const warnings: string[] = [];
    if (enabled) {
      warnings.push(
        'OpenBot seals an endpoint\'s key with the system keychain, which Astra cannot write: turn off "require gateway key" in Astra\'s settings so local requests are accepted (they use the default token), or give the endpoint the key inside OpenBot.',
      );
      warnings.push(
        "OpenBot reads its custom endpoints at start-up: restart OpenBot to see Astra's models in its model picker, before adding or editing an endpoint there.",
      );
    }
    return this.buildStatus(detection, enabled, warnings);
  }
}
