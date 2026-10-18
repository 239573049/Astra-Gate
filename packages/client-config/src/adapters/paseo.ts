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
import { ClientKinds } from '../domain/kinds.js';
import { trimSlashes } from '../domain/group5-json.js';
import { toJsonText } from '../editing/jsonc-editor.js';

const PROVIDER_ID = 'astra';
const PROVIDER_PATH = `agents.providers.${PROVIDER_ID}`;
const CODEX_PROVIDER_PATH = `agents.providers.${PROVIDER_ID}-codex`;

/**
 * Paseo (paseo.sh; Coexist): adds `agents.providers.astra` (extends Paseo's `claude` provider) to
 * `$PASEO_HOME/config.json` (default `~/.paseo`), plus `agents.providers.astra-codex` (extends `codex`) while
 * models are Responses-native. (Port of PaseoClientAdapter.)
 */
export class PaseoClientAdapter extends ClientAdapterBase {
  static readonly providerId = PROVIDER_ID;

  readonly kind = ClientKinds.Paseo;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get homeDir(): string {
    const dir = this.env.getEnvironmentVariable('PASEO_HOME');
    return dir ? dir : this.env.combine('.paseo');
  }

  private get configFile(): string {
    return path.join(this.homeDir, 'config.json');
  }

  detect(): ClientDetection {
    const dir = this.homeDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('paseo');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no paseo executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const root = trimSlashes(ctx.gatewayBaseUrl);
    const provider: Record<string, unknown> = {
      extends: 'claude',
      label: 'Astra',
      description: 'Claude Code through the Astra gateway',
      env: { ANTHROPIC_BASE_URL: root, ANTHROPIC_AUTH_TOKEN: ctx.localKey },
      disallowedTools: ['WebSearch'],
    };
    const protocols = this.modelProtocols(ctx, 'anthropic');
    const entries = ClientAdapterBase.modelEntries(ctx);
    const codex = entries.filter((e) => protocols.get(e.id) === 'openai-responses');
    const claude = entries.filter((e) => protocols.get(e.id) !== 'openai-responses');
    const models = modelsJson(claude.length > 0 ? claude : entries, ctx.model);
    if (models.length > 0) provider.models = models;
    const changes: ConfigChange[] = [
      new ConfigChange(file, 'json', PROVIDER_PATH, this.currentValue(file, 'json', PROVIDER_PATH), toJsonText(provider)),
    ];
    const codexBefore = this.currentValue(file, 'json', CODEX_PROVIDER_PATH);
    if (codex.length > 0) {
      const codexProvider = {
        extends: 'codex',
        label: 'Astra (Codex)',
        description: 'Codex through the Astra gateway, for models served over the Responses API',
        env: { OPENAI_BASE_URL: root, OPENAI_API_KEY: ctx.localKey },
        models: modelsJson(codex, ctx.model),
      };
      changes.push(new ConfigChange(file, 'json', CODEX_PROVIDER_PATH, codexBefore, toJsonText(codexProvider)));
    } else if (codexBefore !== null && this.store.get(this.kind, file, CODEX_PROVIDER_PATH) !== null)
      changes.push(new ConfigChange(file, 'json', CODEX_PROVIDER_PATH, codexBefore, null));
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    const before = this.currentValue(file, 'json', PROVIDER_PATH);
    if (before !== null) changes.push(new ConfigChange(file, 'json', PROVIDER_PATH, before, null));
    if (this.store.get(this.kind, file, CODEX_PROVIDER_PATH) !== null) {
      const codex = this.currentValue(file, 'json', CODEX_PROVIDER_PATH);
      if (codex !== null) changes.push(new ConfigChange(file, 'json', CODEX_PROVIDER_PATH, codex, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.stillOurs(this.configFile, 'json', PROVIDER_PATH);
    return this.buildStatus(detection, enabled, []);
  }
}

/** Models as Paseo profile models: `[{id, label, optional isDefault}]`; the selected model (else the first) is the default. */
function modelsJson(
  entries: ReadonlyArray<{ id: string; name: string | null }>,
  selected: string | null,
): Array<Record<string, unknown>> {
  const defaultId = entries.some((e) => e.id === selected) ? selected : (entries[0]?.id ?? null);
  return entries.map(({ id, name }) => {
    const model: Record<string, unknown> = { id, label: name ?? id };
    if (id === defaultId) model.isDefault = true;
    return model;
  });
}
