import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

/** `~/.astra/connect/` (ASTRA_HOME overrides the root): the state of astra-connect. */
export interface ConnectPaths {
  dir: string;
  profileFile: string;
  stateFile: string;
  backupsDir: string;
  /** Astra's own data dir root (a server running on this machine keeps runtime.json there). */
  astraHome: string;
}

export function connectPaths(env: NodeJS.ProcessEnv = process.env, homedir: string = os.homedir()): ConnectPaths {
  const override = env.ASTRA_HOME?.trim();
  const astraHome = override ? path.resolve(override) : path.join(homedir, '.astra');
  const dir = path.join(astraHome, 'connect');
  return {
    dir,
    profileFile: path.join(dir, 'profile.json'),
    stateFile: path.join(dir, 'state.json'),
    backupsDir: path.join(dir, 'backups'),
    astraHome,
  };
}

export interface Profile {
  gatewayUrl: string;
  /** The company-issued key, written into the clients' configs too (owner-only file, like those configs). */
  apiKey: string;
  name: string | null;
  /** The default model chosen at join, if any. */
  model: string | null;
  clients: string[];
  joinedAt: string;
  updatedAt: string;
}

export function readProfile(paths: ConnectPaths): Profile | null {
  try {
    return JSON.parse(fs.readFileSync(paths.profileFile, 'utf8')) as Profile;
  } catch (e) {
    if ((e as NodeJS.ErrnoException).code === 'ENOENT') return null;
    throw e;
  }
}

export function writeProfile(paths: ConnectPaths, profile: Profile): void {
  fs.mkdirSync(paths.dir, { recursive: true, mode: 0o700 });
  const tmp = `${paths.profileFile}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(profile, null, 2) + '\n', { mode: 0o600 });
  fs.renameSync(tmp, paths.profileFile);
}

export function deleteProfile(paths: ConnectPaths): void {
  fs.rmSync(paths.profileFile, { force: true });
}

/** A server of this machine records its pid and port here (see AstraPaths.RuntimeFile). */
export function localAstraRuns(paths: ConnectPaths): boolean {
  try {
    const runtime = JSON.parse(fs.readFileSync(path.join(paths.astraHome, 'runtime.json'), 'utf8')) as { pid?: number };
    if (typeof runtime.pid !== 'number') return false;
    process.kill(runtime.pid, 0);
    return true;
  } catch (e) {
    return (e as NodeJS.ErrnoException).code === 'EPERM';
  }
}
