import { EditorException } from './errors.js';
import { newlineOf } from './text-tool.js';

/** .NET `char.IsWhiteSpace` (differs from JS `trim`: U+0085 is whitespace, U+FEFF is not). */
const WS = '\\t-\\r \\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000';
const trimStartWs = (s: string): string => s.replace(new RegExp(`^[${WS}]+`), '');
const trimEndWs = (s: string): string => s.replace(new RegExp(`[${WS}]+$`), '');
const trimWs = (s: string): string => trimEndWs(trimStartWs(s));
const isWs = (c: string): boolean => new RegExp(`^[${WS}]$`).test(c);
const isBlankWs = (s: string): boolean => trimWs(s).length === 0;

/** Yields lines including their '\n' terminators. */
function splitKeepingTerminators(text: string): string[] {
  const lines: string[] = [];
  let start = 0;
  while (start < text.length) {
    const nl = text.indexOf('\n', start);
    const end = nl < 0 ? text.length : nl + 1;
    lines.push(text.slice(start, end));
    start = end;
  }
  return lines;
}

function trySplit(line: string, key: string): string | null {
  let trimmed = trimStartWs(line);
  if (trimmed.startsWith('#')) return null;
  if (trimmed.startsWith('export') && (trimmed.length === 6 || isWs(trimmed[6]!)))
    trimmed = trimStartWs(trimmed.slice(6));
  const eq = trimmed.indexOf('=');
  if (eq <= 0) return null;
  const name = trimEndWs(trimmed.slice(0, eq));
  if (name !== key) return null;
  return trimWs(trimmed.slice(eq + 1));
}

const indexOfValueStart = (line: string): number => line.indexOf('=') + 1;

function validateKey(key: string): void {
  if (key.length === 0 || /[^A-Za-z0-9_.]/.test(key))
    throw new EditorException(`Invalid .env key '${key}'.`);
}

const terminatorOf = (line: string): string =>
  line.endsWith('\r\n') ? '\r\n' : line.endsWith('\n') ? '\n' : '';

/** A value as its raw source text: unquoted when it has only safe characters, else double-quoted. */
export function formatValue(value: string): string {
  if (value.length === 0) return '""';
  if (/^[A-Za-z0-9_\-./:+@]+$/.test(value)) return value;
  let out = '"';
  for (const c of value) {
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
        break;
      default:
        out += c;
    }
  }
  return out + '"';
}

function unquote(valueText: string): string {
  if (valueText.length >= 2 && valueText[0] === '"' && valueText[valueText.length - 1] === '"') {
    let out = '';
    for (let i = 1; i < valueText.length - 1; i++) {
      const c = valueText[i]!;
      if (c === '\\' && i + 1 < valueText.length - 1) {
        const n = valueText[++i]!;
        switch (n) {
          case 'n':
            out += '\n';
            break;
          case 't':
            out += '\t';
            break;
          case 'r':
            break;
          default:
            out += n;
        }
      } else out += c;
    }
    return out;
  }
  if (valueText.length >= 2 && valueText[0] === "'" && valueText[valueText.length - 1] === "'")
    return valueText.slice(1, -1);
  return valueText;
}

/**
 * Line-based, format-preserving editor for .env files: get/set/remove KEY=value pairs while
 * preserving all other lines, comments, ordering, quoting and line endings. Every mutation is
 * verified by re-reading the result (throws EditorException on mismatch).
 */
export class DotEnvEditor {
  constructor(private readonly _text: string) {}

  /** Current document text (after any mutations). */
  get text(): string {
    return this._text;
  }

  /** A value as its raw source text (what `set` writes and `setRaw` accepts). */
  static formatValue(value: string): string {
    return formatValue(value);
  }

  /** The logical (unquoted) value of `key`, or null when absent. */
  get(key: string): string | null {
    for (const line of splitKeepingTerminators(this._text)) {
      const v = trySplit(line, key);
      if (v !== null) return unquote(v);
    }
    return null;
  }

  /** The raw value source text (as written after '='), or null when absent. */
  getRaw(key: string): string | null {
    for (const line of splitKeepingTerminators(this._text)) {
      const v = trySplit(line, key);
      if (v !== null) return v;
    }
    return null;
  }

  /** True when the key exists (commented lines do not count). */
  has(key: string): boolean {
    return splitKeepingTerminators(this._text).some((l) => trySplit(l, key) !== null);
  }

  /** Sets the key to the raw value source text, spliced verbatim after the '='. */
  setRaw(key: string, rawValueText: string): DotEnvEditor {
    validateKey(key);
    if (isBlankWs(rawValueText)) {
      const e = new Error('Raw value text is empty. (Parameter \'rawValueText\')');
      e.name = 'ArgumentError';
      throw e;
    }
    const lines = splitKeepingTerminators(this._text);
    for (let i = 0; i < lines.length; i++) {
      if (trySplit(lines[i]!, key) === null) continue;
      const eq = indexOfValueStart(lines[i]!);
      lines[i] = lines[i]!.slice(0, eq) + rawValueText + terminatorOf(lines[i]!);
      return this.verifyRaw(lines.join(''), key, rawValueText);
    }
    const nl = newlineOf(this._text);
    const prefix = this._text.length === 0 ? '' : this._text.endsWith('\n') ? '' : nl;
    return this.verifyRaw(this._text + prefix + key + '=' + rawValueText + nl, key, rawValueText);
  }

  /** Sets the key to `value`, quoting when needed. Appends the key when absent. */
  set(key: string, value: string): DotEnvEditor {
    validateKey(key);
    if (value.includes('\n') || value.includes('\r'))
      throw new EditorException('Multi-line values are not supported in .env files.');
    const valueText = formatValue(value);
    const lines = splitKeepingTerminators(this._text);
    for (let i = 0; i < lines.length; i++) {
      if (trySplit(lines[i]!, key) === null) continue;
      const eq = indexOfValueStart(lines[i]!);
      lines[i] = lines[i]!.slice(0, eq) + valueText + terminatorOf(lines[i]!);
      return this.verify(lines.join(''), key, value);
    }
    const nl = newlineOf(this._text);
    const prefix = this._text.length === 0 ? '' : this._text.endsWith('\n') ? '' : nl;
    return this.verify(this._text + prefix + key + '=' + valueText + nl, key, value);
  }

  /** Removes the key's line. No-op when absent. */
  remove(key: string): DotEnvEditor {
    validateKey(key);
    const lines = splitKeepingTerminators(this._text);
    for (let i = 0; i < lines.length; i++) {
      if (trySplit(lines[i]!, key) === null) continue;
      lines.splice(i, 1);
      const updated = lines.join('');
      if (new DotEnvEditor(updated).has(key))
        throw new EditorException(`Self-check failed: ${key} still present after remove.`);
      return new DotEnvEditor(updated);
    }
    return this;
  }

  private verify(updated: string, key: string, expectedLogical: string): DotEnvEditor {
    const actual = new DotEnvEditor(updated).get(key);
    if (actual !== expectedLogical)
      throw new EditorException(
        `Self-check failed: ${key}=${actual ?? '<absent>'}, expected ${expectedLogical}.`,
      );
    return new DotEnvEditor(updated);
  }

  private verifyRaw(updated: string, key: string, expectedRaw: string): DotEnvEditor {
    const editor = new DotEnvEditor(updated);
    const actual = editor.getRaw(key);
    if (actual !== expectedRaw)
      throw new EditorException(
        `Self-check failed: ${key}=${actual ?? '<absent>'}, expected ${expectedRaw}.`,
      );
    return editor;
  }
}
