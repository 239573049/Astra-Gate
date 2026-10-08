import { Menu, Notification, Tray, nativeImage, type Rectangle } from 'electron';
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
 * - macOS: template icon in the menu bar; a click opens the tray panel, a right click (or ⌃-click) the menu.
 * - Windows: left click opens the tray panel, right click opens the menu.
 * - Linux: most desktops (AppIndicator) only show the menu; click events may never arrive, so the
 *   menu carries "Open Astra" and "Show Tray Panel" too.
 *
 * Next to the icon, macOS shows the configured title (today's calls, cost, …); Windows and Linux have no
 * title, so the same information goes into the tooltip.
 */
export class TrayController {
  private tray: Tray | null = null;
  private lastKey: string | null = null;
  private menu: Menu | null = null;
  private baseTooltip = 'Astra';
  private title = '';
  private detail = '';

  constructor(
    private readonly assetsDir: string,
    private readonly env: TrayMenuEnv,
    /** Icon click: open / close the tray panel next to the icon (bounds are empty where unknown). */
    private readonly onActivate: (bounds: Rectangle | null) => void,
  ) {}

  ensure(): Tray {
    if (this.tray) return this.tray;
    const mac = this.env.platform === 'darwin';
    // trayTemplate.png + @2x: macOS tints "Template" images for light/dark menu bars.
    const icon = nativeImage.createFromPath(path.join(this.assetsDir, mac ? 'trayTemplate.png' : 'tray.png'));
    if (mac) icon.setTemplateImage(true);
    this.tray = new Tray(icon);
    this.tray.setToolTip('Astra');
    // macOS: no setContextMenu (it would turn every click into the menu); the menu pops on right click.
    // Windows: setContextMenu covers right click; primary click opens the panel. Linux: fires only where
    // the desktop supports activation; harmless elsewhere.
    this.tray.on('click', (_event, bounds) => this.onActivate(bounds ?? null));
    if (mac) this.tray.on('right-click', () => this.popUpMenu());
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
    this.baseTooltip = trayTooltip(state, this.env.locale);
    this.applyTooltip();
    this.menu = Menu.buildFromTemplate(buildTrayMenuTemplate(state, cb, this.env));
    if (this.env.platform !== 'darwin') tray.setContextMenu(this.menu);
  }

  /** Shows the native menu at the icon (macOS right click, the panel's "more" button). */
  popUpMenu(): void {
    if (this.tray && this.menu) this.tray.popUpContextMenu(this.menu);
  }

  /**
   * Text next to the icon (macOS) and the extra tooltip line (all platforms), computed by the tray panel
   * from the user's "menu bar text" choice. Empty strings clear them.
   */
  setTitle(title: string, detail: string): void {
    if (title === this.title && detail === this.detail) return;
    this.title = title;
    this.detail = detail;
    const tray = this.ensure();
    if (this.env.platform === 'darwin') tray.setTitle(title, { fontType: 'monospacedDigit' });
    this.applyTooltip();
  }

  getBounds(): Rectangle | null {
    return this.tray?.getBounds() ?? null;
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
    this.menu = null;
  }

  private applyTooltip(): void {
    this.tray?.setToolTip(this.detail ? `${this.baseTooltip} · ${this.detail}` : this.baseTooltip);
  }
}
