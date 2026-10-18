import {
  ConfigChange,
  ConfigChangePlan,
  type ClientAvailability,
  type ClientDetection,
  type ClientMode,
  type ClientStatus,
  type ConfigChangeKind,
  type ConfigRepair,
  type EnableContext,
  type FileDiff,
  type IClientAdapter,
  type IClientConfigStateStore,
  type JsonObject,
} from './contracts.js';
import { equalsValue } from './config-value-codec.js';
import { createOps } from './config-editor-ops.js';
import { DiffTool } from './editing/diff-tool.js';
import { encodeString } from './editing/jsonc-editor.js';
import { findProblem } from './editing/strict-json.js';
import type { ClientEnvironment } from './env.js';
import { clientProtocolsFor, parseApiProtocol, protocolOverrides, type ApiProtocol } from './domain/protocols.js';
import type { ConfigFileFormat } from './types.js';

/** Shared plumbing for client adapters: config file IO, plan building with diffs, and inspection. */
export abstract class ClientAdapterBase implements IClientAdapter {
  constructor(
    protected readonly env: ClientEnvironment,
    protected readonly store: IClientConfigStateStore,
  ) {}

  abstract readonly kind: string;
  abstract readonly mode: ClientMode;
  abstract readonly availability: ClientAvailability;

  get unavailableReason(): string | null {
    return null;
  }

  abstract detect(): ClientDetection;
  abstract configPaths(): string[];
  abstract planEnable(ctx: EnableContext): ConfigChangePlan;
  abstract planDisable(): ConfigChangePlan;
  abstract planPurge(): ConfigChangePlan;
  abstract inspect(): ClientStatus;

  // ---------------------------------------------------------------- helpers for subclasses

  /** The primary config file path (first existing candidate, else the first candidate). */
  protected primaryConfigPath(candidates: readonly string[]): string {
    return candidates.find((c) => this.env.fileExists(c)) ?? candidates[0]!;
  }

  protected readFile(path: string): { text: string; bom: boolean } {
    const read = this.env.readTextWithBom(path);
    return read ? { text: read.text, bom: read.hadBom } : { text: '', bom: false };
  }

  /** Builds the plan and computes a unified diff per affected file by simulating the changes. */
  protected buildPlan(changes: readonly ConfigChange[]): ConfigChangePlan {
    const diffs: FileDiff[] = [];
    const warnings: string[] = [];
    for (const [file, group] of new ConfigChangePlan(this.kind, changes, []).byFile()) {
      const ops = createOps(group[0]!.format, this.readFile(file).text);
      const before = ops.text;
      for (const change of group) ops.apply(change, warnings);
      if (ops.text !== before) diffs.push({ file, unifiedDiff: DiffTool.unified(file, before, ops.text) });
    }
    return new ConfigChangePlan(this.kind, changes, diffs);
  }

  /** Current value of a key through the same machinery the applier uses. */
  protected currentValue(
    file: string,
    format: ConfigFileFormat,
    keyPath: string,
    kind: ConfigChangeKind = 'key',
  ): string | null {
    return createOps(format, this.readFile(file).text).read(
      new ConfigChange(file, format, keyPath, null, null, { kind }),
    );
  }

  /** TOML key paths that were recorded as table entries (subclasses with table entries override this). */
  protected isTableKeyPath(_keyPath: string): boolean {
    return false;
  }

  /**
   * Keys the client itself rewrites in normal use (e.g. the model the user picks in its UI). A changed value
   * there is not drift. Restore still treats it like any edited value (left alone unless forced).
   */
  protected isClientManagedKey(_file: string, _keyPath: string): boolean {
    return false;
  }

  /**
   * Whether the current value of a key (source text) is still what Astra wrote (`appliedJson`). Exact by default;
   * an adapter whose client rewrites parts of an Astra-owned value in normal use compares only the parts Astra
   * owns, and hands the same comparison to restores through ConfigChange.stillOurs.
   */
  protected valueStillOurs(
    _file: string,
    _keyPath: string,
    format: ConfigFileFormat,
    current: string | null,
    appliedJson: string | null,
  ): boolean {
    return equalsValue(format, current, appliedJson);
  }

  /** JSON config files the client reads with a strict JSON parser (comments / trailing commas break it). */
  protected strictJsonFiles(): string[] {
    return [];
  }

  private findRepairs(): ConfigRepair[] {
    const repairs: ConfigRepair[] = [];
    for (const file of this.strictJsonFiles()) {
      const text = this.env.readTextOrNull(file);
      const found = text === null ? null : findProblem(text);
      if (found) repairs.push({ file, problem: found.problem, line: found.line });
    }
    return repairs;
  }

  /** The file format of a config file, derived from its name. */
  protected static formatOfPath(path: string): ConfigFileFormat {
    const name = path.split(/[\\/]/).pop() ?? path;
    const lower = path.toLowerCase();
    if (['.zshrc', '.bashrc', '.zprofile', '.bash_profile'].includes(name)) return 'shellblock';
    if (lower.endsWith('.toml')) return 'toml';
    if (lower.endsWith('.yaml') || lower.endsWith('.yml')) return 'yaml';
    if (lower.endsWith('.json') || lower.endsWith('.jsonc')) return 'json';
    if (path.includes('.env')) return 'env';
    return 'json';
  }

