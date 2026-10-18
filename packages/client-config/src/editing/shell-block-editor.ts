import { EditorException } from './errors.js';
import { newlineOf } from './text-tool.js';

interface Span {
  start: number;
  end: number;
  bodyStart: number;
  bodyEnd: number;
}

const trimEndNewlines = (s: string): string => s.replace(/[\r\n]+$/, '');
const trimEndLf = (s: string): string => s.replace(/\n+$/, '');

// .NET char.IsWhiteSpace set, plus angle brackets.
const WS_OR_ANGLE = /[\t-\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000<>]/;

function validateId(id: string): void {
  if (id.length === 0 || WS_OR_ANGLE.test(id)) {
    const e = new Error(
      "Shell block ids must be non-empty and contain no whitespace or angle brackets. (Parameter 'id')",
    );
    e.name = 'ArgumentError';
    throw e;
  }
}

function normalizeBody(body: string): string {
  const normalized = trimEndLf(body.replaceAll('\r\n', '\n'));
  if (normalized.includes('\r'))
    throw new EditorException('Shell block bodies must not contain carriage returns.');
  return normalized;
}

/**
 * Line-based editor for Astra-managed blocks in a POSIX shell rc file (`~/.zshrc`, `~/.bashrc`). A
 * block is delimited by `# >>> astra <id> >>>` and `# <<< astra <id> <<<` lines; everything outside
 * the markers is preserved as-is. Block bodies are exchanged with "\n" line endings. Every mutation
 * is verified by re-reading the block (throws EditorException on mismatch).
 */
export class ShellBlockEditor {
  constructor(private readonly _text: string) {}

  /** Current document text (after any mutations). */
  get text(): string {
    return this._text;
  }

  static beginMarker(id: string): string {
    return `# >>> astra ${id} >>>`;
  }

  static endMarker(id: string): string {
    return `# <<< astra ${id} <<<`;
  }

  /** The block body (lines joined by "\n", without the markers), or null when absent. */
  get(id: string): string | null {
    const block = this.find(id);
    if (block === null) return null;
    const body = this._text.slice(block.bodyStart, block.bodyEnd);
    return trimEndLf(body.replaceAll('\r\n', '\n'));
  }

  /** True when the block exists. */
  has(id: string): boolean {
    return this.find(id) !== null;
  }

  /** Replaces the block in place when it exists; otherwise appends it at the end of the file. */
  set(id: string, body: string): ShellBlockEditor {
    validateId(id);
    const normalized = normalizeBody(body);
    const nl = newlineOf(this._text);
    const block =
      ShellBlockEditor.beginMarker(id) +
      nl +
      normalized.split('\n').join(nl) +
      nl +
      ShellBlockEditor.endMarker(id) +
      nl;
    const span = this.find(id);
    if (span !== null)
      return this.verify(
        this._text.slice(0, span.start) + block + this._text.slice(span.end),
        id,
        normalized,
      );
    const prefix = this._text.length === 0 || this._text.endsWith('\n') ? '' : nl;
    return this.verify(this._text + prefix + block, id, normalized);
  }

  /** Removes the block including its markers. No-op when absent. */
  remove(id: string): ShellBlockEditor {
    validateId(id);
    const span = this.find(id);
    if (span === null) return this;
    return this.verify(this._text.slice(0, span.start) + this._text.slice(span.end), id, null);
  }

  private find(id: string): Span | null {
    const begin = ShellBlockEditor.beginMarker(id);
    const end = ShellBlockEditor.endMarker(id);
    let start = -1;
    let bodyStart = -1;
    let offset = 0;
    while (offset < this._text.length) {
      const next = this._text.indexOf('\n', offset);
      const lineEnd = next < 0 ? this._text.length : next + 1;
      const content = trimEndNewlines(this._text.slice(offset, lineEnd));
      if (start < 0) {
        if (content === begin) {
          start = offset;
          bodyStart = lineEnd;
        }
      } else if (content === end) {
        return { start, end: lineEnd, bodyStart, bodyEnd: offset };
      }
      offset = lineEnd;
    }
    if (start >= 0)
      throw new EditorException(`Unterminated Astra block '${id}' (missing '${end}').`);
    return null;
  }

  private verify(updated: string, id: string, expected: string | null): ShellBlockEditor {
    const editor = new ShellBlockEditor(updated);
    if (editor.get(id) !== expected)
      throw new EditorException(`Shell block '${id}' did not verify after the edit.`);
    return editor;
  }
}
