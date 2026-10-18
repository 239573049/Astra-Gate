import { isMap, isScalar, isSeq, parseAllDocuments } from 'yaml';
import type { Node, Pair, Scalar, YAMLMap, YAMLSeq } from 'yaml';
import { EditorException } from './errors';
import { atLineContentStart, isBlank, lineEnd, lineStart, newlineOf, splice } from './text-tool';

/**
 * Port of Astra.Clients YamlEditor: a format-preserving editor for YAML config files whose root is a
 * block mapping, or a block sequence of mappings addressed by a leading `[key=value]` selector.
 * Values are exchanged as JSON text; entries are located with source ranges and edited as text
 * splices. Every mutation re-parses the result and compares it against the expected document.
 */

const INDENT_UNIT = '  ';

// ------------------------------------------------------------------ JSON model (System.Text.Json shape)

/** A JSON number that remembers its text (System.Text.Json echoes the source text of a number). */
class JNum {
  constructor(readonly raw: string) {}
}
type J = null | boolean | string | JNum | J[] | JObj;
type JObj = Map<string, J>;

/** Plain JS view of a JSON value, as returned by `get` / `parseToJson`. */
export type JsonValue =
  null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };

const isObj = (v: J | undefined): v is JObj => v instanceof Map;

function toPlain(v: J): JsonValue {
  if (v instanceof JNum) return Number(v.raw);
  if (Array.isArray(v)) return v.map(toPlain);
  if (v instanceof Map) {
    const o: { [key: string]: JsonValue } = {};
    for (const [k, c] of v) o[k] = toPlain(c);
    return o;
  }
  return v;
}

function clone(v: J): J {
  if (Array.isArray(v)) return v.map(clone);
  if (v instanceof Map) return new Map([...v].map(([k, c]) => [k, clone(c)] as [string, J]));
  return v;
}

function deepEquals(a: J, b: J): boolean {
  if (a instanceof JNum || b instanceof JNum)
    return a instanceof JNum && b instanceof JNum && Number(a.raw) === Number(b.raw);
  if (Array.isArray(a) || Array.isArray(b)) {
    return (
      Array.isArray(a) &&
      Array.isArray(b) &&
      a.length === b.length &&
      a.every((x, i) => deepEquals(x, b[i]!))
    );
  }
  if (a instanceof Map || b instanceof Map) {
    if (!(a instanceof Map) || !(b instanceof Map) || a.size !== b.size) return false;
    for (const [k, v] of a) {
      if (!b.has(k) || !deepEquals(v, b.get(k)!)) return false;
    }
    return true;
  }
  return a === b;
}

/** System.Text.Json's default encoder: everything outside printable ASCII and HTML-sensitive chars. */
function jsonString(s: string): string {
  let out = '"';
  for (let i = 0; i < s.length; i++) {
    const c = s[i]!;
    const code = s.charCodeAt(i);
    switch (c) {
      case '\\':
        out += '\\\\';
        continue;
      case '\n':
        out += '\\n';
        continue;
      case '\r':
        out += '\\r';
        continue;
      case '\t':
        out += '\\t';
        continue;
      case '\b':
        out += '\\b';
        continue;
      case '\f':
        out += '\\f';
        continue;
    }
    if (code < 0x20 || code > 0x7e || '"&\'+<>`'.includes(c)) {
      out += '\\u' + code.toString(16).toUpperCase().padStart(4, '0');
    } else out += c;
  }
  return out + '"';
}

function toJsonString(v: J): string {
  if (v === null) return 'null';
  if (typeof v === 'boolean') return v ? 'true' : 'false';
  if (typeof v === 'string') return jsonString(v);
  if (v instanceof JNum) return v.raw;
  if (Array.isArray(v)) return '[' + v.map(toJsonString).join(',') + ']';
  return '{' + [...v].map(([k, c]) => jsonString(k) + ':' + toJsonString(c)).join(',') + '}';
}

