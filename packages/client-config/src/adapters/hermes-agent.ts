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

const SECTION_PATH = 'model';
const PROVIDER_PATH = 'model.provider';
const BASE_URL_PATH = 'model.base_url';
const API_KEY_PATH = 'model.api_key';
const API_MODE_PATH = 'model.api_mode';
const CHAT_API_MODE = 'chat_completions';
const ANTHROPIC_API_MODE = 'anthropic_messages';

/** Every key this adapter may write (restored on disable when tracked). */
const MANAGED_PATHS = [SECTION_PATH, PROVIDER_PATH, BASE_URL_PATH, API_KEY_PATH, 'model.default', 'model.model', API_MODE_PATH];

/**
 * Hermes Agent (Switch): points the `model:` section of `<HERMES_HOME>/config.yaml` at the gateway (`provider: custom`,
 * `base_url`, `api_key`, the model id and, for an Anthropic-native model, `api_mode`). HERMES_HOME defaults to
 * `~/.hermes` (`%LOCALAPPDATA%\hermes` on Windows). (Port of HermesAgentClientAdapter.)
 */
export class HermesAgentClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.HermesAgent;
  readonly mode = 'switch' as const;
  readonly availability = 'available' as const;

  private get homeDir(): string {
    const dir = this.env.getEnvironmentVariable('HERMES_HOME');
    if (dir) return dir;
    if (this.env.os.toLowerCase() === 'windows') {
      const local = this.env.getEnvironmentVariable('LOCALAPPDATA');
      if (local) return path.join(local, 'hermes');
    }
    return this.env.combine('.hermes');
  }

  private get configFile(): string {
    return path.join(this.homeDir, 'config.yaml');
  }

  detect(): ClientDetection {
    const dir = this.homeDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('hermes');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no hermes executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    // Older configs write the model as a bare string ("model: provider/name"): replace that scalar with a mapping as
    // a whole (the original string comes back on disable). Once converted, later re-applies keep owning the whole
    // section so its recorded value stays exact.
    const mode =
      ctx.model && this.modelProtocols(ctx, 'openai-chat').get(ctx.model) === 'anthropic'
        ? ANTHROPIC_API_MODE
        : CHAT_API_MODE;
    const section = this.currentValue(file, 'yaml', SECTION_PATH);
    const recorded = this.store.get(this.kind, file, SECTION_PATH);
    const bareModel = recorded ? ClientAdapterBase.stringOf(recorded.originalValueJson) : ClientAdapterBase.stringOf(section);
    if (bareModel !== null) {
      const mapping: Record<string, string> = {
        default: ctx.model ? ctx.model : bareModel,
        provider: 'custom',
        base_url: ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1',
        api_key: ctx.localKey,
      };
      if (mode !== CHAT_API_MODE) mapping.api_mode = mode;
      return this.buildPlan([new ConfigChange(file, 'yaml', SECTION_PATH, section, toJsonText(mapping))]);
    }

    const changes: ConfigChange[] = [
      this.key(PROVIDER_PATH, 'custom'),
      this.key(BASE_URL_PATH, ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1'),
      this.key(API_KEY_PATH, ctx.localKey),
    ];
    if (ctx.model) changes.push(this.key(this.modelPath, ctx.model));
    // The wire mode follows the selected model; any other mode the user had would not match the endpoint we hand out.
    const apiMode = this.currentValue(file, 'yaml', API_MODE_PATH);
    if (mode !== CHAT_API_MODE) changes.push(this.key(API_MODE_PATH, mode));
    else if (apiMode !== null && ClientAdapterBase.stringOf(apiMode) !== CHAT_API_MODE)
      changes.push(
        this.store.get(this.kind, file, API_MODE_PATH)?.originalAbsent === true
          ? new ConfigChange(file, 'yaml', API_MODE_PATH, apiMode, null) // the mode Astra added goes again
          : this.key(API_MODE_PATH, CHAT_API_MODE),
      );
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    for (const p of MANAGED_PATHS) {
      if (this.store.get(this.kind, file, p) === null) continue;
      const current = this.currentValue(file, 'yaml', p);
      if (current !== null) changes.push(new ConfigChange(file, 'yaml', p, current, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const file = this.configFile;
    const section = this.store.get(this.kind, file, SECTION_PATH);
    const entry = this.store.get(this.kind, file, BASE_URL_PATH);
    const enabled = section
      ? equalsValue('yaml', this.currentValue(file, 'yaml', SECTION_PATH), section.appliedValueJson)
      : entry !== null && equalsValue('yaml', this.currentValue(file, 'yaml', BASE_URL_PATH), entry.appliedValueJson);
    const warnings: string[] = [];
    for (const key of ['key_env', 'key_cmd'])
      if (this.currentValue(file, 'yaml', `model.${key}`) !== null)
        warnings.push(
          `config.yaml sets model.${key}; it may take precedence over the gateway key written to model.api_key (not modified by Astra).`,
        );
    return this.buildStatus(detection, enabled, warnings);
  }

  /** "model.default", or the "model.model" alias when the file already uses it instead. */
  private get modelPath(): string {
    const file = this.configFile;
    return this.currentValue(file, 'yaml', 'model.default') === null &&
      this.currentValue(file, 'yaml', 'model.model') !== null
      ? 'model.model'
      : 'model.default';
  }

  private key(p: string, value: string): ConfigChange {
    const file = this.configFile;
    return new ConfigChange(file, 'yaml', p, this.currentValue(file, 'yaml', p), ClientAdapterBase.jsonString(value));
  }
}
