import { EditorException } from './errors.js';
import { indentBefore, isBlank, lineEnd, lineStart, splice, usesCrlf } from './text-tool.js';

/**
 * Format-preserving editor for JSON and JSONC documents (comments and trailing commas allowed).
 * Port of Astra.Clients JsoncEditor: tokens are located by a small strict parser (offsets of the
 * values and member names) and every mutation is a surgical text splice, so comments, formatting and
 * ordering elsewhere in the file stay byte-identical. Every mutation re-parses and verifies the
 * expected value, throwing {@link EditorException} instead of returning a corrupted document.
 * Key paths are dotted member names; a segment written as `[key=value&key2=value2]` selects the
 * element of an array whose object has those string members, e.g. `providers.[name=astra]`.
 * Setting a selector path that matches nothing appends the element.
 *
 * Offsets are UTF-16 indexes here (the C# works on UTF-8 bytes); the splices are content-based, so
 * the resulting text is identical.
 */

const DEFAULT_UNIT = '  ';
const MAX_DEPTH = 64;

// ------------------------------------------------------------------ JSON value model

/** A parsed JSON number keeps its source text (System.Text.Json writes it back verbatim). */
export class JNum {
  constructor(readonly raw: string) {}
}

/** A parsed JSON object keeps member order and duplicates (a lazily parsed JsonNode does too). */
export class JObj {
  constructor(readonly entries: [string, JV][]) {}
}

export type JV = null | boolean | string | JNum | JObj | JV[];

/** Plain JS rendition of a JSON value. */
export type JsonPlain =
  null | boolean | number | string | JsonPlain[] | { [key: string]: JsonPlain };

const ESC_SHORT: Record<string, string> = {
  '\b': '\\b',
  '\t': '\\t',
  '\n': '\\n',
  '\f': '\\f',
  '\r': '\\r',
  '\\': '\\\\',
};

/**
 * JSON string literal with System.Text.Json's default encoder: ASCII only; non-ASCII, control
 * characters, DEL and the HTML-sensitive characters `" & ' + < > \`` become \uXXXX (upper-case hex).
 */
export function encodeString(value: string): string {
  let out = '"';
  for (let i = 0; i < value.length; i++) {
    const ch = value[i]!;
    const code = value.charCodeAt(i);
    const short = ESC_SHORT[ch];
    if (short !== undefined) out += short;
    else if (code < 0x20 || code >= 0x7f || '"&\'+<>`'.includes(ch))
      out += '\\u' + code.toString(16).toUpperCase().padStart(4, '0');
    else out += ch;
  }
  return out + '"';
}

/** Number text as System.Text.Json writes a double (shortest round trip, E-notation outside 1e-5..1e15). */
function numberText(n: number): string {
  if (!Number.isFinite(n)) throw new EditorException(`Number ${n} cannot be written as JSON.`);
  if (Object.is(n, -0)) return '-0';
  if (n === 0) return '0';
  const [mantissa, exp] = n.toExponential().split('e') as [string, string];
  const e = Number(exp);
  if (e >= 15 || e < -5)
    return `${mantissa}E${e < 0 ? '-' : '+'}${String(Math.abs(e)).padStart(2, '0')}`;
  return String(n);
}

/** Compact JSON text of a parsed value, byte-identical to `JsonNode.ToJsonString()` for the same value. */
export function compactJson(v: JV): string {
  if (v === null) return 'null';
  if (typeof v === 'boolean') return v ? 'true' : 'false';
  if (typeof v === 'string') return encodeString(v);
  if (v instanceof JNum) return v.raw;
  if (Array.isArray(v)) return '[' + v.map(compactJson).join(',') + ']';
  return '{' + v.entries.map(([k, x]) => encodeString(k) + ':' + compactJson(x)).join(',') + '}';
}

