import {
  app,
  autoUpdater as nativeAutoUpdater,
  BrowserWindow,
  clipboard,
  dialog,
  ipcMain,
  Menu,
  nativeTheme,
  protocol,
  shell,
  systemPreferences,
  type MenuItemConstructorOptions,
} from 'electron';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

import { handleAppRequest } from './appProtocol';
import { fetchClients, fetchProviders, fetchVersion, putBinding } from './api';
import { ServiceManager, StartupError, type RunningService } from './service';
import { TrayController, type TrayCallbacks, type TrayState } from './tray';
import { UpdateController } from './update';
import {
  NAV_ORDER,
  isThemeSource,
  menuLabels,
  navHash,
  normalizeAccentColor,
  overlaySymbolColor,
  sanitizeContextMenuItems,
  themeBackground,
  windowChromeOptions,
  type MenuCommand,
  type NavTarget,
} from './shared/chrome';
import { astraPaths, type AstraPaths } from './shared/paths';
import { apiBase } from './shared/port';
import { parsePrefs, type DesktopPrefs } from './shared/prefs';
import { bundledServerBinaryPath } from './shared/resolveServerBinary';
import { trayLabels, type ServiceActivity } from './shared/trayMenu';
import { EXPECTED_API_MAJOR } from './shared/version';
import type { GatewayClient, GatewayProvider } from './shared/types';

// Must run before app ready: makes Origin `app://astra` real for CORS,
// fetch, and secure-context purposes.
protocol.registerSchemesAsPrivileged([
  {
    scheme: 'app',
    privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true },
  },
]);

const DEV_RENDERER_URL = process.env.ASTRA_RENDERER_URL?.trim() || '';
/** Passed by the Windows login item: start in the tray without opening the window. */
const HIDDEN_ARG = '--hidden';
const IS_MAC = process.platform === 'darwin';

let paths: AstraPaths | null = null;
let service: ServiceManager | null = null;
let tray: TrayController | null = null;
let updater: UpdateController | null = null;
let mainWindow: BrowserWindow | null = null;
let quitting = false;
/** Set when Squirrel's quitAndInstall closes the windows ahead of before-quit. */
let closingForUpdate = false;
let mismatchDialogShown = false;
let prefs: DesktopPrefs | null = null;
let trayState: TrayState = {
  running: false,
  port: null,
  apiVersionMismatch: false,
  clients: [],
  providers: [],
  updateAvailable: null,
  activity: null,
  openAtLogin: null,
};
let lastBoot = { apiBase: apiBase(17321), apiVersionMismatch: false };

