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
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { PiAiModels } from './pi-ai-models.js';

const PROVIDER_ID = 'astra';
const MODEL_REF_PREFIX = PROVIDER_ID + '/';
const PROVIDER_PATH = `providers.${PROVIDER_ID}`;
const DEFAULT_ROLE_PATH = 'modelRoles.default';

/**
 * omp / oh-my-pi (a fork of Pi that runs on Bun; Coexist): adds `providers.astra` (`api: openai-completions`,
 * `baseUrl`, a literal `apiKey`, `auth: apiKey`, the bound provider's models) to `models.yml` and, when a model is
 * chosen, sets `modelRoles.default` to `astra/<id>` in `config.yml`, both in the agent directory `~/.omp/agent`
 * (`PI_CODING_AGENT_DIR` overrides it). The `.yaml` spelling is honored when it already exists. (Port of OmpClientAdapter.)
 */
export class OmpClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.Omp;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get agentDir(): string {
    const dir = this.env.getEnvironmentVariable('PI_CODING_AGENT_DIR');
    return dir ? dir : this.env.combine('.omp', 'agent');
  }

  private get modelsFile(): string {
    return this.existing('models');
  }

  private get configFile(): string {
    return this.existing('config');
  }

  detect(): ClientDetection {
    const dir = this.agentDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('omp');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no omp executable on PATH`);
  }

  configPaths(): string[] {
    return [this.modelsFile, this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const modelsFile = this.modelsFile;
    const configFile = this.configFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    const baseUrl = PiAiModels.baseUrlFor(ctx.gatewayBaseUrl, 'openai-chat');
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const model: Record<string, unknown> = { id, name: name ?? id };
      const protocol = protocols.get(id)!;
      if (protocol !== 'openai-chat') {
        model.api = PiAiModels.apiOf(protocol);
        const own = PiAiModels.baseUrlFor(ctx.gatewayBaseUrl, protocol);
        if (own !== baseUrl) model.baseUrl = own;
      }
      PiAiModels.addHints(model, hints.get(id));
      // omp rejects an output limit that leaves no room for input.
      const context = ClientAdapterBase.positiveLong(model.contextWindow);
      const maxTokens = ClientAdapterBase.positiveLong(model.maxTokens);
      if (context !== null && maxTokens !== null && maxTokens >= context)
        model.maxTokens = Math.max(1, Math.floor(context / 4));
      models.push(model);
    }
    const provider: Record<string, unknown> = {
      baseUrl,
      api: 'openai-completions',
      apiKey: ctx.localKey,
      auth: 'apiKey',
    };
    if (models.length > 0) provider.models = models;

    const changes: ConfigChange[] = [
      new ConfigChange(
        modelsFile,
        'yaml',
        PROVIDER_PATH,
        this.currentValue(modelsFile, 'yaml', PROVIDER_PATH),
        toJsonText(provider),
      ),
    ];
    if (ctx.model) {
      changes.push(
        new ConfigChange(
          configFile,
          'yaml',
          DEFAULT_ROLE_PATH,
          this.currentValue(configFile, 'yaml', DEFAULT_ROLE_PATH),
          ClientAdapterBase.jsonString(MODEL_REF_PREFIX + ctx.model),
        ),
      );
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const modelsFile = this.modelsFile;
    const configFile = this.configFile;
    const changes: ConfigChange[] = [];
    const providerBefore = this.currentValue(modelsFile, 'yaml', PROVIDER_PATH);
    if (providerBefore !== null)
      changes.push(new ConfigChange(modelsFile, 'yaml', PROVIDER_PATH, providerBefore, null));
    // A model the user picked afterwards (another provider) is theirs and stays.
    const role = this.currentValue(configFile, 'yaml', DEFAULT_ROLE_PATH);
    if (
      this.store.get(this.kind, configFile, DEFAULT_ROLE_PATH) !== null &&
      ClientAdapterBase.stringOf(role)?.startsWith(MODEL_REF_PREFIX) === true
    )
      changes.push(new ConfigChange(configFile, 'yaml', DEFAULT_ROLE_PATH, role, null));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    return this.buildStatus(this.detect(), this.stillOurs(this.modelsFile, 'yaml', PROVIDER_PATH), []);
  }

  /** The existing `.yml` / `.yaml` file of a name (the `.yml` one wins), else the `.yml` path. */
  private existing(name: string): string {
    const yml = path.join(this.agentDir, name + '.yml');
    const yaml = path.join(this.agentDir, name + '.yaml');
    return !this.env.fileExists(yml) && this.env.fileExists(yaml) ? yaml : yml;
  }
}