/** Compact JSON text of a plain JS value (the `JsonNode.ToJsonString()` of the equivalent node). */
export function toJsonText(value: unknown): string {
  if (value === null) return 'null';
  switch (typeof value) {
    case 'boolean':
      return value ? 'true' : 'false';
    case 'string':
      return encodeString(value);
    case 'number':
      return numberText(value);
    case 'object': {
      if (Array.isArray(value)) {
        return '[' + value.map((x) => (x === undefined ? 'null' : toJsonText(x))).join(',') + ']';
      }
      const parts: string[] = [];
      for (const [k, x] of Object.entries(value as Record<string, unknown>)) {
        if (x !== undefined) parts.push(encodeString(k) + ':' + toJsonText(x));
      }
      return '{' + parts.join(',') + '}';
    }
    default:
      throw new EditorException(`Cannot serialise a ${typeof value} as JSON.`);
  }
}

/** Throws when an object in the value repeats a member name (what materialising a JsonObject does). */
function assertNoDuplicates(v: JV): void {
  if (Array.isArray(v)) v.forEach(assertNoDuplicates);
  else if (v instanceof JObj) {
    const seen = new Set<string>();
    for (const [k, x] of v.entries) {
      if (seen.has(k)) throw new EditorException(`Duplicate member '${k}' in JSON object.`);
      seen.add(k);
      assertNoDuplicates(x);
    }
  }
}

function toPlain(v: JV): JsonPlain {
  if (v === null || typeof v === 'boolean' || typeof v === 'string') return v;
  if (v instanceof JNum) return Number(v.raw);
  if (Array.isArray(v)) return v.map(toPlain);
  return Object.fromEntries(v.entries.map(([k, x]) => [k, toPlain(x)]));
}

/** `JsonNode.DeepEquals`: objects compare member-wise regardless of order. Throws on duplicate members. */
export function jsonDeepEquals(a: JV, b: JV): boolean {
  assertNoDuplicates(a);
  assertNoDuplicates(b);
  return equalsRec(a, b);
}

function equalsRec(a: JV, b: JV): boolean {
  if (a === null || b === null) return a === b;
  if (a instanceof JNum)
    return b instanceof JNum && (a.raw === b.raw || Number(a.raw) === Number(b.raw));
  if (Array.isArray(a)) {
    return Array.isArray(b) && a.length === b.length && a.every((x, i) => equalsRec(x, b[i]!));
  }
  if (a instanceof JObj) {
    if (!(b instanceof JObj) || a.entries.length !== b.entries.length) return false;
    const other = new Map(b.entries);
    return a.entries.every(([k, x]) => other.has(k) && equalsRec(x, other.get(k)!));
  }
  return a === b;
}

// ------------------------------------------------------------------ parser

/** A located JSON node: container/value span plus (for objects) member name spans. */
export interface Node {
  kind: 'object' | 'array' | 'value';
  start: number;
  end: number;
  members: Member[];
  elements: Node[];
  value: JV;
}

interface Member {
  name: string;
  nameStart: number;
  nameEnd: number;
  value: Node;
}

export interface ParseOptions {
  /** `//` and block comments are skipped (otherwise they are an error). */
  comments: boolean;
  /** A comma before `}` / `]` is accepted. */
  trailingCommas: boolean;
}

const WS = new Set([' ', '\t', '\r', '\n']);
const NUMBER = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/y;

class Parser {
  private i = 0;
  private depth = 0;
  constructor(
    private readonly s: string,
    private readonly o: ParseOptions,
  ) {}

  parseDocument(): Node {
    if (this.s.length === 0) throw new EditorException('JSON document is empty.');
    const root = this.parseValue();
    this.skipTrivia();
    if (this.i < this.s.length)
      throw this.fail('JSON document has trailing content after the root value.');
    return root;
  }

  private fail(message: string): EditorException {
    return new EditorException(`Invalid JSON document: ${message} (at ${this.i})`);
  }

