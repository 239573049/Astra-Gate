import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { npmChildEnv, withNodeOnPath } from '../src/npm-runner.js';

describe('npmChildEnv', () => {
  it('drops the keys a parent npx exports that break nested installs', () => {
    const env = npmChildEnv({
      PATH: '/usr/bin',
      npm_config_allow_scripts: '@anthropic-ai/claude-code',
      'npm_config_allow-scripts': 'x',
      NPM_CONFIG_ALLOW_SCRIPTS: 'x',
      npm_config_call: 'astra install --desktop',
      npm_config_package: '@aidotnet/astra-gate',
      npm_config_registry: 'https://registry.npmmirror.com/',
      npm_config_cache: '/Users/demo/.npm',
    });
    expect(env).toEqual({
      PATH: '/usr/bin',
      npm_config_registry: 'https://registry.npmmirror.com/',
      npm_config_cache: '/Users/demo/.npm',
    });
  });
});

describe('withNodeOnPath', () => {
  const GUI_PATH = '/usr/bin:/bin:/usr/sbin:/sbin';
  let tmp: string;
  beforeEach(() => {
    tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-npm-path-'));
  });
  afterEach(() => {
    fs.rmSync(tmp, { recursive: true, force: true });
  });

  function fakeNodeDir(name: string): string {
    const dir = path.join(tmp, name);
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'node'), '#!/bin/sh\n', { mode: 0o755 });
    return dir;
  }

  const npm = { command: 'npm', args: [] };
  const never = async (): Promise<string | null> => {
    throw new Error('the login shell must not be consulted');
  };

  it('leaves the env untouched when node is already on PATH (terminal launch)', async () => {
    const nodeDir = fakeNodeDir('nvm-bin');
    const env = { PATH: `${GUI_PATH}:${nodeDir}`, HOME: '/Users/demo' };
    const out = await withNodeOnPath(env, npm, { platform: 'darwin', loginShellPath: never });
    expect(out).toBe(env);
  });

  it('appends the login shell PATH when a GUI PATH cannot see node, keeping existing entries first', async () => {
    const nodeDir = fakeNodeDir('nvm-bin');
    const out = await withNodeOnPath({ PATH: GUI_PATH }, npm, {
      platform: 'darwin',
      wellKnownBinDirs: [],
      loginShellPath: async () => `${nodeDir}:/usr/bin`,
    });
    const dirs = out.PATH?.split(path.delimiter) ?? [];
    expect(dirs.slice(0, 4)).toEqual(GUI_PATH.split(':'));
    expect(dirs).toContain(nodeDir);
    expect(dirs.filter((d) => d === '/usr/bin')).toHaveLength(1);
  });

  it("adds the located npm's own directory without asking the shell when node sits beside it", async () => {
    const binDir = fakeNodeDir('prefix-bin');
    const out = await withNodeOnPath(
      { PATH: GUI_PATH },
      { command: path.join(binDir, 'npm'), args: [] },
      { platform: 'darwin', wellKnownBinDirs: [], loginShellPath: never },
    );
    expect(out.PATH?.split(path.delimiter)).toContain(binDir);
  });

  it('survives a login shell that yields nothing and never touches Windows', async () => {
    const out = await withNodeOnPath({ PATH: GUI_PATH }, npm, { platform: 'darwin', wellKnownBinDirs: [], loginShellPath: async () => null });
    expect(out.PATH?.startsWith(GUI_PATH)).toBe(true);

    const win = { PATH: 'C:\\Windows' };
    expect(await withNodeOnPath(win, npm, { platform: 'win32', loginShellPath: never })).toBe(win);
  });
});
