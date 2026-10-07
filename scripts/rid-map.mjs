/**
 * RID → npm platform package directory mapping shared by the packaging
 * scripts and the release workflow.
 */
const SERVER_DIRS = {
  'osx-arm64': 'server-darwin-arm64',
  'osx-x64': 'server-darwin-x64',
  'linux-x64': 'server-linux-x64',
  'linux-arm64': 'server-linux-arm64',
  'win-x64': 'server-win32-x64',
  'win-arm64': 'server-win32-arm64',
};

export const ALL_RIDS = Object.keys(SERVER_DIRS);

/** @returns the directory under npm/ for a .NET RID */
export function ridToServerDir(rid) {
  const dir = SERVER_DIRS[rid];
  if (!dir) {
    throw new Error(`Unknown RID "${rid}". Expected one of: ${ALL_RIDS.join(', ')}`);
  }
  return dir;
}

/** @returns the directory under npm/ for a desktop package */
export function ridToDesktopDir(rid) {
  return ridToServerDir(rid).replace(/^server-/, 'desktop-');
}
