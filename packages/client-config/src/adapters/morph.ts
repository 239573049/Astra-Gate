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

const USER_AGENT = 'mistermorph';

/**
 * Mister Morph (Coexist): `~/.morph/config.yaml` (or the file `MISTER_MORPH_CONFIG` names). Astra takes `llm` over key by key
 * (`inference_provider: openai_response_compatible`, `endpoint`, `api_key`, and with a model `model` + `context_window_tokens`,
 * plus a `User-Agent` header); what the user had is recorded and put back. (Port of MorphClientAdapter.)
 */
export class MorphClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.Morph;

  private get configFile(): string {
    const file = this.env.getEnvironmentVariable('MISTER_MORPH_CONFIG');
    return file ? file : this.env.combine('.morph', 'config.yaml');
  }

  protected get markerFile(): string {
    return this.configFile;
  }

  protected get markerKey(): string {
    return 'llm.endpoint';
  }

  protected override get enabledWarnings(): string[] {
    return ["Mister Morph's Console uses this for new tasks: restart open morph chats to use it there."];
  }

  detect(): ClientDetection {
    const file = this.configFile;
    const dir = path.dirname(file);
    return this.env.fileExists(file) || this.env.directoryExists(dir)
      ? ClientDetections.found(null, `config ${file}`)
      : ClientDetections.notFound(`no ${file}`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [
      this.yamlSet(file, 'llm.inference_provider', 'openai_response_compatible'),
      this.yamlSet(file, 'llm.endpoint', FileClientAdapter.gateway(ctx.gatewayBaseUrl)),
      this.yamlSet(file, 'llm.api_key', ctx.localKey),
    ];
    if (ctx.model) {
      changes.push(this.yamlSet(file, 'llm.model', ctx.model));
      const window = ClientAdapterBase.positiveLong(ClientAdapterBase.modelHints(ctx).get(ctx.model)?.contextWindow);
      if (window !== null) changes.push(this.yamlSet(file, 'llm.context_window_tokens', window));
      else {
        const drop = this.drop(file, 'yaml', 'llm.context_window_tokens');
        if (drop) changes.push(drop);
      }
    }
    changes.push(this.yamlSet(file, 'llm.headers.User-Agent', USER_AGENT));
    return this.buildPlan(changes);
  }
}
