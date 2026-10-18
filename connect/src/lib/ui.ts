import * as p from '@clack/prompts';
import pc from 'picocolors';

/** Where a command talks to the user: rich in a terminal, plain lines in scripts, silent for `--json`. */
export interface Ui {
  readonly interactive: boolean;
  intro(title: string): void;
  outro(text: string): void;
  info(text: string): void;
  success(text: string): void;
  warn(text: string): void;
  error(text: string): void;
  note(body: string, title?: string): void;
  spinner(): { start(text: string): void; stop(text: string): void };
}

export class CancelledError extends Error {
  constructor() {
    super('cancelled');
    this.name = 'CancelledError';
  }
}

/** Unwraps a clack answer; Ctrl+C / Esc ends the command cleanly. */
export function ask<T>(answer: T | symbol): T {
  if (p.isCancel(answer)) {
    p.cancel('已取消');
    throw new CancelledError();
  }
  return answer as T;
}

export const clackUi: Ui = {
  interactive: true,
  intro: (t) => p.intro(pc.bgCyan(pc.black(` ${t} `))),
  outro: (t) => p.outro(t),
  info: (t) => p.log.info(t),
  success: (t) => p.log.success(t),
  warn: (t) => p.log.warn(t),
  error: (t) => p.log.error(t),
  note: (body, title) => p.note(body, title),
  spinner: () => p.spinner(),
};

export const plainUi: Ui = {
  interactive: false,
  intro: () => {},
  outro: (t) => console.log(t),
  info: (t) => console.log(t),
  success: (t) => console.log(`✓ ${t}`),
  warn: (t) => console.error(`! ${t}`),
  error: (t) => console.error(`✗ ${t}`),
  note: (body, title) => console.log(title ? `${title}\n${body}` : body),
  spinner: () => ({ start: () => {}, stop: () => {} }),
};

export const silentUi: Ui = {
  interactive: false,
  intro: () => {},
  outro: () => {},
  info: () => {},
  success: () => {},
  warn: () => {},
  error: () => {},
  note: () => {},
  spinner: () => ({ start: () => {}, stop: () => {} }),
};

export const pick = pc;
export { p as prompts };

/** `sk-astra-1234…cdef`: enough to recognise a key, never the whole of it. */
export const maskKey = (key: string): string =>
  key.length <= 12 ? '••••' : `${key.slice(0, 8)}…${key.slice(-4)}`;

/** A diff or message may hold the key (Astra writes it into the client's config): hide it. */
export const redact = (text: string, key: string): string => text.split(key).join(maskKey(key));
