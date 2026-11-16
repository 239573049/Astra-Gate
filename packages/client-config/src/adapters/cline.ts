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

const SLOT = 'openai-compatible';
const PROVIDER_PATH = `providers.${SLOT}`;

/**
 * Cline's CLI (3.x; Coexist): `data/settings/providers.json` under `CLINE_DIR` (`~/.cline`; `CLINE_DATA_DIR` moves `data`). Astra
 * takes Cline's built-in `openai-compatible` provider, makes it `lastUsedProvider` and puts the bound provider's models in
 * `models.json` beside it. The VS Code extension's state, the desktop app, `updatedAt` and the reasoning effort are not written.
 * (Port of ClineClientAdapter.)
 */
export class ClineClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.Cline;

  private get dir(): string {
    const dir = this.env.getEnvironmentVariable('CLINE_DIR');
    return dir ? dir : this.env.combine('.cline');
  }

  private get dataDir(): string {
    const dir = this.env.getEnvironmentVariable('CLINE_DATA_DIR');
    return dir ? dir : path.join(this.dir, 'data');
  }

  private get providersFile(): string {
    return path.join(this.dataDir, 'settings', 'providers.json');
  }

  private get modelsFile(): string {
    return path.join(this.dataDir, 'settings', 'models.json');
  }

  protected get markerFile(): string {
    return this.providersFile;
  }

  protected get markerKey(): string {
    return PROVIDER_PATH;
  }

  protected override get enabledWarnings(): string[] {
    return [
      "Cline reads its provider as a session starts: open sessions keep the model they have, new ones use this. Reload VS Code's window for its extension (not configured here).",
    ];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('cline');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no cline executable on PATH`);
  }

  configPaths(): string[] {
    return [this.providersFile, this.modelsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const entries = ClientAdapterBase.modelEntries(ctx);
    const model = ctx.model ? ctx.model : (entries[0]?.id ?? null);
    // Cline needs a model to start on: with none there is nothing to write.
    if (!model) return this.buildPlan([]);
    const v1 = FileClientAdapter.gateway(ctx.gatewayBaseUrl);
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Record<string, unknown> = {};
    for (const { id, name } of entries) {
      const info = hints.get(id);
      const capabilities = ['streaming', 'tools'];
      if (ClientAdapterBase.boolHint(info?.vision) === true) capabilities.push('images');
      if (ClientAdapterBase.boolHint(info?.reasoning) === true) capabilities.push('reasoning');
      const entry: Record<string, unknown> = { id, name: name ?? id, capabilities };
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) entry.contextWindow = context;
      const output = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (output !== null) entry.maxTokens = output;
      models[id] = entry;
    }
    const listing = { provider: { name: 'Astra', baseUrl: v1, defaultModelId: model }, models };
    const provider = {
      settings: { provider: SLOT, apiKey: ctx.localKey, model, baseUrl: v1, headers: { 'User-Agent': 'cline' } },
      tokenSource: 'manual',
    };

    const changes: ConfigChange[] = [];
    // Cline refuses a file without its envelope: a file Astra makes carries the one Cline writes itself.
    if (!this.env.fileExists(this.providersFile)) {
      changes.push(this.jsonSet(this.providersFile, 'version', 1));
      changes.push(this.jsonSet(this.providersFile, 'modes', {}));
    }
    changes.push(this.jsonSet(this.providersFile, PROVIDER_PATH, provider));
    changes.push(this.jsonSet(this.providersFile, 'lastUsedProvider', SLOT));
    if (!this.env.fileExists(this.modelsFile)) changes.push(this.jsonSet(this.modelsFile, 'version', 1));
    changes.push(this.jsonSet(this.modelsFile, PROVIDER_PATH, listing));
    return this.buildPlan(changes);
  }
}
