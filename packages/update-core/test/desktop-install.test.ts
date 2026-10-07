import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { installDesktopClient } from '../src/desktop-install.js';

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
