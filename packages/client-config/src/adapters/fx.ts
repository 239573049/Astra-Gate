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
import { JNum, JObj, compactJson, type JV } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';

const PROVIDER_ID = 'astra';
const PROVIDER_PATH = `providers.${PROVIDER_ID}`;
const MODEL_PATH = `models.${PROVIDER_ID}`;
/** How many models fx takes in a provider's model_metadata. */
const MAX_MODELS = 256;
const num = (n: number): JNum => new JNum(String(n));

/**
 * fx (fx.sh, Vercel Labs; Coexist): `~/.fx/settings.json`. Adds `providers.astra` (`openai-chat-completions`, keyless `auth`
 * none, the bound provider's models as `model_metadata`, at most 256) and, when a model is chosen, `provider` = `astra` and
 * `models.astra` = the model. (Port of FxClientAdapter.)
 */
export class FxClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.Fx;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dir(): string {
    return this.env.combine('.fx');
  }

  private get settingsFile(): string {
    return path.join(this.dir, 'settings.json');
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
    const hints = ClientAdapterBase.modelHints(ctx);
    // The model in use first, so it is never the one cut at fx's limit (a stable sort, like OrderBy).
    const entries = ClientAdapterBase.modelEntries(ctx)
      .map((e, i) => ({ e, i }))
      .sort((a, b) => (a.e.id === ctx.model ? 0 : 1) - (b.e.id === ctx.model ? 0 : 1) || a.i - b.i)
      .map((x) => x.e);
    const metadata = new Map<string, JV>();
    for (const { id } of entries) {
      if (metadata.size === MAX_MODELS) break;
      const info = hints.get(id);
      const model: Array<[string, JV]> = [
        ['supports_tool_use', true],
        ['supports_vision', ClientAdapterBase.boolHint(info?.vision) === true],
      ];
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) model.push(['context_window', num(context)]);
      // fx refuses an output limit that is not below the context window.
      const output = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (output !== null && (context === null || output < context)) model.push(['max_output_tokens', num(output)]);
      metadata.set(id, new JObj(model));
    }
    const provider = new JObj([
      ['protocol', 'openai-chat-completions'],
      ['base_url', ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1'],
      ['auth', new JObj([['type', 'none']])],
      ['tool_choice_mode', 'send'],
      ['model_metadata', new JObj([...metadata])],
    ]);
    const changes: ConfigChange[] = [
      new ConfigChange(file, 'json', PROVIDER_PATH, this.currentValue(file, 'json', PROVIDER_PATH), compactJson(provider)),
    ];
    if (ctx.model) {
      changes.push(this.key('provider', PROVIDER_ID));
      changes.push(this.key(MODEL_PATH, ctx.model));
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.settingsFile;
    const changes: ConfigChange[] = [];
    const provider = this.currentValue(file, 'json', PROVIDER_PATH);
    if (provider !== null) changes.push(new ConfigChange(file, 'json', PROVIDER_PATH, provider, null));
    // The active provider and its model are only ours while the provider is still Astra's.
    const active = this.currentValue(file, 'json', 'provider');
    if (active !== null && ClientAdapterBase.stringOf(active) === PROVIDER_ID)
      changes.push(new ConfigChange(file, 'json', 'provider', active, null));
    const model = this.currentValue(file, 'json', MODEL_PATH);
    if (this.store.get(this.kind, file, MODEL_PATH) !== null && model !== null)
      changes.push(new ConfigChange(file, 'json', MODEL_PATH, model, null));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.stillOurs(this.settingsFile, 'json', PROVIDER_PATH);
    const warnings: string[] = [];
    if (enabled) {
      warnings.push(
        'fx cannot hold a literal key for a provider: turn off "require gateway key" in Astra\'s settings so local requests are accepted (they use the default token).',
      );
      warnings.push('fx reads its settings at start-up: restart open fx sessions to use this.');
    }
    return this.buildStatus(detection, enabled, warnings);
  }

  private key(key: string, value: string): ConfigChange {
    return new ConfigChange(
      this.settingsFile,
      'json',
      key,
      this.currentValue(this.settingsFile, 'json', key),
      ClientAdapterBase.jsonString(value),
    );
  }
}
