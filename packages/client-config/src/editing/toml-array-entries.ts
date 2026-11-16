import { EditorException } from './errors';
import { newlineOf } from './text-tool';
import { validateToml } from './toml-editor';

/**
 * The entries of a TOML array of tables (`[[models]]`) that one client owns, found by a key they carry: the selector
 * `models[provider=astra]` names every `[[models]]` entry whose `provider` is the string `"astra"`. The entries are
 * located by a line scan (an entry runs from its `[[name]]` header to the next table header), which is exact for the
 * documents clients write themselves; every mutation re-parses the result and fails when it is not valid TOML any more.
 * (Port of Astra.Clients TomlArrayEntries.)
 */
const SELECTOR = /^(?<name>[A-Za-z0-9_.-]+)\[(?<key>[A-Za-z0-9_-]+)=(?<value>[^\]]*)\]$/;

export const isArrayEntrySelector = (keyPath: string): boolean => SELECTOR.test(keyPath);

function parseSelector(keyPath: string): { name: string; key: string; value: string } {
  const m = SELECTOR.exec(keyPath);
  if (!m) throw new EditorException(`'${keyPath}' is not an array-entry selector (name[key=value]).`);
  return { name: m.groups!.name!, key: m.groups!.key!, value: m.groups!.value! };
}

const escapeRegex = (s: string): string => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

function headerOf(content: string, name: string): boolean {
  let c = content;
  const hash = c.indexOf('#');
  if (hash >= 0) c = c.slice(0, hash).trimEnd();
  return c === `[[${name}]]`;
}

function keyEquals(content: string, key: string, value: string): boolean {
  const m = new RegExp(`^${escapeRegex(key)}\\s*=\\s*"(?<v>(?:[^"\\\\]|\\\\.)*)"`).exec(content);
  return m !== null && m.groups!.v === value;
}

function endsWithBlankLine(text: string): boolean {
  let i = text.length - 1;
  while (i >= 0 && text[i] === '\r') i--;
  if (i < 0 || text[i] !== '\n') return false;
  i--;
  while (i >= 0 && text[i] === '\r') i--;
  return i < 0 || text[i] === '\n';
}

/** The character range of every matching entry: header line through the line before the next table header. */
function spans(text: string, keyPath: string): Array<[number, number]> {
  const { name, key, value } = parseSelector(keyPath);
  const lines: Array<{ start: number; content: string }> = [];
  for (let offset = 0; offset < text.length; ) {
    const next = text.indexOf('\n', offset);
    const end = next < 0 ? text.length : next + 1;
    lines.push({ start: offset, content: text.slice(offset, end).trim() });
    offset = end;
  }
  const result: Array<[number, number]> = [];
  for (let i = 0; i < lines.length; i++) {
    if (!headerOf(lines[i]!.content, name)) continue;
    let j = i + 1;
    let matches = false;
    for (; j < lines.length && !lines[j]!.content.startsWith('['); j++)
      if (keyEquals(lines[j]!.content, key, value)) matches = true;
    if (matches) result.push([lines[i]!.start, j < lines.length ? lines[j]!.start : text.length]);
    i = j - 1;
  }
  return result;
}

function removeSpans(text: string, found: Array<[number, number]>): string {
  let out = text;
  for (let i = found.length - 1; i >= 0; i--) out = out.slice(0, found[i]![0]) + out.slice(found[i]![1]);
  return out;
}

/** True when at least one entry matches. */
export const hasArrayEntries = (text: string, keyPath: string): boolean => spans(text, keyPath).length > 0;

function checked(updated: string, keyPath: string, expectEntries: boolean): string {
  validateToml(updated);
  if (hasArrayEntries(updated, keyPath) !== expectEntries)
    throw new EditorException(
      `Self-check failed: entries '${keyPath}' ${expectEntries ? 'missing' : 'still present'} after edit.`,
    );
  return updated;
}

/**
 * Replaces the matching entries with `entries` (each a list of key / raw TOML value text, written in order), appended at
 * the end of the document after the old ones are removed.
 */
export function setArrayEntries(
  text: string,
  keyPath: string,
  entries: ReadonlyArray<ReadonlyArray<readonly [string, string]>>,
): string {
  const { name } = parseSelector(keyPath);
  let out = removeSpans(text, spans(text, keyPath));
  const nl = newlineOf(text);
  if (out.length > 0 && !out.endsWith('\n')) out += nl;
  for (const entry of entries) {
    if (out.length > 0 && !endsWithBlankLine(out)) out += nl;
    out += `[[${name}]]${nl}`;
    for (const [key, value] of entry) out += `${key} = ${value}${nl}`;
  }
  return checked(out, keyPath, entries.length > 0);
}

/** Removes the matching entries (no-op when there are none). */
export function removeArrayEntries(text: string, keyPath: string): string {
  const found = spans(text, keyPath);
  return found.length === 0 ? text : checked(removeSpans(text, found), keyPath, false);
}
