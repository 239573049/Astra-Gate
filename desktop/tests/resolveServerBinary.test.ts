import { describe, expect, it } from 'vitest';

import * as path from 'node:path';

import { devServerBinaryPath, resolveServerBinary } from '../src/shared/resolveServerBinary';

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
});
