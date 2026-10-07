import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { npmInstallIntoPrefix, type NpmRunnerOptions } from './npm-runner.js';

/**
 * Desktop-app installs for the npm track (Windows, Linux, and macOS installs
 * managed by `astra install --desktop`): an npm prefix under ~/.astra/desktop
 * holds @aidotnet/desktop-<platform>, and a per-OS launcher points at it.
 * Ported from the CLI's lib/desktop.ts so the desktop app can run the very
 * same flow when it drives updates itself.
 */
export type SupportedPlatform = 'darwin' | 'linux' | 'win32';

export interface DesktopEntry {
  path: string;
  isBundle: boolean;
}

function walkFiles(dir: string, depth: number): string[] {
  if (depth < 0) return [];
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return [];
  }
  const out: string[] = [];
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) out.push(...walkFiles(full, depth - 1));
    else if (e.isFile()) out.push(full);
  }
  return out;
}

function walkDirs(dir: string, depth: number): string[] {
  if (depth < 0) return [];
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return [];
  }
  const out: string[] = [];
  for (const e of entries) {
    if (e.isDirectory()) {
      const full = path.join(dir, e.name);
      out.push(full, ...walkDirs(full, depth - 1));
    }
  }
  return out;
}

/** Locate the launchable inside <desktopPrefix>/node_modules/<pkg>/app. */
export function findDesktopEntry(appDir: string, platform: SupportedPlatform): DesktopEntry {
  if (!fs.existsSync(appDir)) {
    throw new Error(`The desktop package does not contain an app directory (${appDir}).`);
  }
  if (platform === 'darwin') {
    const bundles = walkDirs(appDir, 4)
      .filter((d) => d.endsWith('.app'))
      .sort((a, b) => a.length - b.length);
    const first = bundles[0];
    if (first) return { path: first, isBundle: true };
    throw new Error('Could not find the .app bundle inside the desktop package.');
  }
  const exes = walkFiles(appDir, 3)
    .filter((f) => (platform === 'win32' ? /\.exe$/i.test(f) : !/\.(so|png|json|sh|txt|desktop)$/i.test(f)))
    .filter((f) => {
      if (platform === 'win32') return !/(crashpad|installer|unins|squirrel)/i.test(f);
      try {
        fs.accessSync(f, fs.constants.X_OK);
        return true;
      } catch {
        return false;
      }
    })
    .sort((a, b) => a.split(path.sep).length - b.split(path.sep).length || a.length - b.length);
  const first = exes[0];
  if (first) return { path: first, isBundle: false };
  throw new Error('Could not find the desktop executable inside the desktop package.');
}

export function macLauncherTarget(home: string = ''): string {
  return path.join(home, 'Applications', 'Astra.app');
}

export function winLauncherTarget(home: string, env: NodeJS.ProcessEnv = process.env): string {
  const base = env.APPDATA || path.join(home, 'AppData', 'Roaming');
  return path.join(base, 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Astra.lnk');
}

export function linuxLauncherTarget(home: string): string {
  return path.join(home, '.local', 'share', 'applications', 'astra.desktop');
}

export function renderLinuxDesktopFile(exePath: string): string {
  return `[Desktop Entry]
Type=Application
Name=Astra
Comment=Local AI gateway
Exec=${exePath}
Terminal=false
Categories=Development;Network;
`;
}

function runCapture(command: string, args: readonly string[], timeoutMs: number): Promise<{ code: number | null; stderr: string }> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, [...args], { stdio: 'ignore', windowsHide: true, shell: command.endsWith('.cmd') });
    let settled = false;
    const timer = setTimeout(() => {
      settled = true;
      reject(new Error(`${command} timed out`));
    }, timeoutMs);
    child.on('error', (err) => {
      clearTimeout(timer);
      if (!settled) reject(err);
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (!settled) resolve({ code, stderr: '' });
    });
  });
}

/**
 * Symlink (macOS) / shortcut (Windows) / .desktop file (Linux) pointing at the
 * installed app. `home` is the user's home directory, not the Astra data dir.
 */
export async function createLauncher(
  entry: DesktopEntry,
  platform: SupportedPlatform,
  home: string,
  env: NodeJS.ProcessEnv = process.env,
): Promise<string> {
  if (platform === 'darwin') {
    const target = macLauncherTarget(home);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.rmSync(target, { force: true, recursive: true });
    fs.symlinkSync(entry.path, target, 'dir');
    return target;
  }
  if (platform === 'win32') {
    const target = winLauncherTarget(home, env);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    const esc = (s: string): string => s.replace(/'/g, "''");
    const ps =
      `$s=(New-Object -ComObject WScript.Shell).CreateShortcut('${esc(target)}');` +
      `$s.TargetPath='${esc(entry.path)}';$s.Save()`;
    const r = await runCapture('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', ps], 60_000);
    if (r.code !== 0) throw new Error('Failed to create the Start Menu shortcut.');
    return target;
  }
  const target = linuxLauncherTarget(home);
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, renderLinuxDesktopFile(entry.path));
  return target;
}

export function removeLauncher(platform: SupportedPlatform, home: string, env: NodeJS.ProcessEnv = process.env): void {
  if (platform === 'darwin') {
    const target = macLauncherTarget(home);
    try {
      const st = fs.lstatSync(target);
      if (st.isSymbolicLink()) fs.rmSync(target);
    } catch {
      /* absent */
    }
    return;
  }
  const target = platform === 'win32' ? winLauncherTarget(home, env) : linuxLauncherTarget(home);
  fs.rmSync(target, { force: true });
}

export interface InstalledDesktop {
  path: string;
  version: string;
  launcher: string;
}

export interface InstallDesktopClientOptions {
  /** Astra data dir (~/.astra): the npm prefix lives in <home>/desktop. */
  home: string;
  /**
   * The user's home directory, where launchers go (~/Applications,
   * ~/.local/share/applications). Not the Astra data dir. Defaults to
   * os.homedir().
   */
  userHome?: string;
  version: string;
  desktopPackage: string;
  platform: SupportedPlatform;
  npm?: NpmRunnerOptions;
  log?: (line: string) => void;
}

/** Installs (or replaces) the desktop app under the npm prefix and refreshes the launcher. */
export async function installDesktopClient(o: InstallDesktopClientOptions): Promise<InstalledDesktop> {
  const prefix = path.join(o.home, 'desktop');
  const spec = `${o.desktopPackage}@${o.version}`;
  o.log?.(`Installing ${spec} (this may take a minute)…`);
  await npmInstallIntoPrefix(prefix, spec, o.npm);
  const pkgDir = path.join(prefix, 'node_modules', ...o.desktopPackage.split('/'));
  const entry = findDesktopEntry(path.join(pkgDir, 'app'), o.platform);
  const launcher = await createLauncher(entry, o.platform, o.userHome ?? os.homedir(), o.npm?.env);
  let version = o.version;
  try {
    const pkg = JSON.parse(fs.readFileSync(path.join(pkgDir, 'package.json'), 'utf8')) as { version?: string };
    version = pkg.version ?? version;
  } catch {
    /* keep the requested version */
  }
  return { path: entry.path, version, launcher };
}
