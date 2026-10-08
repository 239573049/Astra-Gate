import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { stageServerBinary, type StageServerBinaryOptions } from '../src/staging.js';

const FAKE_BINARY = '#!/bin/sh\necho astra-server\n';

/** Writes a fake server package into the staging prefix: bin/<name> -> content. */
function fakeInstaller(files: Record<string, string>) {
  return async (prefix: string, spec: string): Promise<void> => {
    const at = spec.lastIndexOf('@');
    const pkg = spec.slice(0, at);
    const bin = path.join(prefix, 'node_modules', ...pkg.split('/'), 'bin');
    fs.mkdirSync(bin, { recursive: true });
    for (const [name, content] of Object.entries(files)) {
      fs.writeFileSync(path.join(bin, name), content);
    }
  };
}

describe('stageServerBinary', () => {
  let tmp: string;
  beforeEach(() => {
    tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-stage-'));
  });
  afterEach(() => {
    fs.rmSync(tmp, { recursive: true, force: true });
  });

  function baseOpts(over: Partial<StageServerBinaryOptions> = {}): StageServerBinaryOptions {
    return {
      home: tmp,
      stagingDir: path.join(tmp, 'staging'),
      serverPackage: '@aidotnet/server-darwin-arm64',
      version: '0.2.0',
      platform: 'darwin',
      installIntoPrefix: fakeInstaller({ 'astra-server': FAKE_BINARY }),
      ...over,
    };
  }

  it('picks up the native companion libraries staged beside the binary', async () => {
    const staged = await stageServerBinary(
      baseOpts({
        installIntoPrefix: fakeInstaller({
          'astra-server': FAKE_BINARY,
          'libe_sqlite3.dylib': 'dylib-bytes',
          'libsqlite3.so.0': 'versioned-so',
          'libe_sqlite3.dylib.tmp': 'half-written',
        }),
      }),
    );

    const bin = path.dirname(staged.path);
    expect(staged.version).toBe('0.2.0');
    expect(staged.nativeLibraries.sort()).toEqual([
      path.join(bin, 'libe_sqlite3.dylib'),
      path.join(bin, 'libsqlite3.so.0'),
    ]);
  });

  it('stages a package without companions (non-AOT build) with an empty list', async () => {
    const logs: string[] = [];
    const staged = await stageServerBinary(baseOpts({ log: (l) => logs.push(l) }));

    expect(staged.nativeLibraries).toEqual([]);
    expect(fs.readFileSync(staged.path, 'utf8')).toBe(FAKE_BINARY);
    // Absence is reported, never fatal — pre-AOT packages legitimately have none.
    expect(logs.some((l) => l.includes('no native companion libraries'))).toBe(true);
  });
});
