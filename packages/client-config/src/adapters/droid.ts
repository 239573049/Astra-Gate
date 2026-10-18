import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { EditorException } from '../editing/errors.js';
import { JsoncEditor, toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { ApiProtocols } from '../domain/protocols.js';

const DISPLAY_PREFIX = 'Astra: ';
const MODEL_PATH_PREFIX = 'customModels.[displayName=' + DISPLAY_PREFIX;
const modelPath = (id: string) =>
  'customModels.' + JsoncEditor.selector(['displayName', DISPLAY_PREFIX + id]);

function canSelect(id: string): boolean {
  try {
    JsoncEditor.selector(['displayName', DISPLAY_PREFIX + id]);
    return true;
  } catch (e) {
    if (e instanceof EditorException) return false;
    throw e;
  }
}

/**
 * Droid (Factory; Coexist): adds one bring-your-own-key entry per bound model to the `customModels` array of
 * `~/.factory/settings.json`, addressed by a selector on `displayName`; each carries its own `provider` for its
 * native protocol. Astra does not select a default model. (Port of DroidClientAdapter.)
 */
export class DroidClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.Droid;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get configFile(): string {
    return this.env.combine('.factory', 'settings.json');
  }

  detect(): ClientDetection {
    const dir = this.env.combine('.factory');
    if (this.env.directoryExists(dir))
      return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('droid');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no droid executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const root = ctx.gatewayBaseUrl.replace(/\/+$/, '');
    const hints = ClientAdapterBase.modelHints(ctx);
    const protocols = this.modelProtocols(ctx, ApiProtocols.openAIChat);
    const changes: ConfigChange[] = [];
    const wanted = new Set<string>();
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) {
      if (!canSelect(id)) continue; // ids with selector metacharacters cannot be addressed safely
      wanted.add(id);
      const info = hints.get(id);
      const protocol = protocols.get(id)!;
      const entry: Record<string, unknown> = {
        model: id,
        displayName: DISPLAY_PREFIX + id,
        baseUrl: protocol === 'anthropic' ? root : root + '/v1',
        apiKey: ctx.localKey,
        provider:
          protocol === 'openai-responses'
            ? 'openai'
            : protocol === 'anthropic'
              ? 'anthropic'
              : 'generic-chat-completion-api',
      };
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) entry.maxContextLimit = context;
      const maxOutput = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (maxOutput !== null) entry.maxOutputTokens = maxOutput;
      if (ClientAdapterBase.boolHint(info?.vision) === false) entry.noImageSupport = true;
      const p = modelPath(id);
      changes.push(
        new ConfigChange(file, 'json', p, this.currentValue(file, 'json', p), toJsonText(entry)),
      );
    }
    for (const p of this.trackedModelPaths(file)) {
      if (wanted.has(p.slice(MODEL_PATH_PREFIX.length, -1))) continue;
      const current = this.currentValue(file, 'json', p);
      if (current !== null) changes.push(new ConfigChange(file, 'json', p, current, null));
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    for (const p of this.trackedModelPaths(file)) {
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
    const enabled = this.trackedModelPaths(this.configFile).some((p) =>
      this.stillOurs(this.configFile, 'json', p),
    );
    return this.buildStatus(detection, enabled, []);
  }

  private trackedModelPaths(file: string): string[] {
    return (
      this.store
        .list(this.kind)
        .filter(
          (e) =>
            e.filePath === file &&
            e.keyPath.startsWith(MODEL_PATH_PREFIX) &&
            e.keyPath.endsWith(']'),
        )
        .map((e) => e.keyPath)
        // the C# state store lists ordered by file, then key (ordinal)
        .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
    );
  }
}
