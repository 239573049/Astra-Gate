import { describe, expect, it } from 'vitest';

import { parseInstallJson, serverPathFromInstall } from '../src/shared/install';

describe('parseInstallJson', () => {
  it('parses known fields', () => {
    const text = JSON.stringify({
      serverPath: '/opt/astra/astra-server',
      serverVersion: '0.1.0',
      desktopPath: '/opt/astra/desktop',
      desktopVersion: '0.1.0',
      updatedAt: '2026-10-06T00:00:00Z',
      extra: 'ignored',
    });
    expect(parseInstallJson(text)).toEqual({
      serverPath: '/opt/astra/astra-server',
      serverVersion: '0.1.0',
      desktopPath: '/opt/astra/desktop',
      desktopVersion: '0.1.0',
      updatedAt: '2026-10-06T00:00:00Z',
    });
  });

  it('returns null on invalid input', () => {
    expect(parseInstallJson(null)).toBeNull();
    expect(parseInstallJson('nope')).toBeNull();
    expect(parseInstallJson('42')).toBeNull();
  });
});

describe('serverPathFromInstall', () => {
  it('extracts a non-empty serverPath', () => {
    expect(serverPathFromInstall({ serverPath: '/x/y' })).toBe('/x/y');
    expect(serverPathFromInstall({ serverPath: '  ' })).toBeNull();
    expect(serverPathFromInstall({})).toBeNull();
    expect(serverPathFromInstall(null)).toBeNull();
  });
});
