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

const PROFILE_NAME = 'astra';
const DEFAULT_CONTEXT = 200_000;
const DEFAULT_OUTPUT = 64_000;

/**
 * Snow CLI (`snow-ai`; Coexist): `~/.snow` (`SNOW_CONFIG_DIR` when absolute). Adds `profiles/astra.json` (the `snowcfg` of the
 * profile the user was on, pointed at the gateway), makes it the active profile and copies `snowcfg` into `config.json`, as
 * Snow's own profile switch does. (Port of SnowClientAdapter.)
 */
export class SnowClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.Snow;

  private get dir(): string {
    const dir = this.env.getEnvironmentVariable('SNOW_CONFIG_DIR')?.trim();
    return dir && path.isAbsolute(dir) ? dir : this.env.combine('.snow');
  }

  private profileFile(name: string): string {
    return path.join(this.dir, 'profiles', name + '.json');
  }

  private get activeFile(): string {
    return path.join(this.dir, 'active-profile.json');
  }

  private get configFile(): string {
    return path.join(this.dir, 'config.json');
  }

  protected get markerFile(): string {
    return this.profileFile(PROFILE_NAME);
  }

  protected get markerKey(): string {
    return 'snowcfg';
  }

  protected override get enabledWarnings(): string[] {
    return [
      'Snow reads its profile at start-up: restart open snow sessions to use this. Snow App takes the profile in with "Sync Snow CLI API config".',
    ];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('snow');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no snow executable on PATH`);
  }

  configPaths(): string[] {
    return [this.profileFile(PROFILE_NAME), this.activeFile, this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const entries = ClientAdapterBase.modelEntries(ctx);
    const model = ctx.model ? ctx.model : (entries[0]?.id ?? null);
    // The profile needs a model: with none there is nothing to write.
    if (!model) return this.buildPlan([]);

    const cfg = this.baseConfig();
    delete cfg.oauth;
    const info = ClientAdapterBase.modelHints(ctx).get(model);
    const context = ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT;
    const output = Math.min(ClientAdapterBase.positiveLong(info?.maxOutputTokens) ?? DEFAULT_OUTPUT, context);
    cfg.maxContextTokens = context;
    cfg.maxTokens = output;
    cfg.supportsVision = ClientAdapterBase.boolHint(info?.vision) === true;
    cfg.baseUrl = FileClientAdapter.gateway(ctx.gatewayBaseUrl);
    cfg.baseUrlMode = 'base';
    cfg.apiKey = ctx.localKey;
    cfg.requestMethod = 'chat';
    cfg.advancedModel = model;
    cfg.basicModel = model;

    return this.buildPlan([
      this.jsonSet(this.profileFile(PROFILE_NAME), 'snowcfg', cfg),
      this.jsonSet(this.activeFile, 'activeProfile', PROFILE_NAME),
      this.jsonSet(this.configFile, 'snowcfg', cfg),
    ]);
  }

  /** The `snowcfg` of the profile the user is on (config.json's when the profile file is missing); empty when there is none. */
  private baseConfig(): Record<string, unknown> {
    const activeText = ClientAdapterBase.stringOf(this.currentValue(this.activeFile, 'json', 'activeProfile'));
    const active = activeText ? activeText : 'default';
    const text =
      this.currentValue(this.profileFile(active), 'json', 'snowcfg') ??
      (this.env.fileExists(this.profileFile(active)) ? null : this.currentValue(this.configFile, 'json', 'snowcfg'));
    if (text === null) return {};
    try {
      const parsed: unknown = JSON.parse(text);
      return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : {};
    } catch {
      return {};
    }
  }
}
