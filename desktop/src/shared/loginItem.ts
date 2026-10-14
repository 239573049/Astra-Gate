import * as path from 'node:path';

/** Launch at login, as the tray and Settings show it. Mirrored in web/src/shell/bridge.ts. */
export interface LoginItemState {
  /** False in dev runs: a login item needs the stable executable of a packaged app. */
  supported: boolean;
  enabled: boolean;
}

/** Passed by the login item (Windows run key, Linux autostart entry): start in the tray without opening the window. */
export const HIDDEN_ARG = '--hidden';

/**
 * Linux has no Electron login-item API; an XDG autostart entry does the job instead (GNOME, KDE, Xfce and the other
 * desktops that follow the spec). Its name is not the launcher's astra.desktop, so neither file shadows the other.
 */
export function linuxAutostartPath(env: NodeJS.ProcessEnv, home: string): string {
  const configHome = env.XDG_CONFIG_HOME?.trim() || path.join(home, '.config');
  return path.join(configHome, 'autostart', 'io.astra.desktop.desktop');
}

/** What the entry starts: the AppImage file itself (its mount point changes every launch), else the executable. */
export function linuxLoginExecutable(env: NodeJS.ProcessEnv, execPath: string): string {
  return env.APPIMAGE?.trim() || execPath;
}

/** Reserved characters of the Desktop Entry spec's Exec key: an argument containing one is quoted. */
const EXEC_RESERVED = /[\s"'\\><~|&;$*?#`()]/;

/** One Exec argument: `%` doubled; quoted with `"` `` ` `` `$` `\` escaped, and then every `\` escaped again (string escaping). */
export function desktopExecArg(arg: string): string {
  const value = arg.replace(/%/g, '%%');
  if (!EXEC_RESERVED.test(value)) return value;
  const quoted = value.replace(/(["`$\\])/g, '\\$1');
  return `"${quoted.replace(/\\/g, '\\\\')}"`;
}

function execLine(argv: readonly string[]): string {
  return `Exec=${argv.map(desktopExecArg).join(' ')}`;
}

export function renderLinuxAutostart(argv: readonly string[]): string {
  return `[Desktop Entry]
Type=Application
Name=Astra
Comment=Local AI gateway
${execLine(argv)}
Terminal=false
X-GNOME-Autostart-enabled=true
`;
}

/** An entry the desktop's own startup settings switched off (they keep the file and mark it) does not count as on. */
export function linuxAutostartEnabled(text: string): boolean {
  return !/^\s*Hidden\s*=\s*true\s*$/im.test(text) && !/^\s*X-GNOME-Autostart-enabled\s*=\s*false\s*$/im.test(text);
}

/**
 * The entry pointed at `argv` with everything else kept (a switch-off by the desktop stays switched off); null when
 * it already is.
 */
export function retargetLinuxAutostart(text: string, argv: readonly string[]): string | null {
  const line = execLine(argv);
  if (!/^Exec=.*$/m.test(text)) return null;
  const next = text.replace(/^Exec=.*$/m, () => line);
  return next === text ? null : next;
}