  private skipTrivia(): void {
    const s = this.s;
    for (;;) {
      while (this.i < s.length && WS.has(s[this.i]!)) this.i++;
      if (s[this.i] !== '/') return;
      if (!this.o.comments) throw this.fail('comments are not allowed');
      const next = s[this.i + 1];
      if (next === '/') {
        this.i += 2;
        while (this.i < s.length && !'\n\r  '.includes(s[this.i]!)) this.i++;
        if (this.i < s.length) this.i++;
      } else if (next === '*') {
        const close = s.indexOf('*/', this.i + 2);
        if (close < 0) throw this.fail('unterminated comment');
        this.i = close + 2;
      } else throw this.fail("unexpected '/'");
    }
  }

  private parseString(): string {
    const s = this.s;
    let out = '';
    this.i++; // opening quote
    for (;;) {
      if (this.i >= s.length) throw this.fail('unterminated string');
      const c = s[this.i]!;
      if (c === '"') {
        this.i++;
        return out;
      }
      if (c.charCodeAt(0) < 0x20) throw this.fail('control character in string');
      if (c !== '\\') {
        out += c;
        this.i++;
        continue;
      }
      const e = s[this.i + 1];
      const simple: Record<string, string> = {
        '"': '"',
        '\\': '\\',
        '/': '/',
        b: '\b',
        f: '\f',
        n: '\n',
        r: '\r',
        t: '\t',
      };
      if (e !== undefined && simple[e] !== undefined) {
        out += simple[e];
        this.i += 2;
      } else if (e === 'u') {
        const hex = s.slice(this.i + 2, this.i + 6);
        if (!/^[0-9a-fA-F]{4}$/.test(hex)) throw this.fail('invalid \\u escape');
        out += String.fromCharCode(parseInt(hex, 16));
        this.i += 6;
      } else throw this.fail('invalid escape');
    }
  }

  private parseValue(): Node {
    this.skipTrivia();
    const s = this.s;
    const start = this.i;
    const c = s[start];
    if (c === undefined) throw this.fail('unexpected end of document');
    if (c === '{') return this.parseObject();
    if (c === '[') return this.parseArray();
    const leaf = (value: JV): Node => {
      const next = s[this.i];
      if (next !== undefined && !WS.has(next) && !',]}/'.includes(next))
        throw this.fail('unexpected character');
      return { kind: 'value', start, end: this.i, members: [], elements: [], value };
    };
    if (c === '"') return leaf(this.parseString());
    for (const [word, value] of [
      ['true', true],
      ['false', false],
      ['null', null],
    ] as const) {
      if (s.startsWith(word, start)) {
        this.i += word.length;
        return leaf(value);
      }
    }
    NUMBER.lastIndex = start;
    const m = NUMBER.exec(s);
    if (!m) throw this.fail('unexpected character');
    this.i += m[0].length;
    return leaf(new JNum(m[0]));
  }

  private enter(): void {
    if (++this.depth > MAX_DEPTH) throw this.fail('maximum depth exceeded');
  }

  private parseObject(): Node {
    const s = this.s;
    const node: Node = {
      kind: 'object',
      start: this.i,
      end: 0,
      members: [],
      elements: [],
      value: null,
    };
    const entries: [string, JV][] = [];
    this.enter();
    this.i++;
    this.skipTrivia();
    if (s[this.i] === '}') this.i++;
    else {
      for (;;) {
        if (s[this.i] !== '"') throw this.fail('expected a property name');
        const nameStart = this.i;
        const name = this.parseString();
        const nameEnd = this.i;
        this.skipTrivia();
        if (s[this.i] !== ':') throw this.fail("expected ':'");
        this.i++;
        const value = this.parseValue();
        node.members.push({ name, nameStart, nameEnd, value });
        entries.push([name, value.value]);
        this.skipTrivia();
        if (s[this.i] === ',') {
          this.i++;
          this.skipTrivia();
          if (s[this.i] === '}') {
            if (!this.o.trailingCommas) throw this.fail('trailing comma');
            this.i++;
            break;
          }
        } else if (s[this.i] === '}') {
          this.i++;
          break;
        } else throw this.fail("expected ',' or '}'");
      }
    }
    this.depth--;
    node.end = this.i;
    node.value = new JObj(entries);
    return node;
  }

