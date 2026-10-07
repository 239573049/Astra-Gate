import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { applyServerUpdate, UpdateError, type ApplyServerUpdateOptions, type ServerControl } from '../src/apply.js';
import { readInstallInfo, writeInstallInfo } from '../src/install-info.js';
import { readUpdateState, updatePaths } from '../src/index.js';
import type { UpdateManifest } from '../src/manifest.js';

const FAKE_BINARY = '#!/bin/sh\necho astra-server\n';
const REAL_SHA = createHash('sha256').update(FAKE_BINARY).digest('hex');

class FakeControl implements ServerControl {
  running = false;
  stops = 0;
  starts = 0;
  versionToReport: string | null = '0.2.0';

  async isRunning(): Promise<boolean> {
    return this.running;
  }
  async stop(): Promise<void> {
    this.stops++;
    this.running = false;
  }
  async start(): Promise<void> {
    this.starts++;
    this.running = true;
  }
  async probeVersion(): Promise<string | null> {
    return this.versionToReport;
  }
}

/** Writes a fake server package into the staging prefix with controllable bytes. */
function fakeInstaller(binaryContent: () => string) {
  return async (prefix: string, spec: string): Promise<void> => {
    const at = spec.lastIndexOf('@');
    const pkg = spec.slice(0, at);
    const bin = path.join(prefix, 'node_modules', ...pkg.split('/'), 'bin', 'astra-server');
    fs.mkdirSync(path.dirname(bin), { recursive: true });
    fs.writeFileSync(bin, binaryContent());
  };
}

interface Harness {
  home: string;
  control: FakeControl;
  oldBinary: string;
  manifest: UpdateManifest;
  opts: ApplyServerUpdateOptions;
  installer: (prefix: string, spec: string) => Promise<void>;
}

function harness(tmp: string, sub = 'main'): Harness {
  const home = path.join(tmp, sub, 'astra');
  fs.mkdirSync(home, { recursive: true });
  const oldBinary = path.join(home, 'node-modules-bin', 'astra-server');
  fs.mkdirSync(path.dirname(oldBinary), { recursive: true });
  fs.writeFileSync(oldBinary, FAKE_BINARY);
  writeInstallInfo(home, { serverPath: oldBinary, serverVersion: '0.1.0' });

  const control = new FakeControl();
  control.running = true;
  const manifest: UpdateManifest = { version: '0.2.0', apiVersion: '1.0', notes: 'hello' };
  const installer = fakeInstaller(() => FAKE_BINARY);
  const opts: ApplyServerUpdateOptions = {
    home,
    manifest,
    platformKey: 'darwin-arm64',
    serverPackage: '@aidotnet/server-darwin-arm64',
    platform: 'darwin',
    currentServerPath: oldBinary,
    currentVersion: '0.1.0',
    expectedApiMajor: 1,
    control,
    installIntoPrefix: installer,
    log: () => {},
  };
  return { home, control, oldBinary, manifest, opts, installer };
}

