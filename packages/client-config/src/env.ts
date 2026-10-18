import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export type ClientOs = 'osx' | 'linux' | 'windows';

const PATH_BEGIN = '__ASTRA_PATH_BEGIN__';
const PATH_END = '__ASTRA_PATH_END__';

export interface ClientEnvironmentOptions {
  lookup?: (name: string) => string | undefined;
  extraToolDirectories?: string[];
  applicationDirectories?: string[];
  readLoginShellPath?: boolean;
}

/**
 * Everything adapters and the applier need from the machine: home directory, OS, environment variables and file
 * IO. Production uses `ClientEnvironment.real()`; tests inject a fake home directory and an environment map, so
 * real user configuration is never touched. (Port of Astra.Clients ClientEnvironment.)
 */
export class ClientEnvironment {
  readonly extraToolDirectories: string[];
  readonly applicationDirectories: string[];
  private readonly lookup: (name: string) => string | undefined;
  private readonly readShellPath: boolean;
  private shellPath: string[] | null = null;

  constructor(
    readonly homeDirectory: string,
    readonly os: ClientOs,
    options: ClientEnvironmentOptions = {},
  ) {
    this.lookup = options.lookup ?? (() => undefined);
    this.extraToolDirectories = options.extraToolDirectories ?? [];
    this.applicationDirectories = options.applicationDirectories ?? [];
    this.readShellPath = options.readLoginShellPath ?? false;
  }

  /** The real machine environment (never used by tests). */
  static real(): ClientEnvironment {
    const home = os.homedir();
    const clientOs: ClientOs =
      process.platform === 'win32' ? 'windows' : process.platform === 'darwin' ? 'osx' : 'linux';
    const lookup = (name: string) => process.env[name];
    const apps =
      clientOs === 'osx'
        ? ['/Applications', path.join(home, 'Applications')]
        : clientOs === 'windows' && process.env.LOCALAPPDATA
          ? [process.env.LOCALAPPDATA]
          : [];
    return new ClientEnvironment(home, clientOs, {
      lookup,
      extraToolDirectories: commonToolDirectories(home, clientOs, lookup),
      applicationDirectories: apps,
      readLoginShellPath: true,
    });
  }

  getEnvironmentVariable(name: string): string | null {
    return this.lookup(name) ?? null;
  }

  combine(...parts: string[]): string {
    return path.join(this.homeDirectory, ...parts);
  }

  fileExists(p: string): boolean {
    try {
      return fs.statSync(p).isFile();
    } catch {
      return false;
    }
  }

  directoryExists(p: string): boolean {
    try {
      return fs.statSync(p).isDirectory();
    } catch {
      return false;
    }
  }

  /** Reads a file as text (BOM stripped), or null when it does not exist. */
  readTextWithBom(p: string): { text: string; hadBom: boolean } | null {
    const bytes = this.readAllBytesOrNull(p);
    if (!bytes) return null;
    const hadBom = bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf;
    return { text: bytes.subarray(hadBom ? 3 : 0).toString('utf8'), hadBom };
  }

  readTextOrNull(p: string): string | null {
    return this.readTextWithBom(p)?.text ?? null;
  }

  /** Writes text as UTF-8 (optionally with BOM) via a temp file and an atomic rename. */
  writeTextAtomic(p: string, text: string, bom: boolean): void {
    fs.mkdirSync(path.dirname(p), { recursive: true });
    const tmp = `${p}.astra-tmp`;
    fs.writeFileSync(tmp, (bom ? '﻿' : '') + text, 'utf8');
    fs.renameSync(tmp, p);
  }

  /** Writes raw bytes, creating parent directories. */
  writeAllBytes(p: string, bytes: Uint8Array): void {
    fs.mkdirSync(path.dirname(p), { recursive: true });
    fs.writeFileSync(p, bytes);
  }

  readAllBytesOrNull(p: string): Buffer | null {
    try {
      return fs.readFileSync(p);
    } catch (e) {
      const code = (e as NodeJS.ErrnoException).code;
      if (code === 'ENOENT' || code === 'EISDIR' || code === 'ENOTDIR') return null;
      throw e;
    }
  }

  deleteFile(p: string): void {
    if (this.fileExists(p)) fs.unlinkSync(p);
  }

  /** Finds an executable on PATH (for detection only). */
  findOnPath(fileName: string): string | null {
    return this.findIn(this.pathDirectories(), fileName);
  }

