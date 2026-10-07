import {
  app,
  BrowserWindow,
  dialog,
  ipcMain,
  Menu,
  nativeTheme,
  protocol,
  shell,
  systemPreferences,
  type MenuItemConstructorOptions,
} from 'electron';
import * as os from 'node:os';
import * as path from 'node:path';

import { handleAppRequest } from './appProtocol';
import { fetchClients, fetchProviders, fetchVersion, putBinding } from './api';
import { ServiceManager, StartupError, type RunningService } from './service';
import { TrayController, type TrayState } from './tray';
import { UpdateController } from './update';
import {
  NAV_ORDER,
  isThemeSource,
  menuLabels,
  normalizeAccentColor,
  overlaySymbolColor,
  sanitizeContextMenuItems,
  themeBackground,
  windowChromeOptions,
  type MenuCommand,
} from './shared/chrome';
import { astraPaths, type AstraPaths } from './shared/paths';
import { apiBase } from './shared/port';
import { bundledServerBinaryPath } from './shared/resolveServerBinary';
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

let paths: AstraPaths | null = null;
let service: ServiceManager | null = null;
let tray: TrayController | null = null;
let updater: UpdateController | null = null;
let mainWindow: BrowserWindow | null = null;
let quitting = false;
let mismatchDialogShown = false;
let trayState: TrayState = {
  running: false,
  port: null,
  apiVersionMismatch: false,
  clients: [],
  providers: [],
  updateAvailable: null,
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
  tray = new TrayController(path.join(appRoot, 'assets'));

  app.on('second-instance', () => {
    void showMainWindow();
  });
  app.on('activate', () => {
    void showMainWindow();
  });
  // Tray app: closing the window only hides it; quitting happens via the tray.
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
  process.on('unhandledRejection', (err) => console.error('[astra] unhandled rejection:', err));

  registerIpc();

  await app.whenReady();
  // macOS gets a native menu (shortcuts → renderer commands); on Windows/Linux the
  // renderer handles Ctrl+ shortcuts itself and the window has no menu bar.
  if (process.platform === 'darwin') Menu.setApplicationMenu(buildAppMenu());
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

  await showMainWindow();
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

async function refreshTray(): Promise<void> {
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
  };
  tray!.update(trayState, trayCallbacks());
}

function trayCallbacks() {
  return {
    onStartService: () => {
      void service!
        .ensureRunning()
        .then(() => refreshTray())
        .catch((err) => {
          void dialog.showMessageBox({
            type: 'error',
            title: 'Astra',
            message: 'Failed to start the Astra service',
            detail: err instanceof StartupError && err.logPath ? `${err.message}\n\nLog file:\n${err.logPath}` : String(err instanceof Error ? err.message : err),
            buttons: ['OK'],
            noLink: true,
          });
        });
    },
    onStopService: () => {
      void service!.stopService().then(() => refreshTray());
    },
    onOpenWindow: () => {
      void showMainWindow();
    },
    onSwitchProvider: (clientKind: string, providerId: string) => {
      void (async () => {
        const svc = service!;
        if (trayState.port === null) return;
        const runtime = await svc.readRuntime();
        try {
          await putBinding(apiBase(trayState.port), clientKind, providerId, runtime?.runtimeToken ?? null);
        } catch (err) {
          void dialog.showMessageBox({
            type: 'error',
            title: 'Astra',
            message: `Failed to switch provider for ${clientKind}`,
            detail: String(err instanceof Error ? err.message : err),
            buttons: ['OK'],
            noLink: true,
          });
        }
        await refreshTray();
      })();
    },
    onCheckUpdates: () => {
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

function createWindow(): BrowserWindow {
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
    if (!quitting) {
      event.preventDefault();
      win.hide();
    }
  });
  win.on('closed', () => {
    if (mainWindow === win) mainWindow = null;
  });

  if (DEV_RENDERER_URL) {
    void win.loadURL(DEV_RENDERER_URL);
    win.webContents.openDevTools({ mode: 'detach' });
  } else {
    void win.loadURL('app://astra/');
  }
  return win;
}

async function showMainWindow(): Promise<void> {
  if (mainWindow && !mainWindow.isDestroyed()) {
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.show();
    mainWindow.focus();
    return;
  }
  mainWindow = createWindow();
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
    void showMainWindow();
    return;
  }
  if (!win.isVisible()) win.show();
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
