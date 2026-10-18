import { jsonDeepEquals, parseJsonc } from './jsonc-editor.js';

/**
 * For clients that read their config with a strict JSON parser (Electron's JSON.parse): finds what JSONC
 * allows but strict JSON does not (comments and trailing commas) and removes exactly that. Every other
 * character, whitespace included, stays as it was, and a repair is only returned when the result is strict
 * JSON with the same content. Port of Astra.Clients StrictJson.
 */

/** Problem code: a comma right before a closing `}` or `]`. */
export const TRAILING_COMMA = 'trailing-comma';

/** Problem code: a `//` or block comment. */
export const COMMENT = 'comment';

interface Removal {
  start: number;
  end: number;
  problem: string;
}

/** True when the text parses as strict JSON (no comments, no trailing commas). */
export function isStrict(text: string): boolean {
  try {
    parseJsonc(text, { comments: false, trailingCommas: false });
    return true;
  } catch {
    return false;
  }
}

/**
 * The first thing that keeps a JSONC document from being strict JSON, as a problem code and 1-based line; null
 * when the text is strict already, or is not JSONC either (then nobody can tell what the file should say).
 */
export function findProblem(text: string): { problem: string; line: number } | null {
  const removals = scan(text);
  if (!removals || removals.length === 0 || !verify(text, apply(text, removals))) return null;
  return { problem: removals[0]!.problem, line: lineOf(text, removals[0]!.start) };
}

/** The text without its comments and trailing commas; null when {@link findProblem} finds nothing. */
export function repair(text: string): string | null {
  const removals = scan(text);
  if (!removals || removals.length === 0) return null;
  const repaired = apply(text, removals);
  return verify(text, repaired) ? repaired : null;
}

/** The ranges to remove, in document order; null when a comment is never closed. */
function scan(text: string): Removal[] | null {
  const removals: Removal[] = [];
  let pendingComma = -1;
  let i = 0;
  while (i < text.length) {
    const c = text[i]!;
    if (c === '"') {
      pendingComma = -1;
      i = skipString(text, i);
      continue;
    }
    if (c === '/' && i + 1 < text.length && (text[i + 1] === '/' || text[i + 1] === '*')) {
      const end = text[i + 1] === '/' ? lineCommentEnd(text, i) : blockCommentEnd(text, i);
      if (end < 0) return null;
      const [start, stop] = commentRange(text, i, end);
      removals.push({ start, end: stop, problem: COMMENT });
      i = end; // a comment between a comma and the closing bracket keeps the comma pending
      continue;
    }
    if (c === ',') pendingComma = i;
    else if ((c === '}' || c === ']') && pendingComma >= 0) {
      removals.push({ start: pendingComma, end: pendingComma + 1, problem: TRAILING_COMMA });
      pendingComma = -1;
    } else if (!isWhiteSpace(c)) pendingComma = -1;
    i++;
  }
  removals.sort((a, b) => a.start - b.start); // stable, like List.Sort for distinct starts
  return removals;
}

/** char.IsWhiteSpace. */
const isWhiteSpace = (c: string): boolean => {
  const code = c.charCodeAt(0);
  return (
    (code >= 9 && code <= 13) ||
    code === 0x20 ||
    code === 0x85 ||
    code === 0xa0 ||
    code === 0x1680 ||
    (code >= 0x2000 && code <= 0x200a) ||
    code === 0x2028 ||
    code === 0x2029 ||
    code === 0x202f ||
    code === 0x205f ||
    code === 0x3000
  );
};

/** Index just past the string literal that opens at `i`. */
function skipString(text: string, i: number): number {
  for (let j = i + 1; j < text.length; j++) {
    if (text[j] === '\\') j++;
    else if (text[j] === '"') return j + 1;
  }
  return text.length;
}

/** End of a `//` comment: its line break (which stays), or the end of the text. */
function lineCommentEnd(text: string, i: number): number {
  const n = text.indexOf('\n', i);
  if (n < 0) return text.length;
  return n > i && text[n - 1] === '\r' ? n - 1 : n;
}

/** Index just past the end of a block comment, or -1 when it is never closed. */
function blockCommentEnd(text: string, i: number): number {
  const n = text.indexOf('*/', i + 2);
  return n < 0 ? -1 : n + 2;
}

/**
 * A comment's range, widened to its whole line (break included) when nothing else is on that line, to the blanks
 * before it when it ends a line, and to the blanks after it inside a line, so a repair leaves no empty lines,
 * trailing spaces or doubled blanks behind.
 */
function commentRange(text: string, start: number, end: number): [number, number] {
  let lineStart = start;
  while (lineStart > 0 && (text[lineStart - 1] === ' ' || text[lineStart - 1] === '\t')) lineStart--;
  let lineEnd = end;
  while (lineEnd < text.length && (text[lineEnd] === ' ' || text[lineEnd] === '\t')) lineEnd++;
  const aloneFromStart = lineStart === 0 || text[lineStart - 1] === '\n';
  const endsLine = lineEnd === text.length || text[lineEnd] === '\r' || text[lineEnd] === '\n';
  if (!endsLine) return lineStart < start ? [start, lineEnd] : [start, end];
  if (!aloneFromStart) return [lineStart, lineEnd];
  if (lineEnd < text.length && text[lineEnd] === '\r') lineEnd++;
  if (lineEnd < text.length && text[lineEnd] === '\n') lineEnd++;
  return [lineStart, lineEnd];
}

function apply(text: string, removals: Removal[]): string {
  let out = '';
  let cursor = 0;
  for (const r of removals) {
    if (r.start > cursor) out += text.slice(cursor, r.start);
    cursor = Math.max(cursor, r.end);
  }
  if (cursor < text.length) out += text.slice(cursor);
  return out;
}

/** True when the repaired text is strict JSON holding exactly what the original JSONC text held. */
function verify(original: string, repaired: string): boolean {
  try {
    const before = parseJsonc(original).value;
    const after = parseJsonc(repaired, { comments: false, trailingCommas: false }).value;
    return jsonDeepEquals(before, after); // throws on duplicate member names
  } catch {
    return false; // not JSONC at all, or duplicate member names
  }
}

function lineOf(text: string, index: number): number {
  let line = 1;
  for (let i = 0; i < index && i < text.length; i++) if (text[i] === '\n') line++;
  return line;
}
