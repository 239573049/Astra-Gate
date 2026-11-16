import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ClientDetections,
  type ClientDetection,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { ClientKinds } from '../domain/kinds.js';
import { FileClientAdapter } from './file-adapter.js';

/**
 * miniQ (command `miniq`; Coexist): `settings.json` in the daemon's data folder (`MINIQ_DATA_DIR`, else the folder
 * `miniq-data-dir.json` names beside the default, else `%LOCALAPPDATA%\miniq` / `~/.local/share/miniq`). Astra owns the
 * `provider` key whole. (Port of MiniQClientAdapter.)
 */
export class MiniQClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.MiniQ;

  private get dataDir(): string {
    const dir = this.env.getEnvironmentVariable('MINIQ_DATA_DIR');
    if (dir) return dir;
    let fallback = this.env.combine('.local', 'share', 'miniq');
    const local = this.env.getEnvironmentVariable('LOCALAPPDATA');
    if (local) fallback = path.join(local, 'miniq');
    // miniq-data-dir.json, beside the default, names a folder the user moved the data to in Settings.
    const pointer = this.env.readTextOrNull(path.join(path.dirname(fallback), 'miniq-data-dir.json'));
    if (pointer !== null) {
      try {
        const parsed: unknown = JSON.parse(pointer);
        if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) {
          const moved = (parsed as Record<string, unknown>).data_dir;
          if (typeof moved === 'string' && moved.length > 0) return moved;
        }
      } catch {
        // a pointer that is no JSON is ignored
      }
    }
    return fallback;
  }

  private get settingsFile(): string {
    return path.join(this.dataDir, 'settings.json');
  }

  protected get markerFile(): string {
    return this.settingsFile;
  }

  protected get markerKey(): string {
    return 'provider';
  }

  protected override get enabledWarnings(): string[] {
    return ['Restart miniQ to use this: its running daemon keeps the provider it started with and writes it back over settings.json.'];
  }

  detect(): ClientDetection {
    const dir = this.dataDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `data directory ${dir}`);
    const exe = this.env.findOnPath('miniq');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no miniq executable on PATH`);
  }

  configPaths(): string[] {
    return [this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const entries = ClientAdapterBase.modelEntries(ctx);
    const model = ctx.model ? ctx.model : (entries[0]?.id ?? null);
    // miniQ needs a model to start on: with none there is nothing to write.
    if (!model) return this.buildPlan([]);
    const provider = {
      baseUrl: FileClientAdapter.gateway(ctx.gatewayBaseUrl),
      apiKey: ctx.localKey,
      model,
      apiProtocol: 'chat_completions',
    };
    return this.buildPlan([this.jsonSet(this.settingsFile, 'provider', provider)]);
  }
}
