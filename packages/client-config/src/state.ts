import fs from 'node:fs';
import path from 'node:path';
import type { ClientConfigStateEntry, IClientConfigStateStore } from './contracts.js';

const keyOf = (clientKind: string, filePath: string, keyPath: string): string =>
  JSON.stringify([clientKind, filePath, keyPath]);

/** Keeps the state in memory (tests). */
export class InMemoryStateStore implements IClientConfigStateStore {
  protected readonly entries = new Map<string, ClientConfigStateEntry>();

  /** Ordered by file then key (ordinal), like the C# stores: plans built from the list depend on it. */
  list(clientKind: string): ClientConfigStateEntry[] {
    const cmp = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
    return [...this.entries.values()]
      .filter((e) => e.clientKind === clientKind)
      .sort((a, b) => cmp(a.filePath, b.filePath) || cmp(a.keyPath, b.keyPath))
      .map((e) => ({ ...e }));
  }

  get(clientKind: string, filePath: string, keyPath: string): ClientConfigStateEntry | null {
    const e = this.entries.get(keyOf(clientKind, filePath, keyPath));
    return e ? { ...e } : null;
  }

  upsert(entry: ClientConfigStateEntry): void {
    this.entries.set(keyOf(entry.clientKind, entry.filePath, entry.keyPath), { ...entry });
    this.changed();
  }

  delete(clientKind: string, filePath: string, keyPath: string): void {
    if (this.entries.delete(keyOf(clientKind, filePath, keyPath))) this.changed();
  }

  deleteAll(clientKind: string): void {
    for (const [k, e] of this.entries) if (e.clientKind === clientKind) this.entries.delete(k);
    this.changed();
  }

  protected changed(): void {}
}

interface StateFile {
  version: 1;
  entries: ClientConfigStateEntry[];
}

/**
 * The state store of astra-connect: one JSON file (`~/.astra/connect/state.json`), the counterpart of the
 * `client_config_state` table. Written atomically after every change.
 */
export class JsonFileStateStore extends InMemoryStateStore {
  constructor(private readonly file: string) {
    super();
    this.load();
  }

  private load(): void {
    let text: string;
    try {
      text = fs.readFileSync(this.file, 'utf8');
    } catch (e) {
      if ((e as NodeJS.ErrnoException).code === 'ENOENT') return;
      throw e;
    }
    const data = JSON.parse(text) as Partial<StateFile>;
    for (const entry of data.entries ?? [])
      this.entries.set(keyOf(entry.clientKind, entry.filePath, entry.keyPath), entry);
  }

  protected override changed(): void {
    const data: StateFile = { version: 1, entries: [...this.entries.values()] };
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    const tmp = `${this.file}.tmp`;
    fs.writeFileSync(tmp, JSON.stringify(data, null, 2) + '\n', { mode: 0o600 });
    fs.renameSync(tmp, this.file);
  }
}
