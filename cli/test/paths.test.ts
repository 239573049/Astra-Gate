import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { homePaths, astraHome } from '../src/lib/paths';
import { normalizeHost, normalizePort, readConfig, writeConfig } from '../src/lib/config-file';

describe('astraHome', () => {
  it('defaults to ~/.astra', () => {
    expect(astraHome({})).toBe(path.join(os.homedir(), '.astra'));
  });

  it('honours ASTRA_HOME', () => {
    expect(astraHome({ ASTRA_HOME: '/tmp/astra-test' })).toBe('/tmp/astra-test');
    expect(astraHome({ ASTRA_HOME: '   ' })).toBe(path.join(os.homedir(), '.astra'));
  });
});

describe('homePaths', () => {
  it('derives the standard layout', () => {
    const p = homePaths('/h');
    expect(p.configFile).toBe('/h/config.json');
    expect(p.runtimeFile).toBe('/h/runtime.json');
    expect(p.installFile).toBe('/h/install.json');
    expect(p.serverStdoutLog).toBe('/h/logs/server-stdout.log');
    expect(p.desktopPrefix).toBe('/h/desktop');
  });
});

describe('config helpers', () => {
  it('normalizes ports and hosts', () => {
    expect(normalizePort(undefined)).toBe(17321);
    expect(normalizePort('18000')).toBe(18000);
    expect(() => normalizePort('99999')).toThrow(/Invalid port/);
    expect(() => normalizePort('abc')).toThrow(/Invalid port/);
    expect(normalizeHost('')).toBe('127.0.0.1');
    expect(normalizeHost('0.0.0.0')).toBe('0.0.0.0');
  });

  it('round-trips config.json', () => {
    const dir = os.tmpdir();
    writeConfig(dir, { port: 18000, host: '127.0.0.1', logLevel: 'info' });
    const cfg = readConfig(dir);
    expect(cfg.port).toBe(18000);
    expect(cfg.host).toBe('127.0.0.1');
    expect(cfg.logLevel).toBe('info');
  });
});
