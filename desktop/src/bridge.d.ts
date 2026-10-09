import type { ContextMenuItem, MenuCommand, ThemeSource } from './shared/chrome';
import type { TrayPrefs } from './shared/prefs';
import type { TrayPanelCommand, TrayPanelState } from './shared/trayPanel';

export {};

declare global {
  interface Window {
    /** Injected by the desktop preload; absent in a normal browser (shell = web). */
    __ASTRA__?: {
      apiBase: string;
      shell: 'desktop';
      desktop: true;
      platform: NodeJS.Platform;
      apiVersionMismatch: boolean;
      version: string;
      accentColor: string | null;
      getAccentColor(): Promise<string | null>;
      onAccentColorChanged(cb: (color: string | null) => void): () => void;
      setThemeSource(source: ThemeSource): Promise<boolean>;
      openPath(p: string): Promise<boolean>;
      revealLogs(): Promise<boolean>;
      startService(): Promise<{ ok: boolean; error: string | null }>;
      updates: {
        check(): Promise<UpdateSummary>;
        apply(): Promise<UiApplyOutcome>;
      };
      showContextMenu(items: ContextMenuItem[]): Promise<string | null>;
      onMenuCommand(cb: (command: MenuCommand) => void): () => void;
      tray: {
        getPrefs(): Promise<TrayPrefs>;
        setPrefs(prefs: TrayPrefs): Promise<TrayPrefs>;
        onPrefsChanged(cb: (prefs: TrayPrefs) => void): () => void;
        getState(): Promise<TrayPanelState>;
        onStateChanged(cb: (state: TrayPanelState) => void): () => void;
        command(command: TrayPanelCommand): Promise<void>;
        setTitle(title: string, detail: string): void;
        resize(height: number): void;
        onVisibilityChanged(cb: (visible: boolean) => void): () => void;
      };
    };
  }
}
