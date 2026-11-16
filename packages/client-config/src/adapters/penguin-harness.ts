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
import { tomlString } from '../editing/toml-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { listState } from './shell-rc.js';

const PROVIDER_ID = 'astra';
const MODELS_SELECTOR = `models[provider=${PROVIDER_ID}]`;
const PROVIDER_TABLE = `providers.${PROVIDER_ID}`;
const raw = (value: string): string => tomlString(value);

/**
 * PenguinHarness (npm @prismshadow/penguin-cli; Coexist): each Project's models live in
 * `<root>/<project>/.project_config.toml` (root: PENGUIN_HOME else `~/.penguin/data`; project: PENGUIN_PROJECT_ID else
 * `default_project`). Adds `[providers.astra]` (openai-responses), one `[[models]]` entry per bound model (the array rows whose
 * `provider` is "astra") and `default_model`. (Port of PenguinHarnessClientAdapter.)
 */
export class PenguinHarnessClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.PenguinHarness;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get root(): string {
    const home = this.env.getEnvironmentVariable('PENGUIN_HOME');
    return home ? home : this.env.combine('.penguin', 'data');
  }

  private get project(): string {
    const id = this.env.getEnvironmentVariable('PENGUIN_PROJECT_ID')?.trim();
    return id && id !== '.' && id !== '..' && !/[/\\]/.test(id) ? id : 'default_project';
  }

  private get configFile(): string {
    return path.join(this.root, this.project, '.project_config.toml');
  }

  protected isTableKeyPath(keyPath: string): boolean {
    return keyPath === PROVIDER_TABLE || keyPath === MODELS_SELECTOR;
  }

  detect(): ClientDetection {
    const dir = this.root;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `data directory ${dir}`);
    const exe = this.env.findOnPath('penguin');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no penguin executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [...this.providerChanges(file, ctx)];

    const hints = ClientAdapterBase.modelHints(ctx);
    const entries: Array<Record<string, string>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const entry: Record<string, string> = { provider: raw(PROVIDER_ID), model_id: raw(id) };
      if (name !== null) entry.display_name = raw(name);
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) entry.context_window = String(context);
      const vision = ClientAdapterBase.boolHint(info?.vision);
      if (vision !== null) entry.vision = vision ? 'true' : 'false';
      const output = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (output !== null) entry.max_tokens = String(output);
      entries.push(entry);
    }
    changes.push(
      new ConfigChange(
        file,
        'toml',
        MODELS_SELECTOR,
        this.currentValue(file, 'toml', MODELS_SELECTOR, 'table'),
        toJsonText(entries),
        { kind: 'table' },
      ),
    );

    if (ctx.model) changes.push(...this.defaultModelChanges(file, ctx.model));
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    for (const entry of listState(this.store, this.kind)) {
      if (entry.filePath !== file) continue;
      const isTable = this.isTableKeyPath(entry.keyPath);
      // A pre-existing provider table only had fields refreshed (tracked one by one); only a table Astra created goes whole.
      if (isTable && !entry.originalAbsent) continue;
      const kind = isTable ? 'table' : 'key';
      const current = this.currentValue(file, 'toml', entry.keyPath, kind);
      if (current !== null) changes.push(new ConfigChange(file, 'toml', entry.keyPath, current, null, { kind }));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    // Purge also removes a provider table and entries that pre-existed under Astra's own name.
    const file = this.configFile;
    const changes: ConfigChange[] = [...this.planDisable().changes];
    for (const p of [MODELS_SELECTOR, PROVIDER_TABLE]) {
      if (changes.some((c) => c.keyPath === p)) continue;
      const current = this.currentValue(file, 'toml', p, 'table');
      if (current !== null) changes.push(new ConfigChange(file, 'toml', p, current, null, { kind: 'table' }));
    }
    return this.buildPlan(changes);
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled =
      this.store.get(this.kind, this.configFile, MODELS_SELECTOR) !== null &&
      this.currentValue(this.configFile, 'toml', MODELS_SELECTOR, 'table') !== null;
    return this.buildStatus(detection, enabled, []);
  }

  /** Creates `[providers.astra]`, or, when it already exists, refreshes only the differing fields. */
  private providerChanges(file: string, ctx: EnableContext): ConfigChange[] {
    const fields: Array<[string, string]> = [
      ['base_url', raw(ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1')],
      ['client_type', raw('openai-responses')],
      ['api_key', raw(ctx.localKey)],
    ];
    if (this.currentValue(file, 'toml', PROVIDER_TABLE, 'table') === null) {
      const items: Record<string, string> = {};
      for (const [key, value] of fields) items[key] = value;
      return [new ConfigChange(file, 'toml', PROVIDER_TABLE, null, toJsonText(items), { kind: 'table' })];
    }
    const result: ConfigChange[] = [];
    for (const [key, value] of fields) {
      const p = `${PROVIDER_TABLE}.${key}`;
      const before = this.currentValue(file, 'toml', p);
      if (before !== value) result.push(new ConfigChange(file, 'toml', p, before, ClientAdapterBase.jsonString(value)));
    }
    return result;
  }

  /** `default_model` in the form the file has it: a key (inline table), or the keys of a `[default_model]` table; a key when absent. */
  private defaultModelChanges(file: string, model: string): ConfigChange[] {
    const set = (p: string, value: string) =>
      new ConfigChange(file, 'toml', p, this.currentValue(file, 'toml', p), ClientAdapterBase.jsonString(value));
    if (
      this.currentValue(file, 'toml', 'default_model') === null &&
      this.currentValue(file, 'toml', 'default_model', 'table') !== null
    )
      return [set('default_model.provider', raw(PROVIDER_ID)), set('default_model.model_id', raw(model))];
    return [set('default_model', `{ provider = ${raw(PROVIDER_ID)}, model_id = ${raw(model)} }`)];
  }
}
