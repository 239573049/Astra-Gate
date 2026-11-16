import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { ConfigFileClientAdapter } from './config-file-adapter.js';
import { PiAiModels } from './pi-ai-models.js';

const PROVIDER_PATH = 'providers.astra';
const DEFAULT_CONTEXT_WINDOW = 128_000;
const DEFAULT_MAX_TOKENS = 16_384;

/**
 * Pencil (pen.dev; Coexist): a custom provider in `~/.pencil/models.json` in Pi's format — `providers.astra` on
 * `openai-completions` with every model carrying the fields Pencil writes for one. (Port of PencilClientAdapter.)
 */
export class PencilClientAdapter extends ConfigFileClientAdapter {
  readonly kind = ClientKinds.Pencil;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dir(): string {
    return this.env.combine('.pencil');
  }

  private get modelsFile(): string {
    return path.join(this.dir, 'models.json');
  }

  detect(): ClientDetection {
    if (this.env.directoryExists(this.dir)) return ClientDetections.found(null, `config directory ${this.dir}`);
    const app = this.findApp('Pencil.app', 'pen.dev.app');
    return app ? ClientDetections.found(null, `application ${app}`) : ClientDetections.notFound(`no ${this.dir}`);
  }

  configPaths(): string[] {
    return [this.modelsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const model: Record<string, unknown> = { id };
      if (name !== null) model.name = name;
      PiAiModels.addHints(model, hints.get(id));
      model.input ??= ['text'];
      const context = ClientAdapterBase.positiveLong(model.contextWindow) ?? DEFAULT_CONTEXT_WINDOW;
      model.contextWindow = context;
      model.maxTokens ??= Math.min(DEFAULT_MAX_TOKENS, context);
      model.cost = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 };
      model.compat = {};
      models.push(model);
    }
    const provider = {
      name: 'Astra',
      baseUrl: ConfigFileClientAdapter.v1(ctx),
      api: 'openai-completions',
      apiKey: ctx.localKey,
      models,
    };
    return this.buildPlan([this.setKey(this.modelsFile, PROVIDER_PATH, toJsonText(provider))]);
  }

  planDisable(): ConfigChangePlan {
    return this.buildPlan(this.trackedRemovals());
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    return this.buildStatus(this.detect(), this.applied(this.modelsFile, PROVIDER_PATH), []);
  }
}
