import { BrowserWindow, screen, type WebContents, type WebPreferences } from 'electron';

import { TRAY_PANEL_DEFAULT_HEIGHT, TRAY_PANEL_WIDTH, trayPanelBounds, type Rect } from './shared/trayPanel';

/** A click on the tray icon right after the panel hid itself on blur is the same click: do not reopen. */
const REOPEN_GUARD_MS = 300;

export interface TrayPanelOptions {
  platform: NodeJS.Platform;
  /** Renderer URL of the panel route (app://astra/#/tray, or the Vite dev server). */
  url: string;
  webPreferences: () => WebPreferences;
}

/**
 * The tray panel: a frameless popover window that renders the web app's /tray route. It is created hidden
 * at startup (so it opens instantly and keeps the menu-bar title current), toggled from the tray icon,
 * positioned next to it, sized to its content, and hidden again when it loses focus.
 */
export class TrayPanel {
  private win: BrowserWindow | null = null;
  private anchor: Rect | null = null;
  private contentHeight = TRAY_PANEL_DEFAULT_HEIGHT;
  private hiddenAt = 0;
  /** >0 while a native menu opened from the panel is up: it takes focus, which must not close the panel. */
  private blurHolds = 0;

  constructor(private readonly opts: TrayPanelOptions) {}

  ensure(): BrowserWindow {
    if (this.win && !this.win.isDestroyed()) return this.win;
    const win = new BrowserWindow({
      width: TRAY_PANEL_WIDTH,
      height: this.contentHeight,
      show: false,
      frame: false,
      // The renderer draws the rounded card itself.
      transparent: true,
      backgroundColor: '#00000000',
      resizable: false,
      movable: false,
      minimizable: false,
      maximizable: false,
      fullscreenable: false,
      skipTaskbar: true,
      alwaysOnTop: true,
      hasShadow: true,
      title: 'Astra',
      webPreferences: this.opts.webPreferences(),
    });
    // macOS: open on the current Space, also over full-screen apps.
    if (this.opts.platform === 'darwin') {
      win.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true, skipTransformProcessType: true });
    }
    win.on('blur', () => {
      if (this.blurHolds > 0) return;
      this.hide();
    });
    win.on('closed', () => {
      if (this.win === win) this.win = null;
    });
    // The panel never opens other windows.
    win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    void win.loadURL(this.opts.url);
    this.win = win;
    return win;
  }

  isPanel(contents: WebContents): boolean {
    return this.win !== null && !this.win.isDestroyed() && this.win.webContents === contents;
  }

  get visible(): boolean {
    return this.win !== null && !this.win.isDestroyed() && this.win.isVisible();
  }

  /** Opens the panel at the icon (null / empty bounds: at the cursor), or closes it when it is open. */
  toggle(anchor: Rect | null): void {
    if (this.visible) {
      this.hide();
      return;
    }
    if (Date.now() - this.hiddenAt < REOPEN_GUARD_MS) return;
    this.show(anchor);
  }

  show(anchor: Rect | null): void {
    const win = this.ensure();
    this.anchor = anchor && anchor.width > 0 && anchor.height > 0 ? anchor : null;
    win.setBounds(this.bounds());
    win.show();
    win.focus();
    win.webContents.send('astra:tray-panel-visibility', true);
  }

  hide(): void {
    const win = this.win;
    if (!win || win.isDestroyed() || !win.isVisible()) return;
    this.hiddenAt = Date.now();
    win.hide();
    win.webContents.send('astra:tray-panel-visibility', false);
  }

  /** The renderer reports its content height; the window follows (and stays next to the icon). */
  setContentHeight(height: number): void {
    if (!Number.isFinite(height) || height <= 0) return;
    this.contentHeight = Math.ceil(Math.min(height, 2000));
    const win = this.win;
    if (!win || win.isDestroyed()) return;
    if (win.isVisible()) {
      win.setBounds(this.bounds());
      if (this.opts.platform === 'darwin') win.invalidateShadow();
    } else {
      win.setSize(TRAY_PANEL_WIDTH, this.contentHeight);
    }
  }

  /** Keeps the panel open while `pending` (a native menu popped from it) is outstanding. */
  async holdOpen<T>(pending: Promise<T>): Promise<T> {
    this.blurHolds++;
    try {
      return await pending;
    } finally {
      this.blurHolds--;
      // The menu took focus; give it back so the next outside click closes the panel again.
      if (this.visible) this.win?.focus();
    }
  }

  destroy(): void {
    if (this.win && !this.win.isDestroyed()) this.win.destroy();
    this.win = null;
  }

  private bounds(): Rect {
    const anchor = this.anchor ?? { ...screen.getCursorScreenPoint(), width: 0, height: 0 };
    const display = this.anchor ? screen.getDisplayMatching(anchor) : screen.getDisplayNearestPoint(anchor);
    return trayPanelBounds(anchor, display.workArea, { width: TRAY_PANEL_WIDTH, height: this.contentHeight });
  }
}
