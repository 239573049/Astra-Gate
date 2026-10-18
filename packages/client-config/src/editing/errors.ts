/** Raised when an editor self-check (re-parse and compare) fails, or the document is malformed. */
export class EditorException extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'EditorException';
  }
}
