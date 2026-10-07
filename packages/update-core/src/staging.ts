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
  return { path: staged, version: o.version };
}
