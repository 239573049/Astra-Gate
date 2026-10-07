import { describe, expect, it } from 'vitest';

import { apiBase, DEFAULT_PORT, normalizePort } from '../src/shared/port';

describe('normalizePort', () => {
  it('accepts valid integer ports', () => {
    expect(normalizePort(17321)).toBe(17321);
    expect(normalizePort(1)).toBe(1);
    expect(normalizePort(65535)).toBe(65535);
    expect(normalizePort('5173')).toBe(5173);
    expect(normalizePort(' 8080 ')).toBe(8080);
  });

  it('falls back on invalid values', () => {
    expect(normalizePort(0)).toBe(DEFAULT_PORT);
    expect(normalizePort(-1)).toBe(DEFAULT_PORT);
    expect(normalizePort(65536)).toBe(DEFAULT_PORT);
    expect(normalizePort(17321.5)).toBe(DEFAULT_PORT);
    expect(normalizePort('abc')).toBe(DEFAULT_PORT);
    expect(normalizePort('')).toBe(DEFAULT_PORT);
    expect(normalizePort(null)).toBe(DEFAULT_PORT);
    expect(normalizePort(undefined)).toBe(DEFAULT_PORT);
    expect(normalizePort({ port: 1 })).toBe(DEFAULT_PORT);
  });

  it('honors a custom fallback', () => {
    expect(normalizePort('nope', 9999)).toBe(9999);
    expect(normalizePort(0, 80)).toBe(80);
  });
});

describe('apiBase', () => {
  it('builds a loopback URL', () => {
    expect(apiBase(17321)).toBe('http://127.0.0.1:17321');
  });
});
