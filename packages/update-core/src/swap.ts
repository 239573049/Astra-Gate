import fs from 'node:fs';
import path from 'node:path';
import { managedServerBinaryPath } from './paths.js';

/** Rolling backups: keep this many per subject (server / desktop). */
export const MAX_BACKUPS = 3;

/**
 * Copies the currently installed binary into backupsDir before anything is
 * replaced. Returns the backup path, or null when there is nothing to back up.
 * The copy is the last-resort rollback anchor — the primary rollback mechanism
 * is repointing install.json at the previous path, which is never deleted.
 */
export function backupCurrentBinary(o: {
  currentPath: string | null;
  backupsDir: string;
  currentVersion?: string | null;
  kind?: 'server' | 'desktop';
  platform?: string;
  maxBackups?: number;
}): string | null {
  if (!o.currentPath || !fs.existsSync(o.currentPath)) return null;
  const kind = o.kind ?? 'server';
  const dir = path.join(o.backupsDir, kind);
  fs.mkdirSync(dir, { recursive: true });
  const ext = o.platform === 'win32' ? '.exe' : '';
  const stamp = o.currentVersion ? `-${o.currentVersion}` : `-${new Date().toISOString().replace(/[:.]/g, '-')}`;
  const backupPath = path.join(dir, `astra-server${stamp}${ext}`);
  fs.copyFileSync(o.currentPath, backupPath);
  pruneBackups(dir, o.maxBackups ?? MAX_BACKUPS);
  return backupPath;
}

/** Removes the oldest entries beyond `keep` (by filename sort — version names sort correctly). */
export function pruneBackups(dir: string, keep: number = MAX_BACKUPS): void {
  let entries: string[] = [];
  try {
    entries = fs.readdirSync(dir).sort();
  } catch {
    return;
  }
  const excess = entries.length - keep;
  for (let i = 0; i < excess; i++) {
    try {
      fs.rmSync(path.join(dir, entries[i]!), { force: true });
    } catch {
      /* best effort */
    }
  }
}

export interface InstallManagedBinaryOptions {
  stagedPath: string;
  serverDir: string;
  version: string;
  platform: string;
}

/**
 * Copies the verified staged binary to its managed, version-suffixed home
 * (~/.astra/server/astra-server-<version>). A fresh file per version means
 * Windows file locks on a running binary are never an issue, and install.json
 * is what makes both resolvers pick this one after the restart.
 */
export function installManagedBinary(o: InstallManagedBinaryOptions): string {
  const target = managedServerBinaryPath(o.serverDir, o.version, o.platform);
  fs.mkdirSync(o.serverDir, { recursive: true });
  const tmp = `${target}.tmp`;
  fs.copyFileSync(o.stagedPath, tmp);
  fs.renameSync(tmp, target);
  if (o.platform !== 'win32') fs.chmodSync(target, 0o755);
  return target;
}

/**
 * Removes managed binaries older than `keep` versions. Never removes the
 * current target; failures are ignored (a leftover old binary is harmless).
 */
export function pruneManagedBinaries(serverDir: string, currentTarget: string, keep: number = 2): void {
  let entries: string[] = [];
  try {
    entries = fs.readdirSync(serverDir).filter((f) => /^astra-server-\d/.test(f));
  } catch {
    return;
  }
  const excess = entries.filter((f) => path.join(serverDir, f) !== currentTarget).length - keep;
  for (let i = 0; i < Math.max(0, excess); i++) {
    try {
      fs.rmSync(path.join(serverDir, entries[i]!), { force: true });
    } catch {
      /* best effort */
    }
  }
}

/**
 * The server serves the web UI from `wwwroot` beside its own executable
 * (AstraApp: AppContext.BaseDirectory), so a managed binary in serverDir needs
 * serverDir/wwwroot. Replaces it with the copy shipped next to `binaryPath`
 * (bin/wwwroot in the platform package); the previous copy is kept as
 * wwwroot.prev until commitManagedWebRoot / restoreManagedWebRoot. Returns
 * false, touching nothing, when the package ships no wwwroot.
 */
export function installManagedWebRoot(binaryPath: string, serverDir: string): boolean {
  const src = path.join(path.dirname(binaryPath), 'wwwroot');
  if (!fs.existsSync(src)) return false;
  const dest = path.join(serverDir, 'wwwroot');
  const tmp = `${dest}.tmp`;
  const prev = `${dest}.prev`;
  fs.mkdirSync(serverDir, { recursive: true });
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.cpSync(src, tmp, { recursive: true });
  fs.rmSync(prev, { recursive: true, force: true });
  if (fs.existsSync(dest)) fs.renameSync(dest, prev);
  fs.renameSync(tmp, dest);
  return true;
}

/** Drops the wwwroot.prev kept by installManagedWebRoot once the update stuck. */
export function commitManagedWebRoot(serverDir: string): void {
  fs.rmSync(path.join(serverDir, 'wwwroot.prev'), { recursive: true, force: true });
}

/** Undoes installManagedWebRoot: puts wwwroot.prev back (no-op without one). */
export function restoreManagedWebRoot(serverDir: string): void {
  const dest = path.join(serverDir, 'wwwroot');
  const prev = `${dest}.prev`;
  if (!fs.existsSync(prev)) return;
  fs.rmSync(dest, { recursive: true, force: true });
  fs.renameSync(prev, dest);
}
