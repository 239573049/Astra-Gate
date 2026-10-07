import { describe, expect, it } from 'vitest';

import { spawnSync } from 'node:child_process';

import { isPidAlive } from '../src/shared/pid';
import { isRuntimeStale, parseRuntimeJson } from '../src/shared/runtime';
import type { RuntimeInfo } from '../src/shared/types';

const valid: RuntimeInfo = {
  pid: 1234,
  port: 17321,
  host: '127.0.0.1',
  version: '0.1.0',
  apiVersion: '1.0',
  startedBy: 'desktop',
  startedAt: '2026-10-06T00:00:00Z',
  runtimeToken: 'tok',
};

describe('parseRuntimeJson', () => {
  it('parses a full runtime.json', () => {
    expect(parseRuntimeJson(JSON.stringify(valid))).toEqual(valid);
  });

  it('defaults missing port and drops blank strings', () => {
    const parsed = parseRuntimeJson('{"pid": 42, "port": "20000", "version": ""}');
    expect(parsed).toEqual({ pid: 42, port: 20000 });
  });

  it('returns null on invalid input', () => {
    expect(parseRuntimeJson(null)).toBeNull();
    expect(parseRuntimeJson('')).toBeNull();
    expect(parseRuntimeJson('{')).toBeNull();
    expect(parseRuntimeJson('[]')).toBeNull();
    expect(parseRuntimeJson('{}')).toBeNull();
    expect(parseRuntimeJson('{"pid": "123"}')).toBeNull();
    expect(parseRuntimeJson('{"pid": -1}')).toBeNull();
    expect(parseRuntimeJson('{"pid": 12.5}')).toBeNull();
  });
});

describe('isRuntimeStale', () => {
  it('is stale when the pid is not alive', () => {
    expect(isRuntimeStale(valid, () => false)).toBe(true);
    expect(isRuntimeStale(valid, () => true)).toBe(false);
  });

  it('detects a dead pid for real', () => {
    const exited = spawnSync('true');
    const deadPid = exited.pid;
    if (deadPid === undefined) return; // skip on exotic platforms
    const rt: RuntimeInfo = { pid: deadPid, port: 1 };
    expect(isRuntimeStale(rt, isPidAlive)).toBe(true);
  });
});
