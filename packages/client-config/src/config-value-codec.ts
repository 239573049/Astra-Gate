import { parse, type ParseError } from 'jsonc-parser';
import type { ConfigFileFormat } from './types.js';

/**
 * Mirrors System.Text.Json's default string encoder (JavaScriptEncoder.Default): printable ASCII
 * other than `" & ' + < > \` and backtick stays literal; everything else is \uXXXX (upper-case hex),
 * with short escapes for \b \t \n \f \r and \\ . Lone surrogates become �.
 */
function encodeString(value: string): string {
  let out = '"';
  for (let i = 0; i < value.length; i++) {
    const c = value.charCodeAt(i);
    const ch = value[i]!;
    if (ch === '\\') out += '\\\\';
    else if (ch === '\b') out += '\\b';
    else if (ch === '\t') out += '\\t';
    else if (ch === '\n') out += '\\n';
    else if (ch === '\f') out += '\\f';
    else if (ch === '\r') out += '\\r';
    else if (c >= 0x20 && c < 0x7f && !'"&\'+<>`'.includes(ch)) out += ch;
    else {
      let unit = c;
      if (c >= 0xd800 && c <= 0xdbff) {
        const n = value.charCodeAt(i + 1);
        if (n >= 0xdc00 && n <= 0xdfff) {
          out += hex4(c) + hex4(n);
          i++;
          continue;
        }
        unit = 0xfffd;
      } else if (c >= 0xdc00 && c <= 0xdfff) unit = 0xfffd;
      out += hex4(unit);
    }
  }
  return out + '"';
}

const hex4 = (c: number): string => '\\u' + c.toString(16).toUpperCase().padStart(4, '0');

/** Decodes the string a JSON text holds; null JSON yields null, a non-string or malformed text throws. */
function decodeString(json: string): string | null {
  const v: unknown = JSON.parse(json);
  if (v === null) return null;
  if (typeof v !== 'string') throw new SyntaxError('The JSON value could not be converted to System.String.');
  return v;
}

function parseNode(text: string): unknown {
  const errors: ParseError[] = [];
  const v: unknown = parse(text, errors, { allowTrailingComma: true, disallowComments: false });
  // A JSON null (JsonNode null) counts as unparseable here, like the C# `a is not null` guard.
  if (errors.length > 0 || v === undefined) return null;
  return v;
}

function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a) && Array.isArray(b))
    return a.length === b.length && a.every((x, i) => deepEqual(x, b[i]));
  const ao = a as Record<string, unknown>;
  const bo = b as Record<string, unknown>;
  const ak = Object.keys(ao);
  return (
    ak.length === Object.keys(bo).length &&
    ak.every((k) => Object.hasOwn(bo, k) && deepEqual(ao[k], bo[k]))
  );
}

/** Formats whose editors exchange values as JSON text (JSON itself, and YAML via its JSON model). */
export const isJsonValued = (format: ConfigFileFormat): boolean =>
  format === 'json' || format === 'yaml';

/**
 * Encodes a raw value source text as the JSON text stored in plans/state. JSON and YAML value
 * texts are already JSON and returned as-is; TOML/.env raw source text is JSON-quoted.
 */
export function encode(format: ConfigFileFormat, rawSourceText: string | null): string | null {
  if (rawSourceText === null) return null;
  return isJsonValued(format) ? rawSourceText : encodeString(rawSourceText);
}

/** Decodes stored JSON text back into the raw value source text to splice into the file. */
export function decodeToSource(format: ConfigFileFormat, jsonText: string | null): string | null {
  if (jsonText === null) return null;
  return isJsonValued(format) ? jsonText : decodeString(jsonText);
}

/** Compares the current value source text against the recorded JSON text (structural for JSON/YAML). */
export function equalsValue(
  format: ConfigFileFormat,
  currentSource: string | null,
  recordedJson: string | null,
): boolean {
  if (recordedJson === null) return currentSource === null;
  if (currentSource === null) return false;
  if (isJsonValued(format)) {
    const a = parseNode(currentSource);
    const b = parseNode(recordedJson);
    return a !== null && b !== null && deepEqual(a, b);
  }
  return currentSource === decodeToSource(format, recordedJson);
}

/** Encoding helpers for the JSON value texts stored in plans and client_config_state. */
export const ConfigValueCodec = { encode, decodeToSource, equalsValue, isJsonValued };