/** Strict JSON parser (like JsonNode.Parse) that keeps number text. */
function parseJson(text: string): J {
  let i = 0;
  const fail = (m: string): never => {
    throw new SyntaxError(`${m} at position ${i}.`);
  };
  const ws = (): void => {
    while (i < text.length && ' \t\r\n'.includes(text[i]!)) i++;
  };
  const value = (depth: number): J => {
    if (depth > 64) fail('Maximum depth exceeded');
    ws();
    const c = text[i];
    if (c === '{') {
      i++;
      const o: JObj = new Map();
      ws();
      if (text[i] === '}') {
        i++;
        return o;
      }
      for (;;) {
        ws();
        if (text[i] !== '"') fail('Expected property name');
        const k = str();
        ws();
        if (text[i] !== ':') fail("Expected ':'");
        i++;
        o.set(k, value(depth + 1));
        ws();
        if (text[i] === ',') {
          i++;
          continue;
        }
        if (text[i] === '}') {
          i++;
          return o;
        }
        fail("Expected ',' or '}'");
      }
    }
    if (c === '[') {
      i++;
      const a: J[] = [];
      ws();
      if (text[i] === ']') {
        i++;
        return a;
      }
      for (;;) {
        a.push(value(depth + 1));
        ws();
        if (text[i] === ',') {
          i++;
          continue;
        }
        if (text[i] === ']') {
          i++;
          return a;
        }
        fail("Expected ',' or ']'");
      }
    }
    if (c === '"') return str();
    for (const [lit, v] of [
      ['true', true],
      ['false', false],
      ['null', null],
    ] as const) {
      if (text.startsWith(lit, i)) {
        i += lit.length;
        return v;
      }
    }
    const m = /^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?/.exec(text.slice(i));
    if (!m) return fail('Unexpected token');
    i += m[0].length;
    return new JNum(m[0]);
  };
  const str = (): string => {
    i++;
    let out = '';
    for (;;) {
      const c = text[i];
      if (c === undefined) fail('Unterminated string');
      if (c === '"') {
        i++;
        return out;
      }
      if (c! < ' ') fail('Invalid control character');
      if (c === '\\') {
        const e = text[i + 1];
        const map: Record<string, string> = {
          '"': '"',
          '\\': '\\',
          '/': '/',
          b: '\b',
          f: '\f',
          n: '\n',
          r: '\r',
          t: '\t',
        };
        if (e !== undefined && e in map) {
          out += map[e];
          i += 2;
        } else if (e === 'u' && /^[0-9a-fA-F]{4}$/.test(text.slice(i + 2, i + 6))) {
          out += String.fromCharCode(parseInt(text.slice(i + 2, i + 6), 16));
          i += 6;
        } else fail('Invalid escape');
      } else {
        out += c;
        i++;
      }
    }
  };
  const result = value(0);
  ws();
  if (i < text.length) fail('Unexpected trailing content');
  return result;
}

// ------------------------------------------------------------------ key paths and selectors

/** Splits a dotted key path; dots inside a `[...]` selector segment do not split. */
function splitKeyPath(path: string): string[] {
  const parts: string[] = [];
  let current = '';
  let depth = 0;
  for (const c of path) {
    if (c === '[') depth++;
    else if (c === ']') depth = Math.max(0, depth - 1);
    if (c === '.' && depth === 0) {
      if (current.trim().length > 0) parts.push(current.trim());
      current = '';
      continue;
    }
    current += c;
  }
  if (current.trim().length > 0) parts.push(current.trim());
  return parts;
}

const isSelector = (segment: string): boolean =>
  segment.length >= 2 && segment[0] === '[' && segment[segment.length - 1] === ']';

