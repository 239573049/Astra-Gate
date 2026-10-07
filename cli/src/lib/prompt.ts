import readline from 'node:readline';
import { Writable } from 'node:stream';

/** Read a line without echoing it (falls back to plain stdin when piped). */
export function promptHidden(question: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const stdin = process.stdin;
    if (!stdin.isTTY) {
      let data = '';
      stdin.setEncoding('utf8');
      stdin.on('data', (chunk: string) => {
        data += chunk;
      });
      stdin.on('end', () => resolve(data.replace(/\r?\n$/, '')));
      stdin.on('error', reject);
      return;
    }
    process.stdout.write(question);
    const muted = new Writable({
      write(_chunk, _encoding, callback) {
        callback();
      },
    });
    const rl = readline.createInterface({ input: stdin, output: muted, terminal: true });
    rl.question('', (answer) => {
      rl.close();
      process.stdout.write('\n');
      resolve(answer);
    });
  });
}

export function confirmPrompt(question: string): Promise<boolean> {
  return new Promise((resolve) => {
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    rl.question(question, (answer) => {
      rl.close();
      resolve(/^\s*(y|yes)/i.test(answer.trim()));
    });
  });
}
