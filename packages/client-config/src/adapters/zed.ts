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
import { toJsonText } from '../editing/jsonc-editor.js';

const PROVIDER_ID = 'astra';
/** Context window used when the model catalog does not know it (Zed requires `max_tokens`). */
export const ZED_DEFAULT_CONTEXT_WINDOW = 128_000;
/** Shown on the Zed card at all times; never contains the token itself. */
export const ZED_TOKEN_WARNING =
  "Zed never reads API keys from settings.json. Open Zed's agent settings, choose the Astra provider and paste an Astra " +
  'token from the Tokens page once (or set the ASTRA_API_KEY environment variable before starting Zed).';

const PROVIDER_PATH = `language_models.openai_compatible.${PROVIDER_ID}`;
const DEFAULT_MODEL_PATH = 'agent.default_model';

/**
 * Zed (Coexist): adds `language_models.openai_compatible.astra` (`api_url` and the bound provider's models as
 * `available_models`) to `settings.json` and, when a model is chosen, selects it with `agent.default_model`.
 * The file is `~/.config/zed/settings.json` on macOS, `$XDG_CONFIG_HOME/zed/settings.json` on Linux and
 * `%APPDATA%\Zed\settings.json` on Windows. Zed never reads an API key from settings.json, so inspect always warns.
 * (Port of ZedClientAdapter.)
 */
export class ZedClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.Zed;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get configDir(): string {
    switch (this.env.os) {
      case 'windows': {
        const appData = this.env.getEnvironmentVariable('APPDATA');
        return path.join(appData ? appData : this.env.combine('AppData', 'Roaming'), 'Zed');
      }
      case 'linux': {
        const xdg = this.env.getEnvironmentVariable('XDG_CONFIG_HOME');
        return path.join(xdg ? xdg : this.env.combine('.config'), 'zed');
      }
      default:
        return this.env.combine('.config', 'zed');
    }
  }

  private get configFile(): string {
    return path.join(this.configDir, 'settings.json');
  }

  detect(): ClientDetection {
    const dir = this.configDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('zed') ?? this.env.findOnPath('zeditor');
    if (exe !== null) return ClientDetections.found(null, `executable ${exe}`);
    const app = this.env.applicationDirectories
      .map((a) => path.join(a, 'Zed.app'))
      .find((p) => this.env.directoryExists(p));
    return app !== undefined
      ? ClientDetections.found(null, `application ${app}`)
      : ClientDetections.notFound(`no ${dir} and no zed executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const model: Record<string, unknown> = {
        name: id,
        display_name: name ?? id,
        max_tokens: ClientAdapterBase.positiveLong(info?.contextWindow) ?? ZED_DEFAULT_CONTEXT_WINDOW,
      };
      const maxOutput = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (maxOutput !== null) model.max_output_tokens = maxOutput;
      const capabilities: Record<string, unknown> = {
        tools: true,
        images: ClientAdapterBase.boolHint(info?.vision) ?? false,
        parallel_tool_calls: false,
        prompt_cache_key: false,
      };
      if (protocols.get(id) === 'openai-responses') capabilities.chat_completions = false;
      model.capabilities = capabilities;
      models.push(model);
    }
    const provider: Record<string, unknown> = {
      api_url: ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1',
    };
    if (models.length > 0) provider.available_models = models;

    const changes: ConfigChange[] = [
      new ConfigChange(file, 'json', PROVIDER_PATH, this.currentValue(file, 'json', PROVIDER_PATH), toJsonText(provider)),
    ];
    if (ctx.model) {
      const selection = { provider: PROVIDER_ID, model: ctx.model };
      changes.push(
        new ConfigChange(file, 'json', DEFAULT_MODEL_PATH, this.currentValue(file, 'json', DEFAULT_MODEL_PATH), toJsonText(selection)),
      );
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    const providerBefore = this.currentValue(file, 'json', PROVIDER_PATH);
    if (providerBefore !== null) changes.push(new ConfigChange(file, 'json', PROVIDER_PATH, providerBefore, null));
    // A model the user selected afterwards (another provider) is theirs and stays.
    if (this.store.get(this.kind, file, DEFAULT_MODEL_PATH) !== null) {
      const selected = this.currentValue(file, 'json', DEFAULT_MODEL_PATH);
      if (selected !== null && selectsAstra(selected))
        changes.push(new ConfigChange(file, 'json', DEFAULT_MODEL_PATH, selected, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    return this.buildStatus(this.detect(), this.stillOurs(this.configFile, 'json', PROVIDER_PATH), [ZED_TOKEN_WARNING]);
  }
}

function selectsAstra(selectionJson: string): boolean {
  try {
    const o: unknown = JSON.parse(selectionJson);
    return o !== null && typeof o === 'object' && !Array.isArray(o) && (o as Record<string, unknown>).provider === PROVIDER_ID;
  } catch {
    return false;
  }
}
