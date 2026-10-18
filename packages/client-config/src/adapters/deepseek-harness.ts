import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChangePlan,
  type EnableContext,
  type JsonObject,
} from '../contracts.js';
import { EditorException } from '../editing/errors.js';
import { JsoncEditor, toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { PiAiModels } from '../domain/pi-ai-models.js';
import type { ApiProtocol } from '../domain/protocols.js';

const PROVIDER_ID = 'astra';
const KEY_VARIABLE = 'ASTRA_GATEWAY_API_KEY';

const LLM_ROW_ID = 'llm-pi-ai';
const LLM_ROW_NAME = '@deepseek-ai/dsh-llm-pi-ai';
const DEFAULT_ROW_ID = 'agent-default-model';
const DEFAULT_ROW_NAME = '@deepseek-ai/dsh-agent-default-model';

const LLM_ROW = JsoncEditor.selector(['id', LLM_ROW_ID]);
const DEFAULT_ROW = JsoncEditor.selector(['id', DEFAULT_ROW_ID]);

const providerPathOf = (key: string): string => `${LLM_ROW}.config.providers.${key}`;
const PROVIDER_PATH = providerPathOf(PROVIDER_ID);
const DEFAULT_PROVIDER_PATH = `${DEFAULT_ROW}.config.provider`;
const DEFAULT_MODEL_PATH = `${DEFAULT_ROW}.config.model`;
const DEFAULT_EFFORT_PATH = `${DEFAULT_ROW}.config.reasoningEffort`;

/** The routes Astra writes, one per protocol: key and display name. */
const ROUTES: Array<{ protocol: ApiProtocol; key: string; name: string }> = [
  { protocol: 'openai-chat', key: PROVIDER_ID, name: 'Astra' },
  { protocol: 'openai-responses', key: PROVIDER_ID + '-responses', name: 'Astra (Responses)' },
  { protocol: 'anthropic', key: PROVIDER_ID + '-anthropic', name: 'Astra (Anthropic)' },
];

const PROFILES = ['desktop', 'web'];

/**
 * DeepSeek Harness (Coexist): adds a custom model route to the profile's patch layer (`cordis.patch.yml` rows
 * `llm-pi-ai` and `agent-default-model`) and the local key to `$DSH_HOME/.env`. A route speaks one wire protocol,
 * so models are listed under per-protocol routes. (Port of DeepSeekHarnessClientAdapter.)
 */
export class DeepSeekHarnessClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.DeepSeekHarness;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get homeDir(): string {
    const dir = this.env.getEnvironmentVariable('DSH_HOME');
    return dir ? dir : this.env.combine('.dsh');
  }

  private get envFile(): string {
    return path.join(this.homeDir, '.env');
  }

  private patchFile(profile: string): string {
    return path.join(this.homeDir, 'profiles', profile, 'cordis.patch.yml');
  }

  /** The patch files of the profiles Harness created (desktop first). */
  private existingPatchFiles(): string[] {
    return PROFILES.filter((p) =>
      this.env.directoryExists(path.join(this.homeDir, 'profiles', p)),
    ).map((p) => this.patchFile(p));
  }

  /** Existing profiles plus every patch file Astra wrote to earlier. */
  private knownPatchFiles(): string[] {
    const stored = this.store
      .list(this.kind)
      .filter((e) => e.filePath.endsWith('cordis.patch.yml'))
      .map((e) => e.filePath);
    return [...new Set([...this.existingPatchFiles(), ...stored])];
  }

  detect(): ClientDetection {
    const dir = this.homeDir;
    if (this.env.directoryExists(dir))
      return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('dsh');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no dsh executable on PATH`);
  }

  configPaths(): string[] {
    return [...this.existingPatchFiles(), this.envFile];
  }

  protected override isTableKeyPath(keyPath: string): boolean {
    return keyPath === LLM_ROW || keyPath === DEFAULT_ROW;
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const files = this.existingPatchFiles();
    if (files.length === 0)
      throw new EditorException(
        `DeepSeek Harness has no desktop or web profile in ${path.join(this.homeDir, 'profiles')} yet; start it once, then enable it here.`,
      );
    const models = DeepSeekHarnessClientAdapter.modelsJson(ctx);
    if (models.length === 0)
      throw new EditorException(
        'The bound provider has no enabled models to offer to DeepSeek Harness.',
      );
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    const selected = ctx.model && models.some((m) => m.id === ctx.model) ? ctx.model : null;
    const selectedRoute =
      selected === null
        ? PROVIDER_ID
        : ROUTES.find((r) => r.protocol === protocols.get(selected))!.key;

    const changes: ConfigChange[] = [
      new ConfigChange(
        this.envFile,
        'env',
        KEY_VARIABLE,
        this.currentValue(this.envFile, 'env', KEY_VARIABLE),
        ClientAdapterBase.jsonString(ctx.localKey),
      ),
    ];
    for (const file of files) {
      changes.push(...this.ensureRow(file, LLM_ROW, LLM_ROW_ID, LLM_ROW_NAME));
      for (const { protocol, key, name } of ROUTES) {
        const own = models.filter((m) => protocols.get(m.id as string) === protocol);
        const keyPath = providerPathOf(key);
        if (protocol !== 'openai-chat' && own.length === 0) {
          // Not needed (any more): a route Astra added goes again.
          if (this.store.get(this.kind, file, keyPath) !== null) {
            const before = this.currentValue(file, 'yaml', keyPath);
            if (before !== null)
              changes.push(new ConfigChange(file, 'yaml', keyPath, before, null));
          }
          continue;
        }
        const route = {
          displayName: name,
          apiKeyEnv: KEY_VARIABLE,
          api: PiAiModels.apiOf(protocol),
          baseURL: PiAiModels.baseUrlFor(ctx.gatewayBaseUrl, protocol),
          models: own,
        };
        changes.push(this.key(file, keyPath, toJsonText(route)));
      }
      if (selected === null) continue;
      changes.push(...this.ensureRow(file, DEFAULT_ROW, DEFAULT_ROW_ID, DEFAULT_ROW_NAME));
      changes.push(
        this.key(file, DEFAULT_PROVIDER_PATH, ClientAdapterBase.jsonString(selectedRoute)),
      );
      changes.push(this.key(file, DEFAULT_MODEL_PATH, ClientAdapterBase.jsonString(selected)));
      // A custom model declares no effort levels; an effort it does not declare is refused.
      const effort = this.currentValue(file, 'yaml', DEFAULT_EFFORT_PATH);
      if (effort !== null)
        changes.push(new ConfigChange(file, 'yaml', DEFAULT_EFFORT_PATH, effort, null));
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    for (const file of this.knownPatchFiles()) {
      if (!this.env.fileExists(file)) continue;
      // A default model the user picked afterwards (another provider) is theirs and stays.
      const defaultRoute = ClientAdapterBase.stringOf(
        this.currentValue(file, 'yaml', DEFAULT_PROVIDER_PATH),
      );
      const ownsDefault = ROUTES.some((r) => r.key === defaultRoute);
      const extraRoutes = ROUTES.slice(1)
        .map((r) => providerPathOf(r.key))
        .filter((p) => this.store.get(this.kind, file, p) !== null);
      const routes = [PROVIDER_PATH, ...extraRoutes];
      const owned = ownsDefault ? [DEFAULT_MODEL_PATH, DEFAULT_PROVIDER_PATH, ...routes] : routes;
      for (const p of owned) {
        const current = this.currentValue(file, 'yaml', p);
        if (current !== null) changes.push(new ConfigChange(file, 'yaml', p, current, null));
      }
      // The reasoning effort dropped on enable comes back from its recorded original.
      if (ownsDefault && this.store.get(this.kind, file, DEFAULT_EFFORT_PATH) !== null)
        changes.push(new ConfigChange(file, 'yaml', DEFAULT_EFFORT_PATH, null, null));
      this.addRowRemoval(
        changes,
        file,
        LLM_ROW,
        ...ROUTES.map((r) => ['config', 'providers', r.key]),
      );
      if (ownsDefault)
        this.addRowRemoval(changes, file, DEFAULT_ROW, ['config', 'provider'], ['config', 'model']);
    }
    const key = this.currentValue(this.envFile, 'env', KEY_VARIABLE);
    if (key !== null) changes.push(new ConfigChange(this.envFile, 'env', KEY_VARIABLE, key, null));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const warnings: string[] = [];
    const files = this.existingPatchFiles();
    if (files.length === 0)
      warnings.push(
        `No DeepSeek Harness profile (desktop or web) exists in ${path.join(this.homeDir, 'profiles')} yet; start Harness once before enabling.`,
      );
    const homePatch = path.join(this.homeDir, 'cordis.patch.yml');
    if (this.env.fileExists(homePatch) && this.currentValue(homePatch, 'yaml', LLM_ROW) !== null)
      warnings.push(
        `${homePatch} defines an llm-pi-ai row; Harness applies it after the profile's, so it replaces the Astra provider (not modified by Astra).`,
      );
    const enabled = this.knownPatchFiles().some((f) => this.stillOurs(f, 'yaml', PROVIDER_PATH));
    return this.buildStatus(detection, enabled, warnings);
  }

  private key(file: string, keyPath: string, valueJson: string): ConfigChange {
    return new ConfigChange(
      file,
      'yaml',
      keyPath,
      this.currentValue(file, 'yaml', keyPath),
      valueJson,
    );
  }

  /** Creates the row (id and name only) when the patch file has none. */
  private ensureRow(file: string, rowPath: string, id: string, name: string): ConfigChange[] {
    if (this.currentValue(file, 'yaml', rowPath, 'table') !== null) return [];
    return [
      new ConfigChange(file, 'yaml', rowPath, null, toJsonText({ id, name }), { kind: 'table' }),
    ];
  }

  /** Removes a row Astra created, but only when taking Astra's keys out leaves nothing but id and name. */
  private addRowRemoval(
    changes: ConfigChange[],
    file: string,
    rowPath: string,
    ...ownedPaths: string[][]
  ): void {
    const entry = this.store.get(this.kind, file, rowPath);
    if (entry === null || !entry.originalAbsent) return;
    const json = this.currentValue(file, 'yaml', rowPath);
    if (json === null) return;
    const row: unknown = JSON.parse(json);
    if (row === null || typeof row !== 'object' || Array.isArray(row)) return;
    const obj = row as JsonObject;
    for (const p of ownedPaths) removePath(obj, p);
    const config = obj.config;
    if (
      config !== null &&
      typeof config === 'object' &&
      !Array.isArray(config) &&
      Object.keys(config).length === 0
    )
      delete obj.config;
    const keys = Object.keys(obj);
    if (keys.length === 2 && 'id' in obj && 'name' in obj)
      changes.push(new ConfigChange(file, 'yaml', rowPath, 'true', null, { kind: 'table' }));
  }

  private static modelsJson(ctx: EnableContext): JsonObject[] {
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: JsonObject[] = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const model: JsonObject = { id, name: name ?? id };
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      let maxTokens = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (context !== null && maxTokens !== null && maxTokens >= context)
        maxTokens = Math.max(1, Math.floor(context / 4));
      if (context !== null) model.contextWindow = context;
      if (maxTokens !== null) model.maxTokens = maxTokens;
      if (ClientAdapterBase.boolHint(info?.vision) === true) model.input = ['text', 'image'];
      models.push(model);
    }
    return models;
  }
}

const isObj = (v: unknown): v is JsonObject =>
  v !== null && typeof v === 'object' && !Array.isArray(v);

/** Removes the member at a path and the objects that only held it. */
function removePath(root: JsonObject, parts: string[]): void {
  const chain: JsonObject[] = [root];
  let current = root;
  for (const part of parts.slice(0, -1)) {
    const next = current[part];
    if (!isObj(next)) return;
    chain.push(next);
    current = next;
  }
  delete current[parts[parts.length - 1]!];
  for (let i = chain.length - 1; i >= 1 && Object.keys(chain[i]!).length === 0; i--)
    delete chain[i - 1]![parts[i - 1]!];
}
