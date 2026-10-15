import { describe, expect, it } from 'vitest';

import { spawnSync } from 'node:child_process';

import { isPidAlive } from '../src/shared/pid';
import { isRuntimeFrom, isRuntimeStale, parseRuntimeJson } from '../src/shared/runtime';
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

describe('isRuntimeFrom', () => {
  const spawnedAt = Date.parse('2026-10-15T02:56:47Z');

  it('accepts the entry the spawned pid wrote', () => {
    expect(isRuntimeFrom({ ...valid, pid: 18515 }, 18515, spawnedAt)).toBe(true);
  });

  it('accepts a server a wrapper started after the spawn', () => {
    expect(isRuntimeFrom({ ...valid, startedAt: '2026-10-15T02:56:49Z' }, 18515, spawnedAt)).toBe(true);
  });

  it('rejects an entry left by a server that was already running', () => {
    expect(isRuntimeFrom({ ...valid, startedAt: '2026-10-14T09:32:23Z' }, 18515, spawnedAt)).toBe(false);
    expect(isRuntimeFrom({ pid: 19419, port: 17321 }, 18515, spawnedAt)).toBe(false);
    expect(isRuntimeFrom({ pid: 19419, port: 17321 }, undefined, spawnedAt)).toBe(false);
  });
});
