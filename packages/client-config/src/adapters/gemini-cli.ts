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
import { equalsValue } from '../config-value-codec.js';
import { ClientKinds } from '../domain/kinds.js';

const AUTH_TYPE_PATH = 'security.auth.selectedType';
const AUTH_TYPE_VALUE = 'gemini-api-key';
const MANAGED_ENV_KEYS = ['GOOGLE_GEMINI_BASE_URL', 'GEMINI_API_KEY', 'GEMINI_MODEL'];

/**
 * Gemini CLI (Switch): writes `GOOGLE_GEMINI_BASE_URL`, `GEMINI_API_KEY` and optionally `GEMINI_MODEL` into
 * `~/.gemini/.env`, and pins `security.auth.selectedType = "gemini-api-key"` in `~/.gemini/settings.json`.
 * (Port of GeminiCliClientAdapter.)
 */
export class GeminiCliClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.GeminiCli;
  readonly mode = 'switch' as const;
  readonly availability = 'available' as const;

  private get geminiDir(): string {
    return this.env.combine('.gemini');
  }

  private get envFile(): string {
    return path.join(this.geminiDir, '.env');
  }

  private get settingsFile(): string {
    return path.join(this.geminiDir, 'settings.json');
  }

  detect(): ClientDetection {
    const dir = this.geminiDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('gemini');
    return exe !== null
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no gemini executable on PATH`);
  }

  configPaths(): string[] {
    return [this.envFile, this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const changes: ConfigChange[] = [
      this.envValue(this.envFile, 'GOOGLE_GEMINI_BASE_URL', ctx.gatewayBaseUrl),
      this.envValue(this.envFile, 'GEMINI_API_KEY', ctx.localKey),
    ];
    if (ctx.model) changes.push(this.envValue(this.envFile, 'GEMINI_MODEL', ctx.model));
    changes.push(
      new ConfigChange(
        this.settingsFile,
        'json',
        AUTH_TYPE_PATH,
        this.currentValue(this.settingsFile, 'json', AUTH_TYPE_PATH),
        ClientAdapterBase.jsonString(AUTH_TYPE_VALUE),
      ),
    );
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    for (const key of MANAGED_ENV_KEYS) {
      const before = this.currentValue(this.envFile, 'env', key);
      if (before !== null) changes.push(new ConfigChange(this.envFile, 'env', key, before, null));
    }
    const before2 = this.currentValue(this.settingsFile, 'json', AUTH_TYPE_PATH);
    if (before2 !== null) changes.push(new ConfigChange(this.settingsFile, 'json', AUTH_TYPE_PATH, before2, null));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const entry = this.store.get(this.kind, this.envFile, 'GOOGLE_GEMINI_BASE_URL');
    const enabled =
      entry !== null &&
      equalsValue('env', this.currentValue(this.envFile, 'env', 'GOOGLE_GEMINI_BASE_URL'), entry.appliedValueJson);
    return this.buildStatus(detection, enabled, []);
  }

  private envValue(file: string, key: string, value: string): ConfigChange {
    const before = this.currentValue(file, 'env', key);
    return new ConfigChange(file, 'env', key, before, ClientAdapterBase.jsonString(value));
  }
}
