import { spawn } from 'node:child_process';
import { locateNpm } from '@aidotnet/update-core';

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

export async function npmInstallGlobal(): Promise<{ code: number | null; stderr: string }> {
  const npm = locateNpm();
  const full = [...npm.args, 'install', '-g', 'astragate@latest', '--no-audit', '--no-fund', '--loglevel', 'error'];
  return new Promise((resolve, reject) => {
    const child = spawn(npm.command, full, {
      stdio: 'inherit',
      windowsHide: true,
      shell: usesShell(npm.command),
    });
    child.on('error', reject);
    child.on('close', (code) => resolve({ code, stderr: '' }));
  });
}
