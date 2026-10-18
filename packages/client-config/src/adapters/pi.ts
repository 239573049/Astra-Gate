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
import { equalsValue } from '../config-value-codec.js';
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { PiAiModels } from './pi-ai-models.js';

const PROVIDER_ID = 'astra';
const PROVIDER_PATH = `providers.${PROVIDER_ID}`;

/**
 * Pi coding agent (Coexist): adds `providers.astra` (an `openai-completions` endpoint listing the bound
 * provider's models) to `<agent-dir>/models.json` and, when a model is chosen, sets `defaultProvider` /
 * `defaultModel` in `<agent-dir>/settings.json`. The agent directory is `~/.pi/agent` unless PI_CODING_AGENT_DIR
 * overrides it. Models whose native protocol is not Chat carry their own `api` (and base). (Port of PiClientAdapter.)
 */
export class PiClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.Pi;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get agentDir(): string {
    const dir = this.env.getEnvironmentVariable('PI_CODING_AGENT_DIR');
    return dir ? dir : this.env.combine('.pi', 'agent');
  }

  private get modelsFile(): string {
    return path.join(this.agentDir, 'models.json');
  }

  private get settingsFile(): string {
    return path.join(this.agentDir, 'settings.json');
  }

  detect(): ClientDetection {
    const dir = this.agentDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('pi');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no pi executable on PATH`);
  }

  configPaths(): string[] {
    return [this.modelsFile, this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const baseUrlOfProvider = ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1';
    const provider: Record<string, unknown> = {
      baseUrl: baseUrlOfProvider,
      api: 'openai-completions',
      apiKey: ctx.localKey,
    };
    const hints = ClientAdapterBase.modelHints(ctx);
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const model: Record<string, unknown> = { id };
      if (name !== null) model.name = name;
      const protocol = protocols.get(id)!;
      if (protocol !== 'openai-chat') {
        model.api = PiAiModels.apiOf(protocol);
        const baseUrl = PiAiModels.baseUrlFor(ctx.gatewayBaseUrl, protocol);
        if (baseUrl !== baseUrlOfProvider) model.baseUrl = baseUrl;
      }
      PiAiModels.addHints(model, hints.get(id));
      models.push(model);
    }
    if (models.length > 0) provider.models = models;

    const changes: ConfigChange[] = [
      new ConfigChange(
        this.modelsFile,
        'json',
        PROVIDER_PATH,
        this.currentValue(this.modelsFile, 'json', PROVIDER_PATH),
        toJsonText(provider),
      ),
    ];
    if (ctx.model) {
      changes.push(this.settingsKey('defaultProvider', PROVIDER_ID));
      changes.push(this.settingsKey('defaultModel', ctx.model));
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    const providerBefore = this.currentValue(this.modelsFile, 'json', PROVIDER_PATH);
    if (providerBefore !== null)
      changes.push(new ConfigChange(this.modelsFile, 'json', PROVIDER_PATH, providerBefore, null));
    // The startup selection is only ours while it still names the Astra provider.
    const defaultProvider = this.currentValue(this.settingsFile, 'json', 'defaultProvider');
    if (defaultProvider !== null && ClientAdapterBase.stringOf(defaultProvider) === PROVIDER_ID) {
      changes.push(new ConfigChange(this.settingsFile, 'json', 'defaultProvider', defaultProvider, null));
      const defaultModel = this.currentValue(this.settingsFile, 'json', 'defaultModel');
      if (defaultModel !== null && this.store.get(this.kind, this.settingsFile, 'defaultModel') !== null)
        changes.push(new ConfigChange(this.settingsFile, 'json', 'defaultModel', defaultModel, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const entry = this.store.get(this.kind, this.modelsFile, PROVIDER_PATH);
    const enabled =
      entry !== null &&
      equalsValue('json', this.currentValue(this.modelsFile, 'json', PROVIDER_PATH), entry.appliedValueJson);
    return this.buildStatus(detection, enabled, []);
  }

  private settingsKey(key: string, value: string): ConfigChange {
    return new ConfigChange(
      this.settingsFile,
      'json',
      key,
      this.currentValue(this.settingsFile, 'json', key),
      ClientAdapterBase.jsonString(value),
    );
  }
}
