import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { AstraError } from '../errors.js';
import { info, success, warn } from '../ui.js';
import { astraHome } from '../lib/paths.js';
import { resolveServerBinary } from '../lib/binary.js';
import { confirmPrompt } from '../lib/prompt.js';
import { exitCodeOf } from '../lib/process-utils.js';
import { stopServer } from '../lib/server-lifecycle.js';
import { runAutostart } from './autostart.js';
import { runUninstallClient } from './desktop.js';
import { PACKAGE_NAME } from '../version.js';

async function restoreAllOffline(purge: boolean): Promise<void> {
  const command = resolveServerBinary();
  const child = spawn(
    command.command,
    [...command.args, 'restore-all', ...(purge ? ['--purge'] : [])],
    { stdio: 'inherit', windowsHide: true },
  );
  const code = await exitCodeOf(child);
  if (code !== 0) throw new AstraError(`restore-all exited with code ${code}.`);
}

/**
 * Full uninstall: restore client configs → disable autostart → remove the
 * desktop app → stop the server → optionally delete ~/.astra.
 */
export async function runUninstall(opts: { purge?: boolean }): Promise<void> {
  const home = astraHome();
  info('Uninstalling Astra…');

  info('• Restoring client configurations…');
  try {
    await restoreAllOffline(opts.purge === true);
  } catch (err) {
    warn(`restore-all failed: ${(err as Error).message}`);
  }

  info('• Disabling autostart…');
  try {
    await runAutostart('disable');
  } catch (err) {
    warn(`Could not disable autostart: ${(err as Error).message}`);
  }

  info('• Removing the desktop app…');
  try {
    await runUninstallClient();
  } catch (err) {
    warn(`Could not remove the desktop app: ${(err as Error).message}`);
  }

  info('• Stopping the server…');
  try {
    await stopServer();
  } catch (err) {
    warn(`Could not stop the server: ${(err as Error).message}`);
  }

  if (opts.purge) {
    if (process.stdin.isTTY) {
      const ok = await confirmPrompt(`Delete all Astra data in ${home}? [y/N] `);
      if (!ok) {
        console.log('Skipped data deletion. Run `astra uninstall --purge` again to delete it.');
        return;
      }
    }
    fs.rmSync(home, { recursive: true, force: true });
    success(`Deleted ${home}.`);
  }

  success(`Astra uninstalled. Remove the npm package with \`npm uninstall -g ${PACKAGE_NAME}\`.`);
}
