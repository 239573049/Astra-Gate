import { DEFAULT_HOST } from './host';
import { DEFAULT_PORT, normalizePort } from './port';

export interface ServerConfig {
  port: number;
  host: string;
}

export const DEFAULT_CONFIG: ServerConfig = { port: DEFAULT_PORT, host: DEFAULT_HOST };

/**
 * Tolerant parser for ~/.astra/config.json. Missing or malformed files
 * fall back to the defaults (port 17321, host 127.0.0.1).
 */
export function parseConfigJson(text: string | null | undefined): ServerConfig {
  if (text == null) return { ...DEFAULT_CONFIG };
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return { ...DEFAULT_CONFIG };
  }
  if (typeof raw !== 'object' || raw === null) return { ...DEFAULT_CONFIG };
  const o = raw as Record<string, unknown>;
  return {
    port: normalizePort(o.port, DEFAULT_PORT),
    host: typeof o.host === 'string' && o.host.trim() !== '' ? o.host : DEFAULT_HOST,
  };
}
