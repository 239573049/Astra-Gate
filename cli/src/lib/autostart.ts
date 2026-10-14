import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { AstraError } from '../errors.js';
import { homePaths } from './paths.js';
import { runCapture } from './process-utils.js';

// The server writes the same entries (src/Astra.Server/Autostart/AutostartService.cs, the settings page's switch):
// names, paths, argv and formats change together.
export const LAUNCHD_LABEL = 'io.astra.server';
export const SYSTEMD_UNIT_NAME = 'astra.service';
export const RUN_KEY = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run';
export const RUN_VALUE_NAME = 'Astra Server';
/** The logon task earlier versions created; creating one needs an elevated prompt, so the Run key replaced it. */
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

/** The Run value: the program always quoted, then the arguments, quoted when they contain spaces. */
export function windowsCommandLine(argv: readonly string[]): string {
  return argv
    .map((a, i) => (i === 0 || a === '' || /[\s"]/.test(a) ? `"${a.replace(/"/g, '\\"')}"` : a))
    .join(' ');
}

export function regAddArgs(argv: readonly string[]): string[] {
  return ['add', RUN_KEY, '/v', RUN_VALUE_NAME, '/t', 'REG_SZ', '/d', windowsCommandLine(argv), '/f'];
}

export function regDeleteArgs(): string[] {
  return ['delete', RUN_KEY, '/v', RUN_VALUE_NAME, '/f'];
}

export function regQueryArgs(): string[] {
  return ['query', RUN_KEY, '/v', RUN_VALUE_NAME];
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

// ---------- Windows (HKCU Run key) ----------

async function legacyTaskExists(): Promise<boolean> {
  const r = await exec(['schtasks', ...schtasksQueryArgs()]).catch(() => null);
  return r !== null && r.code === 0;
}

/** Removes the logon task of earlier versions when there is one; false when it is still there afterwards. */
async function removeLegacyTask(): Promise<boolean> {
  if (!(await legacyTaskExists())) return true;
  await exec(['schtasks', ...schtasksDeleteArgs()]).catch(() => undefined);
  return !(await legacyTaskExists());
}

export async function enableAutostartWindows(argv: readonly string[]): Promise<void> {
  const r = await exec(['reg', ...regAddArgs(argv)]);
  if (r.code !== 0) {
    throw new AstraError('reg add failed.', r.stderr.trim() || undefined);
  }
  // The old logon task would start a second server next to the Run key's.
  await removeLegacyTask();
}

export async function disableAutostartWindows(): Promise<void> {
  await exec(['reg', ...regDeleteArgs()]).catch(() => undefined);
  const still = await exec(['reg', ...regQueryArgs()]);
  if (still.code === 0) {
    throw new AstraError('reg delete failed.', `Remove the "${RUN_VALUE_NAME}" value under ${RUN_KEY}.`);
  }
  if (!(await removeLegacyTask())) {
    throw new AstraError(
      `The scheduled task "${SCHTASKS_TASK_NAME}" could not be removed.`,
      `Run from an administrator prompt: schtasks /Delete /F /TN "${SCHTASKS_TASK_NAME}"`,
    );
  }
}

export async function autostartStatusWindows(): Promise<'enabled' | 'disabled'> {
  const r = await exec(['reg', ...regQueryArgs()]);
  if (r.code === 0) return 'enabled';
  return (await legacyTaskExists()) ? 'enabled' : 'disabled';
}