  private parseArray(): Node {
    const s = this.s;
    const node: Node = {
      kind: 'array',
      start: this.i,
      end: 0,
      members: [],
      elements: [],
      value: null,
    };
    this.enter();
    this.i++;
    this.skipTrivia();
    if (s[this.i] === ']') this.i++;
    else {
      for (;;) {
        node.elements.push(this.parseValue());
        this.skipTrivia();
        if (s[this.i] === ',') {
          this.i++;
          this.skipTrivia();
          if (s[this.i] === ']') {
            if (!this.o.trailingCommas) throw this.fail('trailing comma');
            this.i++;
            break;
          }
        } else if (s[this.i] === ']') {
          this.i++;
          break;
        } else throw this.fail("expected ',' or ']'");
      }
    }
    this.depth--;
    node.end = this.i;
    node.value = node.elements.map((e) => e.value);
    return node;
  }
}

/** Parses a JSON/JSONC document (one root value) and locates every node. Throws {@link EditorException}. */
export function parseJsonc(text: string, options?: Partial<ParseOptions>): Node {
  return new Parser(text, { comments: true, trailingCommas: true, ...options }).parseDocument();
}

// ------------------------------------------------------------------ editor

interface Found {
  nameStart: number;
  nameEnd: number;
  value: Node;
}

export class JsoncEditor {
  private readonly data: string;
  /** True when the editor was created over blank text (no document yet). */
  private readonly blank: boolean;

  /**
   * Creates an editor over JSON/JSONC text. Blank text is treated as an empty object, or as an empty
   * array when the first write addresses a root array element (a path starting with a selector).
   */
  constructor(text: string) {
    this.blank = isBlank(text);
    this.data = this.blank ? '{}' : text;
  }

  private static of(data: string): JsoncEditor {
    return new JsoncEditor(data);
  }

  /** Current document text (after any mutations). */
  get text(): string {
    return this.data;
  }

  // ---------------------------------------------------------------- queries

  /** The plain JS value at `path` (dotted); undefined when absent, null for a JSON null. */
  get(path: string): JsonPlain | undefined {
    const found = navigate(build(this.data), splitPath(path));
    if (!found) return undefined;
    return toPlain(found.value.value);
  }

  /** Compact JSON text of the value at `path` (`Get(path)?.ToJsonString()`); null when absent or JSON null. */
  getJsonText(path: string): string | null {
    const found = navigate(build(this.data), splitPath(path));
    if (!found || found.value.value === null) return null;
    return compactJson(found.value.value);
  }

  /** True when a value exists at this dotted path. */
  has(path: string): boolean {
    return navigate(build(this.data), splitPath(path)) !== null;
  }

  // ---------------------------------------------------------------- mutations

  /** Sets the value at `path` to a plain JS value, creating intermediate objects. */
  set(path: string, value: unknown): JsoncEditor {
    if (value === null || value === undefined) throw new Error('value is null');
    return this.setRaw(path, toJsonText(value));
  }

