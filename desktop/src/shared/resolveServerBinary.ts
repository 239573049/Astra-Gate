import * as path from 'node:path';
import { isNewerVersion } from '@aidotnet/update-core';

export interface ServerBinary {
  path: string;
  source: 'install' | 'env' | 'bundled' | 'dev';
  /** Version the binary is expected to report, when known up front (install.json / bundled app). Null for env and dev targets. */
  version: string | null;
}

export interface ResolveServerBinaryOptions {
  /** serverPath from install.json (highest priority). */
  installServerPath?: string | null;
  /** serverVersion from install.json, compared against the bundled server. */
  installServerVersion?: string | null;
  /** ASTRA_SERVER_BIN env value. */
  envServerBin?: string | null;
  /** Server shipped inside a packaged app (resources/server), with the app's version. */
  bundled?: { path: string; version: string } | null;
  /** Repository root, used for the dev fallback path. */
  repoRoot?: string | null;
  platform?: NodeJS.Platform;
  exists: (p: string) => boolean;
}

/** Dev fallback: <repo>/src/Astra.Server/bin/Debug/net10.0/astra-server. */
export function devServerBinaryPath(
  repoRoot: string,
  platform: NodeJS.Platform = process.platform,
): string {
  const ext = platform === 'win32' ? '.exe' : '';
  return path.join(repoRoot, 'src', 'Astra.Server', 'bin', 'Debug', 'net10.0', `astra-server${ext}`);
}

/** Server bundled with a packaged app: <resources>/server/astra-server[.exe]. */
export function bundledServerBinaryPath(
  resourcesPath: string,
  platform: NodeJS.Platform = process.platform,
): string {
  const ext = platform === 'win32' ? '.exe' : '';
  return path.join(resourcesPath, 'server', `astra-server${ext}`);
}

/**
 * Resolves the server binary in priority order:
 * 1. install.json -> serverPath, unless it records a version older than the
 *    bundled server (after an installer upgrade a stale managed binary must
 *    not keep winning)
 * 2. ASTRA_SERVER_BIN env
 * 3. the server bundled with the packaged app (standalone installers)
 * 4. dev build under <repoRoot>/src/Astra.Server/bin/Debug/net10.0
 * Only returns candidates that `exists` accepts; null when nothing matches.
 * The returned `version` feeds the running-server takeover check in service.ts.
 */
export function resolveServerBinary(opts: ResolveServerBinaryOptions): ServerBinary | null {
  const bundled = opts.bundled && opts.exists(opts.bundled.path) ? opts.bundled : null;
  const installIsStale =
    bundled !== null &&
    !!opts.installServerVersion &&
    isNewerVersion(bundled.version, opts.installServerVersion);

  const candidates: ServerBinary[] = [];
  if (opts.installServerPath && opts.installServerPath.trim() !== '' && !installIsStale) {
    candidates.push({ path: opts.installServerPath, source: 'install', version: opts.installServerVersion ?? null });
  }
  if (opts.envServerBin && opts.envServerBin.trim() !== '') {
    candidates.push({ path: opts.envServerBin, source: 'env', version: null });
  }
  if (bundled) {
    candidates.push({ path: bundled.path, source: 'bundled', version: bundled.version });
  }
  if (opts.repoRoot) {
    candidates.push({
      path: devServerBinaryPath(opts.repoRoot, opts.platform ?? process.platform),
      source: 'dev',
      version: null,
    });
  }
  for (const candidate of candidates) {
    if (opts.exists(candidate.path)) return candidate;
  }
  return null;
}

/**
 * True when a running server must be stopped and replaced by the resolved
 * binary: only a target with a known version decides (install.json / bundled),
 * and only when it differs from the running one. Env/dev targets and probes
 * that could not read the running version adopt the server instead — the
 * desktop must never kill a process it cannot vouch for.
 */
export function shouldReplaceRunningServer(runningVersion: string | null, target: ServerBinary | null): boolean {
  return (
    runningVersion !== null &&
    target !== null &&
    target.version !== null &&
    target.version !== runningVersion
  );
}
