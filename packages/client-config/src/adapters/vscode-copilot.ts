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
import { JsoncEditor, toJsonText } from '../editing/jsonc-editor.js';
import type { ClientEnvironment } from '../env.js';
import { ClientKinds } from '../domain/kinds.js';
import { ApiProtocols, type ApiProtocol } from '../domain/protocols.js';

const GROUP_NAME = 'Astra';
const DEFAULT_CONTEXT_WINDOW = 128_000;
const DEFAULT_MAX_OUTPUT_TOKENS = 16_384;
const GROUP_PATH = JsoncEditor.selector(['vendor', 'customendpoint'], ['name', GROUP_NAME]);
const MANAGED_KEYS = ['apiType', 'models'];

/** The VS Code build a VsCodeCopilotClientAdapter configures. */
export interface VsCodeFlavour {
  kind: string;
  userDirName: string;
  executable: string;
  warning?: string | null;
}

export const VsCodeFlavours = {
  stable: {
    kind: ClientKinds.VsCodeCopilot,
    userDirName: 'Code',
    executable: 'code',
  } as VsCodeFlavour,
  insiders: {
    kind: ClientKinds.VsCodeInsiders,
    userDirName: 'Code - Insiders',
    executable: 'code-insiders',
  } as VsCodeFlavour,
  /** VSCodium disables Copilot and does not ship it (Copilot Chat is not on Open VSX); Astra only writes the endpoint file. */
  vscodium: {
    kind: ClientKinds.VsCodium,
    userDirName: 'VSCodium',
    executable: 'codium',
    warning:
      'VSCodium ships without GitHub Copilot Chat: it needs "chat.disableAIFeatures": false, a custom product.json and a side-loaded ' +
      "Copilot Chat extension before the Astra models appear (see VSCodium's docs/ext-github-copilot.md). Astra only writes the endpoint file.",
  } as VsCodeFlavour,
};

/**
 * GitHub Copilot Chat in VS Code (Coexist): adds a bring-your-own-key "Custom Endpoint" model group to
 * `chatLanguageModels.json` in the VS Code user directory, owned as a whole (table), with one model per bound model
 * carrying its native apiType and gateway URL and the local key as a per-model Authorization header. Insiders and
 * VSCodium are served through a flavour. (Port of VsCodeCopilotClientAdapter.)
 */
export class VsCodeCopilotClientAdapter extends ClientAdapterBase {
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  constructor(
    env: ClientEnvironment,
    store: IClientConfigStateStore,
    private readonly flavour: VsCodeFlavour = VsCodeFlavours.stable,
  ) {
    super(env, store);
  }

  get kind(): string {
    return this.flavour.kind;
  }

  private get userDir(): string {
    switch (this.env.os) {
      case 'osx':
        return this.env.combine('Library', 'Application Support', this.flavour.userDirName, 'User');
      case 'windows': {
        const appData = this.env.getEnvironmentVariable('APPDATA');
        return path.join(
          appData ? appData : this.env.combine('AppData', 'Roaming'),
          this.flavour.userDirName,
          'User',
        );
      }
      default: {
        const xdg = this.env.getEnvironmentVariable('XDG_CONFIG_HOME');
        return path.join(xdg ? xdg : this.env.combine('.config'), this.flavour.userDirName, 'User');
      }
    }
  }

  private get configFile(): string {
    return path.join(this.userDir, 'chatLanguageModels.json');
  }

  detect(): ClientDetection {
    const dir = this.userDir;
    if (this.env.directoryExists(dir))
      return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath(this.flavour.executable);
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no ${this.flavour.executable} executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    if (this.currentValue(file, 'json', GROUP_PATH, 'table') === null)
      changes.push(
        new ConfigChange(
          file,
          'json',
          GROUP_PATH,
          null,
          toJsonText({ name: GROUP_NAME, vendor: 'customendpoint' }),
          { kind: 'table' },
        ),
      );
    changes.push(this.key('apiType', ClientAdapterBase.jsonString('chat-completions')));
    changes.push(this.key('models', toJsonText(this.models(ctx))));
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    for (const key of MANAGED_KEYS) {
      const keyPath = `${GROUP_PATH}.${key}`;
      if (this.store.get(this.kind, file, keyPath) === null) continue;
      const current = this.currentValue(file, 'json', keyPath);
      if (current !== null) changes.push(new ConfigChange(file, 'json', keyPath, current, null));
    }
    // The group Astra created goes last, together with whatever VS Code stored inside it.
    if (this.store.get(this.kind, file, GROUP_PATH)?.originalAbsent === true)
      changes.push(
        new ConfigChange(
          file,
          'json',
          GROUP_PATH,
          this.currentValue(file, 'json', GROUP_PATH, 'table'),
          null,
          {
            kind: 'table',
          },
        ),
      );
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const keyPath = `${GROUP_PATH}.models`;
    const entry = this.store.get(this.kind, this.configFile, keyPath);
    const enabled =
      entry !== null &&
      equalsValue(
        'json',
        this.currentValue(this.configFile, 'json', keyPath),
        entry.appliedValueJson,
      );
    return this.buildStatus(detection, enabled, this.flavour.warning ? [this.flavour.warning] : []);
  }

  protected override isTableKeyPath(keyPath: string): boolean {
    return keyPath === GROUP_PATH;
  }

  private key(key: string, valueJson: string): ConfigChange {
    const keyPath = `${GROUP_PATH}.${key}`;
    return new ConfigChange(
      this.configFile,
      'json',
      keyPath,
      this.currentValue(this.configFile, 'json', keyPath),
      valueJson,
    );
  }

  /** One Custom Endpoint model per bound model, with limits from the model catalog where known. */
  private models(ctx: EnableContext): unknown[] {
    const root = ctx.gatewayBaseUrl.replace(/\/+$/, '');
    const protocols = this.modelProtocols(ctx, ApiProtocols.openAIChat);
    const details = ClientAdapterBase.modelHints(ctx);
    const models: unknown[] = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = details.get(id);
      let maxOutput =
        ClientAdapterBase.positiveLong(info?.maxOutputTokens) ?? DEFAULT_MAX_OUTPUT_TOKENS;
      const context = ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT_WINDOW;
      if (maxOutput >= context) maxOutput = Math.max(1, Math.floor(context / 4));
      const protocol: ApiProtocol = protocols.get(id)!;
      const [apiType, urlPath] =
        protocol === 'openai-responses'
          ? ['responses', '/v1/responses']
          : protocol === 'anthropic'
            ? ['messages', '/v1/messages']
            : ['chat-completions', '/v1/chat/completions'];
      const model: Record<string, unknown> = {
        id,
        name: name ?? id,
        url: root + urlPath,
        apiType,
        toolCalling: true,
        vision: ClientAdapterBase.boolHint(info?.vision) ?? false,
        thinking: ClientAdapterBase.boolHint(info?.reasoning) ?? false,
        maxInputTokens: context - maxOutput,
        maxOutputTokens: maxOutput,
        requestHeaders: { Authorization: 'Bearer ' + ctx.localKey },
      };
      if (protocol === 'openai-responses') model.zeroDataRetentionEnabled = true;
      models.push(model);
    }
    return models;
  }
}
