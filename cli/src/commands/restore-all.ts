import { AstraError } from '../errors.js';
import { success } from '../ui.js';
import { resolveServerBinary } from '../lib/binary.js';
import { exitCodeOf } from '../lib/process-utils.js';
import { spawn } from 'node:child_process';

/** Run the server binary's offline `restore-all` subcommand (no HTTP needed). */
export async function runRestoreAll(opts: { purge?: boolean }): Promise<void> {
  const command = resolveServerBinary();
  const args = [...command.args, 'restore-all', ...(opts.purge ? ['--purge'] : [])];
  const child = spawn(command.command, args, { stdio: 'inherit', windowsHide: true });
  const code = await exitCodeOf(child);
  if (code !== 0) {
    throw new AstraError(`restore-all failed with exit code ${code}.`);
  }
  success(
    opts.purge
      ? 'All client configurations were restored and Astra entries were removed from them.'
      : 'All client configurations were restored.',
  );
}
