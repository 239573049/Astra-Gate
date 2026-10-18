/** Low-level text utilities shared by the format-preserving editors (port of Astra.Clients TextTool). */

/** True when the text uses CRLF line endings (any CRLF wins over LF). */
export const usesCrlf = (text: string): boolean => text.includes('\r\n');

/** The newline used by `text` ("\r\n" or "\n"). */
export const newlineOf = (text: string): string => (usesCrlf(text) ? '\r\n' : '\n');

/** Offset of the first character of the line containing `offset`. */
export function lineStart(text: string, offset: number): number {
  let i = Math.min(Math.max(offset, 0), text.length);
  while (i > 0 && text[i - 1] !== '\n') i--;
  return i;
}

/** Offset just past the end of the line containing `offset` (past the '\n'). */
export function lineEnd(text: string, offset: number): number {
  let i = Math.min(Math.max(offset, 0), text.length);
  while (i < text.length && text[i] !== '\n') i++;
  return i < text.length ? i + 1 : i;
}

/** Whitespace ("indentation") between the start of the line containing `offset` and `offset`. */
export function indentBefore(text: string, offset: number): string {
  const start = lineStart(text, offset);
  let i = start;
  while (i < offset && (text[i] === ' ' || text[i] === '\t')) i++;
  return text.slice(start, i);
}

/** True when `text` is null, empty or whitespace only. */
export const isBlank = (text: string | null | undefined): boolean => text == null || text.trim() === '';

/** Replaces the region [start, end) in `text` with `replacement` and returns the new string. */
export function splice(text: string, start: number, end: number, replacement: string): string {
  if (start < 0 || end < start || end > text.length) throw new RangeError('end');
  return text.slice(0, start) + replacement + text.slice(end);
}

/** True when the offset is preceded (within the same line) only by whitespace or nothing. */
export function atLineContentStart(text: string, offset: number): boolean {
  const start = lineStart(text, offset);
  for (let i = start; i < offset; i++) if (!/\s/.test(text[i]!)) return false;
  return true;
}
