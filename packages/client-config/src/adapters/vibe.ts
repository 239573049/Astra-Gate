import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ClientDetections,
  type ClientDetection,
  type ConfigChange,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { ClientKinds } from '../domain/kinds.js';
import { FileClientAdapter, PROVIDER_ID } from './file-adapter.js';

const PROVIDER_SELECTOR = 'providers[name=astra]';
const MODELS_SELECTOR = 'models[provider=astra]';
const DEFAULT_CONTEXT = 128_000;

/**
 * Mistral Vibe (command `vibe`; Coexist): `config.toml` in `VIBE_HOME` (else `~/.vibe`). Astra owns the `[[providers]]` entry
 * named `astra` (the local key as an `Authorization` header in `extra_headers`, since Vibe reads keys only from the environment)
 * and the `[[models]]` entries whose `provider` is `astra`, aliased `astra/<id>`; `active_model` points at the chosen one.
 * (Port of VibeClientAdapter.)
 */
export class VibeClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.Vibe;

  private get dir(): string {
    const home = this.env.getEnvironmentVariable('VIBE_HOME')?.trim();
    return home ? this.expandHome(home) : this.env.combine('.vibe');
  }

  private get configFile(): string {
    return path.join(this.dir, 'config.toml');
  }

  protected get markerFile(): string {
    return this.configFile;
  }

  protected get markerKey(): string {
    return PROVIDER_SELECTOR;
  }

  protected override isTableKeyPath(keyPath: string): boolean {
    return keyPath === PROVIDER_SELECTOR || keyPath === MODELS_SELECTOR;
  }

  protected override get enabledWarnings(): string[] {
    return ['Vibe reads its settings at start-up: restart open vibe sessions to use this.'];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('vibe');
    // Avibe's `vibe` command is not Mistral Vibe's: an executable under an avibe folder does not count.
    if (exe && !exe.toLowerCase().includes('avibe')) return ClientDetections.found(null, `executable ${exe}`);
    return ClientDetections.notFound(`no ${dir} and no vibe executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const raw = FileClientAdapter.raw;
    const provider: Record<string, string> = {
      name: raw(PROVIDER_ID),
      api_base: raw(FileClientAdapter.gateway(ctx.gatewayBaseUrl)),
      api_style: raw('openai'),
      backend: raw('generic'),
      extra_headers: '{ Authorization = ' + raw('Bearer ' + ctx.localKey) + ' }',
    };
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Array<Record<string, string>> = [];
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      models.push({
        name: raw(id),
        provider: raw(PROVIDER_ID),
        alias: raw(`${PROVIDER_ID}/${id}`),
        thinking_levels: ClientAdapterBase.boolHint(info?.reasoning) === true ? '["off", "low", "medium", "high"]' : '["off"]',
        supports_images: ClientAdapterBase.boolHint(info?.vision) === true ? 'true' : 'false',
        max_context_length: String(ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT),
      });
    }
    const changes: ConfigChange[] = [
      this.tomlEntries(file, PROVIDER_SELECTOR, [provider]),
      this.tomlEntries(file, MODELS_SELECTOR, models),
    ];
    if (ctx.model) changes.push(this.tomlSet(file, 'active_model', raw(`${PROVIDER_ID}/${ctx.model}`)));
    return this.buildPlan(changes);
  }
}
