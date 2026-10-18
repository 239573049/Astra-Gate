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
import { JNum, JObj, compactJson, type JV } from '../editing/jsonc-editor.js';
import type { ClientEnvironment } from '../env.js';
import { ClientKinds } from '../domain/kinds.js';

const PROVIDER_ID = 'astra';

/** The OpenCode build an OpenCodeClientAdapter configures (OpenCode and its forks share the schema). */
export interface OpenCodeFlavour {
  kind: string;
  /** The directory under the XDG config home (`opencode`, `mimocode`). */
  dirName: string;
  /** The config file name without extension. */
  fileBase: string;
  /** The command on PATH. */
  executable: string;
  /** The directory lives under %LOCALAPPDATA% on Windows (not the XDG layout). */
  windowsLocalAppData?: boolean;
  /** An environment variable that relocates the client's paths in an undocumented way (warned about). */
  homeVariable?: string | null;
}

export const OpenCodeFlavours = {
  OpenCode: { kind: ClientKinds.OpenCode, dirName: 'opencode', fileBase: 'opencode', executable: 'opencode' },
  /** MiMo Code is an OpenCode fork: `~/.config/mimocode/mimocode.json(c)`, same provider schema. */
  MiMoCode: {
    kind: ClientKinds.MiMoCode,
    dirName: 'mimocode',
    fileBase: 'mimocode',
    executable: 'mimo',
    windowsLocalAppData: true,
    homeVariable: 'MIMOCODE_HOME',
  },
} as const satisfies Record<string, OpenCodeFlavour>;

const num = (n: number): JNum => new JNum(String(n));
const obj = (...entries: Array<[string, JV]>): JObj => new JObj(entries);

/**
 * OpenCode (Coexist): adds `provider.astra` (an @ai-sdk/openai-compatible provider) to `opencode.json` /
 * `opencode.jsonc` and optionally sets top-level `model` to `astra/<id>`. Models whose native protocol is
 * Responses or Anthropic name their own AI SDK package; per-model limits, modalities and the reasoning flag are
 * written when Astra knows them. MiMo Code is the same adapter with another flavour. (Port of OpenCodeClientAdapter.)
 */
export class OpenCodeClientAdapter extends ClientAdapterBase {
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  constructor(
    env: ClientEnvironment,
    store: IClientConfigStateStore,
    private readonly flavour: OpenCodeFlavour = OpenCodeFlavours.OpenCode,
  ) {
    super(env, store);
  }

  get kind(): string {
    return this.flavour.kind;
  }

  private get configDir(): string {
    const f = this.flavour;
    if (f.windowsLocalAppData && this.env.os === 'windows') {
      const local = this.env.getEnvironmentVariable('LOCALAPPDATA');
      if (local) return path.join(local, f.dirName);
    }
    const xdg = this.env.getEnvironmentVariable('XDG_CONFIG_HOME');
    return xdg ? path.join(xdg, f.dirName) : this.env.combine('.config', f.dirName);
  }

  private get configCandidates(): string[] {
    return [
      path.join(this.configDir, this.flavour.fileBase + '.json'),
      path.join(this.configDir, this.flavour.fileBase + '.jsonc'),
    ];
  }

  /** The existing config file (jsonc wins), or opencode.json when none exists yet. */
  private get configFile(): string {
    const jsonc = path.join(this.configDir, this.flavour.fileBase + '.jsonc');
    return this.env.fileExists(jsonc) ? jsonc : path.join(this.configDir, this.flavour.fileBase + '.json');
  }

