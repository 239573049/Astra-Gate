import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { ClientKinds } from '../domain/kinds.js';
import { FileClientAdapter, PROVIDER_ID } from './file-adapter.js';

const PROVIDER_PATH = 'providers.astra';
const DEFAULTS = 'agents.defaults';

/**
 * nanobot (`pip install nanobot-ai`; Coexist): `~/.nanobot/config.json`. Adds `providers.astra` (the gateway's `/v1`, the local
 * key, a `User-Agent`) and, with a model, `agents.defaults.provider` / `model` (`astra/<id>`) / `contextWindowTokens`; a
 * `modelPreset` that would go over them, and a snake_case spelling of those keys, are taken out and put back on disable.
 * (Port of NanobotClientAdapter.)
 */
export class NanobotClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.Nanobot;

  private get configFile(): string {
    return this.env.combine('.nanobot', 'config.json');
  }

  protected get markerFile(): string {
    return this.configFile;
  }

  protected get markerKey(): string {
    return PROVIDER_PATH;
  }

  protected override get enabledWarnings(): string[] {
    return ["nanobot's gateway and WebUI take this at their next turn; restart `nanobot agent` sessions to use it."];
  }

  detect(): ClientDetection {
    const dir = this.env.combine('.nanobot');
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('nanobot');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no nanobot executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const provider = {
      apiKey: ctx.localKey,
      apiBase: FileClientAdapter.gateway(ctx.gatewayBaseUrl),
      extraHeaders: { 'User-Agent': 'nanobot' },
    };
    const changes: ConfigChange[] = [this.jsonSet(file, PROVIDER_PATH, provider)];
    if (ctx.model) {
      // nanobot reads either spelling of a key; the one this writes must be the only one, and a preset would go over it.
      for (const p of [`${DEFAULTS}.model_preset`, `${DEFAULTS}.modelPreset`, `${DEFAULTS}.context_window_tokens`]) {
        const drop = this.drop(file, 'json', p);
        if (drop) changes.push(drop);
      }
      changes.push(this.jsonSet(file, `${DEFAULTS}.provider`, PROVIDER_ID));
      changes.push(this.jsonSet(file, `${DEFAULTS}.model`, `${PROVIDER_ID}/${ctx.model}`));
      const window = ClientAdapterBase.positiveLong(ClientAdapterBase.modelHints(ctx).get(ctx.model)?.contextWindow);
      if (window !== null)
        changes.push(
          new ConfigChange(
            file,
            'json',
            `${DEFAULTS}.contextWindowTokens`,
            this.currentValue(file, 'json', `${DEFAULTS}.contextWindowTokens`),
            String(window),
          ),
        );
      else {
        const dropWindow = this.drop(file, 'json', `${DEFAULTS}.contextWindowTokens`);
        if (dropWindow) changes.push(dropWindow);
      }
    }
    return this.buildPlan(changes);
  }
}
