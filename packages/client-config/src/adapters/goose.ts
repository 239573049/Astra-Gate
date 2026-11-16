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
import { JNum, JObj, compactJson, type JV } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { listState } from './shell-rc.js';

const PROVIDER_ID = 'astra';
/** The OpenAI reasoning families, the only ones Goose sends a thinking level to. */
const THINKS = /(?:^|[-/])(?:o\d+(?:$|-)|gpt-(?:5|6)(?:$|[-.]))/i;

/**
 * Goose (Block's agent; Coexist): Astra becomes a custom provider of Goose's, `custom_providers/astra.json` beside
 * `config.yaml`, and the chosen model is written into `config.yaml` in the layout Goose keeps it in (new: `active_provider`
 * + `providers.astra.*`; old: `GOOSE_PROVIDER` / `GOOSE_MODEL`). (Port of GooseClientAdapter.)
 */
export class GooseClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.Goose;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get configDir(): string {
    if (this.env.os === 'windows') {
      const app = this.env.getEnvironmentVariable('APPDATA');
      if (app) return path.join(app, 'Block', 'goose', 'config');
    }
    const xdg = this.env.getEnvironmentVariable('XDG_CONFIG_HOME');
    return path.join(xdg ? xdg : this.env.combine('.config'), 'goose');
  }

  private get configFile(): string {
    return path.join(this.configDir, 'config.yaml');
  }

  private get providerFile(): string {
    return path.join(this.configDir, 'custom_providers', `${PROVIDER_ID}.json`);
  }

  detect(): ClientDetection {
    const dir = this.configDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('goose');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no goose executable on PATH`);
  }

  configPaths(): string[] {
    return [this.providerFile, this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.providerFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: JV[] = [];
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) {
      const model: Array<[string, JV]> = [
        ['name', id],
        ['reasoning', THINKS.test(id)],
      ];
      const context = ClientAdapterBase.positiveLong(hints.get(id)?.contextWindow);
      if (context !== null) model.push(['context_limit', new JNum(String(context))]);
      models.push(new JObj(model));
    }
    const provider = new Map<string, JV>([
      ['name', PROVIDER_ID],
      ['engine', 'openai'],
      ['display_name', 'Astra'],
      ['description', 'Every model Astra has, through its local gateway'],
      ['api_key_env', ''],
      ['base_url', ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1'],
      ['headers', new JObj([['Authorization', 'Bearer ' + ctx.localKey]])],
      ['requires_auth', false],
      ['skip_canonical_filtering', true],
      ['models', models],
    ]);
    // Goose refuses a provider whose static list is empty.
    if (models.length > 0) provider.set('dynamic_models', false);

    const changes: ConfigChange[] = [];
    for (const key of PROVIDER_KEYS) {
      const value = provider.get(key);
      if (value !== undefined)
        changes.push(new ConfigChange(file, 'json', key, this.currentValue(file, 'json', key), compactJson(value)));
    }
    if (ctx.model) changes.push(...this.modelChanges(ctx.model));
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    // Every key Astra recorded: removed where it is still there (drift is the applier's call), a key Astra deleted put back.
    for (const file of [this.configFile, this.providerFile]) {
      const format = file === this.configFile ? 'yaml' : 'json';
      for (const entry of listState(this.store, this.kind)) {
        if (entry.filePath !== file) continue;
        const current = this.currentValue(file, format, entry.keyPath);
        if (current !== null) changes.push(new ConfigChange(file, format, entry.keyPath, current, null));
        else if (!entry.originalAbsent)
          changes.push(new ConfigChange(file, format, entry.keyPath, null, entry.originalValueJson));
      }
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled =
      this.store.get(this.kind, this.providerFile, 'base_url') !== null &&
      this.stillOurs(this.providerFile, 'json', 'base_url');
    return this.buildStatus(
      detection,
      enabled,
      enabled ? ['Goose loads custom providers when it starts: restart Goose (CLI sessions and the desktop app) to use this.'] : [],
    );
  }

  /** The model selection, in the layout Goose's config is in. */
  private modelChanges(model: string): ConfigChange[] {
    const file = this.configFile;
    const set = (keyPath: string, json: string) =>
      new ConfigChange(file, 'yaml', keyPath, this.currentValue(file, 'yaml', keyPath), json);
    const newLayout =
      this.currentValue(file, 'yaml', 'providers') !== null || this.currentValue(file, 'yaml', 'active_provider') !== null;
    if (!newLayout)
      return [
        set('GOOSE_PROVIDER', ClientAdapterBase.jsonString(PROVIDER_ID)),
        set('GOOSE_MODEL', ClientAdapterBase.jsonString(model)),
      ];
    const changes = [
      set('active_provider', ClientAdapterBase.jsonString(PROVIDER_ID)),
      set(`providers.${PROVIDER_ID}.enabled`, 'true'),
      set(`providers.${PROVIDER_ID}.model`, ClientAdapterBase.jsonString(model)),
      set(`providers.${PROVIDER_ID}.configured`, 'true'),
    ];
    // Flat keys beside the map are read by no Goose that reads the map, and deleted by the next one that writes.
    for (const flat of ['GOOSE_PROVIDER', 'GOOSE_MODEL']) {
      const before = this.currentValue(file, 'yaml', flat);
      if (before !== null) changes.push(new ConfigChange(file, 'yaml', flat, before, null));
    }
    return changes;
  }
}

const PROVIDER_KEYS = [
  'name',
  'engine',
  'display_name',
  'description',
  'api_key_env',
  'base_url',
  'headers',
  'requires_auth',
  'skip_canonical_filtering',
  'models',
  'dynamic_models',
];
