import { app, dialog } from 'electron';
import * as fs from 'node:fs';
import { autoUpdater } from 'electron-updater';
import {
  applyServerUpdate,
  checkForServerUpdate,
  fetchManifest,
  installDesktopClient,
  isApiVersionCompatible,
  isNewerVersion,
  type ServerControl,
  type UpdateManifest,
} from '@aidotnet/update-core';
import { resolveUpdateTrack, type UpdateTrack } from './shared/updateTrack';

/**
 * Update plumbing for the desktop app, covering three surfaces:
 *  - the desktop app itself on the DMG track, via electron-updater (signed,
 *    notarized artifacts from the generic feed configured at build time);
 *  - the desktop app on the npm track under macOS (files of a running bundle
 *    can be replaced safely there);
 *  - the server binary on every track, via the shared update engine
 *    (staged download → sha256 → swap → restart → rollback).
 *
 * The track rule lives in shared/updateTrack.ts; Windows and Linux npm
 * installs are npm-track but cannot replace their own running files, so their
 * desktop updates are delegated to `astra update --desktop`; the server update
 * flow works everywhere. Unpackaged dev runs never self-update.
 */
export { resolveUpdateTrack, type UpdateTrack } from './shared/updateTrack';

export function isDesktopUpdateAvailable(manifest: UpdateManifest, currentVersion: string): boolean {
  return isNewerVersion(manifest.version, currentVersion);
}

export interface UpdateControllerOptions {
  home: string;
  installDesktopPath: string | null;
  expectedApiMajor: number;
  /** Current base URL of the running server, or null when it is down. */
  apiBase: () => string | null;
  platform?: NodeJS.Platform;
  isPackaged?: boolean;
  exePath?: string;
  feedUrl?: string;
  serverPackage: string;
  desktopPackage: string;
  /** Server lifecycle controls, wired to the ServiceManager by main.ts. */
  control: ServerControl;
  log: (line: string) => void;
}

export interface UpdateSummary {
  /** Human-readable result of the check, for dialogs. */
  message: string;
  /** What the user can do right now. */
  action: 'none' | 'server' | 'desktop-dmg' | 'desktop-npm';
}

export class UpdateController {
  readonly track: UpdateTrack;
  private busy = false;
  private timer: NodeJS.Timeout | null = null;
  private pendingManifest: UpdateManifest | null = null;
  private dmgDownloadedVersion: string | null = null;

  constructor(private readonly o: UpdateControllerOptions) {
    let realExePath = '';
    try {
      realExePath = fs.realpathSync(o.exePath ?? app.getPath('exe'));
    } catch {
      realExePath = o.exePath ?? '';
    }
    this.track = resolveUpdateTrack({
      isPackaged: o.isPackaged ?? app.isPackaged,
      realExePath,
      installDesktopPath: o.installDesktopPath,
      platform: o.platform,
    });
    if (this.track === 'dmg') {
      autoUpdater.on('update-downloaded', (info) => {
        this.dmgDownloadedVersion = info.version ?? null;
        this.o.log(`desktop update downloaded: ${this.dmgDownloadedVersion}`);
      });
      autoUpdater.on('error', (err) => {
        this.o.log(`desktop updater error: ${err.message}`);
      });
    }
    o.log(`update track: ${this.track}`);
  }

  /** Periodic background checks; manual checks call check() directly. */
  start(intervalMs = 12 * 60 * 60 * 1000, initialMs = 5 * 60 * 1000): void {
    if (this.track === 'disabled' || this.timer) return;
    this.timer = setInterval(() => {
      void this.check();
    }, intervalMs);
    setTimeout(() => {
      void this.check();
    }, initialMs);
  }

  stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  /** True when the tray should surface an update entry. */
  get hasUpdate(): boolean {
    return this.dmgDownloadedVersion !== null || this.pendingManifest !== null;
  }

  /**
   * One check across both surfaces. Never throws — failures land in the
   * returned message (surfaces in dialogs, the log otherwise).
   */
  async check(): Promise<UpdateSummary> {
    if (this.busy) return { message: 'An update check is already running.', action: 'none' };
    this.busy = true;
    try {
      const parts: string[] = [];

      if (this.track === 'dmg') {
        const dmg = await this.checkDesktopDmg();
        if (dmg) parts.push(dmg);
      }
      const server = await this.checkServerAndManifest();
      if (server) parts.push(server);

      if (this.dmgDownloadedVersion) {
        return { message: `Desktop app ${this.dmgDownloadedVersion} is downloaded — restart to install.`, action: 'desktop-dmg' };
      }
      if (this.pendingManifest) {
        if (this.track === 'npm' && (this.o.platform ?? process.platform) === 'darwin') {
          return { message: parts.join('\n'), action: 'server' };
        }
        if (this.track === 'npm') {
          return {
            message:
              parts.join('\n') +
              '\n\nThe desktop app itself must be updated with `astra update --desktop` (its files are in use).',
            action: 'server',
          };
        }
        return { message: parts.join('\n'), action: 'server' };
      }
      return { message: parts.filter(Boolean).join('\n') || 'Astra is up to date.', action: 'none' };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      this.o.log(`update check failed: ${message}`);
      return { message: `Update check failed: ${message}`, action: 'none' };
    } finally {
      this.busy = false;
    }
  }

