import { describe, expect, it } from 'vitest';

import * as path from 'node:path';

import { astraPaths, logFileName } from '../src/shared/paths';

describe('astraPaths', () => {
  it('defaults to ~/.astra', () => {
    const p = astraPaths({}, '/Users/alice');
    expect(p.home).toBe(path.join('/Users/alice', '.astra'));
    expect(p.configFile).toBe(path.join(p.home, 'config.json'));
    expect(p.runtimeFile).toBe(path.join(p.home, 'runtime.json'));
    expect(p.installFile).toBe(path.join(p.home, 'install.json'));
    expect(p.logsDir).toBe(path.join(p.home, 'logs'));
  });

  it('honors ASTRA_HOME override', () => {
    const p = astraPaths({ ASTRA_HOME: '/tmp/astra-alt' }, '/Users/alice');
    expect(p.home).toBe(path.resolve('/tmp/astra-alt'));
    expect(p.runtimeFile).toBe(path.join(path.resolve('/tmp/astra-alt'), 'runtime.json'));
  });

  it('ignores blank ASTRA_HOME', () => {
    const p = astraPaths({ ASTRA_HOME: '   ' }, '/Users/alice');
    expect(p.home).toBe(path.join('/Users/alice', '.astra'));
  });

  it('builds the per-day log file path', () => {
    const p = astraPaths({ ASTRA_HOME: '/tmp/lg' }, '/Users/alice');
    expect(p.logFile(new Date(2026, 9, 6, 12, 0, 0))).toBe(path.join('/tmp/lg', 'logs', 'astra-20261006.log'));
  });
});

describe('logFileName', () => {
  it('zero-pads month and day', () => {
    expect(logFileName(new Date(2026, 0, 3))).toBe('astra-20260103.log');
    expect(logFileName(new Date(2026, 11, 31))).toBe('astra-20261231.log');
  });
});
