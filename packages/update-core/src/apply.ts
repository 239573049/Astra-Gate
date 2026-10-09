import {
  applyServerInfo,
  clearServerInfo,
  readInstallInfo,
  writeInstallInfo,
  type InstallInfo,
} from './install-info.js';
import fs from 'node:fs';
import type { UpdateManifest } from './manifest.js';
import { updatePaths } from './paths.js';
import type { NpmRunnerOptions } from './npm-runner.js';
import {
  MAX_CONSECUTIVE_FAILURES,
  initialState,
  readUpdateState,
  writeUpdateState,
  type UpdateState,
} from './state.js';
import { stageServerBinary } from './staging.js';
import {
  backupCurrentBinary,
  commitManagedWebRoot,
  commitNativeCompanions,
  installManagedBinary,
  installManagedWebRoot,
  pruneManagedBinaries,
  restoreManagedWebRoot,
  restoreNativeCompanions,
} from './swap.js';
import { isApiVersionCompatible, isNewerVersion } from './version.js';

export class UpdateError extends Error {
  constructor(
    message: string,
    readonly hint?: string,
  ) {
    super(message);
    this.name = 'UpdateError';
  }
}

/** Process controls the orchestrator needs — implemented over the existing lifecycle helpers. */
export interface ServerControl {
  isRunning(): Promise<boolean>;
  stop(): Promise<void>;
  start(): Promise<void>;
  /** Version the server reports after (re)start; null counts as a failed restart. */
  probeVersion(): Promise<string | null>;
}

export interface ApplyServerUpdateOptions {
  home: string;
  manifest: UpdateManifest;
  /** Key into manifest.platforms, e.g. "darwin-arm64". */
  platformKey: string;
  /** Used when the manifest does not name a server package for this platform. */
  serverPackage: string;
  /** process.platform-style value, decides the .exe suffix. */
  platform: string;
  currentServerPath: string | null;
  currentVersion: string;
  expectedApiMajor: number;
  control: ServerControl;
  npm?: NpmRunnerOptions;
  /** Test seam — replaces the npm runner inside the staging step. */
  installIntoPrefix?: (prefix: string, spec: string) => Promise<void>;
  /** Retry despite the consecutive-failure lock. */
  force?: boolean;
  log?: (line: string) => void;
  now?: () => Date;
}

export interface ApplyServerUpdateResult {
  from: string;
  to: string;
  serverPath: string;
}

/**
 * The staged-swap state machine (plan §P2):
 *   downloading → swapping → restarting → idle
 * with a rollback leg that repoints install.json at the previous binary and
 * restarts. Every phase transition is persisted to ~/.astra/update-state.json
 * so the UI/tray can render progress across process boundaries.
 *
 * Safety rails:
 * - refuses when the manifest is not newer, or its apiVersion major differs
 *   from the running client's expectation (a half-updated pair is worse than
 *   an old pair);
 * - refuses after MAX_CONSECUTIVE_FAILURES failed runs unless `force`;
 * - the new binary lands on a fresh version-suffixed path, so nothing running
 *   is ever replaced in place (Windows file locks stay irrelevant).
 */
