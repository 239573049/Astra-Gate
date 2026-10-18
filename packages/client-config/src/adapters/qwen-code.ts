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
import { ApiProtocols, type ApiProtocol } from '../domain/protocols.js';

const KEY_VARIABLE = 'ASTRA_QWEN_API_KEY';
const KEY_PATH = 'env.' + KEY_VARIABLE;
const BUCKETS = ['openai', 'anthropic', 'gemini'];
const AUTH_TYPE_PATH = 'security.auth.selectedType';
const MODEL_NAME_PATH = 'model.name';

const modelPathPrefix = (bucket: string) => `modelProviders.${bucket}.[envKey=${KEY_VARIABLE}&id=`;
const modelPath = (bucket: string, id: string) =>
  `modelProviders.${bucket}.` + JsoncEditor.selector(['envKey', KEY_VARIABLE], ['id', id]);

const bucketOf = (protocol: ApiProtocol): string =>
  protocol === 'anthropic' ? 'anthropic' : protocol === 'gemini' ? 'gemini' : 'openai';

function canSelect(id: string): boolean {
  try {
    JsoncEditor.selector(['id', id]);
    return true;
  } catch (e) {
    if (e instanceof EditorException) return false;
    throw e;
  }
}

/**
 * Qwen Code (Coexist): adds one owned element per bound model to the `modelProviders.<bucket>` arrays of
 * `~/.qwen/settings.json` (bucket by native protocol), the local key to `env.ASTRA_QWEN_API_KEY`, and, when a model
 * is chosen, activates it with `security.auth.selectedType` and `model.name`. (Port of QwenCodeClientAdapter.)
 */
export class QwenCodeClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.QwenCode;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get configFile(): string {
    return this.env.combine('.qwen', 'settings.json');
  }

  detect(): ClientDetection {
    const dir = this.env.combine('.qwen');
    if (this.env.directoryExists(dir))
      return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('qwen');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no qwen executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [
      new ConfigChange(
        file,
        'json',
        KEY_PATH,
        this.currentValue(file, 'json', KEY_PATH),
        ClientAdapterBase.jsonString(ctx.localKey),
      ),
    ];
    const root = ctx.gatewayBaseUrl.replace(/\/+$/, '');
    const protocols = this.modelProtocols(ctx, ApiProtocols.openAIChat);
    const wanted = new Set<string>();
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      if (!canSelect(id)) continue; // ids with selector metacharacters cannot be addressed safely
      const protocol = protocols.get(id)!;
      const p = modelPath(bucketOf(protocol), id);
      wanted.add(p);
      const entry: Record<string, unknown> = {
        id,
        name: name ?? id,
        envKey: KEY_VARIABLE,
        baseUrl: protocol === 'anthropic' || protocol === 'gemini' ? root : root + '/v1',
      };
      if (protocol === 'openai-responses') entry.wireApi = 'responses';
      changes.push(
        new ConfigChange(file, 'json', p, this.currentValue(file, 'json', p), toJsonText(entry)),
      );
    }
    // Models Astra registered earlier that the bound provider no longer offers (or that moved to another protocol's
    // array) are taken out again.
    for (const p of this.trackedModelPaths(file)) {
      if (wanted.has(p)) continue;
      const current = this.currentValue(file, 'json', p);
      if (current !== null) changes.push(new ConfigChange(file, 'json', p, current, null));
    }
    const selected = ctx.model ? protocols.get(ctx.model) : undefined;
    if (
      ctx.model &&
      selected !== undefined &&
      wanted.has(modelPath(bucketOf(selected), ctx.model))
    ) {
      const authType =
        selected === 'openai-responses'
          ? 'openai-responses'
          : selected === 'anthropic'
            ? 'anthropic'
            : selected === 'gemini'
              ? 'gemini'
              : 'openai';
      changes.push(this.key(AUTH_TYPE_PATH, ClientAdapterBase.jsonString(authType)));
      changes.push(this.key(MODEL_NAME_PATH, ClientAdapterBase.jsonString(ctx.model)));
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    for (const p of [KEY_PATH, ...this.trackedModelPaths(file)]) {
      const current = this.currentValue(file, 'json', p);
      if (current !== null) changes.push(new ConfigChange(file, 'json', p, current, null));
    }
    // The selection is only ours while it still holds what Astra wrote; a model picked afterwards stays.
    for (const p of [MODEL_NAME_PATH, AUTH_TYPE_PATH])
      if (this.stillOurs(file, 'json', p))
        changes.push(new ConfigChange(file, 'json', p, this.currentValue(file, 'json', p), null));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const warnings: string[] = [];
    if (this.currentValue(this.configFile, 'json', 'providerProtocol') !== null)
      warnings.push(
        'settings.json defines providerProtocol; Astra uses the built-in openai provider type and leaves it untouched.',
      );
    return this.buildStatus(detection, this.stillOurs(this.configFile, 'json', KEY_PATH), warnings);
  }

  private key(p: string, valueJson: string): ConfigChange {
    return new ConfigChange(
      this.configFile,
      'json',
      p,
      this.currentValue(this.configFile, 'json', p),
      valueJson,
    );
  }

  private trackedModelPaths(file: string): string[] {
    return (
      this.store
        .list(this.kind)
        .filter(
          (e) =>
            e.filePath === file &&
            e.keyPath.endsWith(']') &&
            BUCKETS.some((b) => e.keyPath.startsWith(modelPathPrefix(b))),
        )
        .map((e) => e.keyPath)
        // the C# state store lists ordered by file, then key (ordinal)
        .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
    );
  }
}
