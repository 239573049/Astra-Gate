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
import type { ConfigFileFormat } from '../types.js';
import { equalsValue } from '../config-value-codec.js';
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { listState } from './shell-rc.js';

const PROVIDER_ID = 'astra';
const ENCRYPTED = 'ENC:';

const parse = (text: string | null): unknown => {
  if (text === null) return null;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return null;
  }
};
const isPlainObject = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const encryptedKey = (current: string | null): boolean => {
  const s = parse(current);
  return typeof s === 'string' && s.startsWith(ENCRYPTED);
};

/**
 * QwenPaw (agentscope-ai/QwenPaw; Coexist): the custom provider file `<secret>/providers/custom/astra.json` (the secret folder is
 * `QWENPAW_SECRET_DIR`, the working folder's `.env`, else `<working folder>.secret`; the working folder is
 * `QWENPAW_WORKING_DIR`, else a legacy `~/.copaw`, else `~/.qwenpaw`) and the default agent's `active_model` in its
 * workspace's `agent.json`. An `ENC:` api_key (QwenPaw encrypts it at its next start) is not drift. (Port of QwenPawClientAdapter.)
 */
export class QwenPawClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.QwenPaw;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private tilde(p: string): string {
    if (p === '~') return this.env.homeDirectory;
    if (p.startsWith('~/') || p.startsWith('~\\')) return path.join(this.env.homeDirectory, p.slice(2));
    return p;
  }

  private get workingDir(): string {
    const dir = this.env.getEnvironmentVariable('QWENPAW_WORKING_DIR')?.trim();
    if (dir) return this.tilde(dir);
    const legacy = this.env.combine('.copaw');
    return this.env.directoryExists(legacy) ? legacy : this.env.combine('.qwenpaw');
  }

  private get secretDir(): string {
    const work = this.workingDir;
    let dir = this.env.getEnvironmentVariable('QWENPAW_SECRET_DIR')?.trim();
    if (!dir) dir = this.dotEnvValue(path.join(work, '.env'), 'QWENPAW_SECRET_DIR');
    return dir ? this.tilde(dir) : work + '.secret';
  }

  /** The value of `key` in a `.env` file as python-dotenv reads it (last one wins); empty when absent. */
  private dotEnvValue(file: string, key: string): string {
    const text = this.env.readTextOrNull(file);
    if (text === null) return '';
    let value = '';
    for (const raw of text.split('\n')) {
      let line = raw.trim();
      if (line.startsWith('export ')) line = line.slice(7).trim();
      const eq = line.indexOf('=');
      if (eq < 0 || line.slice(0, eq).trim() !== key) continue;
      let v = line.slice(eq + 1).trim();
      if (v.length >= 2 && (v[0] === '"' || v[0] === "'")) {
        const end = v.indexOf(v[0]!, 1);
        if (end >= 0) v = v.slice(1, end);
      } else {
        const hash = v.indexOf(' #');
        if (hash >= 0) v = v.slice(0, hash).trim();
      }
      value = v;
    }
    return value;
  }

  private get providerFile(): string {
    return path.join(this.secretDir, 'providers', 'custom', PROVIDER_ID + '.json');
  }

  private get agentWorkspace(): string {
    const work = this.workingDir;
    const text = this.env.readTextOrNull(path.join(work, 'config.json'));
    if (text !== null) {
      const root = parse(text);
      const dir = isPlainObject(root)
        ? (((root.agents as Record<string, unknown> | undefined)?.profiles as Record<string, unknown> | undefined)?.default as
            | Record<string, unknown>
            | undefined)?.workspace_dir
        : undefined;
      if (typeof dir === 'string' && dir.trim() !== '') return this.tilde(dir.trim());
    }
    return path.join(work, 'workspaces', 'default');
  }

  private get agentFile(): string {
    return path.join(this.agentWorkspace, 'agent.json');
  }

  detect(): ClientDetection {
    const work = this.workingDir;
    if (this.env.directoryExists(work)) return ClientDetections.found(null, `working folder ${work}`);
    const exe = this.env.findOnPath('qwenpaw');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${work} and no qwenpaw executable on PATH`);
  }

  configPaths(): string[] {
    return [this.providerFile, this.agentFile];
  }

  private agentUsesQwenPaw(): boolean {
    const backend = ClientAdapterBase.stringOf(this.currentValue(this.agentFile, 'json', 'backend'));
    return !backend || backend.trim() === 'qwenpaw';
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.providerFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const changes: ConfigChange[] = [];
    const set = (key: string, valueJson: string, onlyWhenAbsent = false): void => {
      const current = this.currentValue(file, 'json', key);
      if (onlyWhenAbsent && current !== null) return;
      changes.push(new ConfigChange(file, 'json', key, current, valueJson));
    };

    const extra: Array<Record<string, unknown>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const vision = ClientAdapterBase.boolHint(info?.vision) === true;
      const model: Record<string, unknown> = {
        id,
        name: name ?? id,
        source: 'user',
        supports_image: vision,
        supports_multimodal: vision,
        supports_tool_calling: true,
      };
      const window = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (window !== null && window >= 1000) {
        model.max_input_length = window;
        model.max_input_length_configured = true;
      }
      const output = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (output !== null) model.max_output_length = output;
      extra.push(model);
    }
    set('id', ClientAdapterBase.jsonString(PROVIDER_ID));
    set('name', ClientAdapterBase.jsonString('Astra'));
    set('base_url', ClientAdapterBase.jsonString(ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1'));
    // QwenPaw encrypts a plaintext key at its next start; that stays until the key rotates.
    const currentKey = ClientAdapterBase.stringOf(this.currentValue(file, 'json', 'api_key'));
    const keepEncrypted =
      currentKey !== null &&
      currentKey.startsWith(ENCRYPTED) &&
      this.store.get(this.kind, file, 'api_key')?.appliedValueJson === ClientAdapterBase.jsonString(ctx.localKey);
    if (!keepEncrypted) set('api_key', ClientAdapterBase.jsonString(ctx.localKey));
    set('chat_model', ClientAdapterBase.jsonString('OpenAIChatModel'));
    set('is_custom', 'true');
    set('enabled', 'true', true);
    set('support_model_discovery', 'false', true);
    set('support_connection_check', 'false', true);
    set('models', '[]', true);
    set('generate_kwargs', '{}', true);
    set('custom_headers', '{}', true);
    set('snapshot_schema_version', '2', true);
    set('extra_models', toJsonText(extra));

    const agentFile = this.agentFile;
    if (ctx.model && this.env.fileExists(agentFile) && this.agentUsesQwenPaw()) {
      const current = this.currentValue(agentFile, 'json', 'active_model');
      const existing = parse(current);
      const slot: Record<string, unknown> = isPlainObject(existing) ? { ...existing } : {};
      slot.provider_id = PROVIDER_ID;
      slot.model = ctx.model;
      changes.push(new ConfigChange(agentFile, 'json', 'active_model', current, toJsonText(slot)));
    }
    return this.buildPlan(changes);
  }

  protected override valueStillOurs(
    file: string,
    keyPath: string,
    format: ConfigFileFormat,
    current: string | null,
    appliedJson: string | null,
  ): boolean {
    return (keyPath === 'api_key' && encryptedKey(current)) || super.valueStillOurs(file, keyPath, format, current, appliedJson);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    for (const entry of listState(this.store, this.kind)) {
      const current = this.currentValue(entry.filePath, 'json', entry.keyPath);
      if (current === null) continue;
      changes.push(
        new ConfigChange(
          entry.filePath,
          'json',
          entry.keyPath,
          current,
          null,
          entry.keyPath === 'api_key'
            ? { stillOurs: (now, applied) => encryptedKey(now) || equalsValue('json', now, applied) }
            : {},
        ),
      );
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.stillOurs(this.providerFile, 'json', 'base_url');
    const warnings: string[] = [];
    if (enabled) {
      warnings.push(
        'QwenPaw reads its provider files once, at start-up: restart QwenPaw to use this (until then a turn on an Astra model fails with "Provider \'astra\' not found").',
      );
      const agentFile = this.agentFile;
      if (!this.env.fileExists(agentFile))
        warnings.push(
          `QwenPaw has no ${agentFile} yet: start QwenPaw once so it writes its agents' settings, then enable again to pick Astra's model for the default agent.`,
        );
      else if (!this.agentUsesQwenPaw())
        warnings.push(
          "The default agent runs on another backend, which does not use QwenPaw's models: switch it to QwenPaw's own backend.",
        );
    }
    return this.buildStatus(detection, enabled, warnings);
  }
}
