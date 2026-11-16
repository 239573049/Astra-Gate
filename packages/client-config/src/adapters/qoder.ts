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
import { JObj, compactJson, jsonDeepEquals, parseJsonc, toJsonText, type JV } from '../editing/jsonc-editor.js';
import type { ConfigFileFormat } from '../types.js';
import { ConfigFileClientAdapter } from './config-file-adapter.js';

const PROVIDER_ID = 'astra';
const PROVIDER_PATH = `providers.${PROVIDER_ID}`;
const OWNED_FIELDS = ['protocol', 'baseUrl', 'apiKey', 'model', 'models'];

/** The Qoder build a QoderClientAdapter configures (Qoder and Qoder CN share the settings layout). */
export interface QoderFlavour {
  kind: string;
  /** The settings folder under home (`.qoder`, `.qoder-cn`). */
  dirName: string;
  /** The variable that moves the settings folder. */
  envVariable: string;
  /** The command on PATH. */
  executable: string;
}

export const QoderFlavours = {
  Qoder: { kind: ClientKinds.Qoder, dirName: '.qoder', envVariable: 'QODER_CONFIG_DIR', executable: 'qodercli' },
  QoderCn: { kind: ClientKinds.QoderCn, dirName: '.qoder-cn', envVariable: 'QODERCN_CONFIG_DIR', executable: 'qoderclicn' },
} as const satisfies Record<string, QoderFlavour>;

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

/** Whether the provider still holds what Astra wrote in the fields Astra owns; extras and the display name are not compared. */
function ownedPartsEqual(currentText: string | null, appliedJson: string | null): boolean {
  if (currentText === null || appliedJson === null) return currentText === null && appliedJson === null;
  const current = parseObject(currentText);
  const applied = parseObject(appliedJson);
  if (!current || !applied) return false;
  for (const field of OWNED_FIELDS) {
    const wanted = member(applied, field);
    if (wanted === undefined) continue;
    const now = member(current, field);
    if (now === undefined || !jsonDeepEquals(wanted, now)) return false;
  }
  return true;
}

/**
 * Qoder's CLI (and Qoder CN; Coexist): `providers.astra` (an openai provider with the bound models) in
 * `$QODER_CONFIG_DIR/settings.json` (`~/.qoder`) and, when a model is chosen, `model.name` = `astra/<id>`. What Qoder
 * adds to the provider stays. (Port of QoderClientAdapter.)
 */
export class QoderClientAdapter extends ConfigFileClientAdapter {
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  constructor(
    env: ConstructorParameters<typeof ConfigFileClientAdapter>[0],
    store: ConstructorParameters<typeof ConfigFileClientAdapter>[1],
    private readonly flavour: QoderFlavour,
  ) {
    super(env, store);
  }

  get kind(): string {
    return this.flavour.kind;
  }

  private get dir(): string {
    return this.absoluteEnv(this.flavour.envVariable) ?? this.env.combine(this.flavour.dirName);
  }

  private get settingsFile(): string {
    return path.join(this.dir, 'settings.json');
  }

  detect(): ClientDetection {
    const exe = this.env.findOnPath(this.flavour.executable);
    if (exe) return ClientDetections.found(null, `executable ${exe}`);
    if (this.hasCliSettings()) return ClientDetections.found(null, `config ${this.settingsFile}`);
    for (const status of ['.qoder-app-status.json', '.qoder-app-canary-status.json', '.qoder-app-dev-status.json'])
      if (this.env.fileExists(path.join(this.dir, status))) return ClientDetections.found(null, `app status in ${this.dir}`);
    return ClientDetections.notFound(`no ${this.settingsFile} and no ${this.flavour.executable} executable on PATH`);
  }

  private hasCliSettings(): boolean {
    const text = this.env.readTextOrNull(this.settingsFile);
    const settings = text === null ? null : parseObject(text);
    return settings !== null && settings.entries.some(([k]) => k !== 'enabledPlugins');
  }

  configPaths(): string[] {
    return [this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.settingsFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const entries = ClientAdapterBase.modelEntries(ctx);
    const models: Array<Record<string, unknown>> = [];
    for (const { id, name } of entries) {
      const info = hints.get(id);
      const capabilities: Record<string, unknown> = { tools: true, vision: ClientAdapterBase.boolHint(info?.vision) ?? false };
      if (ClientAdapterBase.boolHint(info?.reasoning) === true) {
        capabilities.thinking = {
          modes: ['enabled'],
          supportsEffort: true,
          supportedEffortLevels: ['low', 'medium', 'high'],
          requiresBudgetForEnabled: false,
        };
      }
      const model: Record<string, unknown> = { model: id, displayName: name ?? id, capabilities };
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) model.contextWindow = context;
      const output = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (output !== null) model.maxOutputTokens = output;
      models.push(model);
    }
    const defaultModel = ctx.model ? ctx.model : (entries[0]?.id ?? null);
    const provider: Record<string, unknown> = {
      displayName: 'Astra',
      protocol: 'openai',
      baseUrl: ConfigFileClientAdapter.v1(ctx),
      apiKey: ctx.localKey,
    };
    if (defaultModel !== null) provider.model = defaultModel;
    provider.models = models;

    const before = this.currentValue(file, 'json', PROVIDER_PATH);
    // What the user or Qoder added to the provider Astra wrote stays; a provider Astra never wrote is replaced.
    const current = this.store.get(this.kind, file, PROVIDER_PATH) !== null ? parseObject(before) : null;
    if (current) {
      const name = member(current, 'displayName');
      if (typeof name === 'string' && name.trim().length > 0) provider.displayName = name;
      for (const [key, value] of current.entries)
        if (!OWNED_FIELDS.includes(key) && key !== 'displayName' && !(key in provider))
          provider[key] = JSON.parse(compactJson(value));
    }
    const changes: ConfigChange[] = [
      new ConfigChange(file, 'json', PROVIDER_PATH, before, toJsonText(provider), { stillOurs: ownedPartsEqual }),
    ];
    if (ctx.model) changes.push(this.setKey(file, 'model.name', ClientAdapterBase.jsonString(`${PROVIDER_ID}/${ctx.model}`)));
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes = this.trackedRemovals().map((c) =>
      c.keyPath === PROVIDER_PATH
        ? new ConfigChange(c.file, c.format, PROVIDER_PATH, c.before, null, { stillOurs: ownedPartsEqual })
        : c,
    );
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    return this.buildStatus(this.detect(), this.applied(this.settingsFile, PROVIDER_PATH), [
      'Qoder reads its settings as a session starts, and offers custom providers only on a plan with BYOK.',
    ]);
  }

  protected override valueStillOurs(
    file: string,
    keyPath: string,
    format: ConfigFileFormat,
    current: string | null,
    appliedJson: string | null,
  ): boolean {
    return keyPath === PROVIDER_PATH
      ? ownedPartsEqual(current, appliedJson)
      : super.valueStillOurs(file, keyPath, format, current, appliedJson);
  }
}
