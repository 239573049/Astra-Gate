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

const PROFILE_TABLE = 'providers.astra';
const PICK_TABLE = 'provider';
const DEFAULT_CONTEXT = 128_000;

/**
 * jcode (Coexist): `config.toml` in `JCODE_HOME` (else `~/.jcode`). Adds `[providers.astra]` (openai-compatible, a literal key,
 * the bound provider's models as one array of inline tables) and, with a model, `[provider] default_provider` /
 * `default_model`. (Port of JcodeClientAdapter.)
 */
export class JcodeClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.Jcode;

  private get dir(): string {
    const home = this.env.getEnvironmentVariable('JCODE_HOME')?.trim();
    return home ? home : this.env.combine('.jcode');
  }

  private get configFile(): string {
    return path.join(this.dir, 'config.toml');
  }

  protected get markerFile(): string {
    return this.configFile;
  }

  protected get markerKey(): string {
    return PROFILE_TABLE;
  }

  protected override isTableKeyPath(keyPath: string): boolean {
    return keyPath === PROFILE_TABLE || keyPath === PICK_TABLE;
  }

  protected override get enabledWarnings(): string[] {
    return ['jcode reads its settings at start-up: restart open jcode sessions to use this.'];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('jcode');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no jcode executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const raw = FileClientAdapter.raw;
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: string[] = [];
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const parts = [
        'id = ' + raw(id),
        'context_window = ' + (ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT),
      ];
      parts.push(ClientAdapterBase.boolHint(info?.vision) === true ? 'input = ["text", "image"]' : 'input = ["text"]');
      if (ClientAdapterBase.boolHint(info?.reasoning) === true) parts.push('reasoning = true');
      models.push('{ ' + parts.join(', ') + ' }');
    }
    const changes: ConfigChange[] = [];
    changes.push(
      ...this.tableChanges(file, PROFILE_TABLE, [
        ['type', raw('openai-compatible')],
        ['base_url', raw(FileClientAdapter.gateway(ctx.gatewayBaseUrl))],
        ['api_key', raw(ctx.localKey)],
        ['requires_api_key', 'true'],
        ['default_model', ctx.model ? raw(ctx.model) : null],
        ['models', '[' + models.join(', ') + ']'],
      ]),
    );
    // [provider] is a table of its own: made whole when absent (and then taken out whole), refreshed key by key when it is the user's.
    if (ctx.model)
      changes.push(
        ...this.tableChanges(file, PICK_TABLE, [
          ['default_provider', raw(PROVIDER_ID)],
          ['default_model', raw(ctx.model)],
        ]),
      );
    return this.buildPlan(changes);
  }
}
