import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChange,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { ClientKinds } from '../domain/kinds.js';
import { EditorException } from '../editing/errors.js';
import { JsoncEditor, toJsonText } from '../editing/jsonc-editor.js';
import { ConfigFileClientAdapter } from './config-file-adapter.js';

const NAME_PREFIX = 'Astra: ';
const MODEL_PATH_PREFIX = 'models.[name=' + NAME_PREFIX;

const modelPath = (id: string): string => 'models.' + JsoncEditor.selector(['name', NAME_PREFIX + id]);

function canSelect(id: string): boolean {
  try {
    JsoncEditor.selector(['name', NAME_PREFIX + id]);
    return true;
  } catch (e) {
    if (e instanceof EditorException) return false;
    throw e;
  }
}

/**
 * CodeBuddy (Tencent's CodeBuddy Code CLI and IDE; Coexist): one entry per bound model in the `models` array of
 * `models.json` in `$CODEBUDDY_CONFIG_DIR` (else `~/.codebuddy`), addressed by a selector on `name`, plus the start-up
 * `model` in `settings.json`. (Port of CodeBuddyClientAdapter.)
 */
export class CodeBuddyClientAdapter extends ConfigFileClientAdapter {
  readonly kind = ClientKinds.CodeBuddy;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dir(): string {
    return this.absoluteEnv('CODEBUDDY_CONFIG_DIR') ?? this.env.combine('.codebuddy');
  }

  private get modelsFile(): string {
    return path.join(this.dir, 'models.json');
  }

  private get settingsFile(): string {
    return path.join(this.dir, 'settings.json');
  }

  detect(): ClientDetection {
    if (this.env.directoryExists(this.dir)) return ClientDetections.found(null, `config directory ${this.dir}`);
    const exe = this.env.findOnPath('codebuddy') ?? this.env.findOnPath('cbc');
    if (exe) return ClientDetections.found(null, `executable ${exe}`);
    const app = this.findApp('CodeBuddy.app', 'CodeBuddy CN.app');
    return app
      ? ClientDetections.found(null, `application ${app}`)
      : ClientDetections.notFound(`no ${this.dir} and no codebuddy executable on PATH`);
  }

  configPaths(): string[] {
    return [this.modelsFile, this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const url = ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1/chat/completions';
    const hints = ClientAdapterBase.modelHints(ctx);
    const changes: ConfigChange[] = [];
    const wanted = new Set<string>();
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) {
      if (!canSelect(id)) continue; // ids with selector metacharacters cannot be addressed safely
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
      changes.push(this.setKey(this.modelsFile, modelPath(id), toJsonText(entry)));
    }
    // A model that left the list takes its entry with it.
    for (const entry of this.store.list(this.kind)) {
      if (entry.filePath !== this.modelsFile || !entry.keyPath.startsWith(MODEL_PATH_PREFIX) || !entry.keyPath.endsWith(']'))
        continue;
      if (wanted.has(entry.keyPath.slice(MODEL_PATH_PREFIX.length, -1))) continue;
      const gone = this.removeKey(this.modelsFile, entry.keyPath);
      if (gone) changes.push(gone);
    }
    if (ctx.model && wanted.has(ctx.model))
      changes.push(this.setKey(this.settingsFile, 'model', ClientAdapterBase.jsonString(ctx.model)));
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    return this.buildPlan(this.trackedRemovals());
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const enabled = this.store
      .list(this.kind)
      .some((e) => e.filePath === this.modelsFile && e.keyPath.startsWith(MODEL_PATH_PREFIX) && this.applied(e.filePath, e.keyPath));
    return this.buildStatus(this.detect(), enabled, []);
  }
}
