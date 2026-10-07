import { Menu, Notification, Tray, nativeImage } from 'electron';
import * as path from 'node:path';

import {
  buildTrayMenuTemplate,
  trayStateKey,
  trayTooltip,
  type TrayCallbacks,
  type TrayMenuEnv,
  type TrayState,
} from './shared/trayMenu';

export type { TrayCallbacks, TrayState } from './shared/trayMenu';

/**
 * Owns the tray / menu-bar icon. Platform behavior:
 * - macOS: template icon in the menu bar; any click opens the menu (the native convention).
 * - Windows: left click opens the window, right click opens the menu.
 * - Linux: most desktops (AppIndicator) only show the menu; click events may never arrive, so the
 *   menu carries "Open Astra" too.
 */
export class TrayController {
  private tray: Tray | null = null;
  private lastKey: string | null = null;

  constructor(
    private readonly assetsDir: string,
    private readonly env: TrayMenuEnv,
    private readonly onActivate: () => void,
  ) {}

  ensure(): Tray {
    if (this.tray) return this.tray;
    const mac = this.env.platform === 'darwin';
    // trayTemplate.png + @2x: macOS tints "Template" images for light/dark menu bars.
    const icon = nativeImage.createFromPath(path.join(this.assetsDir, mac ? 'trayTemplate.png' : 'tray.png'));
    if (mac) icon.setTemplateImage(true);
    this.tray = new Tray(icon);
    this.tray.setToolTip('Astra');
    // Windows: primary click opens the window. Linux: fires only where the desktop supports
    // activation; harmless elsewhere. macOS: setContextMenu makes every click open the menu.
    if (!mac) this.tray.on('click', () => this.onActivate());
    return this.tray;
  }

  /**
   * Rebuilds the menu when the state changed. `force` rebuilds anyway — needed after a radio /
   * checkbox click whose action failed, because the native item already toggled itself.
   */
  update(state: TrayState, cb: TrayCallbacks, force = false): void {
    const tray = this.ensure();
    const key = trayStateKey(state);
    if (!force && key === this.lastKey) return;
    this.lastKey = key;
    tray.setToolTip(trayTooltip(state, this.env.locale));
    tray.setContextMenu(Menu.buildFromTemplate(buildTrayMenuTemplate(state, cb, this.env)));
  }

  /** One-off hint (e.g. "still running in the tray"); balloon on Windows, notification elsewhere. */
  notify(title: string, body: string): void {
    if (this.env.platform === 'win32' && this.tray) {
      this.tray.displayBalloon({ title, content: body, iconType: 'info' });
      return;
    }
    if (Notification.isSupported()) new Notification({ title, body, silent: true }).show();
  }

  destroy(): void {
    this.tray?.destroy();
    this.tray = null;
    this.lastKey = null;
  }
}
