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
import type { ApiProtocol } from '../domain/protocols.js';

const PROVIDER_ID = 'astra';
const DEFAULT_CONTEXT_WINDOW = 128_000;
const DEFAULT_MAX_OUTPUT_TOKENS = 16_384;
const PROVIDER_PATH = `providers.${PROVIDER_ID}`;
const LARGE_MODEL_PATH = 'models.large';

/** The providers Astra writes, one per protocol: id, Crush `type` and display name. */
const PROVIDERS: ReadonlyArray<{ protocol: ApiProtocol; id: string; type: string; name: string }> = [
  { protocol: 'openai-chat', id: PROVIDER_ID, type: 'openai-compat', name: 'Astra' },
  { protocol: 'openai-responses', id: PROVIDER_ID + '-responses', type: 'openai', name: 'Astra (Responses)' },
  { protocol: 'anthropic', id: PROVIDER_ID + '-anthropic', type: 'anthropic', name: 'Astra (Anthropic)' },
  { protocol: 'gemini', id: PROVIDER_ID + '-gemini', type: 'google', name: 'Astra (Gemini)' },
];

/** Crush's `IsResponsesModel`: the ids its `openai` type sends over Responses (others go over Chat). */
const crushUsesResponses = (id: string): boolean =>
  /gpt-(?:[4-9]|[1-9]\d)/.test(id.toLowerCase()) || id === 'chatgpt-4o-latest';

/**
 * Crush (Charm; Coexist): adds `providers.astra` (an `openai-compat` provider listing the bound provider's
 * models) to `crush.json` and, when a model is chosen, selects it with `models.large`. Models whose native
 * protocol is not Chat go to providers of their own (`astra-responses`, `astra-anthropic`, `astra-gemini`).
 * The file is `<CRUSH_GLOBAL_CONFIG>/crush.json`, else `$XDG_CONFIG_HOME/crush/crush.json`, else
 * `~/.config/crush/crush.json`; inspect warns about a `crushrc`. (Port of CrushClientAdapter.)
 */
export class CrushClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.Crush;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get configDir(): string {
    const dir = this.env.getEnvironmentVariable('CRUSH_GLOBAL_CONFIG');
    if (dir) return dir;
    const xdg = this.env.getEnvironmentVariable('XDG_CONFIG_HOME');
    return xdg ? path.join(xdg, 'crush') : this.env.combine('.config', 'crush');
  }

  private get configFile(): string {
    return path.join(this.configDir, 'crush.json');
  }

  private get crushRcFile(): string {
    return path.join(this.configDir, 'crushrc');
  }

  detect(): ClientDetection {
    const dir = this.configDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('crush');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no crush executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const root = ctx.gatewayBaseUrl.replace(/\/+$/, '');
    const protocols = this.modelProtocols(
      ctx,
      'openai-chat',
      undefined,
      (id, p) => p !== 'openai-responses' || crushUsesResponses(id),
    );
    const models = CrushClientAdapter.models(ctx);
    const changes: ConfigChange[] = [];
    for (const { protocol, id, type, name } of PROVIDERS) {
      const own = models.filter((m) => protocols.get(m.id as string) === protocol);
      const keyPath = `providers.${id}`;
      const before = this.currentValue(file, 'json', keyPath);
      if (protocol !== 'openai-chat' && own.length === 0) {
        // Not needed (any more): a provider Astra added goes again.
        if (before !== null && this.store.get(this.kind, file, keyPath) !== null)
          changes.push(new ConfigChange(file, 'json', keyPath, before, null));
        continue;
      }
      const provider: Record<string, unknown> = {
        name,
        type,
        base_url: protocol === 'anthropic' || protocol === 'gemini' ? root : root + '/v1',
        api_key: ctx.localKey,
      };
      // No models known at all: Crush discovers them from /v1/models. Models that all live elsewhere: no discovery.
      if (own.length > 0) provider.models = own;
      else if (models.length > 0) provider.discover_models = false;
      changes.push(new ConfigChange(file, 'json', keyPath, before, toJsonText(provider)));
    }
    if (ctx.model) {
      const selected = protocols.get(ctx.model) ?? 'openai-chat';
      const selection = { provider: PROVIDERS.find((p) => p.protocol === selected)!.id, model: ctx.model };
      changes.push(
        new ConfigChange(
          file,
          'json',
          LARGE_MODEL_PATH,
          this.currentValue(file, 'json', LARGE_MODEL_PATH),
          toJsonText(selection),
        ),
      );
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    const providerBefore = this.currentValue(file, 'json', PROVIDER_PATH);
    if (providerBefore !== null)
      changes.push(new ConfigChange(file, 'json', PROVIDER_PATH, providerBefore, null));
    for (const { id } of PROVIDERS.slice(1)) {
      const keyPath = `providers.${id}`;
      if (this.store.get(this.kind, file, keyPath) === null) continue;
      const before = this.currentValue(file, 'json', keyPath);
      if (before !== null) changes.push(new ConfigChange(file, 'json', keyPath, before, null));
    }
    // A model the user selected afterwards (another provider) is theirs and stays.
    if (this.store.get(this.kind, file, LARGE_MODEL_PATH) !== null) {
      const large = this.currentValue(file, 'json', LARGE_MODEL_PATH);
      if (large !== null && CrushClientAdapter.selectsAstra(large))
        changes.push(new ConfigChange(file, 'json', LARGE_MODEL_PATH, large, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const warnings: string[] = [];
    if (this.env.fileExists(this.crushRcFile))
      warnings.push(
        `${this.crushRcFile} exists; Crush gives crushrc priority over crush.json for the same top-level keys (not modified by Astra).`,
      );
    return this.buildStatus(detection, this.stillOurs(this.configFile, 'json', PROVIDER_PATH), warnings);
  }

  private static selectsAstra(selectionJson: string): boolean {
    try {
      const v: unknown = JSON.parse(selectionJson);
      if (v === null || typeof v !== 'object' || Array.isArray(v)) return false;
      const p = (v as Record<string, unknown>).provider;
      return typeof p === 'string' && PROVIDERS.some((x) => x.id === p);
    } catch {
      return false;
    }
  }

  /** One entry per bound model; every field of Crush's model schema is required. */
  private static models(ctx: EnableContext): Array<Record<string, unknown>> {
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const context = ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT_WINDOW;
      let maxOutput = ClientAdapterBase.positiveLong(info?.maxOutputTokens) ?? DEFAULT_MAX_OUTPUT_TOKENS;
      if (maxOutput >= context) maxOutput = Math.max(1, Math.floor(context / 4));
      models.push({
        id,
        name: name ?? id,
        context_window: context,
        default_max_tokens: maxOutput,
        cost_per_1m_in: 0,
        cost_per_1m_out: 0,
        cost_per_1m_in_cached: 0,
        cost_per_1m_out_cached: 0,
        can_reason: ClientAdapterBase.boolHint(info?.reasoning) ?? false,
        supports_attachments: ClientAdapterBase.boolHint(info?.vision) ?? false,
      });
    }
    return models;
  }
}
