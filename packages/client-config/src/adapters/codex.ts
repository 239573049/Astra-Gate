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
import { decodeToSource, equalsValue } from '../config-value-codec.js';
import {
  ClientOptionKeys,
  clientOptionConfigKeyPaths,
  getBoolOption,
  getChoiceOption,
  getIntOption,
} from '../domain/client-options.js';
import { ClientKinds } from '../domain/kinds.js';
import { JNum, JObj, compactJson, parseJsonc, type JV } from '../editing/jsonc-editor.js';
import { tomlString } from '../editing/toml-editor.js';

const PROVIDER_ID = 'astra';
/** The model catalog file Astra owns in the Codex home. */
export const CODEX_CATALOG_FILE_NAME = 'astra-models.json';
const CATALOG_KEY = 'models';
const CATALOG_PATH_KEY = 'model_catalog_json';

/** Fields of a cloned entry that describe the OpenAI model it was cloned from, not the Astra model. */
const TEMPLATE_ONLY_FIELDS = [
  'context_window',
  'max_context_window',
  'auto_compact_token_limit',
  'model_specialty',
  'available_access_programs',
  'default_service_tier',
  'comp_hash',
  'auto_review_model_override',
  'tool_mode',
  'multi_agent_version',
  'multi_agent_reasoning_effort',
];

/** OpenAI-backend features a cloned entry must not claim for a model served through the gateway. */
const BACKEND_FEATURE_FLAGS = [
  'support_verbosity',
  'supports_search_tool',
  'use_responses_lite',
  'supports_experimental_context',
  'supports_reasoning_effort_updates',
  'supports_image_detail_original',
];

// ---------------------------------------------------------------- JSON node helpers (JsonNode semantics)

const get = (o: JObj, key: string): JV | undefined => o.entries.find(([k]) => k === key)?.[1];
const set = (o: JObj, key: string, value: JV): void => {
  const e = o.entries.find(([k]) => k === key);
  if (e) e[1] = value;
  else o.entries.push([key, value]);
};
const remove = (o: JObj, key: string): void => {
  const i = o.entries.findIndex(([k]) => k === key);
  if (i >= 0) o.entries.splice(i, 1);
};
const str = (v: JV | undefined): string | null => (typeof v === 'string' ? v : null);
const num = (n: number): JNum => new JNum(String(n));
const clone = (v: JV): JV =>
  Array.isArray(v)
    ? v.map(clone)
    : v instanceof JObj
      ? new JObj(v.entries.map(([k, x]): [string, JV] => [k, clone(x)]))
      : v;
const instructionsOf = (entry: JObj): string | null => {
  const messages = get(entry, 'model_messages');
  return (messages instanceof JObj ? str(get(messages, 'instructions_template')) : null) ?? str(get(entry, 'base_instructions'));
};

/** The string a raw TOML value source holds (basic or literal string), trimmed; null otherwise. */
function tomlStringOf(raw: string | null): string | null {
  if (raw === null) return null;
  const src = raw.trim();
  let value: string | null = null;
  if (src.length >= 2 && src.startsWith("'") && src.endsWith("'") && !src.startsWith("'''")) {
    value = src.slice(1, -1);
  } else if (src.length >= 2 && src.startsWith('"') && src.endsWith('"') && !src.startsWith('"""')) {
    try {
      value = JSON.parse(src.replace(/\\U([0-9a-fA-F]{8})/g, (_, h: string) => String.fromCodePoint(parseInt(h, 16)))) as string;
    } catch {
      value = null;
    }
  }
  if (value === null || value.trim() === '') return null;
  return value.trim();
}

/**
 * Codex CLI (Switch): adds a `[model_providers.astra]` table and flips the top-level `model_provider` (plus optional
 * `model`). Disabling restores the file byte-identically: a provider table Astra created is removed again, and fields
 * refreshed inside a user's own table are reset to their originals. planPurge additionally removes the table even
 * when it pre-existed. `auth.json` is never touched. With the "modelCatalog" option it also writes
 * `astra-models.json` beside `config.toml` and points `model_catalog_json` at it (see CodexClientAdapter.cs).
 * (Port of CodexClientAdapter.)
 */