export async function applyServerUpdate(o: ApplyServerUpdateOptions): Promise<ApplyServerUpdateResult> {
  const paths = updatePaths(o.home);
  const log = o.log ?? (() => {});
  const now = o.now ?? (() => new Date());

  const state: UpdateState = readUpdateState(paths.stateFile) ?? initialState(now);
  if (!o.force && state.failedCount >= MAX_CONSECUTIVE_FAILURES) {
    throw new UpdateError(
      `The last ${state.failedCount} update attempts failed. Fix the problem and run the update with --retry.`,
      state.error ?? undefined,
    );
  }
  if (!isNewerVersion(o.manifest.version, o.currentVersion)) {
    throw new UpdateError(`Already on version ${o.currentVersion} (${o.manifest.version} is not newer).`);
  }
  if (o.manifest.apiVersion && !isApiVersionCompatible(o.manifest.apiVersion, o.expectedApiMajor)) {
    throw new UpdateError(
      `The update targets API major ${o.manifest.apiVersion} but this client expects ${o.expectedApiMajor}. ` +
        'Update the whole Astra install manually (npm install -g astragate).',
    );
  }

  const setState = (phase: UpdateState['phase'], step?: string, error?: string | null): void => {
    writeUpdateState(
      paths.stateFile,
      {
        ...state,
        phase,
        targetVersion: o.manifest.version,
        previousVersion: o.currentVersion,
        ...(step !== undefined ? { step } : {}),
        ...(error !== undefined ? { error } : { error: null }),
        failedCount: state.failedCount,
      },
      now,
    );
  };

  const previousInstall = readInstallInfo(o.home);
  let swapped = false;
  let webRootSwapped = false;
  let backupPath: string | null = null;

  try {
    setState('downloading', 'downloading');
    const platformInfo = o.manifest.platforms?.[o.platformKey];
    const staged = await stageServerBinary({
      home: o.home,
      stagingDir: paths.stagingDir,
      serverPackage: platformInfo?.server ?? o.serverPackage,
      version: o.manifest.version,
      sha256: platformInfo?.serverSha256 ?? null,
      platform: o.platform,
      npm: o.npm,
      installIntoPrefix: o.installIntoPrefix,
      log,
    });

    setState('swapping', 'swapping');
    const wasRunning = await o.control.isRunning();
    backupPath = backupCurrentBinary({
      currentPath: o.currentServerPath,
      backupsDir: paths.backupsDir,
      currentVersion: o.currentVersion,
      platform: o.platform,
    });
    if (backupPath) log(`Backed up the current binary to ${backupPath}`);
    const serverPath = installManagedBinary({
      stagedPath: staged.path,
      nativeLibraries: staged.nativeLibraries,
      serverDir: paths.serverDir,
      version: o.manifest.version,
      platform: o.platform,
    });
    writeInstallInfo(o.home, applyServerInfo(previousInstall, { serverPath, serverVersion: o.manifest.version }));
    pruneManagedBinaries(paths.serverDir, serverPath);
    swapped = true;

    setState('restarting', 'restarting');
    if (wasRunning) {
      log('Restarting the Astra server…');
      await o.control.stop();
    }
    // The web UI is read from wwwroot beside the binary; swap it while the
    // server is down (no open handles on Windows). Best effort: a failure
    // leaves the previous UI in place rather than failing the update.
    try {
      webRootSwapped = installManagedWebRoot(staged.path, paths.serverDir);
    } catch (err) {
      log(`Could not update the web UI files: ${err instanceof Error ? err.message : String(err)}`);
    }
    await o.control.start();
    const reported = await o.control.probeVersion();
    if (reported !== o.manifest.version) {
      throw new UpdateError(
        `The server reports ${reported ?? 'no version'} after the restart; expected ${o.manifest.version}.`,
      );
    }

    // Success: free the ~90 MB staging prefix immediately (it is wiped on the next run anyway).
    fs.rmSync(paths.stagingDir, { recursive: true, force: true });
    if (webRootSwapped) commitManagedWebRoot(paths.serverDir);
    // No-op when this run swapped no companions (non-AOT package) — the .prev
    // snapshots, if any, always belong to the swap that just succeeded.
    commitNativeCompanions(paths.serverDir);
    writeUpdateState(paths.stateFile, { ...initialState(now), phase: 'idle' }, now);
    log(`Astra updated to ${o.manifest.version}.`);
    return { from: o.currentVersion, to: o.manifest.version, serverPath };
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    setState('rolling-back', 'rolling-back', message);
    if (swapped) await rollback(o, previousInstall, backupPath, webRootSwapped, setState, log);
    const failed: UpdateState = {
      phase: 'failed',
      targetVersion: o.manifest.version,
      previousVersion: o.currentVersion,
      error: message,
      failedCount: state.failedCount + 1,
      updatedAt: now().toISOString(),
    };
    writeUpdateState(paths.stateFile, failed, now);
    throw err instanceof UpdateError ? err : new UpdateError(message);
  }
}

async function rollback(
  o: ApplyServerUpdateOptions,
  previousInstall: InstallInfo | null,
  backupPath: string | null,
  webRootSwapped: boolean,
  setState: (phase: UpdateState['phase'], step?: string, error?: string | null) => void,
  log: (line: string) => void,
): Promise<void> {
  const restorePath = previousInstall?.serverPath ?? backupPath;
  try {
    if (restorePath) {
      log(`Rolling back to ${restorePath}`);
      writeInstallInfo(o.home, applyServerInfo(previousInstall, { serverPath: restorePath }));
    } else {
      // No managed binary existed before (standalone installer: the server ships inside
      // the app). install.json still points at the failed binary and, being newer, would
      // keep winning — drop the pointer so the resolvers fall back to the bundled one.
      log('Rolling back to the server bundled with the app');
      clearServerInfo(o.home, previousInstall);
    }
    if (await o.control.isRunning()) await o.control.stop();
    const serverDir = updatePaths(o.home).serverDir;
    if (webRootSwapped) restoreManagedWebRoot(serverDir);
    // Put the previous companion libraries back so a rolled-back AOT binary
    // does not crash on its first database access (no-op without snapshots).
    restoreNativeCompanions(serverDir);
    await o.control.start();
  } catch (rollbackErr) {
    setState('failed', undefined, `Rollback failed: ${rollbackErr instanceof Error ? rollbackErr.message : String(rollbackErr)}`);
    log('Rollback failed — see the server log. `astra restore-all` still works offline.');
  }
}
