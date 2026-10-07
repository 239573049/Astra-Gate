/**
 * Pure tray / menu-bar menu model (unit-testable without the Electron runtime). The app lives in the
 * tray: closing the window only hides it, so this menu is the primary surface while it is hidden —
 * status, window shortcuts, per-client provider switching, service control, and Quit.
 */
import type { MenuItemConstructorOptions } from 'electron';

import { NAV_ORDER, menuLabels, type NavTarget } from './chrome';
import type { GatewayClient, GatewayProvider } from './types';

/** A service transition the tray started and is still waiting on. */
export type ServiceActivity = 'starting' | 'stopping' | 'restarting';

export interface TrayState {
  running: boolean;
  port: number | null;
  apiVersionMismatch: boolean;
  clients: GatewayClient[];
  providers: GatewayProvider[];
  /** Non-null when an update check found something newer. */
  updateAvailable: string | null;
  /** In-flight start/stop/restart; service items are disabled meanwhile. */
  activity: ServiceActivity | null;
  /** Launch-at-login state; null where login items are unsupported (Linux, dev builds). */
  openAtLogin: boolean | null;
}

export interface TrayCallbacks {
  onOpenWindow(): void;
  onNavigate(target: NavTarget): void;
  onStartService(): void;
  onStopService(): void;
  onRestartService(): void;
  onCopyApiAddress(): void;
  onOpenInBrowser(): void;
  onOpenLogs(): void;
  onSwitchProvider(clientKind: string, providerId: string): void;
  onToggleOpenAtLogin(enabled: boolean): void;
  onCheckUpdates(): void;
  onQuit(): void;
}

export interface TrayMenuEnv {
  platform: NodeJS.Platform;
  /** OS locale (`app.getLocale()`); zh-* gets Chinese labels. */
  locale: string;
  /** App version shown in the header row. */
  version: string;
}

/** Tray labels; Chinese when the OS locale is zh-*. */
export function trayLabels(locale: string) {
  const zh = locale.toLowerCase().startsWith('zh');
  return {
    running: (port: number | null) => (zh ? `服务运行中 · 端口 ${port ?? '?'}` : `Service running on port ${port ?? '?'}`),
    stopped: zh ? '服务未运行' : 'Service not running',
    starting: zh ? '正在启动服务…' : 'Starting service…',
    stopping: zh ? '正在停止服务…' : 'Stopping service…',
    restarting: zh ? '正在重启服务…' : 'Restarting service…',
    mismatch: zh ? 'API 版本不匹配，请运行 "astra update"' : 'API version mismatch — run "astra update"',
    updateAvailable: zh ? '有可用更新…' : 'Update Available…',
    openWindow: zh ? '打开 Astra' : 'Open Astra',
    goTo: zh ? '前往' : 'Go To',
    clients: zh ? '客户端提供商' : 'Client Providers',
    noProviders: zh ? '未配置提供商' : 'No providers configured',
    disabledProvider: (name: string) => (zh ? `${name}（已停用）` : `${name} (disabled)`),
    unbound: zh ? '未绑定' : 'Not bound',
    startService: zh ? '启动服务' : 'Start Service',
    stopService: zh ? '停止服务' : 'Stop Service',
    restartService: zh ? '重启服务' : 'Restart Service',
    copyApiAddress: zh ? '复制 API 地址' : 'Copy API Address',
    openInBrowser: zh ? '在浏览器中打开' : 'Open in Browser',
    openLogs: zh ? '打开日志目录' : 'Open Logs Folder',
    openAtLogin: zh ? '登录时启动' : 'Launch at Login',
    checkUpdates: zh ? '检查更新…' : 'Check for Updates…',
    quit: zh ? '退出 Astra' : 'Quit Astra',
    tooltipRunning: (port: number | null) => (zh ? `Astra — 服务运行中（端口 ${port ?? '?'}）` : `Astra — running on port ${port ?? '?'}`),
    tooltipStopped: zh ? 'Astra — 服务未运行' : 'Astra — service not running',
    tooltipMismatch: zh ? 'Astra — API 版本不匹配' : 'Astra — API version mismatch',
    loginApprovalTitle: zh ? '需要在系统设置中允许 Astra 登录时启动' : 'Allow Astra to launch at login',
    loginApprovalDetail: zh
      ? '请在“系统设置 › 通用 › 登录项与扩展”中打开 Astra 的开关。'
      : 'Turn Astra on in System Settings › General › Login Items & Extensions.',
    openSystemSettings: zh ? '打开系统设置' : 'Open System Settings',
    ok: zh ? '好' : 'OK',
    closedToTrayTitle: zh ? 'Astra 仍在运行' : 'Astra is still running',
    closedToTray: (platform: NodeJS.Platform) =>
      platform === 'darwin'
        ? zh
          ? '窗口已关闭，网关继续在后台运行。点击菜单栏中的 Astra 图标可重新打开或退出。'
          : 'The window is closed but the gateway keeps running. Use the Astra icon in the menu bar to reopen or quit.'
        : zh
          ? '窗口已关闭，网关继续在后台运行。点击托盘中的 Astra 图标可重新打开，右键可退出。'
          : 'The window is closed but the gateway keeps running. Click the Astra tray icon to reopen it, or right-click to quit.',
  };
}

