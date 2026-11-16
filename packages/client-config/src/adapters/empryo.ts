import {
  ClientDetections,
  type ClientDetection,
  type ConfigChange,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { JsoncEditor } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { FileClientAdapter, PROVIDER_ID } from './file-adapter.js';

const PROVIDER_PATH = 'providers.' + JsoncEditor.selector(['id', PROVIDER_ID]);

/**
 * Empryo (command `empryo`; Coexist): `~/.empryo/config.json`. Adds the `providers` element with `id: "astra"` at the gateway and,
 * with a model, `defaultModel: "astra/<id>"`. Empryo cannot hold a key for a provider, so requests are keyless. (Port of
 * EmpryoClientAdapter.)
 */
export class EmpryoClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.Empryo;

  private get configFile(): string {
    return this.env.combine('.empryo', 'config.json');
  }

  protected get markerFile(): string {
    return this.configFile;
  }

  protected get markerKey(): string {
    return PROVIDER_PATH;
  }

  protected override get enabledWarnings(): string[] {
    return [
      'Empryo cannot hold a key for a provider (it reads one only from an environment variable): turn off "require gateway key" in Astra\'s settings so local requests are accepted (they use the default token).',
      'Empryo reads its config at start-up: restart open empryo sessions to use this.',
    ];
  }

  detect(): ClientDetection {
    const dir = this.env.combine('.empryo');
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('empryo');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no empryo executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const v1 = FileClientAdapter.gateway(ctx.gatewayBaseUrl);
    const provider = { id: PROVIDER_ID, name: 'Astra', baseURL: v1, modelsAPI: v1 + '/models' };
    const changes: ConfigChange[] = [this.jsonSet(file, PROVIDER_PATH, provider)];
    if (ctx.model) changes.push(this.jsonSet(file, 'defaultModel', `${PROVIDER_ID}/${ctx.model}`));
    return this.buildPlan(changes);
  }
}
