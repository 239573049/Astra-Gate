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
import { EditorException } from '../editing/errors.js';
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { listState } from './shell-rc.js';

const INSTANCE_ID = 'astra';
const INSTANCE_PATH = `instances.${INSTANCE_ID}`;
const MODEL_ID = /^[a-z0-9][a-z0-9._:/-]*(?:\[1m\])?$/i;

/** OpenMausBot's default fleet (server/instance-fleets.ts at 516ca75): the engines it runs while its file saves none. */
const DEFAULT_FLEET = `{
  "grok": {"driver": "grokAgent"},
  "kimi": {"driver": "kimiAgent"},
  "droid": {"driver": "droidAgent"},
  "cursor": {"driver": "cursorAgent"},
  "githubCopilot": {"driver": "copilotAgent"},
  "commandCode": {"driver": "commandCodeAgent"},
  "claude": {"driver": "claudeAgent"},
  "codex": {"driver": "codex"},
  "chatgpt": {"driver": "codex", "displayName": "ChatGPT plan", "config": {"authMode": "chatgpt-plan"}},
  "antigravity": {"driver": "antigravityAgent"},
  "opencodeGo": {"driver": "opencodeGo"},
  "openaiCompat": {"driver": "openai-compat"},
  "mistral": {"driver": "mistral"},
  "cerebras": {"driver": "cerebras"},
  "groq": {"driver": "groq"},
  "greenference": {"driver": "greenference"},
  "meta": {"driver": "meta"},
  "deepseek": {"driver": "deepseekAgent"},
  "museCode": {"driver": "museCode"},
  "openai": {"driver": "openai-compat", "displayName": "OpenAI", "access": "api", "icon": {"kind": "preset", "preset": "openai"},
    "config": {"url": "https://api.openai.com/v1", "apiKeyEnv": "OMB_OPENAI_API_KEY", "catalog": "openai"}},
  "claudeApi": {"driver": "claudeAgent", "displayName": "Claude (API key)", "access": "api", "icon": {"kind": "preset", "preset": "anthropic"},
    "config": {"requireApiKey": true}},
  "xaiApi": {"driver": "grok", "displayName": "xAI", "access": "api", "icon": {"kind": "preset", "preset": "xai"}},
  "openrouter": {"driver": "openai-compat", "displayName": "OpenRouter", "access": "api", "icon": {"kind": "preset", "preset": "openrouter"},
    "config": {"url": "https://openrouter.ai/api/v1", "apiKeyEnv": "OMB_OPENROUTER_API_KEY"}},
  "qwen": {"driver": "qwenAgent"},
  "hermes": {"driver": "hermesAgent"},
  "cline": {"driver": "clineAgent"},
  "pi": {"driver": "piAgent"}
}`;

const parse = (text: string | null): unknown => {
  if (text === null) return null;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return null;
  }
};
const isPlainObject = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

/**
 * OpenMausBot (milind-soni/OpenMausBot; Coexist): `config.json` in `OMB_DATA_DIR` (absolute), else `~/.openmausbot`. Adds the
 * `claudeAgent` engine `instances.astra` (OpenMausBot's own "Claude endpoint" shape); with no saved `instances` map it writes
 * the default fleet beside it. (Port of OpenMausBotClientAdapter.)
 */
export class OpenMausBotClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.OpenMausBot;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dir(): string {
    const dir = this.env.getEnvironmentVariable('OMB_DATA_DIR')?.trim();
    return dir && path.isAbsolute(dir) ? dir : this.env.combine('.openmausbot');
  }

  private get configFile(): string {
    return path.join(this.dir, 'config.json');
  }

  detect(): ClientDetection {
    const dir = this.dir;
    return this.env.directoryExists(dir)
      ? ClientDetections.found(null, `data folder ${dir}`)
      : ClientDetections.notFound(`no ${dir}`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const ids: string[] = [];
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) if (MODEL_ID.test(id) && !ids.includes(id)) ids.push(id);
    if (ids.length === 0) throw new EditorException('OpenMausBot takes no model id Astra could list: pick models for it first.');
    const key = ctx.localKey;
    const config: Record<string, unknown> = {
      configDir: path.join(this.dir, 'providers', INSTANCE_ID),
      requireApiKey: true,
      endpointModels: ids,
    };
    const instancesText = this.currentValue(file, 'json', 'instances');
    const instances = instancesText === null ? null : parse(instancesText);
    if (instancesText !== null && !isPlainObject(instances))
      throw new EditorException(`${file}: instances is not a map of engines, so Astra leaves it alone.`);
    // The Claude Code the user gave OpenMausBot (the first claudeAgent with a binary that is no endpoint) carries over.
    if (isPlainObject(instances)) {
      for (const id of Object.keys(instances).sort(ordinal)) {
        const inst = instances[id];
        if (id === INSTANCE_ID || !isPlainObject(inst) || inst.driver !== 'claudeAgent') continue;
        const cfg = inst.config;
        if (!isPlainObject(cfg) || 'endpointModels' in cfg) continue;
        if (typeof cfg.cli === 'string' && cfg.cli !== '') {
          config.cli = cfg.cli;
          break;
        }
      }
    }
    const model = ctx.model && ids.includes(ctx.model) ? ctx.model : ids[0]!;
    const instance = {
      driver: 'claudeAgent',
      displayName: 'Astra',
      access: 'custom',
      config,
      environment: {
        ANTHROPIC_BASE_URL: ctx.gatewayBaseUrl.replace(/\/+$/, ''),
        ANTHROPIC_AUTH_TOKEN: key,
        // Claude Code's own sign-in check needs a key it knows, as on OpenMausBot's endpoints.
        ANTHROPIC_API_KEY: key,
        ANTHROPIC_MODEL: model,
      },
    };
    // Once Astra has written the fleet itself, the whole map stays its change (the fleet is one recorded value).
    const wroteFleet = this.store.get(this.kind, file, 'instances') !== null;
    const hasInstances = isPlainObject(instances) && Object.keys(instances).length > 0;
    if (hasInstances && !wroteFleet) {
      return this.buildPlan([
        new ConfigChange(file, 'json', INSTANCE_PATH, this.currentValue(file, 'json', INSTANCE_PATH), toJsonText(instance)),
      ]);
    }
    // No saved map: the map is the whole fleet, so it starts as OpenMausBot's default one.
    const fleet: Record<string, unknown> =
      wroteFleet && hasInstances ? { ...(instances as Record<string, unknown>) } : (JSON.parse(DEFAULT_FLEET) as Record<string, unknown>);
    fleet[INSTANCE_ID] = instance;
    return this.buildPlan([
      new ConfigChange(file, 'json', 'instances', this.currentValue(file, 'json', 'instances'), toJsonText(fleet)),
    ]);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    for (const entry of listState(this.store, this.kind)) {
      const current = this.currentValue(entry.filePath, 'json', entry.keyPath);
      if (current !== null) changes.push(new ConfigChange(entry.filePath, 'json', entry.keyPath, current, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled =
      this.stillOurs(this.configFile, 'json', INSTANCE_PATH) || this.stillOurs(this.configFile, 'json', 'instances');
    return this.buildStatus(
      detection,
      enabled,
      enabled
        ? [
            'OpenMausBot reads its engines when it starts and writes back the ones it holds when you change one in it: quit OpenMausBot before changing this, then open it and pick Astra as a bot\'s engine.',
          ]
        : [],
    );
  }
}
