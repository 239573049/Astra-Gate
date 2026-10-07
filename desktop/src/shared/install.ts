import type { InstallInfo } from './types';

/**
 * Tolerant parser for ~/.astra/install.json (written by the CLI).
 * Returns null when the file is missing or not a JSON object.
 */
export function parseInstallJson(text: string | null | undefined): InstallInfo | null {
  if (text == null) return null;
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return null;
  }
  if (typeof raw !== 'object' || raw === null) return null;
  const o = raw as Record<string, unknown>;
  const info: InstallInfo = {};
  for (const key of ['serverPath', 'serverVersion', 'desktopPath', 'desktopVersion', 'updatedAt'] as const) {
    const v = o[key];
    if (typeof v === 'string' && v.length > 0) info[key] = v;
  }
  return info;
}

export function serverPathFromInstall(info: InstallInfo | null): string | null {
  const p = info?.serverPath;
  return typeof p === 'string' && p.trim() !== '' ? p : null;
}
