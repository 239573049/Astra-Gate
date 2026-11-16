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
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { listState } from './shell-rc.js';

const PROVIDER_ID = 'astra';
const PROVIDER_PATH = `providers.${PROVIDER_ID}`;

const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

/**
 * LiveAgent (Stack-Cairn/LiveAgent; Coexist): K-brain's JSONC `config.json` in `LIVEAGENT_HOME`, else `K_BRAIN_HOME` (absolute
 * paths), else the folder `~/.liveagent/data-dir.json` points to, else `~/.liveagent`. Adds `providers.astra` (`type: codex`,
 * `openai-completions`, the gateway's `/v1`, `activeModels: null`, models sorted by id) and, when a model is chosen,
 * `defaultProvider` / `defaultModel`. (Port of LiveAgentClientAdapter.)
 */
export class LiveAgentClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.LiveAgent;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get defaultDir(): string {
    return this.env.combine('.liveagent');
  }

  private get dir(): string {
    for (const name of ['LIVEAGENT_HOME', 'K_BRAIN_HOME']) {
      const dir = this.env.getEnvironmentVariable(name)?.trim();
      if (dir && path.isAbsolute(dir)) return dir;
    }
    const def = this.defaultDir;
    const text = this.env.readTextOrNull(path.join(def, 'data-dir.json'));
    if (text !== null) {
      try {
        const o = JSON.parse(text) as unknown;
        const moved = o && typeof o === 'object' ? (o as Record<string, unknown>).path : undefined;
        if (
          typeof moved === 'string' &&
          path.isAbsolute(moved) &&
          moved.replace(/[\\/]+$/, '') !== def.replace(/[\\/]+$/, '')
        )
          return moved;
      } catch {
        // not JSON: the default folder
      }
    }
    return def;
  }

  private get configFile(): string {
    return path.join(this.dir, 'config.json');
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `data folder ${dir}`);
    return this.env.directoryExists(this.defaultDir)
      ? ClientDetections.found(null, `data folder ${this.defaultDir}`)
      : ClientDetections.notFound(`no ${dir}`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const entries = ClientAdapterBase.modelEntries(ctx);
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of [...entries].sort((a, b) => ordinal(a.id, b.id))) {
      const info = hints.get(id);
      const model: Record<string, unknown> = { id };
      if (name !== null && name !== id) model.displayName = name;
      if (ClientAdapterBase.boolHint(info?.vision) === true) model.input = ['text', 'image'];
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) model.contextWindow = context;
      const maxTokens = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (maxTokens !== null) model.maxTokens = maxTokens;
      models.push(model);
    }
    const provider = {
      name: 'Astra',
      type: 'codex',
      baseUrl: ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1',
      api: 'openai-completions',
      apiKey: ctx.localKey,
      activeModels: null,
      modelOrder: entries.map((e) => e.id),
      requestFormat: 'openai-completions',
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
    const changes: ConfigChange[] = [];
    for (const entry of listState(this.store, this.kind)) {
      const current = this.currentValue(entry.filePath, 'json', entry.keyPath);
      if (current !== null) changes.push(new ConfigChange(entry.filePath, 'json', entry.keyPath, current, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.stillOurs(this.configFile, 'json', PROVIDER_PATH);
    return this.buildStatus(
      detection,
      enabled,
      enabled
        ? [
            'LiveAgent keeps its settings in memory and writes them all back when one changes: quit LiveAgent before changing this, then open it and pick an Astra model in its model picker.',
          ]
        : [],
    );
  }

  private key(key: string, value: string): ConfigChange {
    return new ConfigChange(
      this.configFile,
      'json',
      key,
      this.currentValue(this.configFile, 'json', key),
      ClientAdapterBase.jsonString(value),
    );
  }
}
