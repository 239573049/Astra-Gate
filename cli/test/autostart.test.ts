import { describe, expect, it } from 'vitest';
import {
  autostartArgv,
  launchdPlistPath,
  renderLaunchdPlist,
  renderSystemdUnit,
  schtasksCreateArgs,
  schtasksDeleteArgs,
  schtasksQueryArgs,
  systemdUnitPath,
} from '../src/lib/autostart';

const argv = ['/opt/astra/bin/astra-server', 'serve', '--started-by', 'autostart'];
const log = '/Users/demo/.astra/logs/server-stdout.log';

describe('launchd', () => {
  it('renders a plist with label, argv and log paths', () => {
    const plist = renderLaunchdPlist(argv, log);
    expect(plist).toContain('<string>io.astra.server</string>');
    expect(plist).toContain('<string>/opt/astra/bin/astra-server</string>');
    expect(plist).toContain('<string>--started-by</string>');
    expect(plist).toContain('<string>autostart</string>');
    expect(plist).toContain('<key>RunAtLoad</key>');
    expect(plist).toContain(`<string>${log}</string>`);
  });

  it('escapes XML entities and preserves spaces', () => {
    const plist = renderLaunchdPlist(['/apps & tools/astra-server'], log);
    expect(plist).toContain('<string>/apps &amp; tools/astra-server</string>');
  });

  it('places the plist in ~/Library/LaunchAgents', () => {
    expect(launchdPlistPath('/Users/demo')).toBe(
      '/Users/demo/Library/LaunchAgents/io.astra.server.plist',
    );
  });
});

describe('systemd', () => {
  it('renders a user unit with ExecStart and default.target', () => {
    const unit = renderSystemdUnit(argv);
    expect(unit).toContain('ExecStart=/opt/astra/bin/astra-server serve --started-by autostart');
    expect(unit).toContain('WantedBy=default.target');
    expect(unit).toContain('Restart=on-failure');
  });

  it('quotes paths containing spaces', () => {
    const unit = renderSystemdUnit(['/opt/my apps/astra-server', 'serve']);
    expect(unit).toContain('ExecStart="/opt/my apps/astra-server" serve');
  });

  it('places the unit in ~/.config/systemd/user', () => {
    expect(systemdUnitPath('/home/demo')).toBe('/home/demo/.config/systemd/user/astra.service');
  });
});

describe('schtasks', () => {
  const winArgv = ['C:\\Program Files\\Astra\\astra-server.exe', 'serve', '--started-by', 'autostart'];

  it('builds an ONLOGON task with a quoted /TR command line', () => {
    const args = schtasksCreateArgs(winArgv);
    expect(args[0]).toBe('/Create');
    expect(args).toContain('/SC');
    expect(args).toContain('ONLOGON');
    const tn = args.indexOf('/TN');
    expect(args[tn + 1]).toBe('Astra Server');
    const tr = args.indexOf('/TR');
    expect(args[tr + 1]).toBe(
      '"\\"C:\\Program Files\\Astra\\astra-server.exe\\" serve --started-by autostart"',
    );
  });

  it('builds delete and query args for the same task name', () => {
    expect(schtasksDeleteArgs()).toEqual(['/Delete', '/F', '/TN', 'Astra Server']);
    expect(schtasksQueryArgs()).toEqual(['/Query', '/TN', 'Astra Server']);
  });
});

describe('autostartArgv', () => {
  it('appends the serve subcommand and started-by flag', () => {
    expect(
      autostartArgv({ command: 'dotnet', args: ['/srv/astra-server.dll'] }),
    ).toEqual(['dotnet', '/srv/astra-server.dll', 'serve', '--started-by', 'autostart']);
  });
});
