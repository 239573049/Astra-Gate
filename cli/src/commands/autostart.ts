import os from 'node:os';
import process from 'node:process';
import { AstraError } from '../errors.js';
import { style, success } from '../ui.js';
import { astraHome } from '../lib/paths.js';
import { resolveServerBinary } from '../lib/binary.js';
import {
  autostartArgv,
  autostartStatusLinux,
  autostartStatusMac,
  autostartStatusWindows,
  disableAutostartLinux,
  disableAutostartMac,
  disableAutostartWindows,
  enableAutostartLinux,
  enableAutostartMac,
  enableAutostartWindows,
} from '../lib/autostart.js';

export type AutostartAction = 'enable' | 'disable' | 'status';

async function dispatch<T>(mac: () => Promise<T>, linux: () => Promise<T>, win: () => Promise<T>): Promise<T> {
  switch (process.platform) {
    case 'darwin':
      return mac();
    case 'win32':
      return win();
    case 'linux':
      return linux();
    default:
      throw new AstraError(`Autostart is not supported on ${process.platform}.`);
  }
}

export async function runAutostart(action: AutostartAction): Promise<void> {
  const home = astraHome();
  if (action === 'status') {
    const s = await dispatch(
      () => autostartStatusMac(os.homedir()),
      () => autostartStatusLinux(os.homedir()),
      () => autostartStatusWindows(),
    );
    console.log(`Autostart is ${s === 'enabled' ? style.green('enabled') : 'disabled'}.`);
    return;
  }
  if (action === 'enable') {
    const command = resolveServerBinary();
    const argv = autostartArgv(command);
    await dispatch(
      () => enableAutostartMac(argv, os.homedir(), home),
      () => enableAutostartLinux(argv, os.homedir()),
      () => enableAutostartWindows(argv),
    );
    success('Astra will start automatically at login.');
  } else {
    await dispatch(
      () => disableAutostartMac(os.homedir()),
      () => disableAutostartLinux(os.homedir()),
      () => disableAutostartWindows(),
    );
    success('Autostart disabled.');
  }
}
