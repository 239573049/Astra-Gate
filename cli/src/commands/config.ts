import { spawn } from 'node:child_process';
import { AstraError } from '../errors.js';
import { info, success } from '../ui.js';
import { resolveServerBinary } from '../lib/binary.js';
import { promptHidden } from '../lib/prompt.js';
import { exitCodeOf } from '../lib/process-utils.js';
import { serverStatus } from '../lib/server-lifecycle.js';

export async function runSetPassword(): Promise<void> {
  const password = await promptHidden('Enter new admin password: ');
  if (!password) throw new AstraError('Password must not be empty.');
  const confirm = await promptHidden('Repeat password: ');
  if (confirm !== password) throw new AstraError('Passwords do not match.');

  const command = resolveServerBinary();
  const child = spawn(command.command, [...command.args, 'set-password'], {
    stdio: ['pipe', 'inherit', 'inherit'],
    windowsHide: true,
  });
  child.stdin?.write(`${password}\n`);
  child.stdin?.end();
  const code = await exitCodeOf(child);
  if (code !== 0) {
    throw new AstraError(`set-password failed with exit code ${code}.`);
  }
  success('Admin password updated.');
  const status = await serverStatus();
  if (status.running) {
    info('Restart the server for the new password to take effect: `astra restart`.');
  }
}
