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
import { FileClientAdapter } from './file-adapter.js';

const PROVIDER_PATH = 'providers.litellm';

/**
 * Paimon (`uv tool install paimon`; Coexist): `config.json` in `PAIMON_CONFIG_HOME` (absolute) else `~/.config/paimon`. Astra
 * sets `providers.litellm` (pydantic-ai's OpenAI-compatible proxy provider) to the gateway and, with a model, `model` to
 * `litellm:<id>` and `compaction.context_window`. (Port of PaimonClientAdapter.)
 */
export class PaimonClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.Paimon;

  private get dir(): string {
    const home = this.env.getEnvironmentVariable('PAIMON_CONFIG_HOME');
    return home && path.isAbsolute(home) ? home : this.env.combine('.config', 'paimon');
  }

  private get configFile(): string {
    return path.join(this.dir, 'config.json');
  }

  protected get markerFile(): string {
    return this.configFile;
  }

  protected get markerKey(): string {
    return PROVIDER_PATH;
  }

  protected override get enabledWarnings(): string[] {
    return ['Paimon reads its model at start-up: restart open paimon sessions to use this.'];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('paimon');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no paimon executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const provider = { api_base: FileClientAdapter.gateway(ctx.gatewayBaseUrl), api_key: ctx.localKey };
    const changes: ConfigChange[] = [this.jsonSet(file, PROVIDER_PATH, provider)];
    if (ctx.model) {
      changes.push(this.jsonSet(file, 'model', `litellm:${ctx.model}`));
      const window = ClientAdapterBase.positiveLong(ClientAdapterBase.modelHints(ctx).get(ctx.model)?.contextWindow);
      if (window !== null) changes.push(this.jsonSet(file, 'compaction.context_window', window));
    }
    return this.buildPlan(changes);
  }
}
