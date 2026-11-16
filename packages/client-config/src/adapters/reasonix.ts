import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ClientDetections,
  type ClientDetection,
  type ConfigChange,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { ClientKinds } from '../domain/kinds.js';
import { FileClientAdapter, PROVIDER_ID } from './file-adapter.js';

export const REASONIX_KEY_VARIABLE = 'ASTRA_REASONIX_API_KEY';
const SELECTOR = 'providers[name=astra]';
const ACCESS_PATH = 'desktop.provider_access';

/**
 * Reasonix Studio 2.x and its native CLI (Coexist): `config.toml` and `.env` in `REASONIX_HOME` (else `%APPDATA%\reasonix`,
 * `~/.reasonix`). Astra owns one `[[providers]]` entry named `astra`, the key in `.env` under `ASTRA_REASONIX_API_KEY`, and
 * `default_model = "astra/<id>"`; a `[desktop] provider_access` list gets `"astra"` appended. (Port of ReasonixClientAdapter.)
 */
export class ReasonixClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.Reasonix;

  private get dir(): string {
    const home = this.env.getEnvironmentVariable('REASONIX_HOME');
    if (home) return home;
    const app = this.env.getEnvironmentVariable('APPDATA');
    if (this.env.os === 'windows' && app) return path.join(app, 'reasonix');
    return this.env.combine('.reasonix');
  }

  private get configFile(): string {
    return path.join(this.dir, 'config.toml');
  }

  private get envFile(): string {
    return path.join(this.dir, '.env');
  }

  protected get markerFile(): string {
    return this.configFile;
  }

  protected get markerKey(): string {
    return SELECTOR;
  }

  protected override isTableKeyPath(keyPath: string): boolean {
    return keyPath === SELECTOR;
  }

  protected override get enabledWarnings(): string[] {
    return [
      'Reasonix reads its settings at start-up: start a new Reasonix process to load this. A project or session model can still override the Executor and Planner.',
    ];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('reasonix');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no reasonix executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile, this.envFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const raw = FileClientAdapter.raw;
    const hints = ClientAdapterBase.modelHints(ctx);
    const entries = ClientAdapterBase.modelEntries(ctx);
    const models = entries.map((e) => raw(e.id)).join(', ');
    const overrides: string[] = [];
    for (const { id } of entries) {
      const info = hints.get(id);
      const parts: string[] = [];
      if (ClientAdapterBase.boolHint(info?.reasoning) === false) parts.push('reasoning_protocol = "none"');
      const vision = ClientAdapterBase.boolHint(info?.vision);
      if (vision !== null) parts.push('vision = ' + (vision ? 'true' : 'false'));
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) parts.push('context_window = ' + context);
      const output = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (output !== null) parts.push('max_output_tokens = ' + output);
      if (parts.length > 0) overrides.push(`${raw(id)} = { ${parts.join(', ')} }`);
    }
    const selected = ctx.model ? ctx.model : entries.length > 0 ? entries[0]!.id : null;
    const entry: Record<string, string> = {
      name: raw(PROVIDER_ID),
      kind: raw('openai'),
      base_url: raw(FileClientAdapter.gateway(ctx.gatewayBaseUrl)),
      api_key_env: raw(REASONIX_KEY_VARIABLE),
      models: `[${models}]`,
    };
    if (selected !== null) entry.default = raw(selected);
    entry.reasoning_protocol = raw('openai');
    entry.no_proxy = 'true';
    if (overrides.length > 0) entry.model_overrides = `{ ${overrides.join(', ')} }`;

    const changes: ConfigChange[] = [
      this.tomlEntries(file, SELECTOR, [entry]),
      this.envSet(this.envFile, REASONIX_KEY_VARIABLE, ctx.localKey),
    ];
    if (ctx.model) changes.push(this.tomlSet(file, 'default_model', raw(`${PROVIDER_ID}/${ctx.model}`)));
    const access = this.currentValue(file, 'toml', ACCESS_PATH);
    const widened = access === null ? null : appendToArray(access, PROVIDER_ID);
    if (widened !== null) changes.push(this.tomlSet(file, ACCESS_PATH, widened));
    return this.buildPlan(changes);
  }
}

/** The raw TOML array `array` with `value` added; null when it holds it already or is no array. */
export function appendToArray(array: string, value: string): string | null {
  const text = array.trim();
  if (!text.startsWith('[') || !text.endsWith(']')) return null;
  const quoted = FileClientAdapter.raw(value);
  const inner = text.slice(1, -1).trim().replace(/,+\s*$/, '').trimEnd();
  if (inner.includes(quoted)) return null;
  return inner.length === 0 ? `[${quoted}]` : `[${inner}, ${quoted}]`;
}
