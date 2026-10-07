import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { isRuntimeCurrent, parseRuntimeJson, readRuntimeInfo } from '../src/lib/runtime';

const valid = JSON.stringify({
  pid: 4242,
  port: 17321,
  host: '127.0.0.1',
  version: '0.1.0',
  apiVersion: '1.0',
  startedBy: 'cli',
  startedAt: '2026-10-06T00:00:00.000Z',
  runtimeToken: 'secret-token',
});

describe('parseRuntimeJson', () => {
  it('parses a complete runtime.json', () => {
    const rt = parseRuntimeJson(valid);
    expect(rt).not.toBeNull();
    expect(rt?.pid).toBe(4242);
    expect(rt?.port).toBe(17321);
    expect(rt?.host).toBe('127.0.0.1');
    expect(rt?.version).toBe('0.1.0');
    expect(rt?.apiVersion).toBe('1.0');
    expect(rt?.startedBy).toBe('cli');
    expect(rt?.runtimeToken).toBe('secret-token');
  });

  it('accepts the minimal shape', () => {
    const rt = parseRuntimeJson('{"pid":1,"port":80,"host":"0.0.0.0"}');
    expect(rt).toEqual({ pid: 1, port: 80, host: '0.0.0.0' });
  });

  it('returns null for garbage, wrong types and out-of-range values', () => {
    expect(parseRuntimeJson('not json')).toBeNull();
    expect(parseRuntimeJson('[]')).toBeNull();
    expect(parseRuntimeJson('{"pid":"4242","port":1,"host":"h"}')).toBeNull();
    expect(parseRuntimeJson('{"pid":-1,"port":1,"host":"h"}')).toBeNull();
    expect(parseRuntimeJson('{"pid":1,"port":0,"host":"h"}')).toBeNull();
    expect(parseRuntimeJson('{"pid":1,"port":70000,"host":"h"}')).toBeNull();
    expect(parseRuntimeJson('{"pid":1,"port":80}')).toBeNull();
    expect(parseRuntimeJson('{"pid":1,"port":80,"host":""}')).toBeNull();
  });

  it('rejects unknown startedBy values', () => {
    const rt = parseRuntimeJson('{"pid":1,"port":80,"host":"h","startedBy":"cron"}');
    expect(rt?.startedBy).toBeUndefined();
  });
});

describe('isRuntimeCurrent', () => {
  const rt = parseRuntimeJson(valid)!;

  it('is current when the pid is alive', () => {
    expect(isRuntimeCurrent(rt, () => true)).toBe(true);
  });

  it('is stale when the pid is dead', () => {
    expect(isRuntimeCurrent(rt, () => false)).toBe(false);
  });
});

describe('readRuntimeInfo', () => {
  it('reads from disk and treats a missing file as absent', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-runtime-'));
    try {
      expect(readRuntimeInfo(path.join(dir, 'runtime.json'))).toBeNull();
      const file = path.join(dir, 'runtime.json');
      fs.writeFileSync(file, valid);
      expect(readRuntimeInfo(file)?.pid).toBe(4242);
      fs.writeFileSync(file, '{corrupt');
      expect(readRuntimeInfo(file)).toBeNull();
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });
});
