import { describe, expect, it } from 'vitest';

import { EXPECTED_API_MAJOR, isApiVersionCompatible, majorOf } from '../src/shared/version';

describe('majorOf', () => {
  it('extracts the major segment', () => {
    expect(majorOf('1.0')).toBe(1);
    expect(majorOf('1.99.3')).toBe(1);
    expect(majorOf('2')).toBe(2);
    expect(majorOf('10.0')).toBe(10);
    expect(majorOf('')).toBeNull();
    expect(majorOf('v1.0')).toBeNull();
    expect(majorOf('abc')).toBeNull();
  });
});

describe('isApiVersionCompatible', () => {
  it('accepts the expected major family', () => {
    expect(isApiVersionCompatible('1.0')).toBe(true);
    expect(isApiVersionCompatible('1.7')).toBe(true);
    expect(isApiVersionCompatible('1')).toBe(true);
    expect(isApiVersionCompatible('1.0', 1)).toBe(true);
  });

  it('rejects other majors and garbage', () => {
    expect(isApiVersionCompatible('2.0')).toBe(false);
    expect(isApiVersionCompatible('0.9')).toBe(false);
    expect(isApiVersionCompatible('')).toBe(false);
    expect(isApiVersionCompatible('unknown')).toBe(false);
  });

  it('uses major 1 by default', () => {
    expect(EXPECTED_API_MAJOR).toBe(1);
  });
});
