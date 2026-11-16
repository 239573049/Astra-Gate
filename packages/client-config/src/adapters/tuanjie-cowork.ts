import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { JsoncEditor } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { FileClientAdapter, PROVIDER_ID } from './file-adapter.js';

const ID_PREFIX = 'astra-';
const PROVIDER_PATH = 'customModelProviders.' + JsoncEditor.selector(['id', PROVIDER_ID]);
const MODEL_PATH_PREFIX = 'customModels.[id=' + ID_PREFIX;
const modelPath = (id: string): string => 'customModels.' + JsoncEditor.selector(['id', id]);

/**
 * Tuanjie Cowork (Tauri app; Coexist): `~/.codely/settings.json`. Adds the `customModelProviders` element `astra` and one
 * `customModels` element per bound model, id `astra-<id>`, on `wireApi: "chat"`; the model a chat uses is Cowork's own pick in its
 * model menu. (Port of TuanjieCoworkClientAdapter.)
 */
export class TuanjieCoworkClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.TuanjieCowork;

  private get dir(): string {
    return this.env.combine('.codely');
  }

  private get settingsFile(): string {
    return path.join(this.dir, 'settings.json');
  }

  protected get markerFile(): string {
    return this.settingsFile;
  }

  protected get markerKey(): string {
    return PROVIDER_PATH;
  }

  protected override get enabledWarnings(): string[] {
    return ["Pick Astra's model in Tuanjie Cowork's model menu; if it is not there yet, restart Tuanjie Cowork."];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    return this.env.directoryExists(dir)
      ? ClientDetections.found(null, `config directory ${dir}`)
      : ClientDetections.notFound(`no ${dir}`);
  }

  configPaths(): string[] {
    return [this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.settingsFile;
    const provider = {
      id: PROVIDER_ID,
      name: 'Astra',
      baseUrl: FileClientAdapter.gateway(ctx.gatewayBaseUrl),
      apiKey: ctx.localKey,
      authType: 'openai',
    };
    const changes: ConfigChange[] = [this.jsonSet(file, PROVIDER_PATH, provider)];
    const hints = ClientAdapterBase.modelHints(ctx);
    const ids = FileClientAdapter.aliases(ID_PREFIX, ClientAdapterBase.modelEntries(ctx).map((e) => e.id), '.');
    const wanted = new Set<string>();
    for (const [model, id] of ids) {
      if (!FileClientAdapter.canSelect('id', id)) continue;
      wanted.add(id);
      const info = hints.get(model);
      const entry = {
        id,
        providerId: PROVIDER_ID,
        model,
        displayName: model,
        roles: ['model'],
        wireApi: 'chat',
        supportsMultimodal: ClientAdapterBase.boolHint(info?.vision) === true,
      };
      changes.push(this.jsonSet(file, modelPath(id), entry));
    }
    const tracked = this.store
      .list(this.kind)
      .filter((e) => e.filePath === file && e.keyPath.startsWith(MODEL_PATH_PREFIX) && e.keyPath.endsWith(']'))
      .map((e) => e.keyPath)
      .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
    for (const keyPath of tracked) {
      if (wanted.has(keyPath.slice(MODEL_PATH_PREFIX.length - ID_PREFIX.length, -1))) continue;
      const current = this.currentValue(file, 'json', keyPath);
      if (current !== null) changes.push(new ConfigChange(file, 'json', keyPath, current, null));
    }
    return this.buildPlan(changes);
  }
}
