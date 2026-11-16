import fs from 'node:fs';
import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { ClientKinds } from '../domain/kinds.js';
import { toJsonText } from '../editing/jsonc-editor.js';
import { ConfigFileClientAdapter } from './config-file-adapter.js';

const AGENT_NAME = 'Astra';
const PROVIDER_ID = 'astra';
const ENTRY_PATH = `agent_servers.${AGENT_NAME}`;

/** Which Air an AirClientAdapter configures: the standalone app, or the plugin in JetBrains IDEs. */
export interface AirFlavour {
  kind: string;
  ide: boolean;
}

export const AirFlavours = {
  App: { kind: ClientKinds.JetBrainsAir, ide: false },
  Plugin: { kind: ClientKinds.AirIde, ide: true },
} as const satisfies Record<string, AirFlavour>;

/**
 * JetBrains Air and the Air plugin of JetBrains IDEs (Coexist): an ACP agent `Astra` in `acp.json` that runs
 * `opencode acp` on `astra-opencode.json` (beside it), which holds Astra's provider, models and the start-up model.
 * (Port of AirClientAdapter.)
 */
export class AirClientAdapter extends ConfigFileClientAdapter {
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  constructor(
    env: ConstructorParameters<typeof ConfigFileClientAdapter>[0],
    store: ConstructorParameters<typeof ConfigFileClientAdapter>[1],
    private readonly flavour: AirFlavour,
  ) {
    super(env, store);
  }

  get kind(): string {
    return this.flavour.kind;
  }

  private get dir(): string {
    return this.flavour.ide ? this.env.combine('.jetbrains') : this.appSupportDir('JetBrains', 'Air');
  }

  private get acpFile(): string {
    return path.join(this.dir, 'acp.json');
  }

  private get openCodeConfigFile(): string {
    return path.join(this.dir, 'astra-opencode.json');
  }

  detect(): ClientDetection {
    if (!this.flavour.ide) {
      if (this.env.directoryExists(this.dir)) return ClientDetections.found(null, `config directory ${this.dir}`);
      const app = this.findApp('Air.app', 'JetBrains Air.app');
      return app ? ClientDetections.found(null, `application ${app}`) : ClientDetections.notFound(`no ${this.dir}`);
    }
    if (this.currentValue(this.acpFile, 'json', ENTRY_PATH) !== null)
      return ClientDetections.found(null, `agent in ${this.acpFile}`);
    const ide = this.ideSince(2026, 2);
    return ide
      ? ClientDetections.found(null, `JetBrains IDE ${ide}`)
      : ClientDetections.notFound('no JetBrains IDE of 2026.2 or later');
  }

  configPaths(): string[] {
    return [this.acpFile, this.openCodeConfigFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const entries = ClientAdapterBase.modelEntries(ctx);
    const model = ctx.model ? ctx.model : (entries[0]?.id ?? null);
    if (model === null) return this.buildPlan([]);
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Record<string, unknown> = {};
    for (const { id, name } of entries) {
      const info = hints.get(id);
      const entry: Record<string, unknown> = {};
      if (name !== null) entry.name = name;
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) entry.limit = { context, output: ClientAdapterBase.positiveLong(info?.maxOutputTokens) ?? 0 };
      const vision = ClientAdapterBase.boolHint(info?.vision);
      const pdf = ClientAdapterBase.boolHint(info?.pdf);
      if (vision !== null || pdf !== null) {
        const input = ['text'];
        if (vision === true) input.push('image');
        if (pdf === true) input.push('pdf');
        entry.modalities = { input, output: ['text'] };
      }
      const reasoning = ClientAdapterBase.boolHint(info?.reasoning);
      if (reasoning !== null) entry.reasoning = reasoning;
      models[id] = entry;
    }
    const provider = {
      npm: '@ai-sdk/openai-compatible',
      name: 'Astra',
      options: { baseURL: ConfigFileClientAdapter.v1(ctx), apiKey: ctx.localKey },
      models,
    };
    const reference = `${PROVIDER_ID}/${model}`;
    const agent = {
      command: this.openCodePath(),
      args: ['acp'],
      env: { OPENCODE_CONFIG: this.openCodeConfigFile },
    };
    const file = this.openCodeConfigFile;
    return this.buildPlan([
      this.setKey(file, '$schema', ClientAdapterBase.jsonString('https://opencode.ai/config.json')),
      this.setKey(file, 'model', ClientAdapterBase.jsonString(reference)),
      this.setKey(file, 'small_model', ClientAdapterBase.jsonString(reference)),
      this.setKey(file, 'enabled_providers', toJsonText([PROVIDER_ID])),
      this.setKey(file, `provider.${PROVIDER_ID}`, toJsonText(provider)),
      this.setKey(this.acpFile, ENTRY_PATH, toJsonText(agent)),
    ]);
  }

  planDisable(): ConfigChangePlan {
    return this.buildPlan(this.trackedRemovals());
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const enabled = this.applied(this.acpFile, ENTRY_PATH);
    const warnings: string[] = [];
    if (enabled && this.openCodeOnMachine() === null)
      warnings.push(
        "Air runs Astra's models through OpenCode's ACP server and OpenCode is not installed here: install OpenCode, then pick Astra in a new Air task.",
      );
    else if (enabled)
      warnings.push(
        this.flavour.ide
          ? "In your JetBrains IDE (2026.3, or 2026.2 with the Air plugin), pick Astra in Air's agent menu; restart the IDE if it is not listed yet."
          : "In Air, start a new task and pick Astra in its agent menu: Astra's models are in its model menu.",
      );
    return this.buildStatus(this.detect(), enabled, warnings);
  }

  /** The OpenCode Air is to start: the one on PATH, else the one OpenCode's installer puts in `~/.opencode/bin`, else the bare command. */
  private openCodePath(): string {
    return this.openCodeOnMachine() ?? 'opencode';
  }

  private openCodeOnMachine(): string | null {
    const onPath = this.env.findOnPath('opencode');
    if (onPath) return onPath;
    const name = this.env.os === 'windows' ? 'opencode.exe' : 'opencode';
    const installed = this.env.combine('.opencode', 'bin', name);
    return this.env.fileExists(installed) ? installed : null;
  }

  /** A JetBrains IDE config folder (`IntelliJIdea2026.3`, …) of year.minor or later, or null. */
  private ideSince(year: number, minor: number): string | null {
    const root = this.appSupportDir('JetBrains');
    let names: string[];
    try {
      names = fs
        .readdirSync(root, { withFileTypes: true })
        .filter((e) => e.isDirectory())
        .map((e) => e.name)
        .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
    } catch {
      return null;
    }
    for (const name of names) {
      const m = /^[A-Za-z]+(\d{4})\.(\d+)$/.exec(name);
      if (!m) continue;
      const y = Number(m[1]);
      const n = Number(m[2]);
      if (y > year || (y === year && n >= minor)) return name;
    }
    return null;
  }
}
