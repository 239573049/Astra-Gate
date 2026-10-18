import { getStaticTOMLValue, parseTOML } from 'toml-eslint-parser';
import type { AST } from 'toml-eslint-parser';
import { EditorException } from './errors';
import {
  atLineContentStart,
  isBlank,
  lineEnd,
  lineStart,
  newlineOf,
  splice,
} from './text-tool';

/**
 * Format-preserving editor for TOML documents (port of Astra.Clients TomlEditor). The syntax tree is
 * only used to LOCATE spans; all mutations are surgical text splices on the original string, so
 * comments, ordering, whitespace, line endings and missing trailing newlines are preserved outside
 * the edited region. Every mutation re-parses the result and verifies the expected value, throwing
 * `EditorException` on mismatch. Instances are immutable: mutations return the updated editor.
 */

interface Pair {
  node: AST.TOMLKeyValue;
  /** Offset where the pair starts (its key). */
  start: number;
  /** End of the pair's line content: past the value and any trailing comment, before the terminator. */
  end: number;
}

interface TableInfo {
  node: AST.TOMLTable;
  name: string[];
  pairs: Pair[];
  /** Offset of the opening bracket. */
  start: number;
  /** End of the header or its last pair, as a line-content end. */
  end: number;
}

interface Doc {
  topPairs: Pair[];
  tables: TableInfo[];
  model: unknown;
}

/** End of the line content at `pos`: stops before a "\r\n" / "\n" terminator. */
function contentEnd(text: string, pos: number): number {
  let i = pos;
  while (i < text.length && text[i] !== '\n') i++;
  if (i > pos && text[i - 1] === '\r') i--;
  return i;
}

function keySegments(key: AST.TOMLKey): string[] {
  return key.keys.map((k) => (k.type === 'TOMLBare' ? k.name : k.value));
}

function parse(text: string): Doc {
  let ast: AST.TOMLProgram;
  try {
    ast = parseTOML(text);
  } catch (e) {
    const message = e instanceof Error ? e.message : String(e);
    throw new EditorException(`Invalid TOML document: ${message || 'parse error'}`);
  }
  const top = ast.body[0]!;
  const pair = (node: AST.TOMLKeyValue): Pair => ({
    node,
    start: node.range[0],
    end: contentEnd(text, node.value.range[1]),
  });
  const topPairs: Pair[] = [];
  const tables: TableInfo[] = [];
  for (const node of top.body) {
    if (node.type === 'TOMLKeyValue') {
      topPairs.push(pair(node));
    } else {
      const pairs = node.body.map(pair);
      tables.push({
        node,
        name: keySegments(node.key),
        pairs,
        start: node.range[0],
        end: pairs.length > 0 ? pairs[pairs.length - 1]!.end : contentEnd(text, node.range[1]),
      });
    }
  }
  return { topPairs, tables, model: getStaticTOMLValue(ast) };
}

/** Raw TOML source of a pair's value. */
const rawValue = (text: string, p: Pair): string =>
  text.slice(p.node.value.range[0], p.node.value.range[1]);

function splitPath(path: string): string[] {
  const parts = path
    .split('.')
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
  if (parts.length === 0) throw new RangeError('Empty key path.');
  return parts;
}

function validateBareSegments(segments: readonly string[]): void {
  for (const s of segments) {
    if (s.length === 0 || /[^A-Za-z0-9_-]/.test(s)) {
      throw new EditorException(
        `Key segment '${s}' is not a bare TOML key; quoted keys are not supported by this editor.`,
      );
    }
  }
}

function segmentsEqual(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((s, i) => s === b[i]);
}

/**
 * Locates the pair for a dotted path: the longest matching table header provides the context,
 * the remaining segments must match the pair's (possibly dotted) key.
 */
