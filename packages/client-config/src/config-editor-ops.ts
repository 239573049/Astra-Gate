import type { ConfigChange } from './contracts.js';
import { decodeToSource } from './config-value-codec.js';
import { DotEnvEditor } from './editing/dotenv-editor.js';
import { EditorException } from './editing/errors.js';
import { JsoncEditor } from './editing/jsonc-editor.js';
import { ShellBlockEditor } from './editing/shell-block-editor.js';
import { TomlEditor } from './editing/toml-editor.js';
import { YamlEditor } from './editing/yaml-editor.js';
import type { ConfigFileFormat } from './types.js';

/**
 * Uniform editor interface over the formats, shared by the applier (execution) and the adapters (plan simulation
 * and inspection). Port of Astra.Clients ConfigEditorOps.
 */
export interface ConfigEditorOps {
  readonly text: string;
  /** Current value source text (null = absent; "true"/null for tables — TOML tables, JSON containers owned whole). */
  read(change: ConfigChange): string | null;
  apply(change: ConfigChange, warnings: string[]): void;
  /** Restores a raw source text (byte-exact where the format allows). */
  restoreSource(change: ConfigChange, source: string): void;
  remove(change: ConfigChange): void;
}

export function createOps(format: ConfigFileFormat, text: string): ConfigEditorOps {
  switch (format) {
    case 'toml':
      return new TomlOps(new TomlEditor(text));
    case 'json':
      return new JsonOps(new JsoncEditor(text));
    case 'env':
      return new EnvOps(new DotEnvEditor(text));
    case 'yaml':
      return new YamlOps(new YamlEditor(text));
    case 'shellblock':
      return new ShellBlockOps(new ShellBlockEditor(text));
  }
}

class ShellBlockOps implements ConfigEditorOps {
  constructor(private editor: ShellBlockEditor) {}
  get text(): string {
    return this.editor.text;
  }
  // The key path is the block id; the value is the block body (raw text, like .env values).
  read(change: ConfigChange): string | null {
    return this.editor.get(change.keyPath);
  }
  apply(change: ConfigChange): void {
    if (change.after === null) return this.remove(change);
    this.editor = this.editor.set(change.keyPath, decodeToSource('shellblock', change.after)!);
  }
  restoreSource(change: ConfigChange, source: string): void {
    this.editor = this.editor.set(change.keyPath, source);
  }
  remove(change: ConfigChange): void {
    this.editor = this.editor.remove(change.keyPath);
  }
}

class TomlOps implements ConfigEditorOps {
  constructor(private editor: TomlEditor) {}
  get text(): string {
    return this.editor.text;
  }
  read(change: ConfigChange): string | null {
    return change.kind === 'table'
      ? this.editor.tableExists(change.keyPath)
        ? 'true'
        : null
      : this.editor.getValue(change.keyPath);
  }
  apply(change: ConfigChange): void {
    if (change.after === null) return this.remove(change);
    if (change.kind === 'table') {
      // The "after" of a table change is a JSON object mapping item keys to raw TOML value texts.
      const items = JSON.parse(change.after) as Record<string, unknown>;
      this.editor = this.editor.insertTable(
        change.keyPath,
        Object.entries(items).map(([key, value]): [string, string] => {
          if (typeof value !== 'string')
            throw new EditorException(
              `Table change for '${change.keyPath}' has a non-string item value for '${key}'.`,
            );
          return [key, value];
        }),
      );
    } else {
      this.editor = this.editor.set(change.keyPath, decodeToSource('toml', change.after)!);
    }
  }
  restoreSource(change: ConfigChange, source: string): void {
    this.editor = this.editor.set(change.keyPath, source);
  }
  remove(change: ConfigChange): void {
    this.editor =
      change.kind === 'table'
        ? this.editor.removeTable(change.keyPath)
        : this.editor.remove(change.keyPath);
  }
}

class JsonOps implements ConfigEditorOps {
  constructor(private editor: JsoncEditor) {}
  get text(): string {
    return this.editor.text;
  }
  // A "table" in JSON is a container Astra owns as a whole (e.g. an array element it created): like a TOML
  // table it reads as "true"/null, so keys the client adds inside it later never count as drift.
  read(change: ConfigChange): string | null {
    return change.kind === 'table'
      ? this.editor.has(change.keyPath)
        ? 'true'
        : null
      : this.editor.getJsonText(change.keyPath);
  }
  apply(change: ConfigChange): void {
    if (change.after === null) return this.remove(change);
    this.editor = this.editor.setRaw(change.keyPath, change.after);
  }
  restoreSource(change: ConfigChange, source: string): void {
    this.editor = this.editor.setRaw(change.keyPath, source);
  }
  remove(change: ConfigChange): void {
    this.editor = this.editor.remove(change.keyPath);
  }
}

class EnvOps implements ConfigEditorOps {
  constructor(private editor: DotEnvEditor) {}
  get text(): string {
    return this.editor.text;
  }
  read(change: ConfigChange): string | null {
    return this.editor.getRaw(change.keyPath);
  }
  apply(change: ConfigChange): void {
    if (change.after === null) return this.remove(change);
    this.editor = this.editor.setRaw(change.keyPath, decodeToSource('env', change.after)!);
  }
  restoreSource(change: ConfigChange, source: string): void {
    this.editor = this.editor.setRaw(change.keyPath, source);
  }
  remove(change: ConfigChange): void {
    this.editor = this.editor.remove(change.keyPath);
  }
}

class YamlOps implements ConfigEditorOps {
  constructor(private editor: YamlEditor) {}
  get text(): string {
    return this.editor.text;
  }
  // A "table" in YAML is a whole row of a sequence document (a selector path) that Astra owns: "true"/null, like JSON.
  read(change: ConfigChange): string | null {
    return change.kind === 'table'
      ? this.editor.has(change.keyPath)
        ? 'true'
        : null
      : this.editor.getJsonText(change.keyPath);
  }
  apply(change: ConfigChange): void {
    if (change.after === null) return this.remove(change);
    this.editor = this.editor.setRaw(change.keyPath, change.after);
  }
  // YAML values are restored from their JSON data model; the applier then puts the original bytes back once the
  // document is semantically what it was before.
  restoreSource(change: ConfigChange, source: string): void {
    this.editor = this.editor.setRaw(change.keyPath, source);
  }
  remove(change: ConfigChange): void {
    this.editor = this.editor.remove(change.keyPath);
  }
}
