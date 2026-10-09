import { describe, expect, it } from 'vitest';

import * as path from 'node:path';

import {
  bundledServerBinaryPath,
  devServerBinaryPath,
  resolveServerBinary,
  shouldReplaceRunningServer,
} from '../src/shared/resolveServerBinary';

const repoRoot = '/repo';
const devPath = path.join(repoRoot, 'src', 'Astra.Server', 'bin', 'Debug', 'net10.0', 'astra-server');
const devPathExe = `${devPath}.exe`;

describe('devServerBinaryPath', () => {
  it('appends .exe only on Windows', () => {
    expect(devServerBinaryPath(repoRoot, 'darwin')).toBe(devPath);
    expect(devServerBinaryPath(repoRoot, 'linux')).toBe(devPath);
    expect(devServerBinaryPath(repoRoot, 'win32')).toBe(devPathExe);
  });
});

describe('resolveServerBinary', () => {
  const exists = (found: string[]) => (p: string) => found.includes(p);

  it('prefers install.json over env and dev', () => {
    const r = resolveServerBinary({
      installServerPath: '/opt/astra-server',
      envServerBin: '/env/astra-server',
      repoRoot,
      exists: exists(['/opt/astra-server', '/env/astra-server', devPath]),
    });
    expect(r).toEqual({ path: '/opt/astra-server', source: 'install', version: null });
  });

  it('falls back to env, then dev', () => {
    expect(
      resolveServerBinary({
        installServerPath: '/missing',
        envServerBin: '/env/astra-server',
        repoRoot,
        exists: exists(['/env/astra-server', devPath]),
      }),
    ).toEqual({ path: '/env/astra-server', source: 'env', version: null });

    expect(
      resolveServerBinary({
        installServerPath: '/missing',
        envServerBin: '/also-missing',
        repoRoot,
        exists: exists([devPath]),
      }),
    ).toEqual({ path: devPath, source: 'dev', version: null });
  });

  it('returns null when nothing exists', () => {
    expect(
      resolveServerBinary({ installServerPath: null, envServerBin: null, repoRoot, exists: exists([]) }),
    ).toBeNull();
    expect(resolveServerBinary({ repoRoot, exists: exists([]) })).toBeNull();
  });

  it('ignores blank candidates', () => {
    expect(
      resolveServerBinary({
        installServerPath: '   ',
        envServerBin: '',
        repoRoot: null,
        exists: exists([]),
      }),
    ).toBeNull();
  });

  describe('bundled server (standalone installers)', () => {
    const bundled = { path: '/app/resources/server/astra-server', version: '0.3.0' };

    it('is used when nothing else is configured, ahead of the dev build', () => {
      expect(resolveServerBinary({ bundled, repoRoot, exists: exists([bundled.path, devPath]) })).toEqual({
        path: bundled.path,
        source: 'bundled',
        version: '0.3.0',
      });
    });

    it('loses to install.json when that server is at least as new, keeping its recorded version', () => {
      expect(
        resolveServerBinary({
          installServerPath: '/home/.astra/server/astra-server-x',
          installServerVersion: '0.4.0',
          bundled,
          exists: exists(['/home/.astra/server/astra-server-x', bundled.path]),
        }),
      ).toEqual({ path: '/home/.astra/server/astra-server-x', source: 'install', version: '0.4.0' });

      expect(
        resolveServerBinary({
          installServerPath: '/home/.astra/server/astra-server-x',
          bundled,
          exists: exists(['/home/.astra/server/astra-server-x', bundled.path]),
        }),
      ).toEqual({ path: '/home/.astra/server/astra-server-x', source: 'install', version: null });
    });

    it('beats a stale install.json server after an installer upgrade', () => {
      expect(
        resolveServerBinary({
          installServerPath: '/home/.astra/server/astra-server-0.2.0',
          installServerVersion: '0.2.0',
          bundled,
          exists: exists(['/home/.astra/server/astra-server-0.2.0', bundled.path]),
        }),
      ).toEqual({ path: bundled.path, source: 'bundled', version: '0.3.0' });
    });

    it('keeps ASTRA_SERVER_BIN ahead of the bundled server', () => {
      expect(
        resolveServerBinary({
          envServerBin: '/env/astra-server',
          bundled,
          exists: exists(['/env/astra-server', bundled.path]),
        }),
      ).toEqual({ path: '/env/astra-server', source: 'env', version: null });
    });

    it('is skipped when the app ships without one', () => {
      expect(resolveServerBinary({ bundled, repoRoot, exists: exists([devPath]) })).toEqual({
        path: devPath,
        source: 'dev',
        version: null,
      });
    });
  });
});

describe('shouldReplaceRunningServer', () => {
  it('replaces a running server on any version difference with a known target', () => {
    expect(shouldReplaceRunningServer('0.2.3', { path: '/bundled', source: 'bundled', version: '0.3.0' })).toBe(true);
    // A newer running server is replaced too: install.json/bundled decide what this app serves.
    expect(shouldReplaceRunningServer('0.4.0', { path: '/bundled', source: 'bundled', version: '0.3.0' })).toBe(true);
  });

  it('adopts a matching server', () => {
    expect(shouldReplaceRunningServer('0.3.0', { path: '/bundled', source: 'bundled', version: '0.3.0' })).toBe(false);
  });

  it('never replaces without a known target or running version', () => {
    expect(shouldReplaceRunningServer('0.2.3', { path: '/dev', source: 'dev', version: null })).toBe(false);
    expect(shouldReplaceRunningServer('0.2.3', { path: '/env', source: 'env', version: null })).toBe(false);
    expect(shouldReplaceRunningServer(null, { path: '/bundled', source: 'bundled', version: '0.3.0' })).toBe(false);
    expect(shouldReplaceRunningServer('0.2.3', null)).toBe(false);
  });
});

describe('bundledServerBinaryPath', () => {
  it('points at resources/server with .exe only on Windows', () => {
    expect(bundledServerBinaryPath('/r', 'darwin')).toBe(path.join('/r', 'server', 'astra-server'));
    expect(bundledServerBinaryPath('/r', 'win32')).toBe(path.join('/r', 'server', 'astra-server.exe'));
  });
});
