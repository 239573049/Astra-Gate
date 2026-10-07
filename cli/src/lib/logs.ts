import fs from 'node:fs';
import path from 'node:path';
import { AstraError } from '../errors.js';
import { homePaths } from './paths.js';

/** Newest logs/astra-YYYYMMDD.log, falling back to logs/server-stdout.log. */
export function findNewestLog(home: string): string {
  const { logsDir, serverStdoutLog } = homePaths(home);
  let candidates: string[] = [];
  try {
    candidates = fs
      .readdirSync(logsDir)
      .filter((f) => /^astra-\d{8}\.log$/.test(f))
      .map((f) => path.join(logsDir, f));
  } catch {
    /* no logs dir */
  }
  candidates.sort((a, b) => mtimeOf(b) - mtimeOf(a));
  const newest = candidates[0];
  if (newest) return newest;
  if (fs.existsSync(serverStdoutLog)) return serverStdoutLog;
  throw new AstraError('No log files found.', 'Start the server first: `astra start`.');
}

function mtimeOf(p: string): number {
  try {
    return fs.statSync(p).mtimeMs;
  } catch {
    return 0;
  }
}

export function tailLines(filePath: string, lineCount: number): string {
  const text = fs.readFileSync(filePath, 'utf8');
  const lines = text.split(/\r?\n/);
  while (lines.length > 0 && lines[lines.length - 1] === '') lines.pop();
  return lines.slice(-lineCount).join('\n');
}

/** Poll a file for growth and stream appended content. Returns a stop function. */
export function followFile(filePath: string, write: (chunk: string) => void): () => void {
  let pos = fs.existsSync(filePath) ? fs.statSync(filePath).size : 0;
  let stopped = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const tick = (): void => {
    if (stopped) return;
    try {
      const st = fs.existsSync(filePath) ? fs.statSync(filePath) : null;
      if (!st) {
        pos = 0;
      } else if (st.size < pos) {
        pos = 0; // rotated or truncated
      } else if (st.size > pos) {
        const fd = fs.openSync(filePath, 'r');
        try {
          const buf = Buffer.alloc(st.size - pos);
          const bytesRead = fs.readSync(fd, buf, 0, buf.length, pos);
          pos += bytesRead;
          write(buf.toString('utf8'));
        } finally {
          fs.closeSync(fd);
        }
      }
    } catch {
      /* keep polling */
    }
    timer = setTimeout(tick, 1_000);
  };
  tick();
  return () => {
    stopped = true;
    if (timer) clearTimeout(timer);
  };
}
