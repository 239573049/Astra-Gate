import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { findDesktopEntry, installDesktopClient } from '../src/desktop-install.js';

describe('installDesktopClient', () => {
  let tmp: string;
  let fakeNpm: string;

  beforeEach(() => {
    tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-desktop-install-'));
    // Stands in for `npm install --prefix <prefix> <pkg>@<ver>`: lays out the
    // desktop package with a macOS app bundle.
    fakeNpm = path.join(tmp, 'fake-npm.cjs');
    fs.writeFileSync(
      fakeNpm,
      `const fs = require('fs'), path = require('path');
const a = process.argv.slice(2);
const prefix = a[a.indexOf('--prefix') + 1];
const spec = a[a.length - 1];
const pkg = spec.slice(0, spec.lastIndexOf('@'));
const dir = path.join(prefix, 'node_modules', ...pkg.split('/'));
fs.mkdirSync(path.join(dir, 'app', 'Astra.app', 'Contents'), { recursive: true });
fs.writeFileSync(path.join(dir, 'package.json'), JSON.stringify({ version: spec.slice(spec.lastIndexOf('@') + 1) }));
`,
    );
  });

  afterEach(() => {
    fs.rmSync(tmp, { recursive: true, force: true });
  });

  it('puts the launcher in the user home, not the Astra data dir', async () => {
    const astraHome = path.join(tmp, 'user', '.astra');
    const userHome = path.join(tmp, 'user');
    const r = await installDesktopClient({
      home: astraHome,
      userHome,
      version: '0.2.2',
      desktopPackage: '@aidotnet/desktop-darwin-arm64',
      platform: 'darwin',
      npm: { npm: { command: process.execPath, args: [fakeNpm] } },
    });

    expect(r.launcher).toBe(path.join(userHome, 'Applications', 'Astra.app'));
    expect(fs.lstatSync(r.launcher).isSymbolicLink()).toBe(true);
    expect(fs.realpathSync(r.launcher)).toBe(fs.realpathSync(r.path));
    expect(r.path.startsWith(path.join(astraHome, 'desktop'))).toBe(true);
    expect(fs.existsSync(path.join(astraHome, 'Applications'))).toBe(false);
    expect(r.version).toBe('0.2.2');
  });
});

describe('findDesktopEntry', () => {
  let tmp: string;
  beforeEach(() => {
    tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-find-entry-'));
  });
  afterEach(() => {
    fs.rmSync(tmp, { recursive: true, force: true });
  });
  const put = (rel: string, mode = 0o644, content = 'x'): string => {
    const p = path.join(tmp, rel);
    fs.mkdirSync(path.dirname(p), { recursive: true });
    fs.writeFileSync(p, content);
    fs.chmodSync(p, mode);
    return p;
  };
  const marker = (m: unknown): void => {
    put('astra-desktop.json', 0o644, JSON.stringify(m));
  };

  // The real compatibility guard: the heuristic (no marker) on Avalonia layouts.
  it('heuristic picks Astra.app on a darwin Avalonia layout', () => {
    put('Astra.app/Contents/MacOS/astra-desktop', 0o755);
    put('Astra.app/Contents/MacOS/libSkiaSharp.dylib');
    put('Astra.app/Contents/Resources/server/astra-server', 0o755);
    expect(findDesktopEntry(tmp, 'darwin')).toEqual({ path: path.join(tmp, 'Astra.app'), isBundle: true });
  });

  it('heuristic picks astra-desktop.exe on a win32 Avalonia layout', () => {
    put('astra-desktop.exe', 0o644);
    put('libSkiaSharp.dll');
    put('libHarfBuzzSharp.dll');
    put('av_libglesv2.dll');
    expect(findDesktopEntry(tmp, 'win32')).toEqual({ path: path.join(tmp, 'astra-desktop.exe'), isBundle: false });
  });

  it('heuristic picks the only executable on a linux Avalonia layout', () => {
    put('astra-desktop', 0o755);
    put('libSkiaSharp.so');
    put('libHarfBuzzSharp.so');
    put('libonigwrap.so');
    put('astra-desktop.dbg');
    expect(findDesktopEntry(tmp, 'linux')).toEqual({ path: path.join(tmp, 'astra-desktop'), isBundle: false });
  });

  it('uses a valid marker before the heuristic', () => {
    put('astra-desktop', 0o755);
    put('other/main', 0o755);
    marker({ entry: 'other/main', kind: 'exe' });
    expect(findDesktopEntry(tmp, 'linux').path).toBe(path.join(tmp, 'other', 'main'));
  });

  it('accepts an app-bundle marker on darwin', () => {
    put('Astra.app/Contents/MacOS/x', 0o755);
    put('Zzz.app/Contents/MacOS/x', 0o755);
    marker({ entry: 'Zzz.app', kind: 'app-bundle' });
    expect(findDesktopEntry(tmp, 'darwin')).toEqual({ path: path.join(tmp, 'Zzz.app'), isBundle: true });
  });

  it('ignores a marker whose entry escapes the app dir', () => {
    const app = path.join(tmp, 'app');
    fs.mkdirSync(app);
    fs.writeFileSync(path.join(tmp, 'outside'), 'x', { mode: 0o755 });
    fs.writeFileSync(path.join(app, 'real'), 'x', { mode: 0o755 });
    fs.writeFileSync(path.join(app, 'astra-desktop.json'), JSON.stringify({ entry: '../outside', kind: 'exe' }));
    expect(findDesktopEntry(app, 'linux').path).toBe(path.join(app, 'real'));
  });

  it('ignores a marker whose file is missing, or whose kind mismatches the platform', () => {
    put('real', 0o755);
    marker({ entry: 'gone', kind: 'exe' });
    expect(findDesktopEntry(tmp, 'linux').path).toBe(path.join(tmp, 'real'));
    marker({ entry: 'real', kind: 'app-bundle' });
    expect(findDesktopEntry(tmp, 'linux').path).toBe(path.join(tmp, 'real'));
  });

  it('falls back when the marker file is absent or malformed', () => {
    put('real', 0o755);
    expect(findDesktopEntry(tmp, 'linux').path).toBe(path.join(tmp, 'real'));
    put('astra-desktop.json', 0o644, '{not json');
    expect(findDesktopEntry(tmp, 'linux').path).toBe(path.join(tmp, 'real'));
  });
});