  /**
   * Sets the value at `path` to the raw JSON source text, spliced verbatim (used to restore original
   * formatting byte-for-byte).
   */
  setRaw(path: string, rawJsonText: string): JsoncEditor {
    if (isBlank(rawJsonText)) throw new Error('Raw JSON text is empty.');
    const rawRoot = parseJsonc(rawJsonText);
    if (rawRoot.value === null)
      throw new EditorException(`Raw JSON text for '${path}' is not a valid JSON value.`);

    const segments = splitPath(path);
    let data = this.blank && isSelector(segments[0]!) ? '[]' : this.data;
    // A document whose root is an empty array holds nothing to keep: an object key replaces the array with "{}".
    // Spacing inside the brackets and comments around them do not matter, and the comments stay.
    if (!isSelector(segments[0]!)) {
      const root = build(data);
      if (root.kind === 'array' && root.elements.length === 0)
        data = splice(data, root.start, root.end, '{}');
    }
    for (let i = 0; i < segments.length - 1; i++) {
      if (isSelector(segments[i]!)) {
        // Array elements are never created implicitly: only whole elements are appended.
        if (navigate(build(data), segments.slice(0, i + 1)) === null)
          throw new EditorException(
            `Cannot set '${path}': no array element matches '${segments[i]}'.`,
          );
        continue;
      }
      data = ensureContainer(data, segments.slice(0, i + 1), isSelector(segments[i + 1]!));
    }

    const model = build(data);
    const parentSegments = segments.slice(0, -1);
    let parent: Node;
    if (parentSegments.length === 0) parent = model;
    else {
      const p = navigate(model, parentSegments);
      if (!p) throw new EditorException(`Self-check failed: parent of '${path}' missing.`);
      parent = p.value;
    }
    const last = segments[segments.length - 1]!;
    const selector = isSelector(last);
    if (parentSegments.length === 0 && !selector && parent.kind !== 'object')
      throw nonObjectRoot(path, parent.kind);
    if (parent.kind !== (selector ? 'array' : 'object'))
      throw new EditorException(
        `Cannot set '${last}' inside a non-${selector ? 'array' : 'object'} at '${parentSegments.join('.')}'.`,
      );

    let existing: Node | null;
    if (selector) existing = findElement(parent, last);
    else {
      const idx = findMember(parent, last);
      existing = idx >= 0 ? parent.members[idx]!.value : null;
    }
    let result: string;
    if (existing !== null) result = splice(data, existing.start, existing.end, rawJsonText);
    else {
      const [at, end, insert] = buildInsert(parent, selector ? null : last, rawJsonText, data);
      result = splice(data, at, end, insert);
    }

    const verify = JsoncEditor.of(result);
    const found = navigate(build(result), segments);
    if (!found || found.value.value === null)
      throw new EditorException(`Self-check failed: '${path}' missing after set.`);
    if (!jsonDeepEquals(found.value.value, rawRoot.value))
      throw new EditorException(`Self-check failed: value at '${path}' differs after set.`);
    return verify;
  }

  /** Removes the value at `path`. No-op when absent. */
  remove(path: string): JsoncEditor {
    const data = this.data;
    const model = build(data);
    const member = navigate(model, splitPath(path));
    if (member === null) return this;

    let start = member.nameStart;
    let end = member.value.end;

    // A member whose value is followed only by an optional comma and whitespace to the end of its
    // line owns that line: remove it whole. Any comma the removal strands before '}' is a trailing
    // comma, which JSONC allows, so it stays for trailing-comma files; files without any trailing
    // comma may be read by strict JSON parsers, so there the stranded comma is removed too.
    const lEnd = lineEnd(data, end);
    let afterValue = end;
    if (afterValue < lEnd && data[afterValue] === ',') afterValue++;
    let strandedComma = -1;
    if (atLineContentStart(data, start) && onlyWhitespace(data, afterValue, lEnd)) {
      start = lineStart(data, start);
      end = lEnd;
      const before = skipWsBackward(data, start);
      const next = skipWsForward(data, end);
      if (
        before >= 0 &&
        data[before] === ',' &&
        next < data.length &&
        (data[next] === '}' || data[next] === ']') &&
        !usesTrailingCommas(data)
      )
        strandedComma = before;
    } else {
      const p = skipWsForward(data, end);
      if (p < data.length && data[p] === ',') end = p + 1;
      else {
        const q = skipWsBackward(data, start);
        if (q >= 0 && data[q] === ',') start = q;
      }
    }

    let spliced = splice(data, start, end, '');
    if (strandedComma >= 0) spliced = splice(spliced, strandedComma, strandedComma + 1, '');
    const verify = JsoncEditor.of(spliced);
    if (verify.has(path))
      throw new EditorException(`Self-check failed: '${path}' still present after remove.`);
    build(spliced); // a successful full re-parse proves the commas are still balanced
    return verify;
  }

