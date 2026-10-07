import { describe, expect, it } from 'vitest';

import { parseConfigJson } from '../src/shared/config';

describe('parseConfigJson', () => {
  it('returns defaults for missing or invalid input', () => {
    expect(parseConfigJson(null)).toEqual({ port: 17321, host: '127.0.0.1' });
    expect(parseConfigJson(undefined)).toEqual({ port: 17321, host: '127.0.0.1' });
    expect(parseConfigJson('not json {')).toEqual({ port: 17321, host: '127.0.0.1' });
    expect(parseConfigJson('[]')).toEqual({ port: 17321, host: '127.0.0.1' });
  });

  it('reads port and host', () => {
    expect(parseConfigJson('{"port": 18000, "host": "0.0.0.0"}')).toEqual({ port: 18000, host: '0.0.0.0' });
    expect(parseConfigJson('{"port": "18000"}')).toEqual({ port: 18000, host: '127.0.0.1' });
  });

  it('rejects invalid ports', () => {
    expect(parseConfigJson('{"port": 0}').port).toBe(17321);
    expect(parseConfigJson('{"port": -5}').port).toBe(17321);
    expect(parseConfigJson('{"port": "http"}').port).toBe(17321);
  });

  it('rejects invalid hosts', () => {
    expect(parseConfigJson('{"host": "  "}').host).toBe('127.0.0.1');
    expect(parseConfigJson('{"host": 42}').host).toBe('127.0.0.1');
  });
});