function find(doc: Doc, segments: readonly string[]): { pair: Pair; owner: TableInfo | null } | null {
  for (const p of doc.topPairs) {
    if (segmentsEqual(keySegments(p.node.key), segments)) return { pair: p, owner: null };
  }
  for (const t of doc.tables) {
    if (t.name.length === 0 || t.name.length >= segments.length) continue;
    if (!t.name.every((s, i) => s === segments[i])) continue;
    const rest = segments.slice(t.name.length);
    for (const p of t.pairs) {
      if (segmentsEqual(keySegments(p.node.key), rest)) return { pair: p, owner: t };
    }
  }
  return null;
}

function exactTable(doc: Doc, segments: readonly string[]): TableInfo | null {
  if (segments.length === 0) return null;
  return doc.tables.find((t) => segmentsEqual(t.name, segments)) ?? null;
}

function endsWithBlankLine(text: string): boolean {
  let i = text.length - 1;
  while (i >= 0 && text[i] === '\r') i--;
  if (i < 0 || text[i] !== '\n') return false;
  i--;
  while (i >= 0 && text[i] === '\r') i--;
  return i < 0 || text[i] === '\n';
}

/** End of the pair's line including its terminator: the new line belongs after it. */
function itemEnd(text: string, p: Pair | undefined): number {
  if (!p) return 0;
  let end = Math.min(p.end, text.length);
  if (end < text.length && text[end - 1] !== '\n' && text[end - 1] !== '\r') {
    if (text[end] === '\r') end++;
    if (end < text.length && text[end] === '\n') end++;
  }
  return end;
}

function insertLine(text: string, at: number, line: string, nl: string): string {
  const prefix = at > 0 && at <= text.length && text[at - 1] !== '\n' ? nl : '';
  return splice(text, at, at, prefix + line + nl);
}

function insert(text: string, doc: Doc, segments: readonly string[], value: string): string {
  const nl = newlineOf(text);
  const line = `${segments[segments.length - 1]!} = ${value}`;

  // 1. Exact table header for the parent path exists: append the pair inside it.
  const table = exactTable(doc, segments.slice(0, -1));
  if (table) {
    const at =
      table.pairs.length > 0
        ? itemEnd(text, table.pairs[table.pairs.length - 1])
        : lineEnd(text, table.start);
    return insertLine(text, at, line, nl);
  }

  // 2. No table: create "[a.b]" at end of file (only for paths with a table part).
  if (segments.length > 1) {
    const header = `[${segments.slice(0, -1).join('.')}]`;
    const tail = text.length > 0 && !text.endsWith('\n') ? text + nl : text;
    const blank = tail.length > 0 && !endsWithBlankLine(tail) ? nl : '';
    return tail + blank + header + nl + line + nl;
  }

  // 3. Top-level key: after the last top-level pair, else before the first table, else at EOF.
  if (doc.topPairs.length > 0) {
    return insertLine(text, itemEnd(text, doc.topPairs[doc.topPairs.length - 1]), line, nl);
  }
  if (doc.tables.length > 0) return insertLine(text, doc.tables[0]!.start, line, nl);
  const tail = text.length > 0 && !text.endsWith('\n') ? text + nl : text;
  return tail + line + nl;
}

/** Model-layer check: each segment must resolve through plain tables of the parsed model. */
function navigate(model: unknown, segments: readonly string[]): void {
  let current: unknown = model;
  for (const s of segments) {
    if (
      typeof current !== 'object' ||
      current === null ||
      Array.isArray(current) ||
      !Object.prototype.hasOwnProperty.call(current, s)
    ) {
      throw new EditorException(
        `Self-check failed: '${segments.join('.')}' not found in TOML model.`,
      );
    }
    current = (current as Record<string, unknown>)[s];
  }
}

export class TomlEditor {
  private readonly _text: string;

  constructor(text: string) {
    this._text = text;
  }

  /** Current document text (after any mutations). */
  get text(): string {
    return this._text;
  }

  // ---------------------------------------------------------------- queries