function statusLabel(state: TrayState, t: ReturnType<typeof trayLabels>): string {
  if (state.activity) return t[state.activity];
  return state.running ? t.running(state.port) : t.stopped;
}

/** Hover text for the tray icon. */
export function trayTooltip(state: TrayState, locale: string): string {
  const t = trayLabels(locale);
  if (state.activity) return `Astra — ${t[state.activity]}`;
  if (!state.running) return t.tooltipStopped;
  if (state.apiVersionMismatch) return t.tooltipMismatch;
  return t.tooltipRunning(state.port);
}

/**
 * Change-detection key: the menu is rebuilt only when this changes, so the 10s poll does not
 * churn native menus (and does not disturb an open menu on Windows/Linux).
 */
export function trayStateKey(state: TrayState): string {
  return JSON.stringify(state);
}

export function buildTrayMenuTemplate(
  state: TrayState,
  cb: TrayCallbacks,
  env: TrayMenuEnv,
): MenuItemConstructorOptions[] {
  const t = trayLabels(env.locale);
  const nav = menuLabels(env.locale).nav;
  const mac = env.platform === 'darwin';
  const busy = state.activity !== null;
  const template: MenuItemConstructorOptions[] = [];

  // Status block.
  template.push({ label: env.version ? `Astra ${env.version}` : 'Astra', enabled: false });
  template.push({ label: statusLabel(state, t), enabled: false });
  if (state.running && state.apiVersionMismatch) template.push({ label: t.mismatch, enabled: false });
  if (state.updateAvailable) template.push({ label: t.updateAvailable, click: () => cb.onCheckUpdates() });

  // Window.
  template.push(
    { type: 'separator' },
    { label: t.openWindow, click: () => cb.onOpenWindow() },
    {
      label: t.goTo,
      submenu: NAV_ORDER.map((target) => ({ label: nav[target], click: () => cb.onNavigate(target) })),
    },
  );

  // Per-client provider switching (needs the server).
  const enabledClients = state.clients.filter((c) => c.enabled);
  if (enabledClients.length > 0) {
    template.push({ type: 'separator' }, { label: t.clients, enabled: false });
    for (const client of enabledClients) {
      const current = state.providers.find((p) => p.id === client.providerId);
      const currentName = current?.name ?? t.unbound;
      const items: MenuItemConstructorOptions[] = state.providers.map((p) => ({
        label: p.enabled ? p.name : t.disabledProvider(p.name),
        type: 'radio',
        checked: client.providerId != null && client.providerId === p.id,
        click: () => cb.onSwitchProvider(client.kind, p.id),
      }));
      if (items.length === 0) items.push({ label: t.noProviders, enabled: false });
      template.push({
        // macOS 14.4+ renders a native sublabel; elsewhere the binding goes inline.
        label: mac ? client.name : `${client.name} · ${currentName}`,
        ...(mac ? { sublabel: currentName } : {}),
        enabled: state.running && !busy,
        submenu: items,
      });
    }
  }

  // Service control.
  template.push({ type: 'separator' });
  if (state.running) {
    template.push(
      { label: t.stopService, enabled: !busy, click: () => cb.onStopService() },
      { label: t.restartService, enabled: !busy, click: () => cb.onRestartService() },
    );
  } else {
    template.push({ label: t.startService, enabled: !busy, click: () => cb.onStartService() });
  }
  template.push(
    { label: t.copyApiAddress, enabled: state.running && state.port !== null, click: () => cb.onCopyApiAddress() },
    { label: t.openInBrowser, enabled: state.running && state.port !== null, click: () => cb.onOpenInBrowser() },
  );

  // App.
  template.push({ type: 'separator' });
  if (state.openAtLogin !== null) {
    const next = !state.openAtLogin;
    template.push({
      label: t.openAtLogin,
      type: 'checkbox',
      checked: state.openAtLogin,
      click: () => cb.onToggleOpenAtLogin(next),
    });
  }
  template.push(
    { label: t.checkUpdates, click: () => cb.onCheckUpdates() },
    { label: t.openLogs, click: () => cb.onOpenLogs() },
    { type: 'separator' },
    { label: t.quit, ...(mac ? { accelerator: 'Command+Q' } : {}), click: () => cb.onQuit() },
  );
  return template;
}
