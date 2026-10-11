import { describe, expect, it } from 'vitest';
import { isClientKind, platformInfo } from '../src/lib/platform';
import { AstraError } from '../src/errors';

describe('platformInfo', () => {
  it('maps the six supported platforms', () => {
    expect(platformInfo('darwin', 'arm64')).toMatchObject({
      rid: 'osx-arm64',
      serverPackage: '@aidotnet/server-darwin-arm64',
      desktopPackage: '@aidotnet/desktop-darwin-arm64',
      serverDir: 'server-darwin-arm64',
      binaryName: 'astra-server',
    });
    expect(platformInfo('darwin', 'x64').rid).toBe('osx-x64');
    expect(platformInfo('linux', 'x64').rid).toBe('linux-x64');
    expect(platformInfo('linux', 'arm64').rid).toBe('linux-arm64');
    expect(platformInfo('win32', 'x64')).toMatchObject({
      rid: 'win-x64',
      serverPackage: '@aidotnet/server-win32-x64',
      binaryName: 'astra-server.exe',
    });
    expect(platformInfo('win32', 'arm64').rid).toBe('win-arm64');
  });

  it('rejects unsupported platforms and arches', () => {
    expect(() => platformInfo('freebsd', 'x64')).toThrow(AstraError);
    expect(() => platformInfo('openbsd', 'arm64')).toThrow(AstraError);
    expect(() => platformInfo('linux', 'arm')).toThrow(AstraError);
    expect(() => platformInfo('win32', 'ia32')).toThrow(AstraError);
  });
});

describe('isClientKind', () => {
  it('accepts built-in kinds and plugin-<agent id>', () => {
    expect(isClientKind('codex')).toBe(true);
    expect(isClientKind('plugin-aider')).toBe(true);
    expect(isClientKind('plugin-my.agent_2')).toBe(true);
  });

  it('rejects unknown kinds and malformed plugin ids', () => {
    expect(isClientKind('nope')).toBe(false);
    expect(isClientKind('plugin-')).toBe(false);
    expect(isClientKind('plugin-Upper')).toBe(false);
  });
});
