import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChangePlan,
  type EnableContext,
  type IClientConfigStateStore,
} from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { listState, trimSlashes } from '../domain/group5-json.js';
import { ClientKinds } from '../domain/kinds.js';
import { EditorException } from '../editing/errors.js';
import { JsoncEditor, toJsonText } from '../editing/jsonc-editor.js';

const NAME_PREFIX = 'Astra: ';
const MODEL_PATH_PREFIX = 'models.[name=' + NAME_PREFIX;

const modelPath = (id: string): string => 'models.' + JsoncEditor.selector(['name', NAME_PREFIX + id]);

/**
 * WorkBuddy (Tencent's AI work agent; Coexist): adds one custom-model entry per bound model to the `models` array
 * of `~/.workbuddy/models.json`, addressed by a selector on `name`. With `international` it is WorkBuddy AI
 * (`~/.workbuddy-ai/models.json`). Entries recorded in the legacy `~/.codebuddy/models.json` are removed.
 * (Port of WorkBuddyClientAdapter.)
 */
export class WorkBuddyClientAdapter extends ClientAdapterBase {
  static readonly namePrefix = NAME_PREFIX;

  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  constructor(
    env: ClientEnvironment,
    store: IClientConfigStateStore,
    private readonly international = false,
  ) {
    super(env, store);
  }

  get kind(): string {
    return this.international ? ClientKinds.WorkBuddyAi : ClientKinds.WorkBuddy;
  }

  private get homeDir(): string {
    return this.international ? '.workbuddy-ai' : '.workbuddy';
  }

  private get configFile(): string {
    return this.env.combine(this.homeDir, 'models.json');
  }

  private get legacyConfigFile(): string {
    return this.env.combine('.codebuddy', 'models.json');
  }

  detect(): ClientDetection {
    if (this.international) {
      const home = this.env.combine(this.homeDir);
      return this.env.directoryExists(home)
        ? ClientDetections.found(null, `config directory ${home}`)
        : ClientDetections.notFound(`no ${home}`);
    }
    for (const dir of [this.env.combine('.workbuddy'), this.env.combine('.codebuddy')])
      if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('codebuddy');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(
          `no ${this.env.combine('.workbuddy')} or ${this.env.combine('.codebuddy')} and no codebuddy executable on PATH`,
        );
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const url = trimSlashes(ctx.gatewayBaseUrl) + '/v1/chat/completions';
    const hints = ClientAdapterBase.modelHints(ctx);
    const changes: ConfigChange[] = [];
    const wanted = new Set<string>();
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) {
      if (!WorkBuddyClientAdapter.canSelect(id)) continue; // ids with selector metacharacters cannot be addressed safely
      wanted.add(id);
      const info = hints.get(id);
      const entry: Record<string, unknown> = {
        id,
        name: NAME_PREFIX + id,
        vendor: 'Astra',
        url,
        apiKey: ctx.localKey,
      };
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) entry.maxInputTokens = context;
      const maxOutput = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (maxOutput !== null) entry.maxOutputTokens = maxOutput;
      entry.supportsToolCall = true;
      const vision = ClientAdapterBase.boolHint(info?.vision);
      if (vision !== null) entry.supportsImages = vision;
      const reasoning = ClientAdapterBase.boolHint(info?.reasoning);
      if (reasoning !== null) entry.supportsReasoning = reasoning;
      const p = modelPath(id);
      changes.push(new ConfigChange(file, 'json', p, this.currentValue(file, 'json', p), toJsonText(entry)));
    }
    for (const { file: f, path: p } of this.trackedModels()) {
      // Entries are written to the config file now; anything recorded in the legacy file is removed on apply.
      if (f === file && wanted.has(p.slice(MODEL_PATH_PREFIX.length, -1))) continue;
      const current = this.currentValue(f, 'json', p);
      if (current !== null) changes.push(new ConfigChange(f, 'json', p, current, null));
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    for (const { file, path: p } of this.trackedModels()) {
      const current = this.currentValue(file, 'json', p);
      if (current !== null) changes.push(new ConfigChange(file, 'json', p, current, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.trackedModels().some((t) => this.stillOurs(t.file, 'json', t.path));
    return this.buildStatus(detection, enabled, []);
  }

  /** The Astra entries recorded in this client's files (current and legacy), as (file, key path). */
  private trackedModels(): Array<{ file: string; path: string }> {
    return listState(this.store, this.kind)
      .filter((e) => e.filePath === this.configFile || e.filePath === this.legacyConfigFile)
      .filter((e) => e.keyPath.startsWith(MODEL_PATH_PREFIX) && e.keyPath.endsWith(']'))
      .map((e) => ({ file: e.filePath, path: e.keyPath }));
  }

  private static canSelect(id: string): boolean {
    try {
      JsoncEditor.selector(['name', NAME_PREFIX + id]);
      return true;
    } catch (e) {
      if (e instanceof EditorException) return false;
      throw e;
    }
  }
}
