import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ClientDetections,
  type ClientDetection,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { FileClientAdapter } from './file-adapter.js';

const CHANNEL_TABLE = 'model.astra';

/**
 * Grok App (a Tauri front end for the grok CLI; Coexist): `agent-home/config.toml` in its data folder (`GROK_APP_HOME`, else the
 * per-OS location). Astra adds the provider channel `[model.astra]` as the App writes one; it never sets `[models] default` (the
 * route). (Port of GrokAppClientAdapter.)
 */
export class GrokAppClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.GrokApp;

  private get dir(): string {
    const home = this.env.getEnvironmentVariable('GROK_APP_HOME')?.trim();
    if (home) return home;
    let dirs: string[];
    switch (this.env.os) {
      case 'osx':
        dirs = [this.env.combine('Library', 'Application Support', 'com.grokapp.grok-app'), this.env.combine('.grok-app')];
        break;
      case 'windows': {
        const a = this.env.getEnvironmentVariable('APPDATA');
        const app = a ? a : this.env.combine('AppData', 'Roaming');
        dirs = [path.join(app, 'grokapp', 'grok-app', 'data'), path.join(app, 'grokapp', 'grok-app'), path.join(app, 'grok-app')];
        break;
      }
      default: {
        const x = this.env.getEnvironmentVariable('XDG_DATA_HOME');
        const data = x ? x : this.env.combine('.local', 'share');
        dirs = [path.join(data, 'grok-app'), this.env.combine('.grok-app')];
        break;
      }
    }
    return dirs.find((d) => this.env.directoryExists(d)) ?? dirs[0]!;
  }

  private get configFile(): string {
    return path.join(this.dir, 'agent-home', 'config.toml');
  }

  protected get markerFile(): string {
    return this.configFile;
  }

  protected get markerKey(): string {
    return CHANNEL_TABLE;
  }

  protected override isTableKeyPath(keyPath: string): boolean {
    return keyPath === CHANNEL_TABLE;
  }

  protected override get enabledWarnings(): string[] {
    return [
      "Astra is a custom provider in Grok App's Settings > Account. Press Use on it there: Astra never changes the App's route ([models] default).",
    ];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    return this.env.directoryExists(dir)
      ? ClientDetections.found(null, `data directory ${dir}`)
      : ClientDetections.notFound(`no ${dir}`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const entries = ClientAdapterBase.modelEntries(ctx);
    // Grok App needs a model for the channel: with none there is nothing to write.
    if (entries.length === 0) return this.buildPlan([]);
    const raw = FileClientAdapter.raw;
    const hints = ClientAdapterBase.modelHints(ctx);
    const list: Array<Record<string, unknown>> = [];
    for (const { id, name } of entries) {
      const info = hints.get(id);
      const entry: Record<string, unknown> = { id, name: name ?? id };
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) entry.contextWindow = context;
      if (ClientAdapterBase.boolHint(info?.vision) === true) entry.supportsVision = true;
      list.push(entry);
    }
    const picked = ctx.model && entries.some((e) => e.id === ctx.model) ? ctx.model : entries[0]!.id;
    const pickedInfo = hints.get(picked);
    const window = ClientAdapterBase.positiveLong(pickedInfo?.contextWindow);
    const fields: Array<[string, string | null]> = [
      ['model', raw(picked)],
      ['base_url', raw(FileClientAdapter.gateway(ctx.gatewayBaseUrl))],
      ['name', raw('Astra')],
      ['api_key', raw(ctx.localKey)],
      ['api_backend', raw('chat_completions')],
      ['app_provider_mode', raw('generic')],
      ['app_models', raw(toJsonText(list))],
      ['app_efforts', raw('[]')],
      ['supports_reasoning_effort', 'false'],
      ['app_supports_vision', ClientAdapterBase.boolHint(pickedInfo?.vision) === true ? 'true' : null],
      ['context_window', window !== null ? String(window) : null],
    ];
    return this.buildPlan(this.tableChanges(this.configFile, CHANNEL_TABLE, fields));
  }
}
