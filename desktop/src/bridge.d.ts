import type { ContextMenuItem, MenuCommand, ThemeSource } from './shared/chrome';

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
      showContextMenu(items: ContextMenuItem[]): Promise<string | null>;
      onMenuCommand(cb: (command: MenuCommand) => void): () => void;
    };
  }
}
