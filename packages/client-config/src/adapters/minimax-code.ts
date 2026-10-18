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
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import type { ApiProtocol } from '../domain/protocols.js';

const PROVIDER_ID = 'astra';
const DEFAULT_MODEL_PATH = 'defaultModel';
const PROVIDER_PATH = `custom_provider.${PROVIDER_ID}`;

/** The providers Astra writes, one per protocol: key, `api` and display name. */
const PROVIDERS: Array<{ protocol: ApiProtocol; key: string; name: string; api: string }> = [
  { protocol: 'openai-chat', key: PROVIDER_ID, name: 'Astra', api: 'openai-completions' },
  { protocol: 'openai-responses', key: `${PROVIDER_ID}-responses`, name: 'Astra (Responses)', api: 'openai-responses' },
  { protocol: 'anthropic', key: `${PROVIDER_ID}-anthropic`, name: 'Astra (Anthropic)', api: 'anthropic-messages' },
];

/**
 * MiniMax Code (`mcode`; Coexist): adds a custom provider `custom_provider.astra` (plus `astra-responses` /
 * `astra-anthropic` while they have models) to `<data-dir>/config.yaml` and, when a model is chosen, selects it with
 * `defaultModel`. The data directory is `~/.minimax` unless MINIMAX_DATA_DIR (or MAVIS_DATA_DIR) overrides it.
 * (Port of MiniMaxCodeClientAdapter.)
 */
export class MiniMaxCodeClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.MiniMaxCode;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dataDir(): string {
    return (
      this.env.getEnvironmentVariable('MINIMAX_DATA_DIR') ||
      this.env.getEnvironmentVariable('MAVIS_DATA_DIR') ||
      this.env.combine('.minimax')
    );
  }

  private get configFile(): string {
    return path.join(this.dataDir, 'config.yaml');
  }

  detect(): ClientDetection {
    const dir = this.dataDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('mcode');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no mcode executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    const entries = ClientAdapterBase.modelEntries(ctx);
    const changes: ConfigChange[] = [];
    for (const { protocol, key, name: displayName, api } of PROVIDERS) {
      const models: Record<string, unknown> = {};
      let count = 0;
      for (const e of entries.filter((x) => protocols.get(x.id) === protocol)) {
        models[e.id] = modelJson(e.name, hints.get(e.id));
        count++;
      }
      const p = `custom_provider.${key}`;
      const before = this.currentValue(file, 'yaml', p);
      if (protocol !== 'openai-chat' && count === 0) {
        if (before !== null && this.store.get(this.kind, file, p) !== null)
          changes.push(new ConfigChange(file, 'yaml', p, before, null));
        continue;
      }
      const provider = {
        name: displayName,
        api,
        options: { baseURL: ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1', apiKey: ctx.localKey },
        models,
      };
      changes.push(new ConfigChange(file, 'yaml', p, before, toJsonText(provider)));
    }
    if (ctx.model) {
      const protocol = protocols.get(ctx.model) ?? 'openai-chat';
      const key = PROVIDERS.find((p) => p.protocol === protocol)!.key;
      changes.push(
        new ConfigChange(
          file,
          'yaml',
          DEFAULT_MODEL_PATH,
          this.currentValue(file, 'yaml', DEFAULT_MODEL_PATH),
          ClientAdapterBase.jsonString(`custom_provider:${key}/${ctx.model}`),
        ),
      );
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    const providerBefore = this.currentValue(file, 'yaml', PROVIDER_PATH);
    if (providerBefore !== null) changes.push(new ConfigChange(file, 'yaml', PROVIDER_PATH, providerBefore, null));
    for (const { key } of PROVIDERS.slice(1)) {
      const p = `custom_provider.${key}`;
      const before = this.store.get(this.kind, file, p) !== null ? this.currentValue(file, 'yaml', p) : null;
      if (before !== null) changes.push(new ConfigChange(file, 'yaml', p, before, null));
    }
    // A model the user picked afterwards (another provider) is theirs and stays.
    const model = this.currentValue(file, 'yaml', DEFAULT_MODEL_PATH);
    const modelText = ClientAdapterBase.stringOf(model);
    if (PROVIDERS.some((p) => modelText?.startsWith(`custom_provider:${p.key}/`) === true))
      changes.push(new ConfigChange(file, 'yaml', DEFAULT_MODEL_PATH, model, null));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const entry = this.store.get(this.kind, this.configFile, PROVIDER_PATH);
    const enabled =
      entry !== null && equalsValue('yaml', this.currentValue(this.configFile, 'yaml', PROVIDER_PATH), entry.appliedValueJson);
    return this.buildStatus(detection, enabled, []);
  }
}

/** A model entry (models.dev field names): name, and the limits / image input / reasoning flag Astra knows. */
function modelJson(name: string | null, info: JsonObject | undefined): Record<string, unknown> {
  const model: Record<string, unknown> = {};
  if (name !== null) model.name = name;
  const limit: Record<string, number> = {};
  const context = ClientAdapterBase.positiveLong(info?.contextWindow);
  if (context !== null) limit.context = context;
  const output = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
  if (output !== null) limit.output = output;
  if (Object.keys(limit).length > 0) model.limit = limit;
  const vision = ClientAdapterBase.boolHint(info?.vision);
  if (vision !== null) model.modalities = { input: vision ? ['text', 'image'] : ['text'] };
  const reasoning = ClientAdapterBase.boolHint(info?.reasoning);
  if (reasoning !== null) model.reasoning = reasoning;
  return model;
}
