import { describe, expect, it } from 'vitest';

import {
  findServerListenerPid,
  isAstraServerExecutable,
  parseLsofPids,
  parseNetstatListenerPids,
  parseTasklistImage,
  type RunCommand,
} from '../src/shared/listener';

describe('listener output parsers', () => {
  it('reads one pid per lsof line', () => {
    expect(parseLsofPids('19419\n')).toEqual([19419]);
    expect(parseLsofPids('12\r\n34\n\n')).toEqual([12, 34]);
    expect(parseLsofPids('')).toEqual([]);
  });

  it('keeps only netstat listeners on the port, whatever the state column says', () => {
    const out = [
      '',
      'Active Connections',
      '',
      '  Proto  Local Address          Foreign Address        State           PID',
      '  TCP    127.0.0.1:17321        0.0.0.0:0              ABHÖREN         4242',
      '  TCP    127.0.0.1:17321        127.0.0.1:50123        ESTABLISHED     4242',
      '  TCP    127.0.0.1:50123        127.0.0.1:17321        ESTABLISHED     777',
      '  TCP    127.0.0.1:173210       0.0.0.0:0              LISTENING       1',
      '  TCP    [::1]:17321            [::]:0                 LISTENING       4242',
    ].join('\r\n');
    expect(parseNetstatListenerPids(out, 17321)).toEqual([4242]);
  });

  it('reads the image name from a tasklist CSV row', () => {
    expect(parseTasklistImage('"astra-server-0.9.3.exe","4242","Console","1","51,200 K"\r\n')).toBe(
      'astra-server-0.9.3.exe',
    );
    expect(parseTasklistImage('INFO: No tasks are running which match the specified criteria.')).toBeNull();
  });

  it('recognises the bundled, managed and dev server executables only', () => {
    expect(isAstraServerExecutable('/Applications/Astra.app/Contents/Resources/server/astra-server')).toBe(true);
    expect(isAstraServerExecutable('/Users/a/.astra/server/astra-server-0.9.3\n')).toBe(true);
    expect(isAstraServerExecutable('astra-server-0.')).toBe(true); // Linux comm is cut at 15 chars
    expect(isAstraServerExecutable('C:\\Astra\\resources\\server\\astra-server.exe')).toBe(true);
    expect(isAstraServerExecutable('dotnet')).toBe(false);
    expect(isAstraServerExecutable('/usr/bin/ssh')).toBe(false);
    expect(isAstraServerExecutable('astra-server-proxy/sh')).toBe(false);
  });
});

describe('findServerListenerPid', () => {
  const runner = (outputs: Record<string, string | null>): { run: RunCommand; calls: string[] } => {
    const calls: string[] = [];
    return {
      calls,
      run: async (command, args) => {
        calls.push([command, ...args].join(' '));
        return outputs[command] ?? null;
      },
    };
  };

  it('returns the lsof listener when it is an astra-server (macOS)', async () => {
    const r = runner({ '/usr/sbin/lsof': '19419\n', ps: '/Users/a/.astra/server/astra-server-0.9.3\n' });
    expect(await findServerListenerPid(17321, 'darwin', r.run)).toBe(19419);
    expect(r.calls).toEqual([
      '/usr/sbin/lsof -nP -t -iTCP:17321 -sTCP:LISTEN',
      'ps -o comm= -p 19419',
    ]);
  });

  it('refuses a listener that is not an astra-server', async () => {
    const r = runner({ lsof: '555\n', ps: 'ssh\n' });
    expect(await findServerListenerPid(17321, 'linux', r.run)).toBeNull();
  });

  it('refuses when lsof is missing or finds several listeners', async () => {
    expect(await findServerListenerPid(17321, 'linux', runner({}).run)).toBeNull();
    expect(await findServerListenerPid(17321, 'linux', runner({ lsof: '1\n2\n', ps: 'astra-server' }).run)).toBeNull();
  });

  it('uses netstat and tasklist on Windows', async () => {
    const r = runner({
      netstat: '  TCP    127.0.0.1:17321        0.0.0.0:0              LISTENING       4242\r\n',
      tasklist: '"astra-server.exe","4242","Console","1","51,200 K"\r\n',
    });
    expect(await findServerListenerPid(17321, 'win32', r.run)).toBe(4242);
    expect(r.calls[1]).toBe('tasklist /FI PID eq 4242 /FO CSV /NH');
  });
});
