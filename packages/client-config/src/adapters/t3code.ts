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
import { toJsonText } from '../editing/jsonc-editor.js';
import type { ConfigFileFormat } from '../types.js';
import { ConfigFileClientAdapter, jsonEqual, parsePlainObject } from './config-file-adapter.js';

type Obj = Record<string, unknown>;

const INSTANCE_ID = 'astra';
const INSTANCE_PATH = `providerInstances.${INSTANCE_ID}`;
const OWNED_ENVIRONMENT = ['ANTHROPIC_BASE_URL', 'ANTHROPIC_AUTH_TOKEN'];
/** Switches that take Claude Code to a cloud provider past the gateway: never carried over from what the user set on the instance. */
const PROVIDER_SWITCHES = ['CLAUDE_CODE_USE_BEDROCK', 'CLAUDE_CODE_USE_VERTEX', 'CLAUDE_CODE_USE_FOUNDRY'];
const SEARCH_VARIABLE = 'ENABLE_TOOL_SEARCH';
const KNOWN = ['driver', 'displayName', 'enabled', 'environment', 'config'];

const isObj = (v: unknown): v is Obj => v !== null && typeof v === 'object' && !Array.isArray(v);

const environmentEntry = (name: string, value: string): Obj => ({ name, value, sensitive: false });

/** The picks T3 shows beside a custom model: without them it shows none and sends no effort. */
function capabilities(): Obj {
  return {
    optionDescriptors: [
      {
        id: 'effort',
        label: 'Reasoning',
        type: 'select',
        options: [
          { id: 'low', label: 'Low' },
          { id: 'medium', label: 'Medium', isDefault: true },
          { id: 'high', label: 'High' },
        ],
      },
      { id: 'thinking', label: 'Thinking', type: 'boolean', currentValue: true },
    ],
  };
}

function environmentValue(instance: Obj, name: string): string | null {
  const environment = instance.environment;
  if (!Array.isArray(environment)) return null;
  for (const entry of environment)
    if (isObj(entry) && entry.name === name) return typeof entry.value === 'string' ? entry.value : null;
  return null;
}

/** Whether the instance still holds what Astra wrote: the driver, Astra's gateway variables and the custom models. */
function ownedPartsEqual(currentText: string | null, appliedJson: string | null): boolean {
  if (currentText === null || appliedJson === null) return currentText === null && appliedJson === null;
  const current = parsePlainObject(currentText);
  const applied = parsePlainObject(appliedJson);
  if (!current || !applied) return false;
  if (!jsonEqual(applied.driver, current.driver)) return false;
  for (const variable of OWNED_ENVIRONMENT)
    if (environmentValue(applied, variable) !== environmentValue(current, variable)) return false;
  const a = isObj(applied.config) ? applied.config.customModels : undefined;
  const c = isObj(current.config) ? current.config.customModels : undefined;
  return jsonEqual(a, c);
}

/**
 * T3 Code (pingdotgg/t3code; Coexist): the provider instance `astra` (a `claudeAgent` pointed at the gateway, every bound
 * model one of its custom models) in `<T3CODE_HOME>/userdata/settings.json`. Port of T3CodeClientAdapter.
 */
export class T3CodeClientAdapter extends ConfigFileClientAdapter {
  readonly kind = ClientKinds.T3Code;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get home(): string {
    const value = this.env.getEnvironmentVariable('T3CODE_HOME')?.trim();
    if (!value) return this.env.combine('.t3');
    if (value === '~' || value.startsWith('~/') || value.startsWith('~\\')) return this.env.homeDirectory + value.slice(1);
    return value;
  }

  private get settingsFile(): string {
    return path.join(this.home, 'userdata', 'settings.json');
  }

  detect(): ClientDetection {
    if (this.env.directoryExists(this.home)) return ClientDetections.found(null, `config directory ${this.home}`);
    const app = this.findApp('T3 Code (Alpha).app', 'T3 Code.app');
    return app ? ClientDetections.found(null, `application ${app}`) : ClientDetections.notFound(`no ${this.home}`);
  }