  /** Raw TOML source text of the value at `path` (dotted), or null when absent. */
  getValue(path: string): string | null {
    const found = find(parse(this._text), splitPath(path));
    return found ? rawValue(this._text, found.pair) : null;
  }

  /** Decoded string value at `path`, or null when absent or not a TOML string. */
  getStringValue(path: string): string | null {
    const found = find(parse(this._text), splitPath(path));
    const v = found?.pair.node.value;
    return v && v.type === 'TOMLValue' && v.kind === 'string' ? v.value : null;
  }

  /** Boolean value at `path`, or null when absent or not a boolean. */
  getBoolValue(path: string): boolean | null {
    const v = this.getValue(path);
    return v === 'true' ? true : v === 'false' ? false : null;
  }

  /** True when a key/value pair exists at this dotted path. */
  keyExists(path: string): boolean {
    return find(parse(this._text), splitPath(path)) !== null;
  }

  /** True when a table header (`[a.b]` or `[[a.b]]`) with this dotted name exists. */
  tableExists(path: string): boolean {
    const segments = splitPath(path);
    return parse(this._text).tables.some((t) => segmentsEqual(t.name, segments));
  }

  // ---------------------------------------------------------------- mutations

  /** Sets the key to `tomlValueText` (raw TOML value source, e.g. `"x"` or `true`). */
  set(path: string, tomlValueText: string): TomlEditor {
    const segments = splitPath(path);
    validateBareSegments(segments);
    if (isBlank(tomlValueText)) throw new RangeError('Value text is empty.');
    const doc = parse(this._text);
    const found = find(doc, segments);
    const updated = found
      ? splice(
          this._text,
          found.pair.node.value.range[0],
          found.pair.node.value.range[1],
          tomlValueText,
        )
      : insert(this._text, doc, segments, tomlValueText);
    return this.verifySet(updated, segments, tomlValueText);
  }

  /** Sets the key to a TOML basic string. */
  setString(path: string, value: string): TomlEditor {
    return this.set(path, tomlString(value));
  }

  /** Sets the key to a TOML boolean. */
  setBool(path: string, value: boolean): TomlEditor {
    return this.set(path, value ? 'true' : 'false');
  }

  /**
   * Appends a brand-new table `[path]` at end of file with the given items (key, raw TOML value
   * text). Throws when a header with the same name already exists.
   */
  insertTable(path: string, items: ReadonlyArray<readonly [string, string]>): TomlEditor {
    const segments = splitPath(path);
    validateBareSegments(segments);
    for (const [key] of items) validateBareSegments([key]);
    if (this.tableExists(path)) {
      throw new EditorException(`Table [${path}] already exists; use Set to update its keys.`);
    }
    const nl = newlineOf(this._text);
    const text = this._text;
    const tail = text.length > 0 && !text.endsWith('\n') ? text + nl : text;
    const blank = tail.length > 0 && !endsWithBlankLine(tail) ? nl : '';
    let block = `${blank}[${segments.join('.')}]${nl}`;
    for (const [key, value] of items) {
      if (isBlank(value)) throw new RangeError(`Value for '${key}' is empty.`);
      block += `${key} = ${value}${nl}`;
    }
    const updated = tail + block;
    return this.verifyTableInserted(updated, segments, items);
  }

  /** Removes the key/value pair (its whole line). No-op when the key is absent. */
  remove(path: string): TomlEditor {
    const text = this._text;
    const found = find(parse(text), splitPath(path));
    if (!found) return this;
    const p = found.pair;
    const start = atLineContentStart(text, p.start) ? lineStart(text, p.start) : p.start;
    let end = p.end;
    // The pair's span stops short of its line terminator; consume it so no empty line is left behind.
    if (end < text.length && (text[end] === '\r' || text[end] === '\n')) {
      end++;
      if (end < text.length && text[end - 1] === '\r' && text[end] === '\n') end++;
    }
    return this.verifyRemoved(splice(text, start, end, ''), path);
  }

