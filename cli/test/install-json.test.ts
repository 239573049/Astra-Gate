import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  applyDesktopInfo,
  applyServerInfo,
  clearDesktopInfo,
  readInstallJson,
  writeInstallJson,
} from '../src/lib/install-json';

describe('install.json merges', () => {
  it('applies server info to an empty file', () => {
    const next = applyServerInfo(null, { serverPath: '/x/astra-server', serverVersion: '1.2.3' });
    expect(next.serverPath).toBe('/x/astra-server');
    expect(next.serverVersion).toBe('1.2.3');
    expect(next.updatedAt).toBeTruthy();
  });

  it('applies desktop info without losing server info', () => {
    const withServer = applyServerInfo(null, { serverPath: '/x/server', serverVersion: '1.0.0' });
    const withDesktop = applyDesktopInfo(withServer, {
      desktopPath: '/x/app/Astra.app',
      desktopVersion: '2.0.0',
    });
    expect(withDesktop.serverPath).toBe('/x/server');
    expect(withDesktop.serverVersion).toBe('1.0.0');
    expect(withDesktop.desktopPath).toBe('/x/app/Astra.app');
    expect(withDesktop.desktopVersion).toBe('2.0.0');
  });

  it('clears desktop info while keeping server info', () => {
    const both = applyDesktopInfo(applyServerInfo(null, { serverPath: '/x/server' }), {
      desktopPath: '/x/app',
    });
    const cleared = clearDesktopInfo(both);
    expect(cleared.desktopPath).toBeUndefined();
    expect(cleared.desktopVersion).toBeUndefined();
    expect(cleared.serverPath).toBe('/x/server');
  });

  it('round-trips through disk', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-install-'));
    try {
      writeInstallJson(dir, applyServerInfo(null, { serverPath: '/x/server', serverVersion: '9' }));
      expect(readInstallJson(dir)?.serverVersion).toBe('9');
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });
});
