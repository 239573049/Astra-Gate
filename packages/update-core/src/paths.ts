import path from 'node:path';

/**
 * Filesystem layout of everything the updater owns, all under the Astra home
 * (ASTRA_HOME or ~/.astra). The update state file lives here too because the
 * executor may run while the server is stopped — it can never live in the DB.
 */
export interface UpdatePaths {
  /** Staging area for npm installs that are not yet trusted. */
  stagingDir: string;
  /** Rolling binary backups (server/ and desktop/ subdirectories). */
  backupsDir: string;
  /** Managed server binaries: ~/.astra/server/astra-server-<version>[.exe]. */
  serverDir: string;
  /** Persistent executor state: ~/.astra/update-state.json. */
  stateFile: string;
}

export function updatePaths(home: string): UpdatePaths {
  return {
    stagingDir: path.join(home, 'updates', 'staging'),
    backupsDir: path.join(home, 'backups'),
    serverDir: path.join(home, 'server'),
    stateFile: path.join(home, 'update-state.json'),
  };
}

/** The authoritative server binary lives in serverDir, never inside node_modules. */
export function managedServerBinaryPath(serverDir: string, version: string, platform: string): string {
  return path.join(serverDir, `astra-server-${version}${platform === 'win32' ? '.exe' : ''}`);
}
