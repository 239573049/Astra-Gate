import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import { ConfigChange, type ConfigChangeKind, type EnableContext } from '../contracts.js';
import { compactJson, parseJsonc } from '../editing/jsonc-editor.js';

/** Structural equality of two plain JSON values (key order does not matter). */
export function jsonEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a) && Array.isArray(b)) return a.length === b.length && a.every((x, i) => jsonEqual(x, b[i]));
  const ka = Object.keys(a as object);
  const kb = Object.keys(b as object);
  if (ka.length !== kb.length) return false;
  return ka.every(
    (k) => k in (b as object) && jsonEqual((a as Record<string, unknown>)[k], (b as Record<string, unknown>)[k]),
  );
}

/** Parses JSON (comments and trailing commas allowed) into a plain object; null when absent, malformed or not an object. */
export function parsePlainObject(json: string | null): Record<string, unknown> | null {
  if (json === null) return null;
  try {
    const v: unknown = JSON.parse(compactJson(parseJsonc(json).value));
    return v !== null && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/**
 * Plumbing for the adapters that write whole keys (or owned array rows) into a few known files: a `ConfigChange` of one
 * key, the removals that undo everything this client recorded, and the "enabled" test on one anchor key.
 * (Port of ConfigFileClientAdapter.)
 */
export abstract class ConfigFileClientAdapter extends ClientAdapterBase {
  /** Sets one key of `file` (format from its extension) to `valueJson`. */
  protected setKey(file: string, keyPath: string, valueJson: string, kind: ConfigChangeKind = 'key'): ConfigChange {
    const format = ClientAdapterBase.formatOfPath(file);
    return new ConfigChange(file, format, keyPath, this.currentValue(file, format, keyPath, kind), valueJson, { kind });
  }

  /** Removes one key of `file`, or null when it is not there. */
  protected removeKey(file: string, keyPath: string, kind: ConfigChangeKind = 'key'): ConfigChange | null {
    const format = ClientAdapterBase.formatOfPath(file);
    const current = this.currentValue(file, format, keyPath, kind);
    return current === null ? null : new ConfigChange(file, format, keyPath, current, null, { kind });
  }

  /** A removal for every key this client recorded that is still present, in recording order. */
  protected trackedRemovals(include?: (file: string, keyPath: string) => boolean): ConfigChange[] {
    const changes: ConfigChange[] = [];
    for (const entry of this.store.list(this.kind)) {
      if (include && !include(entry.filePath, entry.keyPath)) continue;
      const kind: ConfigChangeKind = this.isTableKeyPath(entry.keyPath) ? 'table' : 'key';
      const change = this.removeKey(entry.filePath, entry.keyPath, kind);
      if (change) changes.push(change);
    }
    return changes;
  }

  /** Whether the key Astra recorded is still in the file exactly as Astra wrote it. */
  protected applied(file: string, keyPath: string): boolean {
    return this.stillOurs(file, ClientAdapterBase.formatOfPath(file), keyPath);
  }

  /** The first absolute path among the named environment variables (a leading `~` stands for home). */
  protected absoluteEnv(...names: string[]): string | null {
    for (const name of names) {
      let value = this.env.getEnvironmentVariable(name)?.trim();
      if (!value) continue;
      if (value === '~' || value.startsWith('~/') || value.startsWith('~\\'))
        value = this.env.homeDirectory + value.slice(1);
      if (path.isAbsolute(value) || /^[A-Za-z]:[\\/]/.test(value)) return value.replace(/[\\/]+$/, '');
    }
    return null;
  }

  /** The user's config directory: `XDG_CONFIG_HOME` when set (Linux), `~/.config` otherwise. */
  protected get xdgConfigHome(): string {
    const xdg = this.env.getEnvironmentVariable('XDG_CONFIG_HOME');
    return xdg ? xdg : this.env.combine('.config');
  }

  /** The roaming application-data folder of Windows (`%APPDATA%`). */
  protected get appData(): string {
    const app = this.env.getEnvironmentVariable('APPDATA');
    return app ? app : this.env.combine('AppData', 'Roaming');
  }

  /** Where a desktop app keeps its data (macOS Application Support, `%APPDATA%`, `$XDG_CONFIG_HOME`). */
  protected appSupportDir(...names: string[]): string {
    switch (this.env.os) {
      case 'osx':
        return path.join(this.env.homeDirectory, 'Library', 'Application Support', ...names);
      case 'windows':
        return path.join(this.appData, ...names);
      default:
        return path.join(this.xdgConfigHome, ...names);
    }
  }

  /** The URL of the gateway's `/v1` root. */
  protected static v1(ctx: EnableContext): string {
    return ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1';
  }

  /** A macOS app bundle among the machine's application folders, or null. */
  protected findApp(...names: string[]): string | null {
    for (const dir of this.env.applicationDirectories)
      for (const name of names) {
        const candidate = path.join(dir, name);
        if (this.env.directoryExists(candidate)) return candidate;
      }
    return null;
  }
}
