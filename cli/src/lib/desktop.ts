import { spawn } from 'node:child_process';
import { locateNpm, npmChildEnv } from '@aidotnet/update-core';
import { PACKAGE_NAME } from '../version.js';

// Desktop-app install/launch helpers now live in the shared update engine
// (@aidotnet/update-core) so the desktop app can drive the exact same flow.
// The CLI keeps only the npm-global self-update, which is CLI-specific.
export {
  createLauncher,
  findDesktopEntry,
  installDesktopClient,
  removeLauncher,
  renderLinuxDesktopFile,
} from '@aidotnet/update-core';

function usesShell(command: string): boolean {
  return process.platform === 'win32' && command.endsWith('.cmd');
}

/**
 * `npm install -g <this package>@<version>`: the exact version the update feed named (the server
 * was just updated to it), not a dist-tag — `latest` would move a preview install back to stable.
 */
export async function npmInstallGlobal(version: string): Promise<{ code: number | null; stderr: string }> {
  const npm = locateNpm();
  const full = [...npm.args, 'install', '-g', `${PACKAGE_NAME}@${version}`, '--no-audit', '--no-fund', '--loglevel', 'error'];
  return new Promise((resolve, reject) => {
    const child = spawn(npm.command, full, {
      stdio: 'inherit',
      env: npmChildEnv(),
      windowsHide: true,
      shell: usesShell(npm.command),
    });
    child.on('error', reject);
    child.on('close', (code) => resolve({ code, stderr: '' }));
  });
}
