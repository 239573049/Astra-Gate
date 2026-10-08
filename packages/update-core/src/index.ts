export {
  DEFAULT_CHANNEL,
  DEFAULT_FEED_URL,
  UpdateFeedError,
  fetchManifest,
  type FetchManifestOptions,
  type UpdateManifest,
  type UpdatePlatformInfo,
} from './manifest.js';
export {
  isApiVersionCompatible,
  isNewerVersion,
  majorOf,
  parseVersion,
} from './version.js';
export {
  MAX_CONSECUTIVE_FAILURES,
  initialState,
  readUpdateState,
  writeUpdateState,
  type UpdatePhase,
  type UpdateState,
} from './state.js';
export { managedServerBinaryPath, updatePaths, type UpdatePaths } from './paths.js';
export { npmChildEnv, npmInstallIntoPrefix, npmView, locateNpm, type LocateNpmOptions, type NpmCommand, type NpmRunnerOptions } from './npm-runner.js';
export { readInstallInfo, writeInstallInfo, applyServerInfo, type InstallInfo } from './install-info.js';
export {
  NATIVE_LIBRARY,
  findNativeCompanions,
  sha256File,
  stageServerBinary,
  type StagedServerBinary,
  type StageServerBinaryOptions,
} from './staging.js';
export {
  MAX_BACKUPS,
  backupCurrentBinary,
  commitManagedWebRoot,
  commitNativeCompanions,
  installManagedBinary,
  installManagedWebRoot,
  pruneBackups,
  pruneManagedBinaries,
  restoreManagedWebRoot,
  restoreNativeCompanions,
} from './swap.js';
export {
  UpdateError,
  applyServerUpdate,
  type ApplyServerUpdateOptions,
  type ApplyServerUpdateResult,
  type ServerControl,
} from './apply.js';
export {
  checkForServerUpdate,
  type ServerUpdateStatus,
} from './check.js';
export {
  createLauncher,
  findDesktopEntry,
  installDesktopClient,
  linuxLauncherTarget,
  macLauncherTarget,
  removeLauncher,
  renderLinuxDesktopFile,
  winLauncherTarget,
  type DesktopEntry,
  type InstallDesktopClientOptions,
  type InstalledDesktop,
  type SupportedPlatform,
} from './desktop-install.js';
