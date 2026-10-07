import { describe, expect, it } from 'vitest';

import { resolveUpdateTrack } from '../src/shared/updateTrack';

const darwinApp = '/Applications/Astra.app/Contents/MacOS/Astra';
const darwinNpm =
  '/Users/demo/.astra/desktop/node_modules/@aidotnet/desktop-darwin-arm64/app/Astra.app/Contents/MacOS/Astra';

describe('resolveUpdateTrack', () => {
  it('dev runs never self-update', () => {
    expect(resolveUpdateTrack({ isPackaged: false, realExePath: darwinApp, platform: 'darwin' })).toBe('disabled');
  });

  it('macOS: applications install is the dmg track', () => {
    expect(resolveUpdateTrack({ isPackaged: true, realExePath: darwinApp, platform: 'darwin' })).toBe('dmg');
    expect(
      resolveUpdateTrack({
        isPackaged: true,
        realExePath: '/Users/demo/Applications/Astra.app/Contents/MacOS/Astra',
        platform: 'darwin',
      }),
    ).toBe('dmg');
  });

  it('macOS: realpath through the launcher symlink exposes node_modules', () => {
    expect(resolveUpdateTrack({ isPackaged: true, realExePath: darwinNpm, platform: 'darwin' })).toBe('npm');
    // Prefix branch: an install under the npm prefix without node_modules in
    // the path still counts as npm-managed.
    expect(
      resolveUpdateTrack({
        isPackaged: true,
        realExePath: '/Users/demo/.astra/desktop/staging/Astra.app/Contents/MacOS/Astra',
        installDesktopPath: '/Users/demo/.astra/desktop',
        platform: 'darwin',
      }),
    ).toBe('npm');
  });

  it('windows and linux installer installs are the installer track', () => {
    expect(
      resolveUpdateTrack({
        isPackaged: true,
        realExePath: 'C:\\Users\\demo\\AppData\\Local\\Programs\\Astra\\Astra.exe',
        platform: 'win32',
      }),
    ).toBe('installer');
    expect(
      resolveUpdateTrack({
        isPackaged: true,
        realExePath: '/tmp/.mount_AstraXyz/astra',
        platform: 'linux',
      }),
    ).toBe('installer');
  });

  it('windows and linux installs from the CLI stay on the npm track', () => {
    expect(
      resolveUpdateTrack({
        isPackaged: true,
        realExePath:
          'C:\\Users\\demo\\.astra\\desktop\\node_modules\\@aidotnet\\desktop-win32-x64\\app\\Astra.exe',
        platform: 'win32',
      }),
    ).toBe('npm');
    expect(
      resolveUpdateTrack({
        isPackaged: true,
        realExePath: '/home/demo/.astra/desktop/node_modules/@aidotnet/desktop-linux-x64/app/astra',
        platform: 'linux',
      }),
    ).toBe('npm');
  });
});
