import { getBridge, type ContextMenuItem } from './bridge';

/**
 * Host integration that differs between shells (plan §9.1): the desktop opens folders in the
 * file manager; the browser can only copy the path.
 */
export const systemActions = {
  canOpenPaths: Boolean(getBridge()),

  /** Opens a folder/file (desktop). Returns false when unavailable. */
  async openPath(path: string): Promise<boolean> {
    const bridge = getBridge();
    if (!bridge) return false;
    return bridge.openPath(path);
  },

  async revealLogs(): Promise<boolean> {
    return (await getBridge()?.revealLogs()) ?? false;
  },

  async copy(text: string): Promise<boolean> {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      return false;
    }
  },

  /** Native context menu on desktop; null on web (callers fall back to an in-app menu). */
  async contextMenu(items: ContextMenuItem[]): Promise<string | null | undefined> {
    const bridge = getBridge();
    if (!bridge) return undefined;
    return bridge.showContextMenu(items);
  },

  async startService(): Promise<{ ok: boolean; error?: string | null } | null> {
    const bridge = getBridge();
    if (!bridge) return null;
    return bridge.startService();
  },
};

/** Parent directory of a config path ("~/.codex/config.toml" → "~/.codex"). */
export function dirnameOf(p: string): string {
  const i = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'));
  return i > 0 ? p.slice(0, i) : p;
}
