/**
 * Which update path applies to this desktop install. Pure logic, no Electron
 * imports, so it stays unit-testable (see tests/updateTrack.test.ts).
 *
 * - disabled: unpackaged dev run — never self-update.
 * - dmg: macOS install living outside the npm prefix (signed, notarized
 *   artifacts via electron-updater).
 * - npm: everything installed under ~/.astra/desktop by the CLI, plus all
 *   Windows/Linux installs (the ~/Applications entry on macOS is a symlink,
 *   so the running exe's realpath exposes node_modules).
 */
export type UpdateTrack = 'dmg' | 'npm' | 'disabled';

export function resolveUpdateTrack(o: {
  isPackaged: boolean;
  realExePath: string;
  installDesktopPath?: string | null;
  platform?: NodeJS.Platform;
}): UpdateTrack {
  if (!o.isPackaged) return 'disabled';
  const platform = o.platform ?? process.platform;
  if (platform !== 'darwin') return 'npm';
  if (o.realExePath.includes('node_modules')) return 'npm';
  if (o.installDesktopPath && o.realExePath.startsWith(o.installDesktopPath)) return 'npm';
  return 'dmg';
}