async function main(): Promise<void> {
  const appRoot = app.getAppPath();
  paths = astraPaths(process.env, os.homedir());
  service = new ServiceManager({
    paths,
    repoRoot: path.resolve(appRoot, '..'),
    // Installers ship the server under resources/server (electron-builder
    // extraResources); it is built from the same release as the app.
    bundledServer: app.isPackaged
      ? { path: bundledServerBinaryPath(process.resourcesPath, process.platform), version: app.getVersion() }
      : null,
    platform: process.platform,
    env: process.env,
    expectedApiMajor: EXPECTED_API_MAJOR,
    log: (line) => console.log(`[astra] ${line}`),
  });
  updater = new UpdateController({
    home: paths.home,
    installDesktopPath: path.join(paths.home, 'desktop'),
    expectedApiMajor: EXPECTED_API_MAJOR,
    apiBase: () => (trayState.port !== null ? apiBase(trayState.port) : null),
    platform: process.platform,
    serverPackage: `@aidotnet/server-${process.platform === 'darwin' ? 'darwin' : process.platform === 'win32' ? 'win32' : 'linux'}-${process.arch}`,
    desktopPackage: `@aidotnet/desktop-${process.platform === 'darwin' ? 'darwin' : process.platform === 'win32' ? 'win32' : 'linux'}-${process.arch}`,
    control: {
      isRunning: async () => (await service!.probe()).running,
      stop: async () => {
        await service!.stopService();
      },
      start: async () => {
        await service!.ensureRunning();
      },
      probeVersion: async () => {
        const probed = await service!.probe();
        if (!probed.running || probed.port === null) return null;
        const v = await fetchVersion(apiBase(probed.port));
        return v.version ?? null;
      },
    },
    log: (line) => console.log(`[astra] ${line}`),
  });
  app.on('second-instance', () => {
    void showMainWindow();
  });
  // macOS: re-opening the app (Finder, Launchpad, Spotlight) while it lives in the menu bar.
  app.on('activate', () => {
    void showMainWindow();
  });
  // Tray app: closing the window only hides it (and drops the Dock / taskbar entry); the process
  // keeps running in the tray / menu bar until Quit.
  app.on('window-all-closed', () => {});
  app.on('before-quit', (event) => {
    if (quitting) return;
    event.preventDefault();
    quitting = true;
    void service!
      .stopIfOurs()
      .catch((err) => console.error('[astra] shutdown error:', err))
      .finally(() => app.quit());
  });
  if (IS_MAC) {
    // quitAndInstall closes every window before before-quit fires; let those closes through.
    nativeAutoUpdater.on('before-quit-for-update', () => {
      closingForUpdate = true;
    });
  }
  process.on('unhandledRejection', (err) => console.error('[astra] unhandled rejection:', err));

  registerIpc();

  await app.whenReady();
  tray = new TrayController(
    path.join(appRoot, 'assets'),
    { platform: process.platform, locale: app.getLocale(), version: app.getVersion() },
    () => void showMainWindow(),
  );
  // Launched at login: stay in the tray / menu bar, no window and no Dock icon.
  const startHidden = launchedAtLogin();
  if (startHidden && IS_MAC) app.dock?.hide();
  // macOS gets a native menu (shortcuts → renderer commands); on Windows/Linux the
  // renderer handles Ctrl+ shortcuts itself and the window has no menu bar.
  if (IS_MAC) Menu.setApplicationMenu(buildAppMenu());
  else Menu.setApplicationMenu(null);
  watchSystemAppearance();

  // Registered before the first window is created.
  const rendererRoot = path.join(appRoot, 'renderer');
  let currentApiOrigin = lastBoot.apiBase;
  protocol.handle('app', (request) => handleAppRequest(request, rendererRoot, currentApiOrigin));

  const running = await ensureServiceWithDialogs();
  if (running === null) {
    app.exit(0);
    return;
  }
  currentApiOrigin = running.apiBase;
  lastBoot = { apiBase: running.apiBase, apiVersionMismatch: running.apiVersionMismatch };
  if (running.apiVersionMismatch) await showMismatchDialog(running.apiVersion);

  await refreshTray();
  // Poll health + client/provider bindings every 10s to keep the tray current.
  setInterval(() => {
    void refreshTray();
  }, 10_000);
  // Background update checks (12h cadence, first one shortly after boot).
  updater!.start();

  if (!startHidden) await showMainWindow();
}

/** Start (or adopt) the service, looping on a retry dialog on failure. Null = user chose Quit. */
async function ensureServiceWithDialogs(): Promise<RunningService | null> {
  for (;;) {
    const svc = service!;
    const dataPaths = paths!;
    try {
      return await svc.ensureRunning();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      const logPath = err instanceof StartupError ? err.logPath : null;
      bringAppToFront();
      const { response } = await dialog.showMessageBox({
        type: 'error',
        title: 'Astra',
        message: 'Failed to start the Astra service',
        detail: `${message}${logPath ? `\n\nLog file:\n${logPath}` : ''}`,
        buttons: ['Retry', 'Open Logs', 'Quit'],
        defaultId: 0,
        cancelId: 2,
        noLink: true,
      });
      if (response === 1) {
        void shell.openPath(dataPaths.logsDir);
        continue;
      }
      if (response === 0) continue;
      return null;
    }
  }
}

async function showMismatchDialog(apiVersion: string | null): Promise<void> {
  if (mismatchDialogShown) return;
  mismatchDialogShown = true;
  await dialog.showMessageBox({
    type: 'warning',
    title: 'Astra',
    message: 'Astra API version mismatch',
    detail:
      `The running server reports API version "${apiVersion ?? 'unknown'}", but this desktop app ` +
      `expects API major version ${EXPECTED_API_MAJOR}.\n\n` +
      'Run "astra update" to update the server, then restart Astra.',
    buttons: ['OK'],
    noLink: true,
  });
}