  private async checkDesktopDmg(): Promise<string> {
    try {
      const result = await autoUpdater.checkForUpdates();
      const v = result?.updateInfo?.version;
      if (v && v !== app.getVersion() && !this.dmgDownloadedVersion) {
        return `Desktop app ${v} is available (downloading in the background).`;
      }
      return '';
    } catch (err) {
      this.o.log(`desktop update check failed: ${err instanceof Error ? err.message : String(err)}`);
      return '';
    }
  }

  private async checkServerAndManifest(): Promise<string> {
    const base = this.o.apiBase();
    const r = await checkForServerUpdate({
      currentVersion: app.getVersion(),
      baseUrl: base ?? undefined,
      feedUrl: this.o.feedUrl,
    });
    if (!r.availableVersion) {
      this.pendingManifest = null;
      return '';
    }
    // The server-side status carries only the version; fetch the manifest for
    // the checksum + platform info the executor needs, and re-validate.
    const manifest = await fetchManifest({ feedUrl: this.o.feedUrl });
    if (!isNewerVersion(manifest.version, app.getVersion())) {
      this.pendingManifest = null;
      return '';
    }
    this.pendingManifest = manifest;
    return `Server update ${manifest.version} is available.`;
  }

  /** Gate shared by both apply paths: an API major change needs a manual update. */
  private gate(manifest: UpdateManifest): string | null {
    if (manifest.apiVersion && !isApiVersionCompatible(manifest.apiVersion, this.o.expectedApiMajor)) {
      return `The update targets API major ${manifest.apiVersion}; this app expects ${this.o.expectedApiMajor}. ` +
        'Run "astra update" from the terminal instead.';
    }
    return null;
  }

  /** Applies the server update through the shared staged-swap state machine. */
  async applyServerUpdate(): Promise<void> {
    const manifest = this.pendingManifest;
    if (!manifest) throw new Error('No pending server update — check for updates first.');
    const blocked = this.gate(manifest);
    if (blocked) throw new Error(blocked);
    const platform = this.o.platform ?? process.platform;
    const ridPrefix = platform === 'darwin' ? 'osx' : platform === 'win32' ? 'win' : 'linux';
    await applyServerUpdate({
      home: this.o.home,
      manifest,
      platformKey: `${ridPrefix}-${process.arch}`,
      serverPackage: this.o.serverPackage,
      platform,
      currentServerPath: null,
      // The gate compares against the SERVER's current version, not the app's.
      currentVersion: (await this.o.control.probeVersion()) ?? '0.0.0',
      expectedApiMajor: this.o.expectedApiMajor,
      control: this.o.control,
      log: (line) => this.o.log(line),
    });
    this.pendingManifest = null;
  }

  /** npm track, macOS only: replaces the desktop app under the npm prefix and relaunches. */
  async applyDesktopNpmUpdate(): Promise<void> {
    const manifest = this.pendingManifest;
    if (!manifest) throw new Error('No pending desktop update — check for updates first.');
    const platform = this.o.platform ?? process.platform;
    if (platform !== 'darwin') {
      throw new Error('The desktop app on this platform must be updated with `astra update --desktop`.');
    }
    const blocked = this.gate(manifest);
    if (blocked) throw new Error(blocked);
    await installDesktopClient({
      home: this.o.home,
      version: manifest.version,
      desktopPackage: this.o.desktopPackage,
      platform: 'darwin',
      log: (line) => this.o.log(line),
    });
    this.pendingManifest = null;
    this.o.log('desktop app updated — relaunching');
    app.relaunch();
    app.quit();
  }

  /** DMG track: quit (which stops an app-started server) and hand over to Squirrel. */
  applyDesktopDmgUpdate(): void {
    if (!this.dmgDownloadedVersion) throw new Error('No downloaded desktop update.');
    autoUpdater.quitAndInstall();
  }

  /** Full interactive flow for the tray item. */
  async checkInteractive(): Promise<void> {
    const summary = await this.check();
    const buttons: string[] = [];
    if (summary.action === 'desktop-dmg') buttons.push(`Restart to ${this.dmgDownloadedVersion}`);
    if (summary.action === 'server' && this.track === 'npm' && (this.o.platform ?? process.platform) === 'darwin') {
      // One executor run covers both the server binary and the app on macOS.
      buttons.push('Update');
    } else if (summary.action === 'server') {
      buttons.push('Update Server');
    }
    buttons.push('OK');
    const updateIndex = buttons.length - 1;

    const { response } = await dialog.showMessageBox({
      type: 'info',
      title: 'Astra',
      message: 'Check for updates',
      detail: summary.message,
      buttons,
      defaultId: 0,
      cancelId: buttons.length - 1,
      noLink: true,
    });
    if (response === updateIndex) return;
    const action = buttons[response]!;
    try {
      if (action.startsWith('Restart to')) this.applyDesktopDmgUpdate();
      else if (action === 'Update') {
        // Server first — the desktop step relaunches the app.
        await this.applyServerUpdate();
        await this.applyDesktopNpmUpdate();
      } else if (action === 'Update Server') await this.applyServerUpdate();
    } catch (err) {
      await dialog.showMessageBox({
        type: 'error',
        title: 'Astra',
        message: 'Update failed',
        detail: err instanceof Error ? err.message : String(err),
        buttons: ['OK'],
        noLink: true,
      });
    }
  }
}
