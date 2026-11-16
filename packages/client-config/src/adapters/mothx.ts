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
import { equalsValue } from '../config-value-codec.js';
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';

const PROVIDER_ID = 'astra';
const PROVIDER_PATH = `providers.${PROVIDER_ID}`;

/**
 * MothX (oschina/mothx; Coexist): one `settings.json` in `MOTHX_DIR` (else `%APPDATA%\mothx` on Windows, `~/.mothx`)
 * holds its providers and start-up model. Adds `providers.astra` (`api: openai-chat`, `thinkingFormat: "openai"`, the bound
 * provider's models) and, when a model is chosen, `defaultProvider` / `defaultModel`. (Port of MothXClientAdapter.)
 */
export class MothXClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.MothX;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get configDir(): string {
    const dir = this.env.getEnvironmentVariable('MOTHX_DIR');
    if (dir) return dir;
    if (this.env.os === 'windows') {
      const app = this.env.getEnvironmentVariable('APPDATA');
      return app ? path.join(app, 'mothx') : this.env.combine('AppData', 'Roaming', 'mothx');
    }
    return this.env.combine('.mothx');
  }

  private get settingsFile(): string {
    return path.join(this.configDir, 'settings.json');
  }

  detect(): ClientDetection {
    const dir = this.configDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('mothx');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no mothx executable on PATH`);
  }

  configPaths(): string[] {
    return [this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.settingsFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const model: Record<string, unknown> = { id };
      if (name !== null) model.name = name;
      // reasoning is what has MothX send a thinking level at all.
      model.reasoning = ClientAdapterBase.boolHint(info?.reasoning) ?? false;
      model.input = ClientAdapterBase.boolHint(info?.vision) === true ? ['text', 'image'] : ['text'];
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) model.contextWindow = context;
      const maxTokens = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (maxTokens !== null) model.maxTokens = maxTokens;
      models.push(model);
    }
    const provider = {
      baseUrl: ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1',
      api: 'openai-chat',
      apiKey: ctx.localKey,
      thinkingFormat: 'openai',
      models,
    };
    const changes: ConfigChange[] = [
      new ConfigChange(file, 'json', PROVIDER_PATH, this.currentValue(file, 'json', PROVIDER_PATH), toJsonText(provider)),
    ];
    if (ctx.model) {
      changes.push(this.key('defaultProvider', PROVIDER_ID));
      changes.push(this.key('defaultModel', ctx.model));
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.settingsFile;
    const changes: ConfigChange[] = [];
    const provider = this.currentValue(file, 'json', PROVIDER_PATH);
    if (provider !== null) changes.push(new ConfigChange(file, 'json', PROVIDER_PATH, provider, null));
    // The start-up selection is only ours while it still names the Astra provider.
    const defaultProvider = this.currentValue(file, 'json', 'defaultProvider');
    if (defaultProvider !== null && ClientAdapterBase.stringOf(defaultProvider) === PROVIDER_ID) {
      changes.push(new ConfigChange(file, 'json', 'defaultProvider', defaultProvider, null));
      const defaultModel = this.currentValue(file, 'json', 'defaultModel');
      if (defaultModel !== null && this.store.get(this.kind, file, 'defaultModel') !== null)
        changes.push(new ConfigChange(file, 'json', 'defaultModel', defaultModel, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const entry = this.store.get(this.kind, this.settingsFile, PROVIDER_PATH);
    const enabled =
      entry !== null &&
      equalsValue('json', this.currentValue(this.settingsFile, 'json', PROVIDER_PATH), entry.appliedValueJson);
    return this.buildStatus(
      detection,
      enabled,
      enabled ? ['MothX reads its settings at start-up: restart open mothx sessions to use this.'] : [],
    );
  }

  private key(key: string, value: string): ConfigChange {
    return new ConfigChange(
      this.settingsFile,
      'json',
      key,
      this.currentValue(this.settingsFile, 'json', key),
      ClientAdapterBase.jsonString(value),
    );
  }
}
