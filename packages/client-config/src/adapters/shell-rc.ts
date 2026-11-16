import type { ClientEnvironment } from '../env.js';
import type { ClientConfigStateEntry, IClientConfigStateStore } from '../contracts.js';

/** The shell profile and quoting shared by the clients routed through a shell function (Dim, Antigravity CLI). */
export const ShellRc = {
  /** The rc file: `~/.zshrc` (macOS default shell), or `~/.bashrc` when only that one exists. */
  file(env: ClientEnvironment): string {
    const zshrc = env.combine('.zshrc');
    const bashrc = env.combine('.bashrc');
    return !env.fileExists(zshrc) && env.fileExists(bashrc) ? bashrc : zshrc;
  },

  /** POSIX single-quoting: wraps the value in single quotes and escapes embedded quotes. */
  quote(value: string): string {
    return "'" + value.split("'").join("'\\''") + "'";
  },
};

/** The recorded keys of a client ordered by file, then key (ordinal), as the C# store lists them. */
export function listState(store: IClientConfigStateStore, kind: string): ClientConfigStateEntry[] {
  const cmp = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
  return store.list(kind).sort((a, b) => cmp(a.filePath, b.filePath) || cmp(a.keyPath, b.keyPath));
}
