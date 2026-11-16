import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChangePlan,
  type EnableContext,
  type IClientConfigStateStore,
} from '../contracts.js';
import { equalsValue } from '../config-value-codec.js';
import { toJsonText } from '../editing/jsonc-editor.js';
import { tomlString } from '../editing/toml-editor.js';
import type { ClientEnvironment } from '../env.js';
import { ClientKinds } from '../domain/kinds.js';
import { PiAiModels } from './pi-ai-models.js';

const PROVIDER_ID = 'astra';
const PROVIDER_PATH = `providers.${PROVIDER_ID}`;

/** Where a Pi fork keeps its start-up model. */
export type PiSettings = 'json' | 'toml' | 'aside';

/** The Pi build a PiClientAdapter configures (Pi and its forks share `models.json`). */
export interface PiFlavour {
  kind: string;
  /** The command on PATH, or null when its name is other tools' too (detection is the directory alone). */
  executable: string | null;
  agentDir(env: ClientEnvironment): string;
  settings: PiSettings;
}

/** The first of the named variables that holds an absolute path (a leading `~` stands for the home directory). */
function firstAbsolute(env: ClientEnvironment, ...names: string[]): string | null {
  for (const name of names) {
    let value = env.getEnvironmentVariable(name)?.trim();
    if (!value) continue;
    if (value === '~' || value.startsWith('~/') || value.startsWith('~\\')) value = env.homeDirectory + value.slice(1);
    if (path.isAbsolute(value)) return value.replace(/[/\\]+$/, '');
  }
  return null;
}

export const PiFlavours = {
  Pi: {
    kind: ClientKinds.Pi,
    executable: 'pi',
    agentDir: (env) => env.getEnvironmentVariable('PI_CODING_AGENT_DIR') || env.combine('.pi', 'agent'),
    settings: 'json',
  },
  /** OmO (omo-ai, engine senpi): `~/.omo/agent`, or an absolute OMO_CODING_AGENT_DIR / SENPI_CODING_AGENT_DIR. */
  OmO: {
    kind: ClientKinds.OmO,
    executable: 'omo',
    agentDir: (env) => firstAbsolute(env, 'OMO_CODING_AGENT_DIR', 'SENPI_CODING_AGENT_DIR') ?? env.combine('.omo', 'agent'),
    settings: 'json',
  },
  /** Prime Agent: `~/.prime/agent`, or PRIME_AGENT_CODING_AGENT_DIR. */
  PrimeAgent: {
    kind: ClientKinds.PrimeAgent,
    executable: 'prime-agent',
    agentDir: (env) => firstAbsolute(env, 'PRIME_AGENT_CODING_AGENT_DIR') ?? env.combine('.prime', 'agent'),
    settings: 'json',
  },
  /** mu (npm mu-agent): `~/.mu/agent` (`~/.kyrn/agent` while only that exists), or one of four variables. */
  Mu: {
    kind: ClientKinds.Mu,
    executable: null,
    agentDir: (env) =>
      firstAbsolute(env, 'MU_AGENT_DIR', 'KYRN_AGENT_DIR', 'MU_CODING_AGENT_DIR', 'KYRN_CODING_AGENT_DIR') ??
      (!env.directoryExists(env.combine('.mu')) && env.directoryExists(env.combine('.kyrn'))
        ? env.combine('.kyrn', 'agent')
        : env.combine('.mu', 'agent')),
    settings: 'json',
  },
  /** StepCode: `~/.stepcode` (STEPCODE_CONFIG_DIR names another folder under home, an absolute STEP_CODING_AGENT_DIR its parent). */
  StepCode: {
    kind: ClientKinds.StepCode,
    executable: null,
    agentDir: (env) => {
      const agent = env.getEnvironmentVariable('STEP_CODING_AGENT_DIR')?.trim();
      if (agent && path.isAbsolute(agent)) return path.dirname(agent.replace(/[/\\]+$/, ''));
      const custom = env.getEnvironmentVariable('STEPCODE_CONFIG_DIR')?.trim();
      return custom ? env.combine(custom) : env.combine('.stepcode');
    },
    settings: 'toml',
  },
  /** Aside: the first account's folder, `~/.aside/u/0`. */
  Aside: {
    kind: ClientKinds.Aside,
    executable: 'aside',
    agentDir: (env) => env.combine('.aside', 'u', '0'),
    settings: 'aside',
  },
} as const satisfies Record<string, PiFlavour>;

