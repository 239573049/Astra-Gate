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
import { tomlString } from '../editing/toml-editor.js';
import { ClientKinds } from '../domain/kinds.js';

const PROFILE_ID = 'astra';
const DEFAULT_MODEL = 'grok-4';
const DEFAULT_CONTEXT_WINDOW = 256000;
const DEFAULT_KEY_PATH = 'models.default';
const PROFILE_TABLE_PATH = `model.${PROFILE_ID}`;
/** The fields refreshed one by one inside a profile table that already exists. */
const REFRESHED_FIELDS = ['base_url', 'api_key', 'model', 'api_backend', 'context_window'];

/**
 * Grok Build CLI (Switch): `~/.grok/config.toml` (GROK_HOME relocates it). Adds an "astra" profile table
 * `[model.astra]` and flips `models.default`; the profile's `api_backend` follows the selected model's protocol.
 * (Port of GrokBuildClientAdapter.)
 */
export class GrokBuildClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.GrokBuild;
  readonly mode = 'switch' as const;
  readonly availability = 'available' as const;

  private get configDir(): string {
    const dir = this.env.getEnvironmentVariable('GROK_HOME');
    return dir ? dir : this.env.combine('.grok');
  }

  private get configFile(): string {
    return path.join(this.configDir, 'config.toml');
  }

  detect(): ClientDetection {
    const dir = this.configDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('grok');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no grok executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [
      new ConfigChange(
        file,
        'toml',
        DEFAULT_KEY_PATH,
        this.currentValue(file, 'toml', DEFAULT_KEY_PATH),
        ClientAdapterBase.jsonString(tomlString(PROFILE_ID)),
      ),
    ];
    const model = ctx.model ? ctx.model : DEFAULT_MODEL;
    const protocol = ctx.model
      ? (this.modelProtocols(ctx, 'openai-responses').get(model) ?? 'openai-responses')
      : 'openai-responses';
    const backend = tomlString(
      protocol === 'openai-chat' ? 'chat_completions' : protocol === 'anthropic' ? 'messages' : 'responses',
    );
    const contextWindow = String(
      ClientAdapterBase.positiveLong(ClientAdapterBase.modelHints(ctx).get(model)?.contextWindow) ??
        DEFAULT_CONTEXT_WINDOW,
    );
    const baseUrl = tomlString(ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1');
    if (this.currentValue(file, 'toml', PROFILE_TABLE_PATH, 'table') === null) {
      const items = {
        model: tomlString(model),
        base_url: baseUrl,
        name: tomlString('Astra'),
        api_key: tomlString(ctx.localKey),
        api_backend: backend,
        context_window: contextWindow,
      };
      changes.push(new ConfigChange(file, 'toml', PROFILE_TABLE_PATH, null, toJsonText(items), { kind: 'table' }));
    } else {
      const fields: Array<[string, string]> = [
        ['base_url', baseUrl],
        ['api_key', tomlString(ctx.localKey)],
        ['model', tomlString(model)],
        ['api_backend', backend],
        ['context_window', contextWindow],
      ];
      for (const [key, value] of fields) {
        const fieldPath = `${PROFILE_TABLE_PATH}.${key}`;
        const before = this.currentValue(file, 'toml', fieldPath);
        if (before !== value) changes.push(new ConfigChange(file, 'toml', fieldPath, before, ClientAdapterBase.jsonString(value)));
      }
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const before = this.currentValue(file, 'toml', DEFAULT_KEY_PATH);
    const changes: ConfigChange[] = [];
    if (before !== null) changes.push(new ConfigChange(file, 'toml', DEFAULT_KEY_PATH, before, null));
    // Fields refreshed inside a pre-existing profile table were tracked individually; restore them.
    for (const field of REFRESHED_FIELDS) {
      const fieldPath = `${PROFILE_TABLE_PATH}.${field}`;
      if (this.store.get(this.kind, file, fieldPath) === null) continue;
      const current = this.currentValue(file, 'toml', fieldPath);
      if (current !== null) changes.push(new ConfigChange(file, 'toml', fieldPath, current, null));
    }
    // A profile table Astra created is removed again so the file restores byte-identically.
    if (this.store.get(this.kind, file, PROFILE_TABLE_PATH)?.originalAbsent === true)
      changes.push(
        new ConfigChange(file, 'toml', PROFILE_TABLE_PATH, this.currentValue(file, 'toml', PROFILE_TABLE_PATH, 'table'), null, {
          kind: 'table',
        }),
      );
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [...this.planDisable().changes];
    changes.push(
      new ConfigChange(file, 'toml', PROFILE_TABLE_PATH, this.currentValue(file, 'toml', PROFILE_TABLE_PATH, 'table'), null, {
        kind: 'table',
      }),
    );
    return this.buildPlan(changes);
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const entry = this.store.get(this.kind, this.configFile, DEFAULT_KEY_PATH);
    const enabled =
      entry !== null && equalsValue('toml', this.currentValue(this.configFile, 'toml', DEFAULT_KEY_PATH), entry.appliedValueJson);
    const warnings = ["Grok Build's config format is verified via cc-switch; no official xAI documentation yet."];
    return this.buildStatus(detection, enabled, warnings);
  }

  protected override isTableKeyPath(keyPath: string): boolean {
    return keyPath === PROFILE_TABLE_PATH;
  }
}
