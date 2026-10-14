import { describe, expect, it } from 'vitest';
import {
  autostartArgv,
  launchdPlistPath,
  renderLaunchdPlist,
  regAddArgs,
  regDeleteArgs,
  regQueryArgs,
  renderSystemdUnit,
  schtasksDeleteArgs,
  schtasksQueryArgs,
  systemdUnitPath,
  windowsCommandLine,
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

describe('Windows Run key', () => {
  const winArgv = ['C:\\Program Files\\Astra\\astra-server.exe', 'serve', '--started-by', 'autostart'];
  const key = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run';

  it('writes the quoted command line under the per-user Run key', () => {
    expect(windowsCommandLine(winArgv)).toBe(
      '"C:\\Program Files\\Astra\\astra-server.exe" serve --started-by autostart',
    );
    expect(regAddArgs(winArgv)).toEqual([
      'add', key, '/v', 'Astra Server', '/t', 'REG_SZ',
      '/d', '"C:\\Program Files\\Astra\\astra-server.exe" serve --started-by autostart', '/f',
    ]);
  });

  it('always quotes the program, and other arguments only with spaces', () => {
    expect(windowsCommandLine(['C:\\astra\\dotnet.exe', 'C:\\my app\\astra-server.dll', 'serve'])).toBe(
      '"C:\\astra\\dotnet.exe" "C:\\my app\\astra-server.dll" serve',
    );
  });

  it('deletes and queries the same value', () => {
    expect(regDeleteArgs()).toEqual(['delete', key, '/v', 'Astra Server', '/f']);
    expect(regQueryArgs()).toEqual(['query', key, '/v', 'Astra Server']);
  });

  it('still finds the logon task earlier versions created', () => {
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