/** Re-probes the service and rebuilds the tray. `force` rebuilds even when nothing changed. */
async function refreshTray(force = false): Promise<void> {
  const svc = service!;
  const probed = await svc.probe(2000);
  let clients: GatewayClient[] = [];
  let providers: GatewayProvider[] = [];
  if (probed.running && probed.port !== null) {
    const base = apiBase(probed.port);
    [clients, providers] = await Promise.all([
      fetchClients(base).catch(() => []),
      fetchProviders(base).catch(() => []),
    ]);
  }
  const mismatch = probed.running ? svc.computeMismatch(probed.apiVersion) : false;
  if (mismatch) void showMismatchDialog(probed.apiVersion);
  trayState = {
    running: probed.running,
    port: probed.port,
    apiVersionMismatch: mismatch,
    clients,
    providers,
    updateAvailable: updater!.hasUpdate ? 'yes' : null,
    // Owned by runServiceAction; a poll finishing mid-transition must not clear it.
    activity: trayState.activity,
    openAtLogin: readOpenAtLogin(),
  };
  tray!.update(trayState, trayCallbacks(), force);
}

/** Runs one start/stop/restart from the tray, showing progress in the menu meanwhile. */
function runServiceAction(activity: ServiceActivity, action: () => Promise<unknown>): void {
  if (trayState.activity) return;
  trayState = { ...trayState, activity };
  tray!.update(trayState, trayCallbacks());
  void action()
    .catch((err) => showServiceError(err))
    .finally(() => {
      trayState = { ...trayState, activity: null };
      void refreshTray();
    });
}

function showServiceError(err: unknown): void {
  bringAppToFront();
  void dialog.showMessageBox({
    type: 'error',
    title: 'Astra',
    message: 'Failed to start the Astra service',
    detail:
      err instanceof StartupError && err.logPath
        ? `${err.message}\n\nLog file:\n${err.logPath}`
        : String(err instanceof Error ? err.message : err),
    buttons: ['OK'],
    noLink: true,
  });
}

function trayCallbacks(): TrayCallbacks {
  return {
    onOpenWindow: () => {
      void showMainWindow();
    },
    onNavigate: (target) => {
      void showMainWindow(target);
    },
    onStartService: () => {
      runServiceAction('starting', () => service!.ensureRunning());
    },
    onStopService: () => {
      runServiceAction('stopping', () => service!.stopService());
    },
    onRestartService: () => {
      runServiceAction('restarting', async () => {
        await service!.stopService();
        await service!.ensureRunning();
      });
    },
    onCopyApiAddress: () => {
      if (trayState.port !== null) clipboard.writeText(apiBase(trayState.port));
    },
    onOpenInBrowser: () => {
      if (trayState.port !== null) void shell.openExternal(`${apiBase(trayState.port)}/`);
    },
    onOpenLogs: () => {
      void shell.openPath(paths!.logsDir);
    },
    onSwitchProvider: (clientKind: string, providerId: string) => {
      void (async () => {
        const svc = service!;
        if (trayState.port === null) return;
        const runtime = await svc.readRuntime();
        try {
          await putBinding(apiBase(trayState.port), clientKind, providerId, runtime?.runtimeToken ?? null);
        } catch (err) {
          bringAppToFront();
          void dialog.showMessageBox({
            type: 'error',
            title: 'Astra',
            message: `Failed to switch provider for ${clientKind}`,
            detail: String(err instanceof Error ? err.message : err),
            buttons: ['OK'],
            noLink: true,
          });
        }
        // Forced: on failure the native radio item has already moved to the clicked provider.
        await refreshTray(true);
      })();
    },
    onToggleOpenAtLogin: (enabled: boolean) => {
      void setOpenAtLogin(enabled).finally(() => refreshTray(true));
    },
    onCheckUpdates: () => {
      bringAppToFront();
      void updater!
        .checkInteractive()
        .catch((err) => console.error('[astra] update error:', err))
        .finally(() => refreshTray());
    },
    onQuit: () => {
      updater?.stop();
      app.quit();
    },
  };
}