  configPaths(): string[] {
    return [this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.settingsFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const custom: Obj[] = [];
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const model: Obj = { slug: id, name: name ?? id };
      if (ClientAdapterBase.boolHint(hints.get(id)?.reasoning) === true) model.capabilities = capabilities();
      custom.push(model);
    }
    const config: Obj = { customModels: custom };
    for (const key of ['binaryPath', 'homePath']) {
      // The user's own Claude instance, else the setting it was before T3 had instances.
      const value = this.textAt(file, `providerInstances.claudeAgent.config.${key}`) ?? this.textAt(file, `providers.claudeAgent.${key}`);
      if (value !== null && value.trim().length > 0) config[key] = value.trim();
    }
    const environment: Obj[] = [
      environmentEntry('ANTHROPIC_BASE_URL', ctx.gatewayBaseUrl.replace(/\/+$/, '')),
      environmentEntry('ANTHROPIC_AUTH_TOKEN', ctx.localKey),
      // tool search on: the Claude Code T3 runs may be one that sends every MCP tool's definition on each request
      environmentEntry(SEARCH_VARIABLE, 'true'),
    ];
    const instance: Obj = {
      driver: 'claudeAgent',
      displayName: 'Astra',
      enabled: true,
      environment,
      config,
    };

    const before = this.currentValue(file, 'json', INSTANCE_PATH);
    const current = this.store.get(this.kind, file, INSTANCE_PATH) !== null ? parsePlainObject(before) : null;
    if (current) this.carryOver(current, instance);
    return this.buildPlan([
      new ConfigChange(file, 'json', INSTANCE_PATH, before, toJsonText(instance), { stillOurs: ownedPartsEqual }),
    ]);
  }

  planDisable(): ConfigChangePlan {
    const changes = this.trackedRemovals().map((c) =>
      c.keyPath === INSTANCE_PATH
        ? new ConfigChange(c.file, c.format, INSTANCE_PATH, c.before, null, { stillOurs: ownedPartsEqual })
        : c,
    );
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    return this.buildStatus(this.detect(), this.applied(this.settingsFile, INSTANCE_PATH), [
      "Pick an Astra model in T3 Code's model picker: the Astra instance runs Claude Code on the gateway.",
    ]);
  }

  protected override valueStillOurs(
    file: string,
    keyPath: string,
    format: ConfigFileFormat,
    current: string | null,
    appliedJson: string | null,
  ): boolean {
    return keyPath === INSTANCE_PATH
      ? ownedPartsEqual(current, appliedJson)
      : super.valueStillOurs(file, keyPath, format, current, appliedJson);
  }

  private textAt(file: string, keyPath: string): string | null {
    return ClientAdapterBase.stringOf(this.currentValue(file, 'json', keyPath));
  }

  /** What the user set on the instance in T3 stays: its name, whether it is on, other environment variables, other config and top-level keys. */
  private carryOver(current: Obj, instance: Obj): void {
    if (typeof current.displayName === 'string' && current.displayName.trim().length > 0)
      instance.displayName = current.displayName;
    if (typeof current.enabled === 'boolean') instance.enabled = current.enabled;
    const environment = instance.environment as Obj[];
    if (Array.isArray(current.environment)) {
      for (const entry of current.environment) {
        if (!isObj(entry) || typeof entry.name !== 'string' || entry.name.length === 0) continue;
        const variable = entry.name;
        if (OWNED_ENVIRONMENT.includes(variable) || PROVIDER_SWITCHES.includes(variable)) continue;
        if (variable === SEARCH_VARIABLE)
          for (let i = environment.length - 1; i >= 0; i--) if (environment[i]!.name === SEARCH_VARIABLE) environment.splice(i, 1);
        environment.push(entry);
      }
    }
    const config = instance.config as Obj;
    if (isObj(current.config))
      for (const [key, value] of Object.entries(current.config))
        if (key !== 'customModels' && !(key in config)) config[key] = value;
    for (const [key, value] of Object.entries(current))
      if (!KNOWN.includes(key) && !(key in instance)) instance[key] = value;
  }
}
