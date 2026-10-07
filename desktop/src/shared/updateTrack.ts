/**
 * Which update path applies to this desktop install. Pure logic, no Electron
 * imports, so it stays unit-testable (see tests/updateTrack.test.ts).
 *
 * - disabled: unpackaged dev run — never self-update.
 * - dmg: macOS install living outside the npm prefix (signed, notarized
 *   artifacts via electron-updater).
 * - npm: everything installed under ~/.astra/desktop by the CLI (the
 *   ~/Applications entry on macOS is a symlink, so the running exe's realpath
 *   exposes node_modules).
 * - installer: Windows / Linux installs from the NSIS / AppImage installers on
 *   the download page — no in-app desktop self-update yet; users install the
 *   newer installer over the old one.
 */
export type UpdateTrack = 'dmg' | 'npm' | 'installer' | 'disabled';

export function resolveUpdateTrack(o: {
  isPackaged: boolean;
  realExePath: string;
  installDesktopPath?: string | null;
  platform?: NodeJS.Platform;
}): UpdateTrack {
  if (!o.isPackaged) return 'disabled';
  const platform = o.platform ?? process.platform;
  const npmManaged =
    o.realExePath.includes('node_modules') ||
    (!!o.installDesktopPath && o.realExePath.startsWith(o.installDesktopPath));
  if (npmManaged) return 'npm';
  return platform === 'darwin' ? 'dmg' : 'installer';
}
