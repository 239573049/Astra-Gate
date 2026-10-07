import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { AstraError } from '../errors.js';
import { platformInfo } from './platform.js';

export interface ServerCommand {
  /** executable to run */
  command: string;
  /** arguments before the subcommand (non-empty in `dotnet <dll>` dev mode) */
  args: string[];
  /** path recorded in install.json */
  serverPath: string;
  source: 'env' | 'install' | 'repo' | 'package';
}

export interface ResolveServerBinaryOptions {
  env?: NodeJS.ProcessEnv;
  cwd?: string;
  /** serverPath from ~/.astra/install.json — written by the updater and previous starts */
  installServerPath?: string | null;
  /** directory used to resolve @aidotnet/server-* (defaults to this module's directory) */
  baseDir?: string;
  platform?: string;
  arch?: string;
  existsSync?: (p: string) => boolean;
  readFileSync?: (p: string, encoding: 'utf8') => string;
  resolveFrom?: (baseDir: string, request: string) => string;
}

/**
 * Find the repository root by looking for src/Astra.Server, walking up
 * from startDir. Returns null when not inside the repo.
 */
export function findRepoRoot(startDir: string, exists: (p: string) => boolean): string | null {
  let dir = path.resolve(startDir);
  for (;;) {
    if (exists(path.join(dir, 'src', 'Astra.Server'))) return dir;
    const parent = path.dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

function defaultResolveFrom(baseDir: string, request: string): string {
  const require2 = createRequire(path.join(baseDir, 'index.js'));
  return require2.resolve(request);
}

/**
 * Resolution order (mirrors the desktop app's resolver):
 *  1. ASTRA_SERVER_BIN env var
 *  2. install.json serverPath — the updater's managed binary (~/.astra/server/…)
 *  3. repo dev build: src/Astra.Server/bin/Debug/net10.0/astra-server[.exe],
 *     or `dotnet astra-server.dll` when only the dll exists
 *  4. the installed platform package @aidotnet/server-<platform>
 */
export function resolveServerBinary(options: ResolveServerBinaryOptions = {}): ServerCommand {
  const env = options.env ?? process.env;
  const exists = options.existsSync ?? ((p: string) => fs.existsSync(p));
  const readText =
    options.readFileSync ?? ((p: string, _enc: 'utf8') => fs.readFileSync(p, 'utf8'));
  const cwd = options.cwd ?? process.cwd();
  const baseDir = options.baseDir ?? path.dirname(fileURLToPath(import.meta.url));
  const plat = platformInfo(options.platform ?? process.platform, options.arch ?? process.arch);

  // 1. explicit override
  const envBin = env.ASTRA_SERVER_BIN?.trim();
  if (envBin) {
    if (!exists(envBin)) {
      throw new AstraError(`ASTRA_SERVER_BIN points to a missing file: ${envBin}`);
    }
    return { command: envBin, args: [], serverPath: envBin, source: 'env' };
  }

  // 2. the updater's managed binary recorded in install.json
  const installPath = options.installServerPath?.trim();
  if (installPath && exists(installPath)) {
    return { command: installPath, args: [], serverPath: installPath, source: 'install' };
  }

  // 3. in-repo dev build
  const repoRoot = findRepoRoot(cwd, exists);
  if (repoRoot) {
    const binDir = path.join(repoRoot, 'src', 'Astra.Server', 'bin', 'Debug', 'net10.0');
    const native = path.join(binDir, plat.binaryName);
    if (exists(native)) return { command: native, args: [], serverPath: native, source: 'repo' };
    const dll = path.join(binDir, 'astra-server.dll');
    if (exists(dll)) return { command: 'dotnet', args: [dll], serverPath: dll, source: 'repo' };
  }

  // 4. installed platform package
  const resolveFrom = options.resolveFrom ?? defaultResolveFrom;
  let packageError: string;
  try {
    const pkgJsonPath = resolveFrom(baseDir, `${plat.serverPackage}/package.json`);
    const pkg = JSON.parse(readText(pkgJsonPath, 'utf8')) as { bin?: Record<string, string> };
    const binRel = pkg.bin?.['astra-server'];
    if (binRel) {
      const binPath = path.resolve(path.dirname(pkgJsonPath), binRel);
      if (exists(binPath)) {
        return { command: binPath, args: [], serverPath: binPath, source: 'package' };
      }
      packageError = `${plat.serverPackage} is installed but its binary is missing (${binPath}).`;
    } else {
      packageError = `${plat.serverPackage} has no 'astra-server' bin entry.`;
    }
  } catch (err) {
    if (err instanceof AstraError) throw err;
    packageError = `${plat.serverPackage} is not installed.`;
  }

  throw new AstraError(
    `Could not find the Astra server binary. ${packageError}`,
    'Reinstall the CLI (`npm install -g astragate`), or point ASTRA_SERVER_BIN at the server binary.',
  );
}
