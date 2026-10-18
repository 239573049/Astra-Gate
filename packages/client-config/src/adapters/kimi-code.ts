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

const PROVIDER_ID = 'astra';
const ALIAS_PREFIX = 'astra-';
const DEFAULT_CONTEXT_SIZE = 131_072;
const DEFAULT_MODEL_PATH = 'default_model';
const PROVIDER_TABLE = `providers.${PROVIDER_ID}`;
const MODEL_PREFIX = `models.${ALIAS_PREFIX}`;

const raw = (value: string): string => tomlString(value);
const modelTable = (alias: string): string => `models.${alias}`;
const isModelTable = (keyPath: string): boolean =>
  keyPath.startsWith(MODEL_PREFIX) && keyPath.indexOf('.', 7) < 0;

/**
 * Kimi Code (Moonshot, bin `kimi`; Coexist): adds a `[providers.astra]` table and one `[models.astra-<id>]` table per
 * bound model to `~/.kimi-code/config.toml` (`KIMI_CODE_HOME` relocates it) and, when a model is chosen, sets the
 * top-level `default_model`. Tables Astra created are owned whole; fields inside a pre-existing table are refreshed
 * one by one. A model whose native protocol is Responses or Anthropic gets its own `protocol`. (Port of
 * KimiCodeClientAdapter.)
 */