/** Login items need a stable, packaged executable; Linux has no Electron API for them. */
function loginItemSupported(): boolean {
  return app.isPackaged && (process.platform === 'darwin' || process.platform === 'win32');
}

/** Windows registers the run key with HIDDEN_ARG; queries must pass the same args to match it. */
function loginItemArgs(): { args?: string[] } {
  return process.platform === 'win32' ? { args: [HIDDEN_ARG] } : {};
}

function readOpenAtLogin(): boolean | null {
  if (!loginItemSupported()) return null;
  try {
    return app.getLoginItemSettings(loginItemArgs()).openAtLogin;
  } catch {
    return null;
  }
}

async function setOpenAtLogin(enabled: boolean): Promise<void> {
  if (!loginItemSupported()) return;
  try {
    app.setLoginItemSettings({ openAtLogin: enabled, ...loginItemArgs() });
  } catch (err) {
    console.error('[astra] login item update failed:', err);
    return;
  }
  // macOS 13+: the user may have to allow the login item in System Settings first.
  if (IS_MAC && enabled && app.getLoginItemSettings().status === 'requires-approval') {
    const t = trayLabels(app.getLocale());
    bringAppToFront();
    const { response } = await dialog.showMessageBox({
      type: 'info',
      title: 'Astra',
      message: t.loginApprovalTitle,
      detail: t.loginApprovalDetail,
      buttons: [t.openSystemSettings, t.ok],
      defaultId: 0,
      cancelId: 1,
      noLink: true,
    });
    if (response === 0) void shell.openExternal('x-apple.systempreferences:com.apple.LoginItems-Settings.extension');
  }
}

/** True when this process was started by the login item (Windows arg, macOS launch reason). */
function launchedAtLogin(): boolean {
  if (process.argv.includes(HIDDEN_ARG)) return true;
  if (!IS_MAC || !app.isPackaged) return false;
  try {
    return app.getLoginItemSettings().wasOpenedAtLogin;
  } catch {
    return false;
  }
}

/** macOS: a menu-bar-only app is not frontmost, so its dialogs would open behind other apps. */
function bringAppToFront(): void {
  if (IS_MAC) app.focus({ steal: true });
}

function prefsPath(): string {
  return path.join(app.getPath('userData'), 'desktop-prefs.json');
}

function loadPrefs(): DesktopPrefs {
  if (prefs) return prefs;
  let text: string | null = null;
  try {
    text = fs.readFileSync(prefsPath(), 'utf8');
  } catch {
    text = null;
  }
  prefs = parsePrefs(text);
  return prefs;
}

function savePrefs(next: DesktopPrefs): void {
  prefs = next;
  try {
    fs.mkdirSync(path.dirname(prefsPath()), { recursive: true });
    fs.writeFileSync(prefsPath(), JSON.stringify(next, null, 2));
  } catch (err) {
    console.error('[astra] could not save desktop prefs:', err);
  }
}

/** The first close explains where the app went (the window and its Dock/taskbar entry vanish). */
function showCloseHintOnce(): void {
  const current = loadPrefs();
  if (current.closeHintShown) return;
  savePrefs({ ...current, closeHintShown: true });
  const t = trayLabels(app.getLocale());
  tray?.notify(t.closedToTrayTitle, t.closedToTray(process.platform));
}

/**
 * Close-to-tray: hide the window and drop it from the Dock / taskbar. Hidden windows have no
 * taskbar button on Windows/Linux; macOS additionally hides the Dock icon (accessory app).
 */
function hideToTray(win: BrowserWindow): void {
  // macOS: hiding a full-screen window leaves an empty black Space behind; leave full screen first.
  if (win.isFullScreen()) {
    win.once('leave-full-screen', () => hideToTray(win));
    win.setFullScreen(false);
    return;
  }
  win.hide();
  if (IS_MAC) app.dock?.hide();
  showCloseHintOnce();
}

