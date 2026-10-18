/** What a command needs from the process; tests replace it. */
export interface Io {
  env: NodeJS.ProcessEnv;
  /** Both ends are a terminal and the command may ask questions. */
  interactive: boolean;
  readStdin(): Promise<string>;
}

export function processIo(): Io {
  return {
    env: process.env,
    interactive: process.stdin.isTTY === true && process.stdout.isTTY === true,
    readStdin: async () => {
      const chunks: Buffer[] = [];
      for await (const chunk of process.stdin) chunks.push(Buffer.from(chunk as Buffer));
      return Buffer.concat(chunks).toString('utf8');
    },
  };
}

/** A failure the user should read and act on: printed without a stack, exit code 1. */
export class UserError extends Error {
  constructor(
    message: string,
    readonly exitCode = 1,
  ) {
    super(message);
    this.name = 'UserError';
  }
}