export class KimiCodeClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.KimiCode;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get homeDir(): string {
    const dir = this.env.getEnvironmentVariable('KIMI_CODE_HOME');
    return dir ? dir : this.env.combine('.kimi-code');
  }

  private get configFile(): string {
    return path.join(this.homeDir, 'config.toml');
  }

  detect(): ClientDetection {
    const dir = this.homeDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('kimi');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no kimi executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  protected override isTableKeyPath(keyPath: string): boolean {
    return keyPath === PROVIDER_TABLE || isModelTable(keyPath);
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    changes.push(
      ...this.tableChanges(file, PROVIDER_TABLE, [
        ['type', raw('openai')],
        ['base_url', raw(ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1')],
        ['api_key', raw(ctx.localKey)],
      ]),
    );

    const hints = ClientAdapterBase.modelHints(ctx);
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    const entries = ClientAdapterBase.modelEntries(ctx);
    const aliases = aliasesOf(entries.map((e) => e.id));
    for (const [id, alias] of aliases) {
      const info = hints.get(id);
      const capabilities = ['"tool_use"'];
      if (ClientAdapterBase.boolHint(info?.reasoning) === true) capabilities.push('"thinking"');
      if (ClientAdapterBase.boolHint(info?.vision) === true) capabilities.push('"image_in"');
      const name = entries.find((e) => e.id === id)!.name;
      const protocol = protocols.get(id);
      const maxOutput = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      changes.push(
        ...this.tableChanges(file, modelTable(alias), [
          ['provider', raw(PROVIDER_ID)],
          ['model', raw(id)],
          ['max_context_size', String(ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT_SIZE)],
          ['capabilities', '[' + capabilities.join(', ') + ']'],
          [
            'protocol',
            protocol === 'openai-responses' ? raw('openai_responses') : protocol === 'anthropic' ? raw('anthropic') : null,
          ],
          ['max_output_size', maxOutput === null ? null : String(maxOutput)],
          ['display_name', name === null ? null : raw(name)],
        ]),
      );
    }

    // Models Astra registered earlier that the bound provider no longer offers are taken out again.
    const aliasValues = new Set(aliases.values());
    for (const p of this.trackedModelTables(file)) {
      if (aliasValues.has(p.slice(7))) continue;
      const current = this.currentValue(file, 'toml', p, 'table');
      if (current !== null) changes.push(new ConfigChange(file, 'toml', p, current, null, { kind: 'table' }));
    }

    const selected = ctx.model ? aliases.get(ctx.model) : undefined;
    if (selected !== undefined)
      changes.push(
        new ConfigChange(
          file,
          'toml',
          DEFAULT_MODEL_PATH,
          this.currentValue(file, 'toml', DEFAULT_MODEL_PATH),
          ClientAdapterBase.jsonString(raw(selected)),
        ),
      );
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    // A model the user chose afterwards is theirs and stays.
    if (this.stillOurs(file, 'toml', DEFAULT_MODEL_PATH))
      changes.push(
        new ConfigChange(file, 'toml', DEFAULT_MODEL_PATH, this.currentValue(file, 'toml', DEFAULT_MODEL_PATH), null),
      );

    // Fields refreshed inside a table that pre-existed were tracked one by one; put them back first.
    for (const entry of this.listState()) {
      if (entry.filePath !== file || this.isTableKeyPath(entry.keyPath) || entry.keyPath === DEFAULT_MODEL_PATH) continue;
      const current = this.currentValue(file, 'toml', entry.keyPath);
      if (current !== null) changes.push(new ConfigChange(file, 'toml', entry.keyPath, current, null));
    }
    // Tables Astra created go away whole.
    for (const entry of this.listState()) {
      if (entry.filePath !== file || !this.isTableKeyPath(entry.keyPath) || !entry.originalAbsent) continue;
      const current = this.currentValue(file, 'toml', entry.keyPath, 'table');
      if (current !== null)
        changes.push(new ConfigChange(file, 'toml', entry.keyPath, current, null, { kind: 'table' }));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    // Purge also removes a provider table that pre-existed under Astra's own name.
    const file = this.configFile;
    const changes: ConfigChange[] = [...this.planDisable().changes];
    for (const p of [PROVIDER_TABLE, ...this.trackedModelTables(file)]) {
      if (changes.some((c) => c.keyPath === p)) continue;
      const current = this.currentValue(file, 'toml', p, 'table');
      if (current !== null) changes.push(new ConfigChange(file, 'toml', p, current, null, { kind: 'table' }));
    }
    return this.buildPlan(changes);
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const warnings: string[] = [];
    const legacy = this.env.combine('.kimi');
    if (!this.env.directoryExists(this.homeDir) && this.env.directoryExists(legacy))
      warnings.push(
        `${legacy} belongs to the archived Python Kimi CLI; Kimi Code reads ${this.configFile} instead (the old directory is not modified).`,
      );
    const enabled =
      this.store.get(this.kind, this.configFile, PROVIDER_TABLE) !== null &&
      this.currentValue(this.configFile, 'toml', PROVIDER_TABLE, 'table') !== null;
    return this.buildStatus(detection, enabled, warnings);
  }

  /**
   * Creates the table with all fields, or, when it already exists, refreshes only the differing fields. A null
   * field is optional and not wanted: left out of a new table, and taken out of an existing one only when Astra put
   * it there.
   */
  private tableChanges(file: string, tablePath: string, fields: Array<[string, string | null]>): ConfigChange[] {
    if (this.currentValue(file, 'toml', tablePath, 'table') === null) {
      const items: Record<string, string> = {};
      for (const [key, value] of fields) if (value !== null) items[key] = value;
      return [new ConfigChange(file, 'toml', tablePath, null, toJsonText(items), { kind: 'table' })];
    }
    const result: ConfigChange[] = [];
    const tableIsOurs = this.store.get(this.kind, file, tablePath)?.originalAbsent === true;
    for (const [key, value] of fields) {
      const p = `${tablePath}.${key}`;
      const before = this.currentValue(file, 'toml', p);
      if (value === null) {
        if (before !== null && (tableIsOurs || this.store.get(this.kind, file, p) !== null))
          result.push(new ConfigChange(file, 'toml', p, before, null));
      } else if (before !== value) result.push(new ConfigChange(file, 'toml', p, before, ClientAdapterBase.jsonString(value)));
    }
    return result;
  }

  /** The recorded keys ordered by file, then key (ordinal), as the C# store lists them. */
  private listState() {
    const cmp = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
    return this.store
      .list(this.kind)
      .sort((a, b) => cmp(a.filePath, b.filePath) || cmp(a.keyPath, b.keyPath));
  }

  private trackedModelTables(file: string): string[] {
    return this
      .listState()
      .filter((e) => e.filePath === file && isModelTable(e.keyPath))
      .map((e) => e.keyPath);
  }
}

/** The alias of every model id: `astra-` plus the id with everything but letters, digits, '_' and '-' turned into '-'. */
function aliasesOf(ids: string[]): Map<string, string> {
  const aliases = new Map<string, string>();
  const taken = new Set<string>();
  for (const id of ids) {
    let base = ALIAS_PREFIX;
    for (const c of id) base += /^[A-Za-z0-9_-]$/.test(c) ? c : '-';
    let alias = base;
    for (let n = 2; taken.has(alias); n++) alias = `${base}-${n}`;
    taken.add(alias);
    aliases.set(id, alias);
  }
  return aliases;
}
