import fs from 'node:fs';
import path from 'node:path';

/**
 * install.json (~/.astra/install.json) is the cross-process anchor: the CLI
 * writes it, the desktop app reads it, and after an update it is what makes
 * both resolvers pick the new managed binary. Shape matches the CLI's
 * InstallJson exactly.
 */
export interface InstallInfo {
  serverPath?: string;
  serverVersion?: string;
  desktopPath?: string;
  desktopVersion?: string;
  updatedAt?: string;
}

export function readInstallInfo(home: string): InstallInfo | null {
  let text: string;
  try {
    text = fs.readFileSync(path.join(home, 'install.json'), 'utf8');
  } catch {
    return null;
  }
  if (!text.trim()) return null;
  try {
    const raw = JSON.parse(text) as Record<string, unknown> | null;
    if (!raw || typeof raw !== 'object') return null;
    const info: InstallInfo = {};
    for (const key of ['serverPath', 'serverVersion', 'desktopPath', 'desktopVersion', 'updatedAt'] as const) {
      const v = raw[key];
      if (typeof v === 'string' && v.length > 0) info[key] = v;
    }
    return info;
  } catch {
    return null;
  }
}

export function writeInstallInfo(home: string, info: InstallInfo): void {
  const file = path.join(home, 'install.json');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${JSON.stringify(info, null, 2)}\n`);
}

export function applyServerInfo(existing: InstallInfo | null, server: { serverPath: string; serverVersion?: string }): InstallInfo {
  return {
    ...(existing ?? {}),
    serverPath: server.serverPath,
    ...(server.serverVersion ? { serverVersion: server.serverVersion } : {}),
    updatedAt: new Date().toISOString(),
  };
}