function elementMatches(element: J, selector: string): boolean {
  if (!isObj(element)) return false;
  for (const condition of selector.slice(1, -1).split('&')) {
    const eq = condition.indexOf('=');
    if (eq <= 0) throw new EditorException(`Invalid array selector '${selector}'.`);
    const v = element.get(condition.slice(0, eq));
    if (typeof v !== 'string' || v !== condition.slice(eq + 1)) return false;
  }
  return true;
}

function splitPath(path: string): string[] {
  const parts = splitKeyPath(path);
  if (parts.length === 0) throw new Error('Empty key path.');
  return parts;
}

// ------------------------------------------------------------------ YAML nodes

type YNode = Node | null;
type Entry = { key: Scalar; value: YNode };

const isBlockMap = (n: YNode): n is YAMLMap => isMap(n) && !n.flow && n.items.length > 0;
const isBlockSeq = (n: YNode): n is YAMLSeq => isSeq(n) && !n.flow && n.items.length > 0;
const isEmptyPlain = (n: YNode): boolean =>
  n === null || (isScalar(n) && (n.type === 'PLAIN' || n.type === undefined) && !n.source);

function entries(map: YAMLMap): Entry[] {
  return (map.items as Pair<Node, YNode>[]).map((p) => ({
    key: p.key as Scalar,
    value: p.value,
  }));
}

const start = (n: Node): number => n.range![0];
const end = (n: Node): number => n.range![1];

/** Plain scalar resolution (YAML 1.2 core schema). */
function resolvePlain(text: string): J {
  switch (text) {
    case '':
    case '~':
    case 'null':
    case 'Null':
    case 'NULL':
      return null;
    case 'true':
    case 'True':
    case 'TRUE':
      return true;
    case 'false':
    case 'False':
    case 'FALSE':
      return false;
  }
  if (/^[-+]?[0-9]+$/.test(text)) {
    const b = BigInt(text);
    if (b >= -(2n ** 63n) && b < 2n ** 63n) return new JNum(b.toString());
  }
  if (/^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$/.test(text)) {
    const d = Number(text);
    if (Number.isFinite(d)) return new JNum(formatDouble(d));
  }
  return text;
}

/** .NET's shortest round-trip double text: scientific form at exponent >= 15 or < -4. */
function formatDouble(d: number): string {
  if (d === 0) return Object.is(d, -0) ? '-0' : '0';
  const m = /^(-?)(\d)(?:\.(\d+))?e([-+]\d+)$/.exec(d.toExponential())!;
  const digits = m[2]! + (m[3] ?? '');
  const exp = Number(m[4]);
  const sign = m[1]!;
  if (exp >= 15 || exp < -4) {
    const mant = digits.length > 1 ? `${digits[0]}.${digits.slice(1)}` : digits;
    return `${sign}${mant}E${exp < 0 ? '-' : '+'}${String(Math.abs(exp)).padStart(2, '0')}`;
  }
  if (exp < 0) return `${sign}0.${'0'.repeat(-exp - 1)}${digits}`;
  if (digits.length <= exp + 1) return sign + digits + '0'.repeat(exp + 1 - digits.length);
  return `${sign}${digits.slice(0, exp + 1)}.${digits.slice(exp + 1)}`;
}

function toJson(node: YNode): J {
  if (node === null) return null;
  if (isScalar(node)) {
    const text = node.source ?? '';
    return node.type === 'PLAIN' || node.type === undefined ? resolvePlain(text) : text;
  }
  if (isMap(node)) {
    const result: JObj = new Map();
    for (const e of entries(node)) {
      if (!isScalar(e.key))
        throw new EditorException('YAML mappings with non-scalar keys are not supported.');
      result.set(e.key.source ?? '', toJson(e.value));
    }
    return result;
  }
  if (isSeq(node)) return (node.items as YNode[]).map(toJson);
  throw new EditorException('Unsupported YAML node.');
}

