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
import { JsoncEditor, toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { listState } from './shell-rc.js';

const SOURCE_ID = 'astra';
const SOURCES_KEY = 'provider_sources';
const MODELS_KEY = 'provider';
const RUNNER_PICK = 'agent_runner.config.model.provider_id';
const LEGACY_PICK = 'provider_settings.default_provider_id';

const SOURCE_PATH = `${SOURCES_KEY}.` + JsoncEditor.selector(['id', SOURCE_ID]);
const modelPath = (model: string): string => `${MODELS_KEY}.` + JsoncEditor.selector(['id', `${SOURCE_ID}/${model}`]);
const addressable = (id: string): boolean => !/[[\]=&]/.test(id);

/**
 * AstrBot (AstrBotDevs/AstrBot; Coexist): `<root>/data/cmd_config.json`. Owns the `provider_sources` element `id = astra`, one
 * `provider` element `astra/<model>` per bound model and the default chat model (`agent_runner.config.model.provider_id`,
 * else the older `provider_settings.default_provider_id`). (Port of AstrBotClientAdapter.)
 */
export class AstrBotClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.AstrBot;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get root(): string {
    const root = this.env.getEnvironmentVariable('ASTRBOT_ROOT')?.trim();
    if (root) return root;
    const desktop = this.env.combine('.astrbot');
    if (this.env.fileExists(path.join(desktop, 'data', 'cmd_config.json'))) return desktop;
    if (this.env.fileExists(desktop)) return this.env.homeDirectory;
    for (const name of ['astrbot', 'AstrBot']) {
      const dir = this.env.combine(name);
      if (this.env.fileExists(path.join(dir, 'data', 'cmd_config.json')) || this.env.fileExists(path.join(dir, '.astrbot')))
        return dir;
    }
    return desktop;
  }

  private get dataDir(): string {
    return path.join(this.root, 'data');
  }

  private get configFile(): string {
    return path.join(this.dataDir, 'cmd_config.json');
  }

  private get pickKey(): string {
    return this.currentValue(this.configFile, 'json', 'agent_runner') === null ? LEGACY_PICK : RUNNER_PICK;
  }

  detect(): ClientDetection {
    if (this.env.fileExists(this.configFile)) return ClientDetections.found(null, `settings ${this.configFile}`);
    if (this.env.directoryExists(this.dataDir)) return ClientDetections.found(null, `data folder ${this.dataDir}`);
    const exe = this.env.findOnPath('astrbot');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${this.dataDir} and no astrbot executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const source = {
      id: SOURCE_ID,
      provider: 'openai',
      type: 'openai_chat_completion',
      provider_type: 'chat_completion',
      enable: true,
      key: [ctx.localKey],
      api_base: ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1',
      timeout: 120,
      proxy: '',
      custom_headers: {},
    };
    const changes: ConfigChange[] = [
      new ConfigChange(file, 'json', SOURCE_PATH, this.currentValue(file, 'json', SOURCE_PATH), toJsonText(source)),
    ];
    const wanted = new Set<string>();
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) {
      if (!addressable(id)) continue;
      wanted.add(modelPath(id));
      const info = hints.get(id);
      // AstrBot drops a model's tools unless tool_use is in its modalities, and the gateway hands a tool call on whatever the model.
      const modalities = ClientAdapterBase.boolHint(info?.vision) === true ? ['text', 'image', 'tool_use'] : ['text', 'tool_use'];
      const model = {
        id: `${SOURCE_ID}/${id}`,
        enable: true,
        provider_source_id: SOURCE_ID,
        model: id,
        modalities,
        custom_extra_body: {},
        max_context_tokens: ClientAdapterBase.positiveLong(info?.contextWindow) ?? 0,
      };
      const p = modelPath(id);
      changes.push(new ConfigChange(file, 'json', p, this.currentValue(file, 'json', p), toJsonText(model)));
    }
    // A model the provider no longer offers goes out again (a list kept current).
    const modelPrefix = `${MODELS_KEY}.[id=${SOURCE_ID}/`;
    for (const entry of listState(this.store, this.kind)) {
      if (entry.filePath !== file || !entry.keyPath.startsWith(modelPrefix) || wanted.has(entry.keyPath)) continue;
      const stale = this.currentValue(file, 'json', entry.keyPath);
      if (stale !== null) changes.push(new ConfigChange(file, 'json', entry.keyPath, stale, null));
    }
    if (ctx.model && addressable(ctx.model)) {
      const pick = this.pickKey;
      changes.push(
        new ConfigChange(file, 'json', pick, this.currentValue(file, 'json', pick), ClientAdapterBase.jsonString(`${SOURCE_ID}/${ctx.model}`)),
      );
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
    const enabled = this.stillOurs(this.configFile, 'json', SOURCE_PATH);
    const warnings: string[] = [];
    if (enabled) {
      warnings.push(
        'AstrBot reads cmd_config.json at start-up and writes it back whole when its WebUI saves settings: restart AstrBot to use this, before saving anything in its WebUI.',
      );
      const runner = ClientAdapterBase.stringOf(this.currentValue(this.configFile, 'json', 'agent_runner.runner_type'));
      if (runner !== null && runner !== 'local')
        warnings.push(`AstrBot's agent runner is ${runner}, which does not ask its chat model, so it does not ask Astra.`);
    }
    return this.buildStatus(detection, enabled, warnings);
  }
}
