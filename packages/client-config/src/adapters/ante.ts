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

const PROVIDER_PATH = 'providers.astra';

/**
 * Ante (command `ante`; Coexist): `catalog.json` in `ANTE_HOME` (else `~/.ante`). Adds `providers.astra` (OpenAiCompatible at
 * the gateway's `/v1`, the bound provider's models in `preferred_models`) and, with a model, `provider` / `model` in the
 * `settings.json` beside it. Ante sends no key to a provider without an `auth` block, so requests are keyless. (Port of
 * AnteClientAdapter.)
 */
export class AnteClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.Ante;

  private get dir(): string {
    const home = this.env.getEnvironmentVariable('ANTE_HOME')?.trim();
    return home ? home : this.env.combine('.ante');
  }

  private get catalogFile(): string {
    return path.join(this.dir, 'catalog.json');
  }

  private get settingsFile(): string {
    return path.join(this.dir, 'settings.json');
  }

  protected get markerFile(): string {
    return this.catalogFile;
  }

  protected get markerKey(): string {
    return PROVIDER_PATH;
  }

  protected override get enabledWarnings(): string[] {
    return [
      'Ante sends no key to a provider without an auth block: turn off "require gateway key" in Astra\'s settings so local requests are accepted (they use the default token).',
      'Ante reads its catalog once per process: restart open ante sessions to use this.',
    ];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('ante');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no ante executable on PATH`);
  }

  configPaths(): string[] {
    return [this.catalogFile, this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const model: Record<string, unknown> = { id };
      if (name !== null && name !== id) model.display_name = name;
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) model.context_limit = context;
      let output = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (output !== null) {
        // Ante skips an entry whose max_tokens is not less than its context_limit.
        if (context !== null && output >= context) output = context - 1;
        if (output > 0) model.max_tokens = output;
      }
      models.push(model);
    }
    const provider: Record<string, unknown> = {
      display_name: 'Astra',
      base_url: FileClientAdapter.gateway(ctx.gatewayBaseUrl),
      wire_style: 'OpenAiCompatible',
    };
    if (models.length > 0) provider.preferred_models = models;
    const changes: ConfigChange[] = [this.jsonSet(this.catalogFile, PROVIDER_PATH, provider)];
    if (ctx.model) {
      changes.push(this.jsonSet(this.settingsFile, 'provider', PROVIDER_ID));
      changes.push(this.jsonSet(this.settingsFile, 'model', ctx.model));
    }
    return this.buildPlan(changes);
  }
}