function tryObject(json: string): Record<string, unknown> | null {
  try {
    const node: unknown = JSON.parse(json);
    return node !== null && typeof node === 'object' && !Array.isArray(node) ? (node as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/**
 * Pi coding agent (Coexist): adds `providers.astra` (an `openai-completions` endpoint listing the bound
 * provider's models) to `<agent-dir>/models.json` and, when a model is chosen, sets `defaultProvider` /
 * `defaultModel` in `<agent-dir>/settings.json`. The agent directory is `~/.pi/agent` unless PI_CODING_AGENT_DIR
 * overrides it. Models whose native protocol is not Chat carry their own `api` (and base). The same adapter serves
 * Pi's forks through a flavour (OmO, Prime Agent, mu, StepCode, Aside). (Port of PiClientAdapter.)
 */
export class PiClientAdapter extends ClientAdapterBase {
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  constructor(
    env: ClientEnvironment,
    store: IClientConfigStateStore,
    private readonly flavour: PiFlavour = PiFlavours.Pi,
  ) {
    super(env, store);
  }

  get kind(): string {
    return this.flavour.kind;
  }

  private get agentDir(): string {
    return this.flavour.agentDir(this.env);
  }

  private get modelsFile(): string {
    return path.join(this.agentDir, 'models.json');
  }

  private get tomlSettings(): boolean {
    return this.flavour.settings === 'toml';
  }

  private get settingsFormat(): 'toml' | 'json' {
    return this.tomlSettings ? 'toml' : 'json';
  }

  private get settingsFile(): string {
    return path.join(this.agentDir, this.tomlSettings ? 'config.toml' : 'settings.json');
  }

  detect(): ClientDetection {
    const dir = this.agentDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    if (this.flavour.executable === null) return ClientDetections.notFound(`no ${dir}`);
    const exe = this.env.findOnPath(this.flavour.executable);
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no ${this.flavour.executable} executable on PATH`);
  }

  configPaths(): string[] {
    return [this.modelsFile, this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const baseUrlOfProvider = ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1';
    const provider: Record<string, unknown> = {
      baseUrl: baseUrlOfProvider,
      api: 'openai-completions',
      apiKey: ctx.localKey,
    };
    const hints = ClientAdapterBase.modelHints(ctx);
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const model: Record<string, unknown> = { id };
      if (name !== null) model.name = name;
      const protocol = protocols.get(id)!;
      if (protocol !== 'openai-chat') {
        model.api = PiAiModels.apiOf(protocol);
        const baseUrl = PiAiModels.baseUrlFor(ctx.gatewayBaseUrl, protocol);
        if (baseUrl !== baseUrlOfProvider) model.baseUrl = baseUrl;
      }
      PiAiModels.addHints(model, hints.get(id));
      models.push(model);
    }
    if (models.length > 0) provider.models = models;

    const changes: ConfigChange[] = [
      new ConfigChange(
        this.modelsFile,
        'json',
        PROVIDER_PATH,
        this.currentValue(this.modelsFile, 'json', PROVIDER_PATH),
        toJsonText(provider),
      ),
    ];
    if (ctx.model) {
      if (this.flavour.settings === 'aside') changes.push(this.asideDefaultModel(ctx.model));
      else {
        changes.push(this.settingsKey('defaultProvider', PROVIDER_ID));
        changes.push(this.settingsKey('defaultModel', ctx.model));
      }
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    const providerBefore = this.currentValue(this.modelsFile, 'json', PROVIDER_PATH);
    if (providerBefore !== null)
      changes.push(new ConfigChange(this.modelsFile, 'json', PROVIDER_PATH, providerBefore, null));
    // The startup selection is only ours while it still names the Astra provider.
    if (this.flavour.settings === 'aside') {
      const current = this.currentValue(this.settingsFile, 'json', 'defaultModel');
      if (current !== null && tryObject(current)?.provider === PROVIDER_ID)
        changes.push(new ConfigChange(this.settingsFile, 'json', 'defaultModel', current, null));
      return this.buildPlan(changes);
    }
    const format = this.settingsFormat;
    const defaultProvider = this.currentValue(this.settingsFile, format, 'defaultProvider');
    if (defaultProvider !== null && this.isProviderValue(defaultProvider)) {
      changes.push(new ConfigChange(this.settingsFile, format, 'defaultProvider', defaultProvider, null));
      const defaultModel = this.currentValue(this.settingsFile, format, 'defaultModel');
      if (defaultModel !== null && this.store.get(this.kind, this.settingsFile, 'defaultModel') !== null)
        changes.push(new ConfigChange(this.settingsFile, format, 'defaultModel', defaultModel, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const entry = this.store.get(this.kind, this.modelsFile, PROVIDER_PATH);
    const enabled =
      entry !== null &&
      equalsValue('json', this.currentValue(this.modelsFile, 'json', PROVIDER_PATH), entry.appliedValueJson);
    const warnings: string[] = [];
    if (enabled && this.flavour.settings === 'aside')
      warnings.push(
        'Aside keeps its settings in memory while it runs and writes them back: restart Aside so the model choice sticks.',
      );
    return this.buildStatus(detection, enabled, warnings);
  }

  private isProviderValue(source: string): boolean {
    return this.tomlSettings ? source === tomlString(PROVIDER_ID) : ClientAdapterBase.stringOf(source) === PROVIDER_ID;
  }

  private settingsKey(key: string, value: string): ConfigChange {
    return new ConfigChange(
      this.settingsFile,
      this.settingsFormat,
      key,
      this.currentValue(this.settingsFile, this.settingsFormat, key),
      ClientAdapterBase.jsonString(this.tomlSettings ? tomlString(value) : value),
    );
  }

  /** Aside's start-up model is one object, `defaultModel: {provider, modelId, thinkingLevel}`; its other members stay. */
  private asideDefaultModel(model: string): ConfigChange {
    const before = this.currentValue(this.settingsFile, 'json', 'defaultModel');
    const selection: Record<string, unknown> = (before !== null ? tryObject(before) : null) ?? {};
    selection.provider = PROVIDER_ID;
    selection.modelId = model;
    selection.thinkingLevel ??= 'medium';
    return new ConfigChange(this.settingsFile, 'json', 'defaultModel', before, toJsonText(selection));
  }
}
