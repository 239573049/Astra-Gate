import { describe, expect, it } from 'vitest';

import * as path from 'node:path';

import { bundledServerBinaryPath, devServerBinaryPath, resolveServerBinary } from '../src/shared/resolveServerBinary';

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
    expect(r).toEqual({ path: '/opt/astra-server', source: 'install' });
  });

  it('falls back to env, then dev', () => {
    expect(
      resolveServerBinary({
        installServerPath: '/missing',
        envServerBin: '/env/astra-server',
        repoRoot,
        exists: exists(['/env/astra-server', devPath]),
      }),
    ).toEqual({ path: '/env/astra-server', source: 'env' });

    expect(
      resolveServerBinary({
        installServerPath: '/missing',
        envServerBin: '/also-missing',
        repoRoot,
        exists: exists([devPath]),
      }),
    ).toEqual({ path: devPath, source: 'dev' });
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
      });
    });

    it('loses to install.json when that server is at least as new', () => {
      for (const installServerVersion of ['0.3.0', '0.4.0', null]) {
        expect(
          resolveServerBinary({
            installServerPath: '/home/.astra/server/astra-server-x',
            installServerVersion,
            bundled,
            exists: exists(['/home/.astra/server/astra-server-x', bundled.path]),
          }),
        ).toEqual({ path: '/home/.astra/server/astra-server-x', source: 'install' });
      }
    });

    it('beats a stale install.json server after an installer upgrade', () => {
      expect(
        resolveServerBinary({
          installServerPath: '/home/.astra/server/astra-server-0.2.0',
          installServerVersion: '0.2.0',
          bundled,
          exists: exists(['/home/.astra/server/astra-server-0.2.0', bundled.path]),
        }),
      ).toEqual({ path: bundled.path, source: 'bundled' });
    });

    it('keeps ASTRA_SERVER_BIN ahead of the bundled server', () => {
      expect(
        resolveServerBinary({
          envServerBin: '/env/astra-server',
          bundled,
          exists: exists(['/env/astra-server', bundled.path]),
        }),
      ).toEqual({ path: '/env/astra-server', source: 'env' });
    });

    it('is skipped when the app ships without one', () => {
      expect(resolveServerBinary({ bundled, repoRoot, exists: exists([devPath]) })).toEqual({
        path: devPath,
        source: 'dev',
      });
    });
  });
});

describe('bundledServerBinaryPath', () => {
  it('points at resources/server with .exe only on Windows', () => {
    expect(bundledServerBinaryPath('/r', 'darwin')).toBe(path.join('/r', 'server', 'astra-server'));
    expect(bundledServerBinaryPath('/r', 'win32')).toBe(path.join('/r', 'server', 'astra-server.exe'));
  });
});
