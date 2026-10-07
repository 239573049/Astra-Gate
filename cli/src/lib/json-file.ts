import fs from 'node:fs';
import path from 'node:path';

export function readTextOrNull(filePath: string): string | null {
  try {
    return fs.readFileSync(filePath, 'utf8');
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code;
    if (code === 'ENOENT' || code === 'EISDIR') return null;
    throw err;
  }
}

/** Read a JSON file; missing or corrupt files are treated as absent (null). */
export function readJsonFile<T>(filePath: string): T | null {
  const text = readTextOrNull(filePath);
  if (text === null || text.trim() === '') return null;
  try {
    return JSON.parse(text) as T;
  } catch {
    return null;
  }
}

export function writeJsonFile(filePath: string, data: unknown, mode: number = 0o644): void {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, `${JSON.stringify(data, null, 2)}\n`, { mode });
}