function loadRoot(text: string): YNode | undefined {
  // undefined = no document; null never returned for a present document
  if (isBlank(text)) return undefined;
  const docs = parseAllDocuments(text, { schema: 'failsafe', uniqueKeys: true });
  for (const d of docs) {
    if (d.errors.length > 0)
      throw new EditorException(`Invalid YAML document: ${d.errors[0]!.message}`);
  }
  if (docs.length === 0) return undefined;
  if (docs.length > 1) throw new EditorException('Multi-document YAML files are not supported.');
  const root = docs[0]!.contents as YNode;
  if (root === null) return undefined;
  return isScalar(root) && (root.type === 'PLAIN' || root.type === undefined) && !root.source
    ? undefined
    : root;
}

// ------------------------------------------------------------------ rendering (JSON -> block YAML)

const PLAIN_KEY = /^[A-Za-z0-9_][A-Za-z0-9_.\-/]*$/;
const YAML11_BOOLEANS = new Set(['y', 'n', 'yes', 'no', 'on', 'off']);

/** A YAML double-quoted scalar. */
export function quote(value: string): string {
  let out = '"';
  for (let i = 0; i < value.length; i++) {
    const c = value[i]!;
    const code = value.charCodeAt(i);
    switch (c) {
      case '"':
        out += '\\"';
        break;
      case '\\':
        out += '\\\\';
        break;
      case '\n':
        out += '\\n';
        break;
      case '\r':
        out += '\\r';
        break;
      case '\t':
        out += '\\t';
        break;
      default:
        if (
          code < 0x20 ||
          (code >= 0x7f && code <= 0x9f) ||
          code === 0x2028 ||
          code === 0x2029 ||
          code === 0xfeff
        )
          out += '\\u' + code.toString(16).padStart(4, '0');
        else out += c;
    }
  }
  return out + '"';
}

function renderKey(key: string): string {
  return PLAIN_KEY.test(key) &&
    !YAML11_BOOLEANS.has(key.toLowerCase()) &&
    typeof resolvePlain(key) === 'string'
    ? key
    : quote(key);
}

function renderScalar(value: J): string {
  if (value === null) return 'null';
  if (typeof value === 'string') return quote(value);
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  return toJsonString(value);
}

function renderEntry(key: string, value: J, indent: string, nl: string): string {
  return indent + renderKey(key) + ':' + renderValue(value, indent, nl);
}

/** The text that follows "key:" — " scalar", " {}" or a newline-separated nested block. */
function renderValue(value: J, indent: string, nl: string): string {
  const childIndent = indent + INDENT_UNIT;
  if (isObj(value)) {
    if (value.size === 0) return ' {}';
    let sb = '';
    for (const [name, child] of value) sb += nl + renderEntry(name, child, childIndent, nl);
    return sb;
  }
  if (Array.isArray(value)) {
    if (value.length === 0) return ' []';
    let sb = '';
    for (const item of value) {
      // Nested collections inside sequences are written in flow style (JSON is valid YAML flow).
      const itemText = isObj(item) || Array.isArray(item) ? toJsonString(item) : renderScalar(item);
      sb += nl + childIndent + '- ' + itemText;
    }
    return sb;
  }
  return ' ' + renderScalar(value);
}

/** A mapping as a sequence item: the first member rides on the "- " line. */
function renderRow(row: JObj, indent: string, nl: string): string {
  if (row.size === 0) return indent + '- {}';
  let sb = '';
  let first = true;
  for (const [name, child] of row) {
    let entry = renderEntry(name, child, indent + INDENT_UNIT, nl);
    if (first) entry = indent + '- ' + entry.slice(indent.length + INDENT_UNIT.length);
    else sb += nl;
    sb += entry;
    first = false;
  }
  return sb;
}

// ------------------------------------------------------------------ editing core

function contentEndEntry(key: Node, value: YNode): number {
  return Math.max(end(key) + 1, contentEnd(value));
}