  /**
   * The search path for running client tools: the user's login-shell PATH, this process's PATH, then
   * `extraDirectories`, then the common tool directories, without duplicates.
   */
  toolSearchPath(...extraDirectories: string[]): string[] {
    const all = [
      ...this.loginShellPath(),
      ...this.pathDirectories(),
      ...extraDirectories,
      ...this.extraToolDirectories,
    ].filter((d) => d.length > 0);
    const seen = new Set<string>();
    return all.filter((d) => {
      const key = this.os === 'windows' ? d.toLowerCase() : d;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  }

  /** Finds a client tool (or npm) on the tool search path. */
  findTool(fileName: string, ...extraDirectories: string[]): string | null {
    return this.findIn(this.toolSearchPath(...extraDirectories), fileName);
  }

  /** Every copy of a tool on the tool search path, in search order (the first is the one that runs). */
  findAllTools(fileName: string, ...extraDirectories: string[]): string[] {
    return this.toolSearchPath(...extraDirectories)
      .map((dir) => this.findIn([dir], fileName))
      .filter((p): p is string => p !== null);
  }

  /**
   * PATH as the user's interactive login shell sets it (macOS / Linux; cached). A process started from a
   * desktop launcher has a minimal PATH, so without this Astra would judge a different copy of a client than
   * the one the user runs in a terminal. Empty in tests, on Windows, for non-POSIX shells, or on failure.
   */
  loginShellPath(): string[] {
    if (!this.readShellPath || this.os === 'windows') return [];
    this.shellPath ??= this.readLoginShellPath();
    return this.shellPath;
  }

  /** Forgets the cached login-shell PATH. */
  refreshLoginShellPath(): void {
    this.shellPath = null;
  }

  private readLoginShellPath(): string[] {
    const configured = this.lookup('SHELL');
    const shell =
      configured && this.fileExists(configured)
        ? configured
        : this.os === 'osx'
          ? '/bin/zsh'
          : '/bin/bash';
    // fish / nushell do not take POSIX "$PATH" syntax; better no shell PATH than a wrong one.
    if (!['zsh', 'bash', 'sh', 'ksh', 'dash'].includes(path.basename(shell))) return [];
    const result = spawnSync(shell, ['-ilc', `printf '${PATH_BEGIN}%s${PATH_END}' "$PATH"`], {
      cwd: this.directoryExists(this.homeDirectory) ? this.homeDirectory : '/',
      encoding: 'utf8',
      timeout: 5000,
      input: '',
      stdio: ['pipe', 'pipe', 'ignore'],
    });
    if (result.error || result.status !== 0 || typeof result.stdout !== 'string') return [];
    return ClientEnvironment.parseMarkedPath(result.stdout);
  }

  /** The PATH printed between the markers (rc files may print banners around it), as absolute directories. */
  static parseMarkedPath(output: string): string[] {
    let start = output.lastIndexOf(PATH_BEGIN);
    if (start < 0) return [];
    start += PATH_BEGIN.length;
    const end = output.indexOf(PATH_END, start);
    if (end < 0) return [];
    return output
      .slice(start, end)
      .split(':')
      .map((d) => d.trim())
      .filter((d) => d.length > 0 && path.isAbsolute(d));
  }

  private pathDirectories(): string[] {
    const value = this.lookup('PATH');
    return value
      ? value
          .split(path.delimiter)
          .map((d) => d.trim())
          .filter((d) => d.length > 0)
      : [];
  }

  private findIn(directories: string[], fileName: string): string | null {
    const extensions = this.os === 'windows' ? ['.exe', '.cmd', '.bat', ''] : [''];
    for (const dir of directories)
      for (const ext of extensions) {
        const candidate = path.join(dir, fileName + ext);
        if (this.fileExists(candidate)) return candidate;
      }
    return null;
  }
}

/**
 * Where CLI clients and npm usually live even when they are missing from PATH: a desktop app launched from the
 * Dock inherits a minimal PATH, so tool lookups also search these.
 */
function commonToolDirectories(
  home: string,
  clientOs: ClientOs,
  lookup: (name: string) => string | undefined,
): string[] {
  if (clientOs === 'windows') {
    const appData = lookup('APPDATA');
    return appData ? [path.join(appData, 'npm')] : [];
  }
  const dirs = [
    '/usr/local/bin',
    '/opt/homebrew/bin',
    '/home/linuxbrew/.linuxbrew/bin',
    path.join(home, '.local', 'bin'),
    path.join(home, '.npm-global', 'bin'),
    path.join(home, '.bun', 'bin'),
    path.join(home, '.volta', 'bin'),
  ];
  // nvm keeps one directory per Node version; prefer the newest.
  const nvm = path.join(home, '.nvm', 'versions', 'node');
  try {
    const versions = fs
      .readdirSync(nvm, { withFileTypes: true })
      .filter((d) => d.isDirectory())
      .map((d) => d.name);
    const parse = (name: string) => name.replace(/^v/, '').split('.').map((n) => Number.parseInt(n, 10) || 0);
    versions.sort((a, b) => {
      const pa = parse(a);
      const pb = parse(b);
      for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
        const d = (pb[i] ?? 0) - (pa[i] ?? 0);
        if (d !== 0) return d;
      }
      return 0;
    });
    dirs.push(...versions.map((v) => path.join(nvm, v, 'bin')));
  } catch {
    // no nvm
  }
  return dirs;
}
