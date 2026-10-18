import type { ConfigFileFormat } from './types.js';

/** A JSON object as the adapters exchange it ("extras", model hints). */
export type JsonObject = Record<string, unknown>;

/** Whether a config change targets a plain key or a whole container (TOML table, owned JSON element). */
export type ConfigChangeKind = 'key' | 'table';

export interface ConfigChangeOptions {
  kind?: ConfigChangeKind;
  /**
   * How a restore decides that the current value (source text) is still what Astra wrote (the recorded JSON):
   * undefined compares exactly. See ClientAdapterBase.valueStillOurs.
   */
  stillOurs?: (current: string | null, recorded: string | null) => boolean;
}

/** One key (or table) Astra plans to write, restore or remove in one client config file. */
export class ConfigChange {
  readonly kind: ConfigChangeKind;
  readonly stillOurs?: (current: string | null, recorded: string | null) => boolean;

  /**
   * @param file Absolute path of the config file.
   * @param format File format.
   * @param keyPath Dotted key path inside the file, e.g. "env.ANTHROPIC_BASE_URL".
   * @param before Value before the change as JSON text, or null when absent (TOML/.env: the JSON-encoded raw source).
   * @param after Value after the change (same encoding), or null to remove the key.
   */
  constructor(
    readonly file: string,
    readonly format: ConfigFileFormat,
    readonly keyPath: string,
    readonly before: string | null,
    readonly after: string | null,
    options: ConfigChangeOptions = {},
  ) {
    this.kind = options.kind ?? 'key';
    this.stillOurs = options.stillOurs;
  }

  toString(): string {
    return `${this.file.split(/[\\/]/).pop()}:${this.keyPath}: ${this.before ?? '<absent>'} -> ${this.after ?? '<absent>'}`;
  }
}

/** A per-file unified diff for the change preview. */
export interface FileDiff {
  file: string;
  unifiedDiff: string;
}

/** The complete, human-reviewable set of file modifications for an enable/disable/purge. */
export class ConfigChangePlan {
  constructor(
    readonly clientKind: string,
    readonly changes: readonly ConfigChange[],
    readonly diffs: readonly FileDiff[],
  ) {}

  /** Changes grouped by file, in first-appearance order. */
  byFile(): Array<[string, ConfigChange[]]> {
    const groups = new Map<string, ConfigChange[]>();
    for (const change of this.changes) {
      const list = groups.get(change.file);
      if (list) list.push(change);
      else groups.set(change.file, [change]);
    }
    return [...groups];
  }

  get isEmpty(): boolean {
    return this.changes.length === 0;
  }
}

/** How a client selects its AI provider. */
export type ClientMode = 'switch' | 'coexist';

/** Whether the adapter can actually configure the client today. */
export type ClientAvailability = 'available' | 'comingSoon';

/** Result of looking for a client installation. */
export interface ClientDetection {
  detected: boolean;
  version: string | null;
  detail: string | null;
}

export const ClientDetections = {
  notFound: (detail: string): ClientDetection => ({ detected: false, version: null, detail }),
  found: (version: string | null, detail: string | null): ClientDetection => ({
    detected: true,
    version,
    detail,
  }),
};

/** A config file the client reads as strict JSON but that holds JSONC syntax. */
export interface ConfigRepair {
  file: string;
  problem: string;
  line: number;
}

/** Enablement state of one client. */
export interface ClientStatus {
  clientKind: string;
  detection: ClientDetection;
  enabled: boolean;
  /** Keys Astra has taken over whose current value no longer matches what we wrote. */
  driftedKeys: string[];
  /** Tracked keys that are still applied. */
  appliedKeys: string[];
  warnings: string[];
  repairs: ConfigRepair[];
}

/** The parameters an enable operation writes into a client's configuration. */
export class EnableContext {
  /** Gateway base URL, e.g. "https://astra.corp.com" (no trailing slash). */
  readonly gatewayBaseUrl: string;
  /** The key written into the client (the company-issued token). */
  readonly localKey: string;
  readonly model: string | null;
  /** Client-specific extras: "models", "protocolOverrides", "options" … (see EnableContext.cs). */
  readonly extras: JsonObject | null;
  /** Model ids known to have a context window below 1M (Claude Code's context1m option). */
  readonly modelsWithoutContext1M: ReadonlySet<string> | null;

