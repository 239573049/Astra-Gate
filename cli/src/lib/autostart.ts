import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { AstraError } from '../errors.js';
import { homePaths } from './paths.js';
import { runCapture } from './process-utils.js';

export const LAUNCHD_LABEL = 'io.astra.server';
export const SYSTEMD_UNIT_NAME = 'astra.service';
export const SCHTASKS_TASK_NAME = 'Astra Server';

function xmlEscape(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

export function launchdPlistPath(home: string = os.homedir()): string {
  return path.join(home, 'Library', 'LaunchAgents', `${LAUNCHD_LABEL}.plist`);
}

export function renderLaunchdPlist(argv: readonly string[], logFile: string): string {
  return `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${xmlEscape(LAUNCHD_LABEL)}</string>
  <key>ProgramArguments</key>
  <array>
${argv.map((a) => `    <string>${xmlEscape(a)}</string>`).join('\n')}
  </array>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <false/>
  <key>ProcessType</key>
  <string>Background</string>
  <key>StandardOutPath</key>
  <string>${xmlEscape(logFile)}</string>
  <key>StandardErrorPath</key>
  <string>${xmlEscape(logFile)}</string>
</dict>
</plist>
`;
}

export function systemdUnitPath(home: string = os.homedir()): string {
  return path.join(home, '.config', 'systemd', 'user', SYSTEMD_UNIT_NAME);
}

function systemdQuote(part: string): string {
  return /[\s"]/.test(part) ? `"${part.replace(/"/g, '\\"')}"` : part;
}

export function renderSystemdUnit(argv: readonly string[]): string {
  const exec = argv.map(systemdQuote).join(' ');
  return `[Unit]
Description=Astra local AI gateway server
After=network.target

[Service]
Type=simple
ExecStart=${exec}
Restart=on-failure
RestartSec=3

[Install]
WantedBy=default.target
`;
}

function quoteForTr(argv: readonly string[]): string {
  // Canonical schtasks /TR form: outer quotes, inner quotes backslash-escaped:
  //   "\"C:\Program Files\app.exe\" serve --flag"
  const inner = argv
    .map((a) => (/[\s"]/.test(a) ? `"${a}"` : a))
    .join(' ')
    .replace(/"/g, '\\"');
  return `"${inner}"`;
}

export function schtasksCreateArgs(argv: readonly string[]): string[] {
  return ['/Create', '/F', '/SC', 'ONLOGON', '/TN', SCHTASKS_TASK_NAME, '/TR', quoteForTr(argv)];
}

export function schtasksDeleteArgs(): string[] {
  return ['/Delete', '/F', '/TN', SCHTASKS_TASK_NAME];
}

export function schtasksQueryArgs(): string[] {
  return ['/Query', '/TN', SCHTASKS_TASK_NAME];
}

/** Full argv for the autostart entry: [<command> ...args] serve --started-by autostart */
export function autostartArgv(command: { command: string; args: readonly string[] }): string[] {
  return [command.command, ...command.args, 'serve', '--started-by', 'autostart'];
}

async function exec(
  args: readonly string[],
): Promise<{ code: number | null; stdout: string; stderr: string }> {
  const [cmd, ...rest] = args;
  if (!cmd) throw new AstraError('Missing command.');
  return runCapture(cmd, rest, { timeoutMs: 30_000 });
}

function logFileFor(lgHome: string): string {
  return path.join(homePaths(lgHome).logsDir, 'server-stdout.log');
}

// ---------- macOS (launchd) ----------

export async function enableAutostartMac(
  argv: readonly string[],
  home: string = os.homedir(),
  lgHome: string = homePaths(home).home,
): Promise<void> {
  const plist = launchdPlistPath(home);
  fs.mkdirSync(path.dirname(plist), { recursive: true });
  fs.writeFileSync(plist, renderLaunchdPlist(argv, logFileFor(lgHome)));
  await exec(['launchctl', 'unload', plist]).catch(() => undefined);
  const r = await exec(['launchctl', 'load', plist]);
  if (r.code !== 0) {
    throw new AstraError('launchctl load failed.', r.stderr.trim() || r.stdout.trim() || undefined);
  }
}

export async function disableAutostartMac(home: string = os.homedir()): Promise<void> {
  const plist = launchdPlistPath(home);
  await exec(['launchctl', 'unload', plist]).catch(() => undefined);
  fs.rmSync(plist, { force: true });
}

export async function autostartStatusMac(home: string = os.homedir()): Promise<'enabled' | 'disabled'> {
  return fs.existsSync(launchdPlistPath(home)) ? 'enabled' : 'disabled';
}

// ---------- Linux (systemd --user) ----------

export async function enableAutostartLinux(
  argv: readonly string[],
  home: string = os.homedir(),
): Promise<void> {
  const unit = systemdUnitPath(home);
  fs.mkdirSync(path.dirname(unit), { recursive: true });
  fs.writeFileSync(unit, renderSystemdUnit(argv));
  const reload = await exec(['systemctl', '--user', 'daemon-reload']);
  if (reload.code !== 0) {
    throw new AstraError(
      'systemctl --user daemon-reload failed. Is systemd available (a WSL setup without systemd is not supported)?',
      reload.stderr.trim() || undefined,
    );
  }
  const enable = await exec(['systemctl', '--user', 'enable', '--now', SYSTEMD_UNIT_NAME]);
  if (enable.code !== 0) {
    throw new AstraError(
      `systemctl --user enable --now ${SYSTEMD_UNIT_NAME} failed.`,
      enable.stderr.trim() || undefined,
    );
  }
}

export async function disableAutostartLinux(home: string = os.homedir()): Promise<void> {
  await exec(['systemctl', '--user', 'disable', '--now', SYSTEMD_UNIT_NAME]).catch(() => undefined);
  fs.rmSync(systemdUnitPath(home), { force: true });
  await exec(['systemctl', '--user', 'daemon-reload']).catch(() => undefined);
}

export async function autostartStatusLinux(
  home: string = os.homedir(),
): Promise<'enabled' | 'disabled'> {
  if (!fs.existsSync(systemdUnitPath(home))) return 'disabled';
  const r = await exec(['systemctl', '--user', 'is-enabled', SYSTEMD_UNIT_NAME]).catch(() => null);
  return r !== null && r.code === 0 && r.stdout.trim() === 'enabled' ? 'enabled' : 'disabled';
}

// ---------- Windows (schtasks) ----------

export async function enableAutostartWindows(argv: readonly string[]): Promise<void> {
  const r = await exec(['schtasks', ...schtasksCreateArgs(argv)]);
  if (r.code !== 0) {
    throw new AstraError('schtasks /Create failed.', r.stderr.trim() || undefined);
  }
}

export async function disableAutostartWindows(): Promise<void> {
  const r = await exec(['schtasks', ...schtasksDeleteArgs()]);
  if (r.code !== 0 && !/does not exist|no existe/i.test(r.stderr + r.stdout)) {
    throw new AstraError('schtasks /Delete failed.', r.stderr.trim() || undefined);
  }
}

export async function autostartStatusWindows(): Promise<'enabled' | 'disabled'> {
  const r = await exec(['schtasks', ...schtasksQueryArgs()]);
  return r.code === 0 ? 'enabled' : 'disabled';
}