function createWindow(nav?: NavTarget): BrowserWindow {
  const appRoot = app.getAppPath();
  const dark = nativeTheme.shouldUseDarkColors;
  const win = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 960,
    minHeight: 640,
    show: false,
    title: 'Astra',
    autoHideMenuBar: true,
    ...windowChromeOptions(process.platform, dark),
    webPreferences: {
      preload: path.join(appRoot, 'dist', 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      spellcheck: false,
      additionalArguments: [
        `--astra-api-base=${lastBoot.apiBase}`,
        `--astra-api-mismatch=${lastBoot.apiVersionMismatch ? '1' : '0'}`,
        `--astra-version=${app.getVersion()}`,
        `--astra-accent=${currentAccentColor() ?? ''}`,
      ],
    },
  });
  win.once('ready-to-show', () => {
    win.show();
  });
  // No new windows: external http(s) links go to the default browser.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:\/\//i.test(url)) void shell.openExternal(url);
    return { action: 'deny' };
  });
  // Close-to-tray: hide instead of closing while the app keeps running.
  win.on('close', (event) => {
    if (quitting || closingForUpdate) return;
    event.preventDefault();
    hideToTray(win);
  });
  win.on('closed', () => {
    if (mainWindow === win) mainWindow = null;
  });

  const hash = nav ? navHash(nav) : '';
  if (DEV_RENDERER_URL) {
    void win.loadURL(`${DEV_RENDERER_URL}${hash}`);
    win.webContents.openDevTools({ mode: 'detach' });
  } else {
    void win.loadURL(`app://astra/${hash}`);
  }
  return win;
}

/** Shows (creating if needed) the main window, optionally on a page; restores the Dock icon on macOS. */
async function showMainWindow(nav?: NavTarget): Promise<void> {
  if (IS_MAC) await app.dock?.show();
  if (mainWindow && !mainWindow.isDestroyed()) {
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.show();
    mainWindow.focus();
    if (nav) mainWindow.webContents.send('astra:menu-command', `nav:${nav}` satisfies MenuCommand);
  } else {
    mainWindow = createWindow(nav);
  }
  // Coming back from accessory (menu-bar-only) mode the app is not active; bring it forward.
  bringAppToFront();
}

function registerIpc(): void {
  ipcMain.handle('astra:open-path', (_event, p: unknown): Promise<boolean> => {
    if (typeof p !== 'string' || p.length === 0) return Promise.resolve(false);
    return shell.openPath(p).then((err) => err === '');
  });
  ipcMain.handle('astra:reveal-logs', (): Promise<boolean> => {
    return shell.openPath(paths!.logsDir).then((err) => err === '');
  });
  // Renderer "Start service" button (shown when the server is unreachable).
  ipcMain.handle('astra:start-service', async (): Promise<{ ok: boolean; error: string | null }> => {
    try {
      await service!.ensureRunning();
      await refreshTray();
      return { ok: true, error: null };
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      const logPath = err instanceof StartupError ? err.logPath : null;
      return { ok: false, error: logPath ? `${message} (${logPath})` : message };
    }
  });
  ipcMain.handle('astra:get-accent', (): string | null => currentAccentColor());
  ipcMain.handle('astra:set-theme', (_event, source: unknown): boolean => {
    if (!isThemeSource(source)) return false;
    nativeTheme.themeSource = source;
    return true;
  });
  // Native context menu; resolves with the clicked item id, or null when dismissed.
  ipcMain.handle('astra:context-menu', (event, rawItems: unknown): Promise<string | null> => {
    const items = sanitizeContextMenuItems(rawItems);
    const win = BrowserWindow.fromWebContents(event.sender);
    if (!items || !win) return Promise.resolve(null);
    return new Promise((resolve) => {
      let picked: string | null = null;
      const template: MenuItemConstructorOptions[] = items.map((item) =>
        item.type === 'separator'
          ? { type: 'separator' }
          : {
              label: item.label,
              type: item.type,
              enabled: item.enabled,
              checked: item.checked,
              click: () => {
                picked = item.id ?? null;
              },
            },
      );
      Menu.buildFromTemplate(template).popup({ window: win, callback: () => resolve(picked) });
    });
  });
}

/** System accent color as "#rrggbb" (macOS / Windows), null elsewhere. */
function currentAccentColor(): string | null {
  if (process.platform !== 'darwin' && process.platform !== 'win32') return null;
  try {
    return normalizeAccentColor(systemPreferences.getAccentColor());
  } catch {
    return null;
  }
}

