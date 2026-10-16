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
    // navigator.clipboard only exists in secure contexts (https / localhost); a plain-http
    // deployment reached by LAN address falls back to a hidden textarea + execCommand.
    if (window.isSecureContext && navigator.clipboard) {
      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch {
        /* permission denied or unfocused document: try the legacy path */
      }
    }
    return legacyCopy(text);
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

  /** Desktop only: restarts the local server (null on web, where `astra restart` does it). */
  async restartService(): Promise<{ ok: boolean; error?: string | null } | null> {
    const bridge = getBridge();
    if (!bridge) return null;
    return bridge.restartService();
  },
};

/** Parent directory of a config path ("~/.codex/config.toml" → "~/.codex"). */
export function dirnameOf(p: string): string {
  const i = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'));
  return i > 0 ? p.slice(0, i) : p;
}

function legacyCopy(text: string): boolean {
  const area = document.createElement('textarea');
  area.value = text;
  area.setAttribute('readonly', '');
  area.style.cssText = 'position:fixed;top:0;left:0;opacity:0;pointer-events:none';
  const active = document.activeElement as HTMLElement | null;
  document.body.appendChild(area);
  try {
    area.focus();
    area.select();
    area.setSelectionRange(0, text.length);
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    document.body.removeChild(area);
    active?.focus?.();
  }
}