  /** Builds the status card: enabled as given, drifted keys from state comparisons. */
  protected buildStatus(detection: ClientDetection, enabled: boolean, warnings: string[]): ClientStatus {
    const driftedKeys: string[] = [];
    const appliedKeys: string[] = [];
    for (const entry of this.store.list(this.kind)) {
      const format = ClientAdapterBase.formatOfPath(entry.filePath);
      const kind: ConfigChangeKind = this.isTableKeyPath(entry.keyPath) ? 'table' : 'key';
      const current = this.currentValue(entry.filePath, format, entry.keyPath, kind);
      if (this.valueStillOurs(entry.filePath, entry.keyPath, format, current, entry.appliedValueJson))
        appliedKeys.push(entry.keyPath);
      else if (!this.isClientManagedKey(entry.filePath, entry.keyPath)) driftedKeys.push(entry.keyPath);
    }
    return {
      clientKind: this.kind,
      detection,
      enabled,
      driftedKeys,
      appliedKeys,
      warnings,
      repairs: this.findRepairs(),
    };
  }

  /** Shorthand for a string as JSON text. */
  protected static jsonString(value: string): string {
    return encodeString(value);
  }

  /**
   * The bound provider's models from the "models" extra (`{"…": {"id": …, "name": …}}`), in order and without
   * duplicates; the selected model is appended when the list does not contain it.
   */
  protected static modelEntries(ctx: EnableContext): Array<{ id: string; name: string | null }> {
    const result: Array<{ id: string; name: string | null }> = [];
    for (const value of Object.values(ctx.extraObject('models') ?? {})) {
      if (!isObject(value)) continue;
      const id = typeof value.id === 'string' ? value.id : null;
      if (!id || result.some((e) => e.id === id)) continue;
      const name = typeof value.name === 'string' && value.name !== '' ? value.name : null;
      result.push({ id, name });
    }
    if (ctx.model && result.every((e) => e.id !== ctx.model)) result.push({ id: ctx.model, name: null });
    return result;
  }

  /**
   * Per-model capability hints from the "models" extra (contextWindow, maxOutputTokens, vision, reasoning),
   * keyed by model id; ids without hints are absent.
   */
  protected static modelHints(ctx: EnableContext): Map<string, JsonObject> {
    const hints = new Map<string, JsonObject>();
    for (const value of Object.values(ctx.extraObject('models') ?? {}))
      if (isObject(value) && typeof value.id === 'string' && value.id !== '' && !hints.has(value.id))
        hints.set(value.id, value);
    return hints;
  }

  /**
   * The protocol each listed model is configured with: the user's choice for it (extras "protocolOverrides")
   * when the client can speak it, else the first of its native protocols (the "protocols" hint, preferred first)
   * the client can speak, else `fallback` — the client's default, which the gateway translates.
   */
  protected modelProtocols(
    ctx: EnableContext,
    fallback: ApiProtocol,
    supported?: readonly ApiProtocol[],
    accepts?: (modelId: string, protocol: ApiProtocol) => boolean,
  ): Map<string, ApiProtocol> {
    const protocolsOfClient = supported ?? clientProtocolsFor(this.kind);
    const hints = ClientAdapterBase.modelHints(ctx);
    const overrides = protocolOverrides(ctx.extras);
    const result = new Map<string, ApiProtocol>();
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) {
      const usable = (p: ApiProtocol) => protocolsOfClient.includes(p) && (accepts?.(id, p) ?? true);
      const chosen = overrides.get(id);
      if (chosen && usable(chosen)) {
        result.set(id, chosen);
        continue;
      }
      const protocols = hints.get(id)?.protocols;
      let native: ApiProtocol | null = null;
      if (Array.isArray(protocols))
        for (const p of protocols) {
          const parsed = typeof p === 'string' ? parseApiProtocol(p) : null;
          if (parsed && usable(parsed)) {
            native = parsed;
            break;
          }
        }
      result.set(id, native ?? fallback);
    }
    return result;
  }

  /** A positive integer hint (e.g. contextWindow), or null when missing or not a positive integer. */
  static positiveLong(node: unknown): number | null {
    return typeof node === 'number' && Number.isInteger(node) && node > 0 ? node : null;
  }

  /** A boolean hint (e.g. vision), or null when missing or not a boolean. */
  static boolHint(node: unknown): boolean | null {
    return typeof node === 'boolean' ? node : null;
  }

  /** True when Astra recorded a value for the key and the file still holds exactly that value. */
  protected stillOurs(
    file: string,
    format: ConfigFileFormat,
    keyPath: string,
    kind: ConfigChangeKind = 'key',
  ): boolean {
    const entry = this.store.get(this.kind, file, keyPath);
    return (
      entry !== null &&
      this.valueStillOurs(file, keyPath, format, this.currentValue(file, format, keyPath, kind), entry.appliedValueJson)
    );
  }

  /** The string a JSON value text holds, or null when it is absent or not a JSON string. */
  protected static stringOf(jsonText: string | null): string | null {
    if (jsonText === null) return null;
    try {
      const v: unknown = JSON.parse(jsonText);
      return typeof v === 'string' ? v : null;
    } catch {
      return null;
    }
  }
}

const isObject = (v: unknown): v is JsonObject => v !== null && typeof v === 'object' && !Array.isArray(v);
