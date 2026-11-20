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

const BLOCK_ID = 'antigravity';
const MODEL_PREFIX = 'astra/';
const BLOCK_NOTE = '# Antigravity is routed through Astra. Turn the Antigravity client off in Astra to remove this block.';
const PROVIDER_API = 'API_PROVIDER_GOOGLE_GEMINI';
const MODELS_PATH = 'customModelsConfig';

function tryObject(json: string): Record<string, unknown> | null {
  try {
    const node: unknown = JSON.parse(json);
    return node !== null && typeof node === 'object' && !Array.isArray(node) ? (node as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/**
 * Antigravity, the desktop agent app (`Antigravity.app`; Switch): the CLI's engine in an Electron shell. Astra writes `modelProvider`,
 * `model` and `customModelsConfig.customModels` into `~/.gemini/antigravity/settings.json` through the applier (originals recorded and
 * given back; the user's own custom models kept) and one managed shell-rc block exporting `GOOGLE_GEMINI_BASE_URL` / `GEMINI_API_KEY`,
 * which the app passes to its engine from the login shell's environment. Windows (no shell profile) reports not found. (Port of
 * AntigravityClientAdapter.)
 */
export class AntigravityClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.Antigravity;
  readonly mode = 'switch' as const;
  readonly availability = 'available' as const;

  private get dir(): string {
    return this.env.combine('.gemini', 'antigravity');
  }

  private get settingsFile(): string {
    return path.join(this.dir, 'settings.json');
  }

  detect(): ClientDetection {
    if (this.env.os === 'windows') {
      return ClientDetections.notFound('Astra routes Antigravity through the shell profile, which Windows does not have');
    }
    const app = this.env.applicationDirectories
      .map((a) => path.join(a, 'Antigravity.app'))
      .find((p) => this.env.directoryExists(p));
    if (app !== undefined) return ClientDetections.found(null, `application ${app}`);
    const dir = this.dir;
    return this.env.directoryExists(dir)
      ? ClientDetections.found(null, `config directory ${dir}`)
      : ClientDetections.notFound(`no Antigravity app and no ${dir}`);
  }

  configPaths(): string[] {
    return [this.settingsFile, ShellRc.file(this.env)];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const rc = ShellRc.file(this.env);
    const entries = ClientAdapterBase.modelEntries(ctx);
    const model = ctx.model ? ctx.model : (entries[0]?.id ?? null);
    const custom: Record<string, unknown> = {};
    // The user's own custom models first (the engine takes customModels whole); a resync drops the ones Astra wrote before.
    const own = this.currentValue(this.settingsFile, 'json', `${MODELS_PATH}.customModels`);
    const ownModels = own !== null ? tryObject(own) : null;
    if (ownModels) for (const [key, value] of Object.entries(ownModels)) if (!key.startsWith(MODEL_PREFIX)) custom[key] = value;
    for (const { id } of entries) custom[MODEL_PREFIX + id] = { apiProvider: PROVIDER_API, modelName: id };

    const changes: ConfigChange[] = [this.key('modelProvider', ClientAdapterBase.jsonString('gemini'))];
    if (model) changes.push(this.key('model', ClientAdapterBase.jsonString(MODEL_PREFIX + model)));
    changes.push(this.key(`${MODELS_PATH}.customModels`, toJsonText(custom)));
    changes.push(
      new ConfigChange(
        rc,
        'shellblock',
        BLOCK_ID,
        this.currentValue(rc, 'shellblock', BLOCK_ID),
        ClientAdapterBase.jsonString(AntigravityClientAdapter.blockBody(ctx)),
      ),
    );
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const rc = ShellRc.file(this.env);
    const changes: ConfigChange[] = [];
    const block = this.currentValue(rc, 'shellblock', BLOCK_ID);
    if (block !== null) changes.push(new ConfigChange(rc, 'shellblock', BLOCK_ID, block, null));
    // Only the keys Astra recorded in the user's settings.json: everything else in it is theirs.
    for (const entry of listState(this.store, this.kind)) {
      if (entry.filePath !== this.settingsFile) continue;
      const current = this.currentValue(this.settingsFile, 'json', entry.keyPath);
      if (current !== null) changes.push(new ConfigChange(this.settingsFile, 'json', entry.keyPath, current, null));
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
      enabled ? ['Quit and reopen Antigravity (from a new terminal or the Dock) so it reads the shell profile again.'] : [],
    );
  }

  private key(key: string, json: string): ConfigChange {
    return new ConfigChange(this.settingsFile, 'json', key, this.currentValue(this.settingsFile, 'json', key), json);
  }

  private static blockBody(ctx: EnableContext): string {
    return [
      BLOCK_NOTE,
      `export GOOGLE_GEMINI_BASE_URL=${ShellRc.quote(ctx.gatewayBaseUrl.replace(/\/+$/, ''))}`,
      `export GEMINI_API_KEY=${ShellRc.quote(ctx.localKey)}`,
    ].join('\n');
  }
}