  constructor(init: {
    gatewayBaseUrl: string;
    localKey: string;
    model?: string | null;
    extras?: JsonObject | null;
    modelsWithoutContext1M?: ReadonlySet<string> | null;
  }) {
    this.gatewayBaseUrl = init.gatewayBaseUrl;
    this.localKey = init.localKey;
    this.model = init.model ?? null;
    this.extras = init.extras ?? null;
    this.modelsWithoutContext1M = init.modelsWithoutContext1M ?? null;
  }

  /** Returns a string extra value, or null. */
  extraString(name: string): string | null {
    const v = this.extras?.[name];
    return typeof v === 'string' ? v : null;
  }

  /** Returns a nested JSON object extra value, or null. */
  extraObject(name: string): JsonObject | null {
    const v = this.extras?.[name];
    return v !== null && typeof v === 'object' && !Array.isArray(v) ? (v as JsonObject) : null;
  }
}

/**
 * One client adapter: knows the client's config files, how to plan the enable/disable/purge changes (never
 * mutating anything), and how to inspect the current state. Execution is done by ClientConfigApplier.
 */
export interface IClientAdapter {
  readonly kind: string;
  readonly mode: ClientMode;
  readonly availability: ClientAvailability;
  readonly unavailableReason: string | null;
  detect(): ClientDetection;
  /** Absolute config file paths this adapter owns, honoring env overrides (CODEX_HOME etc.). */
  configPaths(): string[];
  planEnable(ctx: EnableContext): ConfigChangePlan;
  planDisable(): ConfigChangePlan;
  planPurge(): ConfigChangePlan;
  inspect(): ClientStatus;
}

// ---------------------------------------------------------------- apply / restore reports

/** One key applied by ClientConfigApplier.apply. */
export interface AppliedKey {
  filePath: string;
  keyPath: string;
  wasAbsent: boolean;
  beforeJson: string | null;
  afterJson: string | null;
}

export interface ApplyReport {
  clientKind: string;
  applied: AppliedKey[];
  warnings: string[];
}

/** Outcome for one key during a restore. */
export type RestoreKeyOutcome = 'restored' | 'drifted' | 'notTracked';

export interface RestoredKey {
  filePath: string;
  keyPath: string;
  outcome: RestoreKeyOutcome;
  detail?: string;
}

export interface RestoreReport {
  clientKind: string;
  keys: RestoredKey[];
  /** Files deleted because Astra had created them and they became empty again. */
  deletedFiles: string[];
  warnings: string[];
}

export const hasDrift = (report: RestoreReport): boolean =>
  report.keys.some((k) => k.outcome === 'drifted');

export interface BackupFileEntry {
  originalPath: string;
  existed: boolean;
  backupFile: string | null;
}

/** manifest.json of a backup directory. */
export interface BackupManifest {
  createdAt: string;
  entries: BackupFileEntry[];
}

export interface BackupInfo {
  clientKind: string;
  backupId: string;
  createdAt: string;
  isFirstWrite: boolean;
  files: BackupFileEntry[];
}

// ---------------------------------------------------------------- state

/**
 * The original and applied value of one key Astra wrote into a client config file. Values are JSON text;
 * originalAbsent means the key did not exist before we wrote it.
 */
export interface ClientConfigStateEntry {
  clientKind: string;
  filePath: string;
  /** Dotted key path inside the file, e.g. "env.ANTHROPIC_BASE_URL" or "model_providers.astra". */
  keyPath: string;
  originalAbsent: boolean;
  originalValueJson: string | null;
  appliedValueJson: string | null;
  appliedAt: string;
}

/** Persistence for ClientConfigStateEntry. */
export interface IClientConfigStateStore {
  list(clientKind: string): ClientConfigStateEntry[];
  get(clientKind: string, filePath: string, keyPath: string): ClientConfigStateEntry | null;
  /** Insert or update by (clientKind, filePath, keyPath). */
  upsert(entry: ClientConfigStateEntry): void;
  delete(clientKind: string, filePath: string, keyPath: string): void;
  deleteAll(clientKind: string): void;
}