  // ---------------------------------------------------------------- key paths and array selectors

  /** Splits a dotted key path; dots inside a `[...]` selector segment do not split. */
  static splitKeyPath(path: string): string[] {
    return splitKeyPath(path);
  }

  /** Builds an array-element selector segment such as `[provider=astra&id=gpt-5]`. */
  static selector(...conditions: [key: string, value: string][]): string {
    if (conditions.length === 0) throw new Error('A selector needs at least one condition.');
    for (const [key, value] of conditions) {
      if (key.length === 0 || /[[\]=&]/.test(key) || /[[\]=&]/.test(value))
        throw new EditorException(`'${key}=${value}' cannot be used in an array selector.`);
    }
    return '[' + conditions.map(([k, v]) => k + '=' + v).join('&') + ']';
  }

  /** True when the path segment is an array-element selector. */
  static isSelector(segment: string): boolean {
    return isSelector(segment);
  }

  /** True when `element` is an object whose string members satisfy the selector. */
  static elementMatches(element: unknown, selector: string): boolean {
    if (element === null || typeof element !== 'object' || Array.isArray(element)) return false;
    const entries: [string, unknown][] =
      element instanceof JObj
        ? element.entries
        : Object.entries(element as Record<string, unknown>);
    return matches(entries, selector);
  }
}

// ------------------------------------------------------------------ container scaffolding

/** A key cannot be written into a document whose top level is not an object (an empty array is replaced before this). */
function nonObjectRoot(path: string, kind: Node['kind']): EditorException {
  return new EditorException(
    `Cannot write '${path}': the file's top level is ${kind === 'array' ? 'an array' : 'a value'}, not an object. ` +
      'Astra only replaces an empty array; move its entries into an object by hand.',
  );
}

function ensureContainer(data: string, segments: string[], array: boolean): string {
  const kind = array ? 'array' : 'object';
  const model = build(data);
  const parentSegments = segments.slice(0, -1);
  const parent =
    parentSegments.length === 0 ? model : (navigate(model, parentSegments)?.value ?? null);
  if (parent !== null && parent.kind !== 'object' && parentSegments.length === 0)
    throw nonObjectRoot(segments.join('.'), parent.kind);
  if (parent === null || parent.kind !== 'object')
    throw new EditorException(
      `Cannot create '${segments.join('.')}': parent is missing or not an object.`,
    );
  const name = segments[segments.length - 1]!;
  const idx = findMember(parent, name);
  if (idx >= 0) {
    if (parent.members[idx]!.value.kind !== kind)
      throw new EditorException(
        `'${segments.join('.')}' already exists and is not an ${array ? 'array' : 'object'}.`,
      );
    return data;
  }
  const [at, end, insert] = buildInsert(parent, name, array ? '[]' : '{}', data);
  return splice(data, at, end, insert);
}

/** Splice region and text for appending a member to an object, or (null name) an element to an array. */
function buildInsert(
  parent: Node,
  name: string | null,
  valueText: string,
  data: string,
): [at: number, end: number, text: string] {
  const nl = usesCrlf(data) ? '\r\n' : '\n';
  const memberSource = (name === null ? '' : encodeString(name) + ': ') + valueText;
  const open = parent.start;
  const close = parent.end - 1; // index of '}' or ']'
  const lastContent = skipWsBackward(data, close);
  const multiline = containsNewline(data, open, close);
  const count = parent.kind === 'array' ? parent.elements.length : parent.members.length;
  const memberIndent =
    count > 0
      ? indentBefore(
          data,
          parent.kind === 'array' ? parent.elements[0]!.start : parent.members[0]!.nameStart,
        )
      : indentBefore(data, open) + DEFAULT_UNIT;

  if (count === 0) {
    // Replace the (possibly whitespace-only) interior of the empty container.
    const inner = multiline
      ? nl + memberIndent + memberSource + nl + indentBefore(data, open)
      : ' ' + memberSource + ' ';
    return [open + 1, close, inner];
  }
  if (lastContent >= 0 && data[lastContent] === ',') {
    // A trailing comma is already there: reuse it.
    const t = multiline ? nl + memberIndent + memberSource : ' ' + memberSource;
    return [lastContent + 1, lastContent + 1, t];
  }
  const t = multiline ? ',' + nl + memberIndent + memberSource : ', ' + memberSource;
  return [lastContent + 1, lastContent + 1, t];
}

