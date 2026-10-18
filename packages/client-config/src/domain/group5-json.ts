import type { ClientConfigStateEntry, IClientConfigStateStore } from '../contracts.js';
import { JNum, JObj, parseJsonc, type JV } from '../editing/jsonc-editor.js';

/** Small JsonNode-like helpers over the editor's ordered value model (group 5 adapters). */

export const jobj = (entries: [string, JV][] = []): JObj => new JObj(entries);

export const jnum = (n: number): JNum => new JNum(String(n));

/** The member's value, or null when absent (like a JsonObject indexer). */
export function getMember(o: JObj | null | undefined, key: string): JV {
  const found = o?.entries.find(([k]) => k === key);
  return found ? found[1] : null;
}

export const hasMember = (o: JObj, key: string): boolean => o.entries.some(([k]) => k === key);

/** `o[key] = value`: replaces in place, else appends. */
export function setMember(o: JObj, key: string, value: JV): void {
  const i = o.entries.findIndex(([k]) => k === key);
  if (i >= 0) o.entries[i] = [key, value];
  else o.entries.push([key, value]);
}

export function removeMember(o: JObj, key: string): void {
  const i = o.entries.findIndex(([k]) => k === key);
  if (i >= 0) o.entries.splice(i, 1);
}

export function cloneJv(v: JV): JV {
  if (v === null || typeof v !== 'object') return v;
  if (v instanceof JNum) return new JNum(v.raw);
  if (Array.isArray(v)) return v.map(cloneJv);
  return new JObj(v.entries.map(([k, x]) => [k, cloneJv(x)]));
}

export const asObj = (v: JV | undefined): JObj | null => (v instanceof JObj ? v : null);

/** The string a value holds, or null when it is not a string. */
export const textOf = (v: JV | undefined): string | null => (typeof v === 'string' ? v : null);

/** Parses JSON text into an object; null when absent, malformed or not an object. */
export function parseObject(json: string | null, strict = false): JObj | null {
  if (json === null) return null;
  try {
    return asObj(parseJsonc(json, { comments: !strict, trailingCommas: !strict }).value);
  } catch {
    return null;
  }
}

/** The state entries of a client in the order the C# store lists them: file, then key path (ordinal). */
export function listState(store: IClientConfigStateStore, kind: string): ClientConfigStateEntry[] {
  const cmp = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
  return store.list(kind).sort((a, b) => cmp(a.filePath, b.filePath) || cmp(a.keyPath, b.keyPath));
}

/** The text without trailing slashes (`TrimEnd('/')`). */
export const trimSlashes = (s: string): string => s.replace(/\/+$/, '');
