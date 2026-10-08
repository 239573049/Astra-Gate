import fs from 'node:fs';
import {
  commitManagedWebRoot,
  commitNativeCompanions,
  findNativeCompanions,
  installManagedBinary,
  installManagedWebRoot,
  isNewerVersion,
  pruneManagedBinaries,
  updatePaths,
} from '@aidotnet/update-core';
import { findPackagedServerBinary } from './binary.js';
import {
  applyServerInfo,
  readInstallJson,
  writeInstallJson,
  type InstallJson,
} from './install-json.js';

/**
 * npx / pnpm dlx run packages from a throwaway cache (~/.npm/_npx/<hash>/…,
 * …/pnpm/dlx/…) that may be pruned at any time — never a durable server path.
 */
export function isEphemeralPackagePath(p: string): boolean {
  return /[\\/](_npx|dlx)[\\/]/.test(p);
}

/** Whether install.json already points at a server the desktop app can keep using. */
export function hasUsableServer(
  install: InstallJson | null,
  cliVersion: string,
  exists: (p: string) => boolean = (p) => fs.existsSync(p),
): boolean {
  const current = install?.serverPath?.trim();
  if (!current || !exists(current) || isEphemeralPackagePath(current)) return false;
  // A recorded server older than the one shipped with this CLI gets replaced.
  return !(install?.serverVersion && isNewerVersion(cliVersion, install.serverVersion));
}

export type DesktopServerResult =
  | { action: 'kept' | 'recorded' | 'copied'; serverPath: string }
  | { action: 'missing'; reason: string };

export interface EnsureDesktopServerOptions {
  home: string;
  /** Version of this CLI — its platform package ships the same server version. */
  version: string;
  platform: string;
  /** Test seam — defaults to the platform package installed next to this CLI. */
  findPackaged?: () => { path: string } | { error: string };
}

/**
 * The desktop app finds the server only through install.json / ASTRA_SERVER_BIN
 * (no platform-package fallback), so `astra install --desktop` must leave a
 * durable serverPath behind. A globally installed package path is recorded
 * as-is (the same path `astra start` records). A binary inside an npx cache is
 * copied into the updater's managed dir (~/.astra/server/astra-server-<version>)
 * together with wwwroot and the native companion libraries (SQLite under
 * Native AOT), because the server loads and serves both from the directory of
 * its own executable.
 */
export function ensureDesktopServer(o: EnsureDesktopServerOptions): DesktopServerResult {
  const install = readInstallJson(o.home);
  const current = install?.serverPath;
  if (current && hasUsableServer(install, o.version)) return { action: 'kept', serverPath: current };

  const packaged = (o.findPackaged ?? (() => findPackagedServerBinary()))();
  if (!('path' in packaged)) return { action: 'missing', reason: packaged.error };

  let serverPath = packaged.path;
  let action: 'recorded' | 'copied' = 'recorded';
  if (isEphemeralPackagePath(packaged.path)) {
    const { serverDir } = updatePaths(o.home);
    serverPath = installManagedBinary({
      stagedPath: packaged.path,
      nativeLibraries: findNativeCompanions(packaged.path),
      serverDir,
      version: o.version,
      platform: o.platform,
    });
    if (installManagedWebRoot(packaged.path, serverDir)) commitManagedWebRoot(serverDir);
    commitNativeCompanions(serverDir);
    pruneManagedBinaries(serverDir, serverPath);
    action = 'copied';
  }
  writeInstallJson(
    o.home,
    applyServerInfo(readInstallJson(o.home), { serverPath, serverVersion: o.version }),
  );
  return { action, serverPath };
}