/** Offset just past the last source character of a node (comments after it excluded). */
function contentEnd(node: YNode): number {
  if (isBlockMap(node))
    return Math.max(...entries(node).map((e) => contentEndEntry(e.key, e.value)));
  if (isBlockSeq(node)) return Math.max(...(node.items as YNode[]).map(contentEnd));
  if (isEmptyPlain(node)) return 0; // "key:" - no value text
  // Flow collections: the range ends at the closing bracket.
  return end(node!);
}

function nest(segments: string[], value: J): J {
  for (let i = segments.length - 1; i >= 0; i--) value = new Map([[segments[i]!, value]]);
  return value;
}

function setIn(target: JObj, segments: string[], value: J): void {
  if (segments.length === 0) return;
  let current = target;
  for (const segment of segments.slice(0, -1)) {
    let next = current.get(segment);
    if (!isObj(next)) {
      next = new Map();
      current.set(segment, next);
    }
    current = next;
  }
  current.set(segments[segments.length - 1]!, value);
}

function removeIn(target: JObj, segments: string[]): void {
  let current: J | undefined = target;
  for (const segment of segments.slice(0, -1))
    current = isObj(current) ? current.get(segment) : undefined;
  if (isObj(current)) current.delete(segments[segments.length - 1]!);
}

function findEntry(map: YAMLMap, key: string): Entry | undefined {
  return entries(map).find((e) => isScalar(e.key) && (e.key.source ?? '') === key);
}

/** Replaces "key: value" (value block included) with a freshly rendered value, keeping the key text. */
function replaceEntryValue(text: string, key: Node, value: YNode, newValue: J, nl: string): string {
  const keyStart = start(key);
  const keyEnd = end(key);
  const regionEnd = lineEnd(text, Math.max(contentEndEntry(key, value) - 1, keyStart));
  const hadNewline = regionEnd > 0 && text[regionEnd - 1] === '\n';
  const indent = ' '.repeat(keyStart - lineStart(text, keyStart));
  const replacement =
    text.slice(keyStart, keyEnd) + ':' + renderValue(newValue, indent, nl) + (hadNewline ? nl : '');
  return splice(text, keyStart, regionEnd, replacement);
}

/** Appends a new entry after the last entry of a block mapping, at that mapping's indentation. */
function insertEntry(text: string, mapping: YAMLMap, key: string, value: J, nl: string): string {
  if (mapping.flow || mapping.items.length === 0)
    throw new EditorException(`Cannot insert '${key}' into an empty or flow-style mapping.`);
  const list = entries(mapping);
  const firstStart = start(list[0]!.key);
  const indent = ' '.repeat(firstStart - lineStart(text, firstStart));
  const lastContent = Math.max(...list.map((e) => contentEndEntry(e.key, e.value)));
  const at = lineEnd(text, lastContent - 1);
  const prefix = at > 0 && text[at - 1] === '\n' ? '' : nl;
  return splice(text, at, at, prefix + renderEntry(key, value, indent, nl) + nl);
}

function setCore(text: string, root: YAMLMap, segments: string[], value: J, nl: string): string {
  let current = root;
  for (let i = 0; i < segments.length; i++) {
    const entry = findEntry(current, segments[i]!);
    const last = i === segments.length - 1;
    if (entry) {
      if (last) return replaceEntryValue(text, entry.key, entry.value, value, nl);
      if (isBlockMap(entry.value)) {
        current = entry.value;
        continue;
      }
      // "key:" (null), "key: {}" or a flow mapping: rebuild this entry's value from the data model.
      const existing = toJson(entry.value);
      let rebuilt: JObj;
      if (isObj(existing)) rebuilt = existing;
      else if (existing === null) rebuilt = new Map();
      else
        throw new EditorException(
          `Cannot set '${segments.join('.')}': '${segments[i]}' is not a mapping.`,
        );
      setIn(rebuilt, segments.slice(i + 1), clone(value));
      return replaceEntryValue(text, entry.key, entry.value, rebuilt, nl);
    }
    return insertEntry(text, current, segments[i]!, nest(segments.slice(i + 1), value), nl);
  }
  throw new EditorException('Empty key path.');
}

