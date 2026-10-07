/** An expected failure with a friendly message and an optional hint line. */
export class AstraError extends Error {
  readonly hint?: string;

  constructor(message: string, hint?: string) {
    super(message);
    this.name = 'AstraError';
    this.hint = hint;
  }
}