function broadcast(channel: string, ...args: unknown[]): void {
  for (const win of BrowserWindow.getAllWindows()) {
    if (!win.isDestroyed()) win.webContents.send(channel, ...args);
  }
}

function sendMenuCommand(command: MenuCommand): void {
  const win = mainWindow && !mainWindow.isDestroyed() ? mainWindow : null;
  if (!win) {
    // A fresh window cannot receive commands yet; nav commands become its initial route.
    void showMainWindow(command.startsWith('nav:') ? (command.slice(4) as NavTarget) : undefined);
    return;
  }
  if (!win.isVisible()) void showMainWindow();
  win.webContents.send('astra:menu-command', command);
}

/** Keeps accent color, window background and the Windows overlay in sync with the OS. */
function watchSystemAppearance(): void {
  const pushAccent = () => broadcast('astra:accent-changed', currentAccentColor());
  if (process.platform === 'darwin') {
    systemPreferences.subscribeNotification('AppleColorPreferencesChangedNotification', pushAccent);
  } else if (process.platform === 'win32') {
    systemPreferences.on('accent-color-changed', pushAccent);
  }
  nativeTheme.on('updated', () => {
    const dark = nativeTheme.shouldUseDarkColors;
    for (const win of BrowserWindow.getAllWindows()) {
      if (win.isDestroyed()) continue;
      win.setBackgroundColor(themeBackground(dark));
      if (process.platform === 'win32') {
        win.setTitleBarOverlay({ color: '#00000000', symbolColor: overlaySymbolColor(dark) });
      }
    }
  });
}

function buildAppMenu(): Menu {
  const t = menuLabels(app.getLocale());
  const nav: MenuItemConstructorOptions[] = NAV_ORDER.map((target, i) => ({
    label: t.nav[target],
    accelerator: `CmdOrCtrl+${i + 1}`,
    click: () => sendMenuCommand(`nav:${target}`),
  }));
  const template: MenuItemConstructorOptions[] = [
    {
      role: 'appMenu',
      submenu: [
        { role: 'about' },
        { type: 'separator' },
        { label: t.settings, accelerator: 'CmdOrCtrl+,', click: () => sendMenuCommand('nav:settings') },
        { type: 'separator' },
        { role: 'services' },
        { type: 'separator' },
        { role: 'hide' },
        { role: 'hideOthers' },
        { role: 'unhide' },
        { type: 'separator' },
        { role: 'quit' },
      ],
    },
    {
      label: t.file,
      submenu: [
        { label: t.newProvider, accelerator: 'CmdOrCtrl+N', click: () => sendMenuCommand('new-provider') },
        { type: 'separator' },
        { role: 'close' },
      ],
    },
    { role: 'editMenu' },
    {
      label: t.view,
      submenu: [
        ...nav,
        { type: 'separator' },
        { label: t.find, accelerator: 'CmdOrCtrl+F', click: () => sendMenuCommand('find') },
        // Overrides the default page reload: ⌘R refetches data instead.
        { label: t.refresh, accelerator: 'CmdOrCtrl+R', click: () => sendMenuCommand('refresh') },
        { type: 'separator' },
        { label: t.toggleSidebar, accelerator: 'CmdOrCtrl+Alt+S', click: () => sendMenuCommand('toggle-sidebar') },
        { type: 'separator' },
        ...(DEV_RENDERER_URL ? ([{ role: 'toggleDevTools' }] as MenuItemConstructorOptions[]) : []),
        { role: 'togglefullscreen' },
      ],
    },
    { role: 'windowMenu' },
    {
      role: 'help',
      submenu: [{ label: t.openLogs, click: () => void shell.openPath(paths!.logsDir) }],
    },
  ];
  return Menu.buildFromTemplate(template);
}

// Entry point — last in the module so every module-level `let` above is initialized before main()
// runs (main() assigns them synchronously before its first await).
if (!app.requestSingleInstanceLock()) {
  // Second launch: the first instance will focus its window.
  app.quit();
} else {
  void main().catch((err) => {
    console.error('[astra] fatal:', err);
    dialog.showErrorBox('Astra', `Fatal error: ${err instanceof Error ? err.message : String(err)}`);
    app.exit(1);
  });
}