export class CodexClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.Codex;
  readonly mode = 'switch' as const;
  readonly availability = 'available' as const;

  private get configDir(): string {
    const dir = this.env.getEnvironmentVariable('CODEX_HOME');
    return dir ? dir : this.env.combine('.codex');
  }

  private get configFile(): string {
    return path.join(this.configDir, 'config.toml');
  }

  private get catalogFile(): string {
    return path.join(this.configDir, CODEX_CATALOG_FILE_NAME);
  }

  detect(): ClientDetection {
    const dir = this.configDir;
    const exe = this.env.findOnPath('codex');
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    if (exe !== null) return ClientDetections.found(null, `executable ${exe}`);
    return ClientDetections.notFound(`no ${dir} and no codex executable on PATH`);
  }

  configPaths(): string[] {
    return this.store.get(this.kind, this.catalogFile, CATALOG_KEY) === null
      ? [this.configFile]
      : [this.configFile, this.catalogFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    // Stored "after" values are JSON texts of RAW TOML source (a TOML string includes its quotes).
    const changes: ConfigChange[] = [this.topKey(file, 'model_provider', ClientAdapterBase.jsonString(tomlString(PROVIDER_ID)))];
    if (ctx.model && !this.keepsCodexModel(file, ctx.model))
      changes.push(this.topKey(file, 'model', ClientAdapterBase.jsonString(tomlString(ctx.model))));
    const window = getIntOption(ctx.extras, ClientOptionKeys.contextWindow);
    if (window !== null)
      changes.push(this.topKey(file, 'model_context_window', ClientAdapterBase.jsonString(String(window))));
    const compact = getIntOption(ctx.extras, ClientOptionKeys.autoCompactTokenLimit);
    if (compact !== null)
      changes.push(this.topKey(file, 'model_auto_compact_token_limit', ClientAdapterBase.jsonString(String(compact))));
    const effort = getChoiceOption(ctx.extras, ClientOptionKeys.reasoningEffort);
    if (effort !== null)
      changes.push(this.topKey(file, 'model_reasoning_effort', ClientAdapterBase.jsonString(tomlString(effort))));
    changes.push(...this.providerTableChanges(file, ctx));
    if (getBoolOption(ctx.extras, ClientOptionKeys.modelCatalog)) {
      const catalog = this.catalogModels(ctx);
      if (catalog !== null) {
        changes.push(
          new ConfigChange(this.catalogFile, 'json', CATALOG_KEY, this.currentValue(this.catalogFile, 'json', CATALOG_KEY), compactJson(catalog)),
        );
        changes.push(this.topKey(file, CATALOG_PATH_KEY, ClientAdapterBase.jsonString(tomlString(this.catalogFile))));
      }
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    const keys = [
      'model_provider',
      'model',
      ...clientOptionConfigKeyPaths(this.kind).filter((k) => k !== CATALOG_KEY),
    ];
    for (const key of keys) {
      const before = this.currentValue(file, 'toml', key);
      if (before !== null) changes.push(new ConfigChange(file, 'toml', key, before, null));
    }
    // The catalog file is Astra's own: restoring removes its key, and with it the file Astra created.
    if (this.store.get(this.kind, this.catalogFile, CATALOG_KEY) !== null)
      changes.push(
        new ConfigChange(this.catalogFile, 'json', CATALOG_KEY, this.currentValue(this.catalogFile, 'json', CATALOG_KEY), null),
      );
    // Fields refreshed inside a pre-existing provider table were tracked individually; restore them.
    changes.push(...this.trackedFieldRestores(file, ['base_url', 'experimental_bearer_token']));
    // When the provider table itself did not exist before the takeover, remove it again.
    const tablePath = `model_providers.${PROVIDER_ID}`;
    if (this.store.get(this.kind, file, tablePath)?.originalAbsent === true)
      changes.push(
        new ConfigChange(file, 'toml', tablePath, this.currentValue(file, 'toml', tablePath, 'table'), null, {
          kind: 'table',
        }),
      );
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [...this.planDisable().changes];
    const tablePath = `model_providers.${PROVIDER_ID}`;
    changes.push(
      new ConfigChange(file, 'toml', tablePath, this.currentValue(file, 'toml', tablePath, 'table'), null, {
        kind: 'table',
      }),
    );
    return this.buildPlan(changes);
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const current = this.currentValue(this.configFile, 'toml', 'model_provider');
    const entry = this.store.get(this.kind, this.configFile, 'model_provider');
    const enabled = entry !== null && current === decodeToSource('toml', entry.appliedValueJson);
    return this.buildStatus(detection, enabled, []);
  }

  protected override isTableKeyPath(keyPath: string): boolean {
    return keyPath === `model_providers.${PROVIDER_ID}`;
  }

  /** Codex writes `model` back to config.toml whenever the user picks a model in it: that is not drift. */
  protected override isClientManagedKey(file: string, keyPath: string): boolean {
    return file === this.configFile && keyPath === 'model';
  }

  /**
   * True when the user picked another model in Codex since Astra last wrote `model`, and Astra's own selection is
   * still the one it wrote then: a re-apply keeps the user's pick. A model newly selected in Astra is written as usual.
   */
  private keepsCodexModel(file: string, model: string): boolean {
    const applied = this.store.get(this.kind, file, 'model')?.appliedValueJson;
    if (applied === null || applied === undefined) return false;
    if (!equalsValue('toml', tomlString(model), applied)) return false;
    const current = this.currentValue(file, 'toml', 'model');
    return current !== null && !equalsValue('toml', current, applied);
  }

  /** The provider table: created wholesale on first enable, otherwise only URL/key fields refreshed. */
  private providerTableChanges(file: string, ctx: EnableContext): ConfigChange[] {
    const tablePath = `model_providers.${PROVIDER_ID}`;
    const baseUrl = tomlString(ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1');
    const token = tomlString(ctx.localKey);
    if (this.currentValue(file, 'toml', tablePath, 'table') === null) {
      const items = new JObj([
        ['name', tomlString('Astra')],
        ['base_url', baseUrl],
        ['wire_api', tomlString('responses')],
        ['experimental_bearer_token', token],
        ['requires_openai_auth', 'false'],
      ]);
      return [new ConfigChange(file, 'toml', tablePath, null, compactJson(items), { kind: 'table' })];
    }
    const result: ConfigChange[] = [];
    for (const [key, value] of [
      ['base_url', baseUrl],
      ['experimental_bearer_token', token],
    ] as const) {
      const fieldPath = `${tablePath}.${key}`;
      const before = this.currentValue(file, 'toml', fieldPath);
      if (before !== value)
        result.push(new ConfigChange(file, 'toml', fieldPath, before, ClientAdapterBase.jsonString(value)));
    }
    return result;
  }

  /** Restore changes for tracked fields of an existing provider table (absent when we created it). */
  private trackedFieldRestores(file: string, fields: string[]): ConfigChange[] {
    const result: ConfigChange[] = [];
    for (const field of fields) {
      const fieldPath = `model_providers.${PROVIDER_ID}.${field}`;
      if (this.store.get(this.kind, file, fieldPath) === null) continue;
      const before = this.currentValue(file, 'toml', fieldPath);
      if (before !== null) result.push(new ConfigChange(file, 'toml', fieldPath, before, null));
    }
    return result;
  }

  // ---------------------------------------------------------------- model catalog

  /**
   * The catalog's model list: the models Codex already has, then the bound providers' models it lacks. Null when
   * there is no source of Codex entries with instructions to clone, or no model to add.
   */
  private catalogModels(ctx: EnableContext): JV[] | null {
    const cache = this.catalogEntries(path.join(this.configDir, 'models_cache.json'));
    const userPath = this.userCatalogPath();
    const existing = (userPath !== null ? this.catalogEntries(userPath) : null) ?? cache;
    if (existing === null) return null;
    const template = this.template(existing) ?? (cache === null ? null : this.template(cache));
    const models = ClientAdapterBase.modelEntries(ctx);
    if (template === null || models.length === 0) return null;
    const instructions = instructionsOf(template)!;

    const result: JV[] = [];
    const slugs = new Set<string>();
    let priority = 0;
    for (const entry of existing) {
      // An entry without instructions would make Codex reject the whole file.
      if (instructionsOf(entry) === null) set(entry, 'base_instructions', instructions);
      const p = get(entry, 'priority');
      if (p instanceof JNum && /^-?\d+$/.test(p.raw)) {
        const value = Number(p.raw);
        if (value >= -2147483648 && value <= 2147483647) priority = Math.max(priority, value);
      }
      slugs.add(str(get(entry, 'slug'))!);
      result.push(entry);
    }
    const hints = ClientAdapterBase.modelHints(ctx);
    for (const { id, name } of models) {
      if (slugs.has(id)) continue; // Codex already knows this model: keep its own entry
      slugs.add(id);
      const entry = clone(template) as JObj;
      for (const field of TEMPLATE_ONLY_FIELDS) remove(entry, field);
      for (const flag of BACKEND_FEATURE_FLAGS) set(entry, flag, false);
      set(entry, 'slug', id);
      set(entry, 'display_name', name ?? id);
      set(entry, 'description', 'Astra');
      set(entry, 'visibility', 'list');
      set(entry, 'supported_in_api', true);
      set(entry, 'priority', num(++priority));
      set(entry, 'upgrade', null);
      set(entry, 'availability_nux', null);
      set(entry, 'default_verbosity', null);
      set(entry, 'apply_patch_tool_type', null); // Codex's own choice for models it has no metadata for
      set(entry, 'experimental_supported_tools', []);
      set(entry, 'additional_speed_tiers', []);
      set(entry, 'service_tiers', []);
      remove(entry, 'input_modalities');
      const hint = hints.get(id);
      if (hint) {
        const context = ClientAdapterBase.positiveLong(hint.contextWindow);
        if (context !== null) set(entry, 'context_window', num(context));
        const vision = ClientAdapterBase.boolHint(hint.vision);
        if (vision !== null) set(entry, 'input_modalities', vision ? ['text', 'image'] : ['text']);
        if (ClientAdapterBase.boolHint(hint.reasoning) === false) {
          set(entry, 'supported_reasoning_levels', []);
          remove(entry, 'default_reasoning_level');
          set(entry, 'supports_reasoning_summary_parameter', false);
        }
      }
      result.push(entry);
    }
    return result;
  }

  /**
   * The catalog the user pointed `model_catalog_json` at: the current value, or — once it names Astra's file — the
   * value Astra replaced. Null when there is none.
   */
  private userCatalogPath(): string | null {
    const current = tomlStringOf(this.currentValue(this.configFile, 'toml', CATALOG_PATH_KEY));
    if (current === null) return null;
    const resolved = this.resolvePath(current);
    if (!this.samePath(resolved, this.catalogFile)) return resolved;
    const entry = this.store.get(this.kind, this.configFile, CATALOG_PATH_KEY);
    if (!entry || entry.originalAbsent || entry.originalValueJson === null) return null;
    const replaced = tomlStringOf(decodeToSource('toml', entry.originalValueJson));
    return replaced !== null && !this.samePath(this.resolvePath(replaced), this.catalogFile)
      ? this.resolvePath(replaced)
      : null;
  }

  /** The `models` entries (objects with a slug) of a catalog-shaped JSON file; null when unreadable. */
  private catalogEntries(file: string): JObj[] | null {
    const text = this.env.readTextOrNull(file);
    if (text === null) return null;
    let root: JV;
    try {
      root = clone(parseJsonc(text, { comments: false, trailingCommas: false }).value);
    } catch {
      return null;
    }
    if (!(root instanceof JObj)) return null;
    const models = get(root, 'models');
    if (!Array.isArray(models)) return null;
    return models.filter((m): m is JObj => m instanceof JObj && (str(get(m, 'slug')) ?? '') !== '');
  }

  /** The entry new models are cloned from: a listed one with instructions, else any with instructions. */
  private template(entries: JObj[]): JObj | null {
    const has = (m: JObj) => (instructionsOf(m) ?? '') !== '';
    return entries.find((m) => has(m) && str(get(m, 'visibility')) === 'list') ?? entries.find(has) ?? null;
  }

  /** A path as Codex resolves it: `~/` from home, relative paths from the config.toml directory. */
  private resolvePath(p: string): string {
    return path.resolve(
      p.startsWith('~/') ? this.env.combine(p.slice(2)) : path.isAbsolute(p) ? p : path.join(this.configDir, p),
    );
  }

  private samePath(a: string, b: string): boolean {
    return path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
  }

  private topKey(file: string, key: string, afterJson: string): ConfigChange {
    return new ConfigChange(file, 'toml', key, this.currentValue(file, 'toml', key), afterJson);
  }
}
