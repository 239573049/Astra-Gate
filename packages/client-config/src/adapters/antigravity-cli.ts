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
import { ClientKinds } from '../domain/kinds.js';
import { ShellRc, listState } from './shell-rc.js';

const BLOCK_ID = 'antigravity-cli';
const MODEL_PREFIX = 'astra/';
const BLOCK_NOTE =
  '# Antigravity CLI is routed through Astra. Turn the Antigravity CLI client off in Astra to remove this block.';
const PROVIDER_API = 'API_PROVIDER_GOOGLE_GEMINI';

function tryObject(json: string): Record<string, unknown> | null {
  try {
    const node: unknown = JSON.parse(json);
    return node !== null && typeof node === 'object' && !Array.isArray(node) ? (node as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/**
 * Antigravity CLI (`agy`; Switch): Astra writes its own settings file beside agy's (`astra-settings.json`: the gemini provider,
 * the model and one custom model per bound model, the user's own copied in) and one managed shell-rc block defining an `agy`
 * function that starts the real agy with the gateway root, the client's key, `--settings` and `--model`. (Port of
 * AntigravityCliClientAdapter.)
 */
export class AntigravityCliClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.AntigravityCli;
  readonly mode = 'switch' as const;
  readonly availability = 'available' as const;

  private get dir(): string {
    return this.env.combine('.gemini', 'antigravity-cli');
  }

  private get settingsFile(): string {
    return path.join(this.dir, 'settings.json');
  }

  private get overrideFile(): string {
    return path.join(this.dir, 'astra-settings.json');
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('agy');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no agy executable on PATH`);
  }

  configPaths(): string[] {
    return [this.overrideFile, ShellRc.file(this.env)];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const rc = ShellRc.file(this.env);
    const entries = ClientAdapterBase.modelEntries(ctx);
    const model = ctx.model ? ctx.model : (entries[0]?.id ?? null);
    const custom: Record<string, unknown> = {};
    // The user's own custom models first: agy takes customModels whole, so a --settings file must carry them too.
    const own = this.currentValue(this.settingsFile, 'json', 'customModelsConfig.customModels');
    const ownModels = own !== null ? tryObject(own) : null;
    if (ownModels) for (const [key, value] of Object.entries(ownModels)) if (!key.startsWith(MODEL_PREFIX)) custom[key] = value;
    for (const { id } of entries) custom[MODEL_PREFIX + id] = { apiProvider: PROVIDER_API, modelName: id };

    const changes: ConfigChange[] = [this.key('modelProvider', ClientAdapterBase.jsonString('gemini'))];
    if (model) changes.push(this.key('model', ClientAdapterBase.jsonString(MODEL_PREFIX + model)));
    changes.push(this.key('customModelsConfig', toJsonText({ customModels: custom })));
    changes.push(
      new ConfigChange(
        rc,
        'shellblock',
        BLOCK_ID,
        this.currentValue(rc, 'shellblock', BLOCK_ID),
        ClientAdapterBase.jsonString(AntigravityCliClientAdapter.blockBody(ctx, model ? MODEL_PREFIX + model : null)),
      ),
    );
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const rc = ShellRc.file(this.env);
    const changes: ConfigChange[] = [];
    const block = this.currentValue(rc, 'shellblock', BLOCK_ID);
    if (block !== null) changes.push(new ConfigChange(rc, 'shellblock', BLOCK_ID, block, null));
    // The file is Astra's alone: every key it recorded goes (the file with the last one).
    for (const entry of listState(this.store, this.kind)) {
      if (entry.filePath !== this.overrideFile) continue;
      const current = this.currentValue(this.overrideFile, 'json', entry.keyPath);
      if (current !== null) changes.push(new ConfigChange(this.overrideFile, 'json', entry.keyPath, current, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.stillOurs(ShellRc.file(this.env), 'shellblock', BLOCK_ID);
    return this.buildStatus(
      detection,
      enabled,
      enabled ? ['Open a new terminal (or source your shell rc file) so the agy function is defined.'] : [],
    );
  }

  private key(key: string, json: string): ConfigChange {
    return new ConfigChange(this.overrideFile, 'json', key, this.currentValue(this.overrideFile, 'json', key), json);
  }

  private static blockBody(ctx: EnableContext, model: string | null): string {
    const settings = '"$HOME/.gemini/antigravity-cli/astra-settings.json"';
    return [
      BLOCK_NOTE,
      'unalias agy 2>/dev/null || true',
      'agy() {',
      '  local m a',
      `  m=${model === null ? "''" : ShellRc.quote(model)}`,
      '  for a in "$@"; do case "$a" in remote-control|--remote-control|--remote-control=*|-remote-control) command agy "$@"; return;; esac; done',
      '  for a in "$@"; do case "$a" in --model|--model=*|-model|-model=*) m=;; esac; done',
      '  if [ -n "$m" ]; then set -- --model "$m" "$@"; fi',
      `  set -- --settings ${settings} "$@"`,
      `  GEMINI_API_KEY=${ShellRc.quote(ctx.localKey)} GOOGLE_GEMINI_BASE_URL=${ShellRc.quote(ctx.gatewayBaseUrl.replace(/\/+$/, ''))} command agy "$@"`,
      '}',
    ].join('\n');
  }
}
