import { Menu, Tray, nativeImage } from 'electron';
import type { MenuItemConstructorOptions } from 'electron';
import * as path from 'node:path';

import type { GatewayClient, GatewayProvider } from './shared/types';

export interface TrayState {
  running: boolean;
  port: number | null;
  apiVersionMismatch: boolean;
  clients: GatewayClient[];
  providers: GatewayProvider[];
  /** Non-null when an update check found something newer. */
  updateAvailable: string | null;
}

export interface TrayCallbacks {
  onStartService(): void;
  onStopService(): void;
  onOpenWindow(): void;
  onSwitchProvider(clientKind: string, providerId: string): void;
  onCheckUpdates(): void;
  onQuit(): void;
}

/** Pure menu-template builder (unit-testable without Electron runtime). */
export function buildMenuTemplate(state: TrayState, cb: TrayCallbacks): MenuItemConstructorOptions[] {
  const mismatchSuffix = state.apiVersionMismatch ? ' — API version mismatch, run "astra update"' : '';
  const statusLabel = state.running ? `Service running on port ${state.port ?? '?'}` : 'Service not running';
  const template: MenuItemConstructorOptions[] = [
    { label: `Astra${mismatchSuffix}`, enabled: false },
    { label: statusLabel, enabled: false },
    ...(state.updateAvailable ? [{ label: 'Update available — check for details', enabled: false }] : []),
    { type: 'separator' },
    { label: 'Check for Updates…', click: () => cb.onCheckUpdates() },
    { label: 'Start Service', enabled: !state.running, click: () => cb.onStartService() },
    { label: 'Stop Service', enabled: state.running, click: () => cb.onStopService() },
    { type: 'separator' },
    { label: 'Open Astra', click: () => cb.onOpenWindow() },
  ];
  const enabledClients = state.clients.filter((c) => c.enabled);
  for (const client of enabledClients) {
    const items: MenuItemConstructorOptions[] = state.providers.map((p) => ({
      label: p.enabled ? p.name : `${p.name} (disabled)`,
      type: 'radio',
      checked: client.providerId != null && client.providerId === p.id,
      click: () => cb.onSwitchProvider(client.kind, p.id),
    }));
    if (items.length === 0) items.push({ label: 'No providers configured', enabled: false });
    template.push({ label: client.name, submenu: items });
  }
  if (enabledClients.length > 0) template.push({ type: 'separator' });
  template.push({ label: 'Quit', click: () => cb.onQuit() });
  return template;
}

export class TrayController {
  private tray: Tray | null = null;

  constructor(private readonly assetsDir: string) {}

  ensure(): Tray {
    if (this.tray) return this.tray;
    const file = process.platform === 'darwin' ? 'trayTemplate.png' : 'tray.png';
    const icon = nativeImage.createFromPath(path.join(this.assetsDir, file));
    this.tray = new Tray(icon);
    this.tray.setToolTip('Astra');
    return this.tray;
  }

  update(state: TrayState, cb: TrayCallbacks): void {
    this.ensure().setContextMenu(Menu.buildFromTemplate(buildMenuTemplate(state, cb)));
  }

  destroy(): void {
    this.tray?.destroy();
    this.tray = null;
  }
}