// ------------------------------------------------------------------ document model

function build(data: string): Node {
  return parseJsonc(data);
}

function navigate(root: Node, segments: readonly string[]): Found | null {
  if (segments.length === 0) return null;
  let current = root;
  let result: Found | null = null;
  for (const segment of segments) {
    if (isSelector(segment)) {
      // An array element has no member name: its "name" span is empty at the element start.
      if (current.kind !== 'array') return null;
      const element = findElement(current, segment);
      if (element === null) return null;
      result = { nameStart: element.start, nameEnd: element.start, value: element };
      current = element;
      continue;
    }
    if (current.kind !== 'object') return null;
    const idx = findMember(current, segment);
    if (idx < 0) return null;
    const m = current.members[idx]!;
    result = { nameStart: m.nameStart, nameEnd: m.nameEnd, value: m.value };
    current = m.value;
  }
  return result;
}

/** The first object element of an array that matches a `[key=value&...]` selector. */
function findElement(array: Node, selector: string): Node | null {
  for (const element of array.elements) {
    if (element.kind !== 'object') continue;
    assertNoDuplicates(element.value);
    if (matches((element.value as JObj).entries, selector)) return element;
  }
  return null;
}

function findMember(parent: Node, name: string): number {
  return parent.members.findIndex((m) => m.name === name);
}

function matches(entries: [string, unknown][], selector: string): boolean {
  for (const condition of selector.slice(1, -1).split('&')) {
    const eq = condition.indexOf('=');
    if (eq <= 0) throw new EditorException(`Invalid array selector '${selector}'.`);
    const key = condition.slice(0, eq);
    const value = condition.slice(eq + 1);
    const hit = entries.find(([k]) => k === key);
    if (!hit || typeof hit[1] !== 'string' || hit[1] !== value) return false;
  }
  return true;
}

function splitPath(path: string): string[] {
  const parts = splitKeyPath(path);
  if (parts.length === 0) throw new Error('Empty key path.');
  return parts;
}

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

function isSelector(segment: string): boolean {
  return segment.length >= 2 && segment[0] === '[' && segment[segment.length - 1] === ']';
}

// ------------------------------------------------------------------ text helpers

/** True when the document contains at least one trailing comma (JSONC style). */
function usesTrailingCommas(data: string): boolean {
  try {
    parseJsonc(data, { trailingCommas: false });
    return false;
  } catch {
    return true; // the document parses with trailing commas allowed, so they are the difference
  }
}

const isWs = (c: string | undefined): boolean => c !== undefined && WS.has(c);

function atLineContentStart(data: string, offset: number): boolean {
  for (let i = lineStart(data, offset); i < offset; i++) if (!isWs(data[i])) return false;
  return true;
}

function onlyWhitespace(data: string, start: number, end: number): boolean {
  for (let i = start; i < end && i < data.length; i++) if (!isWs(data[i])) return false;
  return true;
}

function containsNewline(data: string, start: number, end: number): boolean {
  for (let i = Math.max(start, 0); i < end && i < data.length; i++)
    if (data[i] === '\n') return true;
  return false;
}

function skipWsForward(data: string, offset: number): number {
  let i = Math.max(offset, 0);
  while (i < data.length && isWs(data[i])) i++;
  return i;
}

function skipWsBackward(data: string, offset: number): number {
  let i = Math.min(offset, data.length) - 1;
  while (i >= 0 && isWs(data[i])) i--;
  return i;
}