describe('applyServerUpdate', () => {
  let tmp: string;
  beforeEach(() => {
    tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-apply-'));
  });
  afterEach(() => {
    fs.rmSync(tmp, { recursive: true, force: true });
  });

  it('end-to-end success: stages, swaps to a managed path, restarts and lands idle', async () => {
    const h = harness(tmp);
    h.manifest.platforms = { 'darwin-arm64': { server: h.opts.serverPackage, serverSha256: REAL_SHA } };

    const result = await applyServerUpdate(h.opts);

    expect(result.to).toBe('0.2.0');
    const paths = updatePaths(h.home);
    const managed = path.join(paths.serverDir, 'astra-server-0.2.0');
    expect(fs.existsSync(managed)).toBe(true);
    expect(fs.statSync(managed).mode & 0o755).toBe(0o755);
    const install = readInstallInfo(h.home);
    expect(install?.serverPath).toBe(managed);
    expect(install?.serverVersion).toBe('0.2.0');
    expect(h.control.stops).toBe(1);
    expect(h.control.starts).toBe(1);
    expect(readUpdateState(paths.stateFile)).toMatchObject({ phase: 'idle', failedCount: 0 });
    // Staging is removed after a successful swap (freed immediately, wiped on the next run anyway).
    expect(fs.existsSync(paths.stagingDir)).toBe(false);
  });

  it('keeps a rolling backup of the previous binary', async () => {
    const h = harness(tmp);
    await applyServerUpdate(h.opts);
    const backupDir = path.join(updatePaths(h.home).backupsDir, 'server');
    const backups = fs.readdirSync(backupDir);
    expect(backups).toHaveLength(1);
    expect(backups[0]).toContain('0.1.0');
  });

  it('fails before touching anything when the manifest is not newer or api-incompatible', async () => {
    const h = harness(tmp);
    h.manifest.version = '0.1.0';
    await expect(applyServerUpdate(h.opts)).rejects.toBeInstanceOf(UpdateError);
    expect(h.control.starts).toBe(0);
    expect(readInstallInfo(h.home)?.serverPath).toBe(h.oldBinary);

    const h2 = harness(tmp);
    h2.manifest.version = '9.0.0';
    h2.manifest.apiVersion = '2.0';
    await expect(applyServerUpdate(h2.opts)).rejects.toThrow(/API major/);
    expect(readInstallInfo(h2.home)?.serverPath).toBe(h2.oldBinary);
  });

  it('falls back to the serverPackage when the manifest has no platform entry', async () => {
    const h = harness(tmp);
    const result = await applyServerUpdate(h.opts);
    expect(result.to).toBe('0.2.0');
    expect(readInstallInfo(h.home)?.serverVersion).toBe('0.2.0');
  });

  it('marks failed without rollback when the download or checksum fails', async () => {
    const h = harness(tmp);
    h.opts.installIntoPrefix = async () => {
      throw new Error('registry unreachable');
    };
    await expect(applyServerUpdate(h.opts)).rejects.toThrow('registry unreachable');
    const state = readUpdateState(updatePaths(h.home).stateFile);
    expect(state).toMatchObject({ phase: 'failed', failedCount: 1 });
    expect(readInstallInfo(h.home)?.serverPath).toBe(h.oldBinary);

    // Checksum mismatch follows the same path (fresh home so counters stay independent).
    const h2 = harness(tmp, 'checksum');
    h2.manifest.platforms = { 'darwin-arm64': { serverSha256: 'deadbeef' } };
    await expect(applyServerUpdate(h2.opts)).rejects.toThrow(/Checksum mismatch/);
    expect(readInstallInfo(h2.home)?.serverPath).toBe(h2.oldBinary);
    expect(readUpdateState(updatePaths(h2.home).stateFile)).toMatchObject({ phase: 'failed', failedCount: 1 });
  });

  it('rolls back to the previous binary when the restarted server reports the wrong version', async () => {
    const h = harness(tmp);
    h.control.versionToReport = '0.1.0'; // server somehow still reports the old build

    await expect(applyServerUpdate(h.opts)).rejects.toThrow(/reports 0.1.0/);

    expect(readInstallInfo(h.home)?.serverPath).toBe(h.oldBinary);
    const state = readUpdateState(updatePaths(h.home).stateFile);
    expect(state).toMatchObject({ phase: 'failed', failedCount: 1 });
    // stop (pre-restart) + start (initial) + stop + start (rollback)
    expect(h.control.stops).toBe(2);
    expect(h.control.starts).toBe(2);
  });

  it('locks out after two consecutive failures until forced', async () => {
    const h = harness(tmp);
    h.opts.installIntoPrefix = async () => {
      throw new Error('boom');
    };
    await expect(applyServerUpdate(h.opts)).rejects.toThrow('boom');
    await expect(applyServerUpdate(h.opts)).rejects.toThrow('boom');
    expect(readUpdateState(updatePaths(h.home).stateFile)?.failedCount).toBe(2);

    await expect(applyServerUpdate(h.opts)).rejects.toThrow(/2 update attempts failed/);
    // force clears the lock; with a working installer it succeeds and resets the counter.
    h.opts.force = true;
    h.opts.installIntoPrefix = fakeInstaller(() => FAKE_BINARY);
    await expect(applyServerUpdate(h.opts)).resolves.toMatchObject({ to: '0.2.0' });
    expect(readUpdateState(updatePaths(h.home).stateFile)).toMatchObject({ phase: 'idle', failedCount: 0 });
  });

  it('does not stop the server when it was not running', async () => {
    const h = harness(tmp);
    h.control.running = false;
    await applyServerUpdate(h.opts);
    expect(h.control.stops).toBe(0);
    expect(h.control.starts).toBe(1);
  });
});
