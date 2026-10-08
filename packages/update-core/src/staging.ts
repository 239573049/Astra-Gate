import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { npmInstallIntoPrefix, type NpmRunnerOptions } from './npm-runner.js';

/** sha256 of a file, hex-encoded (streamed — binaries are ~90 MB). */
export function sha256File(file: string): string {
  const hash = createHash('sha256');
  hash.update(fs.readFileSync(file));
  return hash.digest('hex');
}

export interface StagedServerBinary {
  path: string;
  version: string;
  /**
   * Native companion libraries staged beside `path` (e.g. libe_sqlite3 under
   * Native AOT — the loader resolves them relative to the executable's
   * directory). Empty for a package built without AOT, which legitimately has
   * none; absence is a log line, never an error.
   */
  nativeLibraries: string[];
}

export interface StageServerBinaryOptions {
  home: string;
  stagingDir: string;
  serverPackage: string;
  version: string;
  /** Expected sha256 (hex) from the manifest; null skips verification. */
  sha256?: string | null;
  platform: string;
  npm?: NpmRunnerOptions;
  /** Test seam — defaults to the real npm runner. */
  installIntoPrefix?: (prefix: string, spec: string) => Promise<void>;
  log?: (line: string) => void;
}

/**
 * A Native AOT publish links SQLite (and any other native library) as separate
 * dynamic libraries beside the executable instead of embedding them, so a
 * managed install has to carry them along. Mirrors NATIVE_LIBRARY in
 * scripts/pack-platform.mjs — keep the two in sync.
 */
export const NATIVE_LIBRARY = /\.(dylib|so(\.\d+)*|dll)$/i;

/**
 * The companion libraries sitting beside `binaryPath` (the executable's own
 * directory — a package bin/ or a managed dir). The executable itself never
 * counts, and neither do installer *.tmp / rollback *.prev siblings.
 */
export function findNativeCompanions(binaryPath: string): string[] {
  const dir = path.dirname(binaryPath);
  const binaryName = path.basename(binaryPath);
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return [];
  }
  return entries
    .filter((e) => e.isFile() && e.name !== binaryName && NATIVE_LIBRARY.test(e.name))
    .map((e) => path.join(dir, e.name));
}

/**
 * Downloads the server platform package into the staging prefix and verifies
 * the binary checksum BEFORE anything on the running installation is touched.
 * Staging is wiped on every run, so a failed run never leaves stale artifacts.
 */
export async function stageServerBinary(o: StageServerBinaryOptions): Promise<StagedServerBinary> {
  const install = o.installIntoPrefix ?? npmInstallIntoPrefix;
  fs.rmSync(o.stagingDir, { recursive: true, force: true });
  fs.mkdirSync(o.stagingDir, { recursive: true });
  o.log?.(`Downloading ${o.serverPackage}@${o.version}…`);
  await install(o.stagingDir, `${o.serverPackage}@${o.version}`);

  const binaryName = o.platform === 'win32' ? 'astra-server.exe' : 'astra-server';
  const staged = path.join(o.stagingDir, 'node_modules', ...o.serverPackage.split('/'), 'bin', binaryName);
  if (!fs.existsSync(staged)) {
    fs.rmSync(o.stagingDir, { recursive: true, force: true });
    throw new Error(`The staged package does not contain a server binary (${staged}).`);
  }
  if (o.sha256) {
    o.log?.('Verifying checksum…');
    const actual = sha256File(staged);
    if (actual.toLowerCase() !== o.sha256.toLowerCase()) {
      fs.rmSync(o.stagingDir, { recursive: true, force: true });
      throw new Error(`Checksum mismatch for the staged server binary (expected ${o.sha256}, got ${actual}).`);
    }
  }
  // A package built without AOT has no companions — that is the normal case
  // for pre-AOT releases, so it must not fail the update (pack-platform only
  // warns the same way). The binary's sha256 stays the trust anchor; the npm
  // tarball integrity covers the companions themselves.
  const nativeLibraries = findNativeCompanions(staged);
  if (nativeLibraries.length === 0) {
    o.log?.('The staged package ships no native companion libraries (normal for a non-AOT build).');
  }
  return { path: staged, version: o.version, nativeLibraries };
}
