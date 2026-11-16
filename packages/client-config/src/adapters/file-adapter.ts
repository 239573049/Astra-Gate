import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  type ClientStatus,
  type ConfigChangePlan,
} from '../contracts.js';
import { JsoncEditor, toJsonText } from '../editing/jsonc-editor.js';
import { EditorException } from '../editing/errors.js';
import { tomlString } from '../editing/toml-editor.js';
import type { ConfigFileFormat } from '../types.js';

export const PROVIDER_ID = 'astra';

const cmp = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

/**
 * The shared plumbing of the file-backed Coexist clients added from magpie (batch 3), the port of FileClientAdapter: an adapter
 * writes keys, tables or array entries and the disable plan is "give back everything the state store recorded"; the applier
 * skips a value the user changed since (drift) and restores the original (or its absence) of the rest, byte for byte.
 */
export abstract class FileClientAdapter extends ClientAdapterBase {
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  /** The file holding the key whose recorded value tells whether the client is enabled. */
  protected abstract get markerFile(): string;
  /** The key (or table, or array-entry selector) whose recorded value tells whether the client is enabled. */
  protected abstract get markerKey(): string;
  /** Warnings shown while the client is enabled. */
  protected get enabledWarnings(): string[] {
    return [];
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    const entries = this.store
      .list(this.kind)
      .slice()
      .sort((a, b) => cmp(a.filePath, b.filePath) || cmp(a.keyPath, b.keyPath));
    // Fields first, then the tables Astra created (so a table goes whole once its fields are back).
    for (const entry of entries.filter((e) => !this.isTableKeyPath(e.keyPath))) {
      const format = ClientAdapterBase.formatOfPath(entry.filePath);
      const current = this.currentValue(entry.filePath, format, entry.keyPath);
      // A value Astra removed (recorded with no applied value) has no current value but is still given back.
      if (current !== null || !entry.originalAbsent)
        changes.push(new ConfigChange(entry.filePath, format, entry.keyPath, current, null));
    }
    for (const entry of entries.filter((e) => this.isTableKeyPath(e.keyPath) && e.originalAbsent)) {
      const format = ClientAdapterBase.formatOfPath(entry.filePath);
      const current = this.currentValue(entry.filePath, format, entry.keyPath, 'table');
      if (current !== null) changes.push(new ConfigChange(entry.filePath, format, entry.keyPath, current, null, { kind: 'table' }));
    }
    return this.buildPlan(changes);
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const isTable = this.isTableKeyPath(this.markerKey);
    const format = ClientAdapterBase.formatOfPath(this.markerFile);
    const enabled =
      this.store.get(this.kind, this.markerFile, this.markerKey) !== null &&
      (isTable
        ? this.currentValue(this.markerFile, format, this.markerKey, 'table') !== null
        : this.stillOurs(this.markerFile, format, this.markerKey));
    return this.buildStatus(detection, enabled, enabled ? this.enabledWarnings : []);
  }

  // ---------------------------------------------------------------- change builders

  protected jsonSet(file: string, keyPath: string, value: unknown): ConfigChange {
    const text = typeof value === 'string' ? ClientAdapterBase.jsonString(value) : toJsonText(value);
    return new ConfigChange(file, 'json', keyPath, this.currentValue(file, 'json', keyPath), text);
  }

  protected yamlSet(file: string, keyPath: string, value: unknown): ConfigChange {
    return new ConfigChange(file, 'yaml', keyPath, this.currentValue(file, 'yaml', keyPath), toJsonText(value));
  }

  protected envSet(file: string, key: string, value: string): ConfigChange {
    return new ConfigChange(file, 'env', key, this.currentValue(file, 'env', key), ClientAdapterBase.jsonString(value));
  }

  /** A TOML key set to raw TOML source. */
  protected tomlSet(file: string, keyPath: string, raw: string): ConfigChange {
    return new ConfigChange(file, 'toml', keyPath, this.currentValue(file, 'toml', keyPath), ClientAdapterBase.jsonString(raw));
  }

  /** Takes out a key the client holds today (null when there is none); a restore puts it back. */
  protected drop(file: string, format: ConfigFileFormat, keyPath: string): ConfigChange | null {
    const current = this.currentValue(file, format, keyPath);
    return current !== null ? new ConfigChange(file, format, keyPath, current, null) : null;
  }

  /** An array-of-tables selector change: `entries` (key to raw TOML value) replace every entry the selector names. */
  protected tomlEntries(file: string, selector: string, entries: Array<Record<string, string>>): ConfigChange {
    return new ConfigChange(file, 'toml', selector, this.currentValue(file, 'toml', selector, 'table'), toJsonText(entries), {
      kind: 'table',
    });
  }

  /**
   * Creates the TOML table with all fields, or, when it already exists, refreshes only the differing fields. A null field is
   * optional and currently not wanted: left out of a new table, and taken out of an existing one only when Astra put it there.
   */
  protected tableChanges(file: string, tablePath: string, fields: Array<[string, string | null]>): ConfigChange[] {
    if (this.currentValue(file, 'toml', tablePath, 'table') === null) {
      const items: Record<string, string> = {};
      for (const [key, raw] of fields) if (raw !== null) items[key] = raw;
      return [new ConfigChange(file, 'toml', tablePath, null, toJsonText(items), { kind: 'table' })];
    }
    const result: ConfigChange[] = [];
    const tableIsOurs = this.store.get(this.kind, file, tablePath)?.originalAbsent === true;
    for (const [key, raw] of fields) {
      const p = `${tablePath}.${key}`;
      const before = this.currentValue(file, 'toml', p);
      if (raw === null) {
        if (before !== null && (tableIsOurs || this.store.get(this.kind, file, p) !== null))
          result.push(new ConfigChange(file, 'toml', p, before, null));
      } else if (before !== raw) result.push(new ConfigChange(file, 'toml', p, before, ClientAdapterBase.jsonString(raw)));
    }
    return result;
  }

  static raw(value: string): string {
    return tomlString(value);
  }

  static gateway(gatewayBaseUrl: string): string {
    return gatewayBaseUrl.replace(/\/+$/, '') + '/v1';
  }

  /** Whether `id` can be the value of an array-element selector. */
  static canSelect(key: string, id: string): boolean {
    try {
      JsoncEditor.selector([key, id]);
      return true;
    } catch (e) {
      if (e instanceof EditorException) return false;
      throw e;
    }
  }

  /**
   * The alias of every model id: `prefix` plus the id with everything but letters, digits, '_', '-' and the characters in `keep`
   * turned into '-' (a TOML bare key; the editor writes no quoted table names).
   */
  static aliases(prefix: string, ids: string[], keep = ''): Map<string, string> {
    const aliases = new Map<string, string>();
    const taken = new Set<string>();
    for (const id of ids) {
      let base = prefix;
      for (const c of id) base += /^[A-Za-z0-9_-]$/.test(c) || keep.includes(c) ? c : '-';
      let alias = base;
      for (let n = 2; taken.has(alias); n++) alias = `${base}-${n}`;
      taken.add(alias);
      aliases.set(id, alias);
    }
    return aliases;
  }

  /** A leading `~` in an environment value is the home directory. */
  protected expandHome(value: string): string {
    if (value.startsWith('~') && (value.length === 1 || value[1] === '/' || value[1] === '\\'))
      return value.length === 1 ? this.env.homeDirectory : path.join(this.env.homeDirectory, value.slice(2));
    return value;
  }
}
