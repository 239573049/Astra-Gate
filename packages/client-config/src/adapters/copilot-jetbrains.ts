import fs from 'node:fs';
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
import {
  JObj,
  jsonDeepEquals,
  parseJsonc,
  toJsonText,
  type JV,
} from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import type { ConfigFileFormat } from '../types.js';
import { ConfigFileClientAdapter } from './config-file-adapter.js';

const PROVIDER_NAME = 'astra';
const CONFIG_KEY = `${PROVIDER_NAME}-provider-config`;
const API_KEY_KEY = `${PROVIDER_NAME}-api-key`;
const MODELS_KEY = `${PROVIDER_NAME}-models-config`;
const PLUGIN_FOLDER = 'github-copilot-intellij';
const DEFAULT_CONTEXT_WINDOW = 128_000;
const DEFAULT_MAX_OUTPUT_TOKENS = 16_000;

function parseObject(json: string | null): JObj | null {
  if (json === null) return null;
  try {
    const v = parseJsonc(json).value;
    return v instanceof JObj ? v : null;
  } catch {
    return null;
  }
}

const member = (o: JObj, key: string): JV | undefined => o.entries.find(([k]) => k === key)?.[1];

/** Whether the models config still holds what Astra wrote, apart from the per-model `isRegistered` flag Copilot's UI flips. */
function modelsStillOurs(current: string | null, applied: string | null): boolean {
  if (current === null || applied === null) return current === null && applied === null;
  const now = parseObject(current);
  const written = parseObject(applied);
  if (!now || !written || now.entries.length !== written.entries.length) return false;
  for (const [id, node] of written.entries) {
    const w = node instanceof JObj ? node : null;
    const n = member(now, id);
    if (!w || !(n instanceof JObj)) return false;
    for (const [key, value] of w.entries) {
      if (key === 'isRegistered') continue;
      const other = member(n, key);
      if (other === undefined || !jsonDeepEquals(value, other)) return false;
    }
  }
  return true;
}

/**
 * GitHub Copilot in JetBrains IDEs (`github-copilot-intellij`; Coexist): the provider `astra` as three keys of the Copilot
 * language server's `byok.json` (`astra-provider-config`, `astra-api-key`, `astra-models-config`). A model the user hid
 * (`isRegistered: false`) stays hidden through a re-apply. (Port of CopilotJetBrainsClientAdapter.)
 */
export class CopilotJetBrainsClientAdapter extends ConfigFileClientAdapter {
  readonly kind = ClientKinds.CopilotJetBrains;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get byokFile(): string {
    const xdg = this.env.getEnvironmentVariable('XDG_CONFIG_HOME');
    if (xdg && path.isAbsolute(xdg)) return path.join(xdg, 'github-copilot', 'byok.json');
    return this.env.os === 'windows'
      ? this.env.combine('AppData', 'Local', 'github-copilot', 'byok.json')
      : this.env.combine('.config', 'github-copilot', 'byok.json');
  }

  detect(): ClientDetection {
    const plugin = this.pluginInstalled();
    if (plugin) return ClientDetections.found(null, `plugin ${plugin}`);
    return this.currentValue(this.byokFile, 'json', CONFIG_KEY) !== null
      ? ClientDetections.found(null, `config ${this.byokFile}`)
      : ClientDetections.notFound(`no ${PLUGIN_FOLDER} plugin in a JetBrains IDE`);
  }

  /** The Copilot plugin among the IDEs' plugin folders (JetBrains and Android Studio), or null. */
  private pluginInstalled(): string | null {
    let root: string;
    let sub: string;
    if (this.env.os === 'osx') {
      root = this.env.combine('Library', 'Application Support');
      sub = 'plugins';
    } else if (this.env.os === 'windows') {
      root = this.appData;
      sub = 'plugins';
    } else {
      const data = this.env.getEnvironmentVariable('XDG_DATA_HOME');
      root = data ? data : this.env.combine('.local', 'share');
      sub = '';
    }
    for (const vendor of ['JetBrains', 'Google']) {
      const dir = path.join(root, vendor);
      let ides: string[];
      try {
        ides = fs
          .readdirSync(dir, { withFileTypes: true })
          .filter((e) => e.isDirectory())
          .map((e) => path.join(dir, e.name))
          .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
      } catch {
        continue;
      }
      for (const ide of ides) {
        const plugin = sub ? path.join(ide, sub, PLUGIN_FOLDER) : path.join(ide, PLUGIN_FOLDER);
        if (this.env.directoryExists(plugin)) return plugin;
      }
    }
    return null;
  }

  configPaths(): string[] {
    return [this.byokFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.byokFile;
    const url = ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1/chat/completions';
    const hidden = this.hiddenModels(file);
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Record<string, unknown> = {};
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const context = ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT_WINDOW;
      let output = ClientAdapterBase.positiveLong(info?.maxOutputTokens) ?? DEFAULT_MAX_OUTPUT_TOKENS;
      output = Math.min(output, Math.max(1, Math.floor(context / 4)));
      models[id] = {
        deploymentUrl: url,
        isRegistered: !hidden.has(id),
        isCustomModel: true,
        modelCapabilities: {
          name: name ?? id,
          maxInputTokens: context,
          maxOutputTokens: output,
          toolCalling: true,
          vision: ClientAdapterBase.boolHint(info?.vision) ?? false,
        },
      };
    }
    return this.buildPlan([
      this.setKey(file, CONFIG_KEY, toJsonText({ groupName: PROVIDER_NAME, apiType: 'chatCompletions' })),
      this.setKey(file, API_KEY_KEY, ClientAdapterBase.jsonString(ctx.localKey)),
      new ConfigChange(file, 'json', MODELS_KEY, this.currentValue(file, 'json', MODELS_KEY), toJsonText(models), {
        stillOurs: modelsStillOurs,
      }),
    ]);
  }

  planDisable(): ConfigChangePlan {
    const changes = this.trackedRemovals().map((c) =>
      c.keyPath === MODELS_KEY
        ? new ConfigChange(c.file, c.format, MODELS_KEY, c.before, null, { stillOurs: modelsStillOurs })
        : c,
    );
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    return this.buildStatus(this.detect(), this.applied(this.byokFile, API_KEY_KEY), [
      'Copilot lists the Astra models after the IDE restarts, and only while it is signed in to GitHub on a plan with Bring Your Own Key.',
    ]);
  }

  protected override valueStillOurs(
    file: string,
    keyPath: string,
    format: ConfigFileFormat,
    current: string | null,
    appliedJson: string | null,
  ): boolean {
    return keyPath === MODELS_KEY
      ? modelsStillOurs(current, appliedJson)
      : super.valueStillOurs(file, keyPath, format, current, appliedJson);
  }

  /** The model ids the user hid in Manage Models (`isRegistered: false`) in the provider's current models config. */
  private hiddenModels(file: string): Set<string> {
    const hidden = new Set<string>();
    const models = parseObject(this.currentValue(file, 'json', MODELS_KEY));
    if (!models) return hidden;
    for (const [id, node] of models.entries)
      if (node instanceof JObj && member(node, 'isRegistered') === false) hidden.add(id);
    return hidden;
  }
}