type Located = {
  key: Scalar;
  value: YNode;
  parent: YAMLMap;
  parentEntry: Entry | null;
};

function locate(root: YNode | undefined, segments: string[]): Located | null {
  if (!isMap(root)) return null;
  let current: YAMLMap = root;
  let owner: Entry | null = null;
  for (let i = 0; i < segments.length; i++) {
    const found = findEntry(current, segments[i]!);
    if (!found) return null;
    if (i === segments.length - 1)
      return { key: found.key, value: found.value, parent: current, parentEntry: owner };
    if (!isMap(found.value)) return null;
    owner = found;
    current = found.value;
  }
  return null;
}

function documentObject(root: YNode | undefined): JObj {
  if (root === undefined) return new Map();
  const j = toJson(root);
  if (!isObj(j)) throw new EditorException('The YAML document root is not a mapping.');
  return j;
}

function documentArray(root: YNode | undefined): J[] {
  if (root === undefined) return [];
  const j = toJson(root);
  if (!Array.isArray(j)) throw new EditorException('The YAML document root is not a sequence.');
  return j;
}

function verify(updated: string, expected: J, operation: string): YamlEditor {
  const root = loadRoot(updated);
  const actual: J = Array.isArray(expected) ? documentArray(root) : documentObject(root);
  if (!deepEquals(actual, expected))
    throw new EditorException(
      `Self-check failed after ${operation}: the YAML document differs from the expected content.`,
    );
  return new YamlEditor(updated);
}

// ------------------------------------------------------------------ rows (sequence-of-mappings documents)

type Row = { sequence: YAMLSeq; index: number; row: YAMLMap };

function findRow(root: YNode | undefined, selector: string): Row | null {
  if (root === undefined) return null;
  if (!isSeq(root))
    throw new EditorException('An array selector needs a YAML document whose root is a sequence.');
  const items = root.items as YNode[];
  for (let i = 0; i < items.length; i++) {
    const row = items[i]!;
    if (isMap(row) && elementMatches(toJson(row), selector))
      return { sequence: root, index: i, row };
  }
  return null;
}

/** The JSON node at a row path; `present` tells an absent key from a YAML null. */
function resolveInRow(
  root: YNode | undefined,
  segments: string[],
): { node: J | undefined; present: boolean } {
  const found = findRow(root, segments[0]!);
  if (!found) return { node: undefined, present: false };
  let current: J = toJson(found.row);
  for (const segment of segments.slice(1)) {
    if (!isObj(current) || !current.has(segment)) return { node: undefined, present: false };
    current = current.get(segment)!;
  }
  return { node: current, present: true };
}

function rowIndent(text: string, row: Node): string {
  const keyStart = start(row);
  const before = text.slice(lineStart(text, keyStart), keyStart);
  return before.endsWith('- ') ? before.slice(0, -2) : '';
}

/** The text span of a row: from its "- " line start to the end of its last content line. */
function rowRegion(text: string, row: YAMLMap): { start: number; end: number; indent: string } {
  if (row.flow) throw new EditorException('A flow-style row in a YAML sequence is not supported.');
  const keyStart = start(row);
  const ls = lineStart(text, keyStart);
  const before = text.slice(ls, keyStart);
  if (!before.endsWith('- ') || before.slice(0, -2).trim().length > 0)
    throw new EditorException("A row in a YAML sequence must start on its own '- ' line.");
  return {
    start: ls,
    end: lineEnd(text, Math.max(contentEnd(row) - 1, keyStart)),
    indent: before.slice(0, -2),
  };
}

function replaceRow(text: string, row: YAMLMap, newRow: JObj, nl: string): string {
  const r = rowRegion(text, row);
  const hadNewline = r.end > 0 && text[r.end - 1] === '\n';
  return splice(text, r.start, r.end, renderRow(newRow, r.indent, nl) + (hadNewline ? nl : ''));
}

