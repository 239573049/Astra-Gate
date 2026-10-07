import { readJsonFile, writeJsonFile } from './json-file.js';
import { homePaths } from './paths.js';

/** Shape of install.json, written by the CLI and read by the Electron app. */
export interface InstallJson {
  serverPath?: string;
  serverVersion?: string;
  desktopPath?: string;
  desktopVersion?: string;
  updatedAt?: string;
}

export function readInstallJson(home: string): InstallJson | null {
  return readJsonFile<InstallJson>(homePaths(home).installFile);
}

export function writeInstallJson(home: string, data: InstallJson): void {
  writeJsonFile(homePaths(home).installFile, data);
}

function nowIso(): string {
  return new Date().toISOString();
}

export function applyServerInfo(
  existing: InstallJson | null,
  server: { serverPath: string; serverVersion?: string },
): InstallJson {
  return {
    ...(existing ?? {}),
    serverPath: server.serverPath,
    ...(server.serverVersion ? { serverVersion: server.serverVersion } : {}),
    updatedAt: nowIso(),
  };
}

export function applyDesktopInfo(
  existing: InstallJson | null,
  desktop: { desktopPath: string; desktopVersion?: string },
): InstallJson {
  return {
    ...(existing ?? {}),
    desktopPath: desktop.desktopPath,
    ...(desktop.desktopVersion ? { desktopVersion: desktop.desktopVersion } : {}),
    updatedAt: nowIso(),
  };
}

export function clearDesktopInfo(existing: InstallJson | null): InstallJson {
  const { desktopPath: _dp, desktopVersion: _dv, ...rest } = existing ?? {};
  return { ...rest, updatedAt: nowIso() };
}
