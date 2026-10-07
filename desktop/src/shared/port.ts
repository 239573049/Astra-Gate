/** Default gateway port, matching config.json's default. */
export const DEFAULT_PORT = 17321;

/**
 * Normalizes an untrusted port value (config.json, env, ...). Falls back to
 * `fallback` unless the value is an integer in [1, 65535].
 */
export function normalizePort(value: unknown, fallback: number = DEFAULT_PORT): number {
  const n =
    typeof value === 'number'
      ? value
      : typeof value === 'string' && value.trim() !== ''
        ? Number(value)
        : Number.NaN;
  if (!Number.isInteger(n) || n < 1 || n > 65535) return fallback;
  return n;
}

/** Base URL the desktop app uses to talk to the local server. */
export function apiBase(port: number): string {
  return `http://127.0.0.1:${port}`;
}
