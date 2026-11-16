import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ClientDetections,
  type ClientDetection,
  type ConfigChange,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { EditorException } from '../editing/errors.js';
import { JsoncEditor } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { FileClientAdapter, PROVIDER_ID } from './file-adapter.js';

const CONNECTION_PATH = 'llmConnections.' + JsoncEditor.selector(['slug', PROVIDER_ID]);

/**
 * Craft Agents (Electron app; Coexist): `config.json` in `CRAFT_CONFIG_DIR` (else `~/.craft-agent`). Adds the `llmConnections`
 * element with `slug: "astra"` (a keyless `pi_compat` connection at the gateway's `/v1`) and, with a model, makes it
 * `defaultLlmConnection`. Craft will not load a config without its `workspaces`, so it must have run once. (Port of
 * CraftAgentsClientAdapter.)
 */
export class CraftAgentsClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.CraftAgents;

  private get dir(): string {
    const dir = this.env.getEnvironmentVariable('CRAFT_CONFIG_DIR');
    return dir ? dir : this.env.combine('.craft-agent');
  }

  private get configFile(): string {
    return path.join(this.dir, 'config.json');
  }

  protected get markerFile(): string {
    return this.configFile;
  }

  protected get markerKey(): string {
    return CONNECTION_PATH;
  }

  protected override get enabledWarnings(): string[] {
    return [
      'Craft Agents sends no key to a keyless connection: turn off "require gateway key" in Astra\'s settings so local requests are accepted (they use the default token).',
      'Craft Agents starts new sessions on this; a session already open stays on the connection it started on.',
    ];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    return this.env.directoryExists(dir)
      ? ClientDetections.found(null, `config directory ${dir}`)
      : ClientDetections.notFound(`no ${dir}`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const workspaces = this.currentValue(file, 'json', 'workspaces');
    if (workspaces === null || !workspaces.startsWith('['))
      throw new EditorException(
        `Craft Agents has no ${file} with its workspaces yet; open Craft Agents once, then enable it here.`,
      );
    const hints = ClientAdapterBase.modelHints(ctx);
    const entries = ClientAdapterBase.modelEntries(ctx);
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of entries) {
      const info = hints.get(id);
      const model: Record<string, unknown> = {
        id,
        name: name ?? id,
        shortName: name ?? id,
        description: '',
        provider: 'pi',
      };
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) model.contextWindow = context;
      if (ClientAdapterBase.boolHint(info?.vision) === true) model.supportsImages = true;
      models.push(model);
    }
    const defaultModel = ctx.model ? ctx.model : (entries[0]?.id ?? null);
    if (!defaultModel) return this.buildPlan([]);
    const connection = {
      slug: PROVIDER_ID,
      name: 'Astra',
      providerType: 'pi_compat',
      authType: 'none',
      baseUrl: FileClientAdapter.gateway(ctx.gatewayBaseUrl),
      customEndpoint: { api: 'openai-completions' },
      models,
      defaultModel,
      modelSelectionMode: 'userDefined3Tier',
    };
    const changes: ConfigChange[] = [this.jsonSet(file, CONNECTION_PATH, connection)];
    if (ctx.model) changes.push(this.jsonSet(file, 'defaultLlmConnection', PROVIDER_ID));
    return this.buildPlan(changes);
  }
}
