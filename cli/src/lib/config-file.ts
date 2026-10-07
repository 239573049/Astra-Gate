import { AstraError } from '../errors.js';
import { readJsonFile, writeJsonFile } from './json-file.js';
import { homePaths } from './paths.js';

export interface AstraConfig {
  port?: number;
  host?: string;
  logLevel?: string;
  [key: string]: unknown;
}

export const DEFAULT_PORT = 17321;
export const DEFAULT_HOST = '127.0.0.1';

export function readConfig(home: string): AstraConfig {
  return readJsonFile<AstraConfig>(homePaths(home).configFile) ?? {};
}

export function writeConfig(home: string, patch: AstraConfig): AstraConfig {
  const next = { ...readConfig(home), ...patch };
  writeJsonFile(homePaths(home).configFile, next);
  return next;
}

export function normalizePort(value: unknown, fallback: number = DEFAULT_PORT): number {
  if (value === undefined || value === null || value === '') return fallback;
  const n = typeof value === 'number' ? value : Number(value);
  if (!Number.isInteger(n) || n < 1 || n > 65535) {
    throw new AstraError(`Invalid port: ${String(value)}. Use a number between 1 and 65535.`);
  }
  return n;
}

export function normalizeHost(value: unknown, fallback: string = DEFAULT_HOST): string {
  if (value === undefined || value === null || value === '') return fallback;
  if (typeof value !== 'string') throw new AstraError(`Invalid host: ${String(value)}`);
  return value;
}