  /**
   * Removes a whole table: its header line and every item belonging to it (up to the next header or
   * EOF). No-op when the table is absent.
   */
  removeTable(path: string): TomlEditor {
    const text = this._text;
    const segments = splitPath(path);
    for (const t of parse(text).tables) {
      if (!segmentsEqual(t.name, segments)) continue;
      let start = atLineContentStart(text, t.start) ? lineStart(text, t.start) : t.start;
      // Absorb blank lines that immediately precede the header so no stray blank line is left
      // between the previous table and whatever follows the removed one.
      while (start > 0 && text[start - 1] === '\n') {
        const prevEnd = start - 1;
        const prevStart = lineStart(text, prevEnd);
        if (!isBlank(text.slice(prevStart, prevEnd))) break;
        start = prevStart;
      }
      let end = t.end;
      // The table's last line stops short of its terminator; consume one so no blank line remains.
      if (end < text.length && text[end] === '\r' && text[end + 1] === '\n') end += 2;
      else if (end < text.length && text[end] === '\n') end++;
      return this.verifyTableRemoved(splice(text, start, end, ''), segments);
    }
    return this;
  }

  // ---------------------------------------------------------------- self-checks

  private verifySet(updated: string, segments: readonly string[], expectedRaw: string): TomlEditor {
    const doc = parse(updated);
    const name = segments.join('.');
    const found = find(doc, segments);
    if (!found) throw new EditorException(`Self-check failed: '${name}' missing after set.`);
    const actualRaw = rawValue(updated, found.pair);
    if (actualRaw !== expectedRaw) {
      throw new EditorException(
        `Self-check failed: '${name}' = ${actualRaw}, expected ${expectedRaw}.`,
      );
    }
    navigate(doc.model, segments);
    return new TomlEditor(updated);
  }

  private verifyTableInserted(
    updated: string,
    segments: readonly string[],
    items: ReadonlyArray<readonly [string, string]>,
  ): TomlEditor {
    const editor = new TomlEditor(updated);
    const name = segments.join('.');
    if (!editor.tableExists(name)) {
      throw new EditorException(`Self-check failed: table [${name}] missing after insert.`);
    }
    const doc = parse(updated);
    for (const [key, value] of items) {
      const found = find(doc, [...segments, key]);
      if (!found) {
        throw new EditorException(
          `Self-check failed: '${key}' missing in [${name}] after insert.`,
        );
      }
      if (rawValue(updated, found.pair) !== value) {
        throw new EditorException(`Self-check failed: '${key}' differs after table insert.`);
      }
    }
    navigate(doc.model, segments);
    return editor;
  }

  private verifyRemoved(updated: string, path: string): TomlEditor {
    if (find(parse(updated), splitPath(path))) {
      throw new EditorException(`Self-check failed: '${path}' still present after remove.`);
    }
    return new TomlEditor(updated);
  }

  private verifyTableRemoved(updated: string, segments: readonly string[]): TomlEditor {
    if (parse(updated).tables.some((t) => segmentsEqual(t.name, segments))) {
      throw new EditorException(
        `Self-check failed: table [${segments.join('.')}] still present after remove.`,
      );
    }
    return new TomlEditor(updated);
  }
}

/** A TOML basic string with escapes for control characters, backslash and quote. */
export function tomlString(value: string): string {
  let out = '"';
  for (const c of value) {
    switch (c) {
      case '"':
        out += '\\"';
        break;
      case '\\':
        out += '\\\\';
        break;
      case '\b':
        out += '\\b';
        break;
      case '\t':
        out += '\\t';
        break;
      case '\n':
        out += '\\n';
        break;
      case '\f':
        out += '\\f';
        break;
      case '\r':
        out += '\\r';
        break;
      default:
        out += c < ' ' ? `\\u${c.charCodeAt(0).toString(16).padStart(4, '0')}` : c;
    }
  }
  return `${out}"`;
}