// ------------------------------------------------------------------ the editor

/** Format-preserving YAML editor. Immutable: every mutation returns the updated editor. */
export class YamlEditor {
  /** Current document text (after any mutations). */
  readonly text: string;

  /** Creates an editor over YAML text. Blank (or comment-only) text is an empty mapping. */
  constructor(text: string) {
    this.text = text;
  }

  /** The value at `path` (dotted) as compact JSON text, or null when the key is absent. */
  getJsonText(path: string): string | null {
    const segments = splitPath(path);
    const root = loadRoot(this.text);
    if (isSelector(segments[0]!)) {
      const { node, present } = resolveInRow(root, segments);
      return node !== undefined ? toJsonString(node) : present ? 'null' : null;
    }
    const entry = locate(root, segments);
    return entry === null ? null : toJsonString(toJson(entry.value));
  }

  /** The value at `path` as plain JS data (null when absent or YAML null). */
  get(path: string): JsonValue {
    const segments = splitPath(path);
    const root = loadRoot(this.text);
    if (isSelector(segments[0]!)) {
      const { node } = resolveInRow(root, segments);
      return node === undefined ? null : toPlain(node);
    }
    const entry = locate(root, segments);
    return entry === null ? null : toPlain(toJson(entry.value));
  }

  /** True when a key exists at this dotted path. */
  has(path: string): boolean {
    const segments = splitPath(path);
    const root = loadRoot(this.text);
    if (isSelector(segments[0]!)) return resolveInRow(root, segments).present;
    return locate(root, segments) !== null;
  }

  /** The whole document in the JSON data model (null for an empty document). */
  static parseToJson(text: string): JsonValue {
    const root = loadRoot(text);
    return root === undefined ? null : toPlain(toJson(root));
  }

  /** Sets the value at `path` to the JSON value `rawJsonText`, creating intermediate mappings. */
  setRaw(path: string, rawJsonText: string): YamlEditor {
    if (isBlank(rawJsonText)) throw new Error('Raw JSON text is empty.');
    let value: J;
    try {
      value = parseJson(rawJsonText);
    } catch (e) {
      throw new EditorException(`Value for '${path}' is not valid JSON: ${(e as Error).message}`);
    }

    const segments = splitPath(path);
    const root = loadRoot(this.text);
    if (isSelector(segments[0]!)) return this.setRow(root, segments, value, path);
    const expected = documentObject(root);
    setIn(expected, segments, clone(value));

    const nl = newlineOf(this.text);
    let updated: string;
    if (root === undefined) {
      // Empty or comment-only document: append the new top-level entry.
      const prefix = this.text.length === 0 || this.text.endsWith('\n') ? '' : nl;
      updated =
        this.text + prefix + renderEntry(segments[0]!, nest(segments.slice(1), value), '', nl) + nl;
    } else {
      if (!isMap(root)) throw new EditorException('The YAML document root is not a mapping.');
      if (root.flow) throw new EditorException('A flow-style YAML root mapping is not supported.');
      updated = setCore(this.text, root, segments, value, nl);
    }
    return verify(updated, expected, `set '${path}'`);
  }

