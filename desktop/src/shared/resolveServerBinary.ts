import * as path from 'node:path';

export interface ServerBinary {
  path: string;
  source: 'install' | 'env' | 'dev';
}

export interface ResolveServerBinaryOptions {
  /** serverPath from install.json (highest priority). */
  installServerPath?: string | null;
  /** ASTRA_SERVER_BIN env value. */
  envServerBin?: string | null;
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

/**
 * Resolves the server binary in priority order:
 * 1. install.json -> serverPath
 * 2. ASTRA_SERVER_BIN env
 * 3. dev build under <repoRoot>/src/Astra.Server/bin/Debug/net10.0
 * Only returns candidates that `exists` accepts; null when nothing matches.
 */
export function resolveServerBinary(opts: ResolveServerBinaryOptions): ServerBinary | null {
  const candidates: ServerBinary[] = [];
  if (opts.installServerPath && opts.installServerPath.trim() !== '') {
    candidates.push({ path: opts.installServerPath, source: 'install' });
  }
  if (opts.envServerBin && opts.envServerBin.trim() !== '') {
    candidates.push({ path: opts.envServerBin, source: 'env' });
  }
  if (opts.repoRoot) {
    candidates.push({
      path: devServerBinaryPath(opts.repoRoot, opts.platform ?? process.platform),
      source: 'dev',
    });
  }
  for (const candidate of candidates) {
    if (opts.exists(candidate.path)) return candidate;
  }
  return null;
}
