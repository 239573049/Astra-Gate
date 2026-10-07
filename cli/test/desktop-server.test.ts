import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  ensureDesktopServer,
  hasUsableServer,
  isEphemeralPackagePath,
} from '../src/lib/desktop-server';
import { readInstallJson, writeInstallJson } from '../src/lib/install-json';

describe('isEphemeralPackagePath', () => {
  it('flags npx and pnpm dlx caches', () => {
    expect(isEphemeralPackagePath('/Users/a/.npm/_npx/1a2b/node_modules/x/bin/astra-server')).toBe(true);
    expect(isEphemeralPackagePath('C:\\Users\\a\\AppData\\Local\\npm-cache\\_npx\\1a2b\\x.exe')).toBe(true);
    expect(isEphemeralPackagePath('/Users/a/Library/Caches/pnpm/dlx/abc/node_modules/x')).toBe(true);
  });

  it('accepts global installs and the managed dir', () => {
    expect(isEphemeralPackagePath('/usr/local/lib/node_modules/@aidotnet/server-darwin-arm64/bin/astra-server')).toBe(false);
    expect(isEphemeralPackagePath('/Users/a/.astra/server/astra-server-0.1.0')).toBe(false);
  });
});

describe('hasUsableServer', () => {
  const exists = (p: string): boolean => p !== '/gone';

  it('keeps an existing durable server at least as new as the CLI', () => {
    expect(hasUsableServer({ serverPath: '/g/astra-server', serverVersion: '0.2.0' }, '0.2.0', exists)).toBe(true);
    expect(hasUsableServer({ serverPath: '/g/astra-server', serverVersion: '0.3.0' }, '0.2.0', exists)).toBe(true);
    expect(hasUsableServer({ serverPath: '/repo/astra-server' }, '0.2.0', exists)).toBe(true);
  });

  it('rejects missing, ephemeral or older servers', () => {
    expect(hasUsableServer(null, '0.2.0', exists)).toBe(false);
    expect(hasUsableServer({ serverPath: '/gone' }, '0.2.0', exists)).toBe(false);
    expect(hasUsableServer({ serverPath: '/a/_npx/h/astra-server' }, '0.2.0', exists)).toBe(false);
    expect(hasUsableServer({ serverPath: '/g/astra-server', serverVersion: '0.1.0' }, '0.2.0', exists)).toBe(false);
  });
});

describe('ensureDesktopServer', () => {
  let root: string;
  let home: string;

  beforeEach(() => {
    root = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-desktop-server-'));
    home = path.join(root, 'home');
    fs.mkdirSync(home);
  });

  afterEach(() => {
    fs.rmSync(root, { recursive: true, force: true });
  });

  function fakePackage(dir: string): string {
    const bin = path.join(root, dir, 'bin', 'astra-server');
    fs.mkdirSync(path.join(path.dirname(bin), 'wwwroot'), { recursive: true });
    fs.writeFileSync(bin, 'server');
    fs.writeFileSync(path.join(path.dirname(bin), 'wwwroot', 'index.html'), '<html>');
    return bin;
  }

  it('copies a server from an npx cache into the managed dir with wwwroot', () => {
    const bin = fakePackage('_npx/abc/node_modules/@aidotnet/server-darwin-arm64');
    const r = ensureDesktopServer({ home, version: '0.2.0', platform: 'darwin', findPackaged: () => ({ path: bin }) });

    const managed = path.join(home, 'server', 'astra-server-0.2.0');
    expect(r).toEqual({ action: 'copied', serverPath: managed });
    expect(fs.readFileSync(managed, 'utf8')).toBe('server');
    expect(fs.existsSync(path.join(home, 'server', 'wwwroot', 'index.html'))).toBe(true);
    expect(readInstallJson(home)).toMatchObject({ serverPath: managed, serverVersion: '0.2.0' });
  });

  it('records a global package path without copying', () => {
    const bin = fakePackage('global/node_modules/@aidotnet/server-darwin-arm64');
    const r = ensureDesktopServer({ home, version: '0.2.0', platform: 'darwin', findPackaged: () => ({ path: bin }) });

    expect(r).toEqual({ action: 'recorded', serverPath: bin });
    expect(fs.existsSync(path.join(home, 'server'))).toBe(false);
    expect(readInstallJson(home)?.serverPath).toBe(bin);
  });

  it('keeps a usable recorded server and preserves desktop info', () => {
    const existing = fakePackage('managed');
    writeInstallJson(home, { serverPath: existing, serverVersion: '0.2.0', desktopPath: '/apps/Astra.app' });
    const r = ensureDesktopServer({
      home,
      version: '0.2.0',
      platform: 'darwin',
      findPackaged: () => {
        throw new Error('must not look up the package');
      },
    });

    expect(r).toEqual({ action: 'kept', serverPath: existing });
    expect(readInstallJson(home)?.desktopPath).toBe('/apps/Astra.app');
  });

  it('reports a missing package without touching install.json', () => {
    const r = ensureDesktopServer({
      home,
      version: '0.2.0',
      platform: 'darwin',
      findPackaged: () => ({ error: '@aidotnet/server-darwin-arm64 is not installed.' }),
    });

    expect(r).toEqual({ action: 'missing', reason: '@aidotnet/server-darwin-arm64 is not installed.' });
    expect(readInstallJson(home)).toBeNull();
  });
});