  detect(): ClientDetection {
    const dir = this.configDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath(this.flavour.executable);
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no ${this.flavour.executable} executable on PATH`);
  }

  configPaths(): string[] {
    return this.configCandidates;
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [this.providerChange(file, ctx)];
    if (ctx.model) {
      const keyPath = 'model';
      const before = this.currentValue(file, 'json', keyPath);
      changes.push(
        new ConfigChange(file, 'json', keyPath, before, ClientAdapterBase.jsonString(`${PROVIDER_ID}/${ctx.model}`)),
      );
    }
    return this.buildPlan(changes);
  }

  /** Rewrites only `provider.astra.models` (used when the bound provider's model list changes). */
  planModelsRefresh(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const keyPath = `provider.${PROVIDER_ID}.models`;
    const before = this.currentValue(file, 'json', keyPath);
    const models = this.modelsJson(ctx);
    return this.buildPlan([new ConfigChange(file, 'json', keyPath, before, models === null ? null : compactJson(models))]);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    const providerPath = `provider.${PROVIDER_ID}`;
    const providerBefore = this.currentValue(file, 'json', providerPath);
    if (providerBefore !== null) changes.push(new ConfigChange(file, 'json', providerPath, providerBefore, null));
    const modelBefore = this.currentValue(file, 'json', 'model');
    if (modelBefore !== null && OpenCodeClientAdapter.isOurModelSelection(modelBefore))
      changes.push(new ConfigChange(file, 'json', 'model', modelBefore, null));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const file = this.configFile;
    const providerPath = `provider.${PROVIDER_ID}`;
    const entry = this.store.get(this.kind, file, providerPath);
    const enabled =
      entry !== null && equalsValue('json', this.currentValue(file, 'json', providerPath), entry.appliedValueJson);
    const warnings: string[] = [];
    const variable = this.flavour.homeVariable;
    if (variable && this.env.getEnvironmentVariable(variable))
      warnings.push(
        `${variable} is set; its effect on the config location is not documented, so Astra still uses ${this.configDir}.`,
      );
    return this.buildStatus(detection, enabled, warnings);
  }

  private providerChange(file: string, ctx: EnableContext): ConfigChange {
    const keyPath = `provider.${PROVIDER_ID}`;
    const entries: Array<[string, JV]> = [
      ['npm', '@ai-sdk/openai-compatible'],
      ['name', 'Astra'],
      [
        'options',
        obj(['baseURL', ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1'], ['apiKey', ctx.localKey]),
      ],
    ];
    const models = this.modelsJson(ctx);
    if (models !== null) entries.push(['models', models]);
    const before = this.currentValue(file, 'json', keyPath);
    return new ConfigChange(file, 'json', keyPath, before, compactJson(new JObj(entries)));
  }

  private modelsJson(ctx: EnableContext): JObj | null {
    const list = ctx.extraObject('models');
    if (list === null || Object.keys(list).length === 0) return null;
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    // A Map keeps ids in order (numeric-looking ids included) and replaces a repeated id in place, like JsonObject.
    const models = new Map<string, JV>();
    for (const m of Object.values(list)) {
      if (m === null || typeof m !== 'object' || Array.isArray(m)) continue;
      const hint = m as Record<string, unknown>;
      const id = typeof hint.id === 'string' ? hint.id : null;
      if (!id) continue;
      const entry: Array<[string, JV]> = [];
      const name = typeof hint.name === 'string' ? hint.name : null;
      if (name) entry.push(['name', name]);
      const protocol = protocols.get(id) ?? 'openai-chat';
      if (protocol === 'openai-responses' || protocol === 'anthropic')
        entry.push(['provider', obj(['npm', protocol === 'anthropic' ? '@ai-sdk/anthropic' : '@ai-sdk/openai'])]);
      const output = ClientAdapterBase.positiveLong(hint.maxOutputTokens);
      // limit needs both numbers; an unknown output stays OpenCode's default (0 = its 32k cap).
      const context = ClientAdapterBase.positiveLong(hint.contextWindow);
      if (context !== null) entry.push(['limit', obj(['context', num(context)], ['output', num(output ?? 0)])]);
      const vision = ClientAdapterBase.boolHint(hint.vision);
      const pdf = ClientAdapterBase.boolHint(hint.pdf);
      if (vision !== null || pdf !== null) {
        // Listing modalities replaces the default (text) entirely, and MiMo Code requires both lists.
        const input: JV[] = ['text'];
        if (vision === true) input.push('image');
        if (pdf === true) input.push('pdf');
        entry.push(['modalities', obj(['input', input], ['output', ['text']])]);
      }
      // Anthropic thinking budgets are derived from limit.output; without it they come out negative.
      const reasoning = ClientAdapterBase.boolHint(hint.reasoning);
      if (reasoning !== null && (!reasoning || protocol !== 'anthropic' || output !== null))
        entry.push(['reasoning', reasoning]);
      models.set(id, new JObj(entry));
    }
    return new JObj([...models]);
  }

  private static isOurModelSelection(modelJsonText: string): boolean {
    return ClientAdapterBase.stringOf(modelJsonText)?.startsWith(`${PROVIDER_ID}/`) === true;
  }
}
