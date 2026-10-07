import { describe, expect, it } from 'vitest';
import { findRepoRoot, resolveServerBinary } from '../src/lib/binary';
import { AstraError } from '../src/errors';

function makeFs(files: Iterable<string>): (p: string) => boolean {
  const set = new Set(files);
  return (p) => set.has(p);
}

const pkgJson = '/store/npm/server-darwin-arm64/package.json';
const pkgBin = '/store/npm/server-darwin-arm64/bin/astra-server';

function pkgReader(): (p: string, enc: 'utf8') => string {
  return (p) => {
    if (p === pkgJson) {
      return JSON.stringify({ bin: { 'astra-server': 'bin/astra-server' } });
    }
    throw new Error(`unexpected read: ${p}`);
  };
}

function resolver(): (baseDir: string, request: string) => string {
  return (_base, request) => {
    if (request === '@aidotnet/server-darwin-arm64/package.json') return pkgJson;
    throw new Error(`cannot resolve ${request}`);
  };
}

const base = { platform: 'darwin', arch: 'arm64', baseDir: '/cli/dist' } as const;

describe('resolveServerBinary', () => {
  it('prefers ASTRA_SERVER_BIN', () => {
    const cmd = resolveServerBinary({
      ...base,
      env: { ASTRA_SERVER_BIN: '/opt/custom/astra-server' },
      existsSync: makeFs(['/opt/custom/astra-server']),
      cwd: '/Users/demo',
    });
    expect(cmd).toEqual({
      command: '/opt/custom/astra-server',
      args: [],
      serverPath: '/opt/custom/astra-server',
      source: 'env',
    });
  });

  it('rejects a ASTRA_SERVER_BIN that does not exist', () => {
    expect(() =>
      resolveServerBinary({
        ...base,
        env: { ASTRA_SERVER_BIN: '/gone/astra-server' },
        existsSync: makeFs([]),
        cwd: '/Users/demo',
      }),
    ).toThrow(AstraError);
  });

  it('uses the repo Debug binary when inside the repository', () => {
    const native = '/repo/src/Astra.Server/bin/Debug/net10.0/astra-server';
    const cmd = resolveServerBinary({
      ...base,
      env: {},
      existsSync: makeFs(['/repo/src/Astra.Server', native]),
      cwd: '/repo/cli',
    });
    expect(cmd.source).toBe('repo');
    expect(cmd.command).toBe(native);
  });

  it('falls back to dotnet + dll inside the repository', () => {
    const dll = '/repo/src/Astra.Server/bin/Debug/net10.0/astra-server.dll';
    const cmd = resolveServerBinary({
      ...base,
      env: {},
      existsSync: makeFs(['/repo/src/Astra.Server', dll]),
      cwd: '/repo/src/Astra.Server',
    });
    expect(cmd.source).toBe('repo');
    expect(cmd.command).toBe('dotnet');
    expect(cmd.args).toEqual([dll]);
  });

  it('resolves the platform package when not in a repo', () => {
    const cmd = resolveServerBinary({
      ...base,
      env: {},
      existsSync: makeFs([pkgBin]),
      readFileSync: pkgReader(),
      resolveFrom: resolver(),
      cwd: '/Users/demo',
    });
    expect(cmd.source).toBe('package');
    expect(cmd.command).toBe(pkgBin);
    expect(cmd.serverPath).toBe(pkgBin);
  });

  it('reports a friendly error when nothing is found', () => {
    expect(() =>
      resolveServerBinary({
        ...base,
        env: {},
        existsSync: makeFs([]),
        resolveFrom: () => {
          throw new Error('not installed');
        },
        cwd: '/Users/demo',
      }),
    ).toThrow(/Could not find the Astra server binary/);
  });

  it('env beats repo beats package', () => {
    const envBin = '/env/astra-server';
    const native = '/repo/src/Astra.Server/bin/Debug/net10.0/astra-server';
    const files = makeFs(['/repo/src/Astra.Server', native, pkgBin, envBin]);
    const cmd = resolveServerBinary({
      ...base,
      env: { ASTRA_SERVER_BIN: envBin },
      existsSync: files,
      readFileSync: pkgReader(),
      resolveFrom: resolver(),
      cwd: '/repo',
    });
    expect(cmd.source).toBe('env');

    const cmd2 = resolveServerBinary({
      ...base,
      env: {},
      existsSync: files,
      readFileSync: pkgReader(),
      resolveFrom: resolver(),
      cwd: '/repo',
    });
    expect(cmd2.source).toBe('repo');
  });

  it('install.json beats repo and package; env still wins', () => {
    const managed = '/home/.astra/server/astra-server-0.2.0';
    const native = '/repo/src/Astra.Server/bin/Debug/net10.0/astra-server';
    const files = makeFs(['/repo/src/Astra.Server', native, pkgBin, managed]);
    const cmd = resolveServerBinary({
      ...base,
      env: {},
      installServerPath: managed,
      existsSync: files,
      readFileSync: pkgReader(),
      resolveFrom: resolver(),
      cwd: '/repo',
    });
    expect(cmd.source).toBe('install');
    expect(cmd.serverPath).toBe(managed);

    const withEnv = resolveServerBinary({
      ...base,
      env: { ASTRA_SERVER_BIN: '/env/astra-server' },
      installServerPath: managed,
      existsSync: makeFs(['/env/astra-server', managed]),
      cwd: '/Users/demo',
    });
    expect(withEnv.source).toBe('env');
  });

  it('skips a missing install.json path and falls through', () => {
    const native = '/repo/src/Astra.Server/bin/Debug/net10.0/astra-server';
    const cmd = resolveServerBinary({
      ...base,
      env: {},
      installServerPath: '/gone/managed/astra-server',
      existsSync: makeFs(['/repo/src/Astra.Server', native]),
      cwd: '/repo',
    });
    expect(cmd.source).toBe('repo');
  });
});

describe('findRepoRoot', () => {
  it('walks up to the repo root', () => {
    const exists = makeFs(['/work/Astra/src/Astra.Server']);
    expect(findRepoRoot('/work/Astra/a/b/c', exists)).toBe('/work/Astra');
  });

  it('returns null outside a repository', () => {
    expect(findRepoRoot('/etc', makeFs([]))).toBeNull();
  });
});
