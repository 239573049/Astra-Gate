import { describe, expect, it } from 'vitest';
import { isApiVersionCompatible, isNewerVersion, majorOf, parseVersion } from '../src/version.js';

describe('parseVersion', () => {
  it('parses semver cores and ignores suffixes', () => {
    expect(parseVersion('1.2.3')).toEqual([1, 2, 3]);
    expect(parseVersion('0.2.0-rc.1+build.5')).toEqual([0, 2, 0]);
    expect(parseVersion('v2')).toEqual([2, 0, 0]);
    expect(parseVersion('1.4')).toEqual([1, 4, 0]);
    expect(parseVersion('not-a-version')).toBeNull();
    expect(parseVersion(null)).toBeNull();
    expect(parseVersion('')).toBeNull();
  });
});

describe('isNewerVersion', () => {
  it('orders versions numerically', () => {
    expect(isNewerVersion('0.2.0', '0.1.0')).toBe(true);
    expect(isNewerVersion('0.10.0', '0.9.0')).toBe(true);
    expect(isNewerVersion('1.0.0', '1.0.0')).toBe(false);
    expect(isNewerVersion('0.0.9', '0.1.0')).toBe(false);
    expect(isNewerVersion('0.1.0-rc.1', '0.1.0')).toBe(false);
    expect(isNewerVersion(null, '0.1.0')).toBe(false);
    expect(isNewerVersion('garbage', '0.1.0')).toBe(false);
    expect(isNewerVersion('0.2.0', 'garbage')).toBe(false);
  });
});

describe('majorOf / isApiVersionCompatible', () => {
  it('extracts the major and gates compatibility', () => {
    expect(majorOf('1.4')).toBe(1);
    expect(majorOf('2')).toBe(2);
    expect(majorOf('nope')).toBeNull();
    expect(isApiVersionCompatible('1.0', 1)).toBe(true);
    expect(isApiVersionCompatible('1.9', 1)).toBe(true);
    expect(isApiVersionCompatible('2.0', 1)).toBe(false);
    expect(isApiVersionCompatible(null, 1)).toBe(false);
  });
});