  /** Removes the key at `path`. No-op (returns this) when absent. */
  remove(path: string): YamlEditor {
    const segments = splitPath(path);
    const root = loadRoot(this.text);
    if (isSelector(segments[0]!)) return this.removeRow(root, segments, path);
    const entry = locate(root, segments);
    if (entry === null) return this;
    const text = this.text;
    const expected = documentObject(root);
    removeIn(expected, segments);
    const nl = newlineOf(text);

    let updated: string;
    const keyStart = start(entry.key);
    const parent = entry.parent;
    if (!parent.flow && parent.items.length === 1 && entry.parentEntry) {
      // Removing the only child would leave "parent:" (a YAML null): write "parent: {}" instead.
      const owner = entry.parentEntry;
      updated = replaceEntryValue(text, owner.key, owner.value, new Map(), nl);
    } else if (!parent.flow && atLineContentStart(text, keyStart)) {
      const s = lineStart(text, keyStart);
      const e = lineEnd(text, Math.max(contentEndEntry(entry.key, entry.value) - 1, keyStart));
      updated = splice(text, s, e, '');
    } else if (entry.parentEntry) {
      // Flow mappings and "- key:" sequence items are rebuilt from the data model.
      const holder = entry.parentEntry;
      const rebuilt = toJson(holder.value);
      if (!isObj(rebuilt)) throw new EditorException(`Cannot remove '${path}'.`);
      rebuilt.delete(segments[segments.length - 1]!);
      updated = replaceEntryValue(text, holder.key, holder.value, rebuilt, nl);
    } else {
      throw new EditorException(`Cannot remove '${path}' from this YAML layout.`);
    }
    return verify(updated, expected, `remove '${path}'`);
  }

  /** A YAML double-quoted scalar. */
  static quote(value: string): string {
    return quote(value);
  }

  private setRow(root: YNode | undefined, segments: string[], value: J, path: string): YamlEditor {
    const text = this.text;
    const expected = documentArray(root);
    const nl = newlineOf(text);
    const found = findRow(root, segments[0]!);
    const rest = segments.slice(1);
    let newRow: JObj;
    if (found) {
      const row = toJson(found.row);
      if (!isObj(row)) throw new EditorException(`Cannot set '${path}': the row is not a mapping.`);
      newRow = row;
      if (rest.length === 0) {
        if (!isObj(value)) throw new EditorException(`'${path}' must be set to a whole mapping.`);
        newRow = value;
      } else setIn(newRow, rest, clone(value));
      expected[found.index] = clone(newRow);
      return verify(replaceRow(text, found.row, newRow, nl), expected, `set '${path}'`);
    }

    if (rest.length > 0)
      throw new EditorException(
        `Cannot set '${path}': no row matches '${segments[0]}'; create the whole row first.`,
      );
    if (!isObj(value)) throw new EditorException(`'${path}' must be set to a whole mapping.`);
    newRow = value;
    expected.push(clone(newRow));
    if (isSeq(root) && root.flow)
      throw new EditorException('A flow-style YAML root sequence is not supported.');
    const items = isSeq(root) ? (root.items as YNode[]) : [];
    const indent = items.length > 0 ? rowIndent(text, items[0]!) : '';
    const rendered = renderRow(newRow, indent, nl) + nl;
    let updated: string;
    if (items.length > 0) {
      const at = lineEnd(text, contentEnd(items[items.length - 1]!) - 1);
      updated = splice(text, at, at, (at > 0 && text[at - 1] === '\n' ? '' : nl) + rendered);
    } else {
      updated = text + (text.length === 0 || text.endsWith('\n') ? '' : nl) + rendered;
    }
    return verify(updated, expected, `set '${path}'`);
  }

  private removeRow(root: YNode | undefined, segments: string[], path: string): YamlEditor {
    const found = findRow(root, segments[0]!);
    if (!found) return this;
    const text = this.text;
    const expected = documentArray(root);
    const nl = newlineOf(text);
    const rest = segments.slice(1);
    if (rest.length === 0) {
      expected.splice(found.index, 1);
      const r = rowRegion(text, found.row);
      return verify(splice(text, r.start, r.end, ''), expected, `remove '${path}'`);
    }

    const row = toJson(found.row);
    if (!isObj(row))
      throw new EditorException(`Cannot remove '${path}': the row is not a mapping.`);
    const resolved = resolveInRow(root, segments);
    if (resolved.node === undefined && !resolved.present) return this;
    removeIn(row, rest);
    expected[found.index] = clone(row);
    return verify(replaceRow(text, found.row, row, nl), expected, `remove '${path}'`);
  }
}
