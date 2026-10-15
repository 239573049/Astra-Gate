import { describe, expect, it } from 'vitest';
import { compareVersions, isApiVersionCompatible, isNewerVersion, isPreviewVersion, majorOf, parseVersion } from '../src/version.js';
import { normalizeChannel } from '../src/manifest.js';

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

  it('follows semver precedence for previews', () => {
    // A release beats its previews; previews order by number, not as text.
    expect(isNewerVersion('0.10.0', '0.10.0-preview.3')).toBe(true);
    expect(isNewerVersion('0.10.0-preview.3', '0.10.0')).toBe(false);
    expect(isNewerVersion('0.10.0-preview.2', '0.10.0-preview.1')).toBe(true);
    expect(isNewerVersion('0.10.0-preview.10', '0.10.0-preview.9')).toBe(true);
    expect(isNewerVersion('0.10.0-preview.1', '0.9.5')).toBe(true);
    // Leaving the preview channel never downgrades.
    expect(isNewerVersion('0.9.5', '0.10.0-preview.1')).toBe(false);
    expect(isNewerVersion('0.10.0+build.2', '0.10.0')).toBe(false);
  });
});

describe('compareVersions', () => {
  it('orders like semver and rejects garbage', () => {
    const sorted = ['0.10.0', '0.9.5', '0.10.0-preview.10', '0.10.0-preview.2', '0.10.1-preview.1'].sort(
      (a, b) => compareVersions(a, b)!,
    );
    expect(sorted).toEqual(['0.9.5', '0.10.0-preview.2', '0.10.0-preview.10', '0.10.0', '0.10.1-preview.1']);
    expect(compareVersions('1.0.0-alpha', '1.0.0-alpha.1')).toBeLessThan(0);
    expect(compareVersions('1.0.0-alpha.1', '1.0.0-alpha.beta')).toBeLessThan(0);
    expect(compareVersions('1.0.0', '1.0.0')).toBe(0);
    expect(compareVersions('garbage', '1.0.0')).toBeNull();
  });
});

describe('preview channel helpers', () => {
  it('recognizes preview versions and channel names', () => {
    expect(isPreviewVersion('0.10.0-preview.1')).toBe(true);
    expect(isPreviewVersion('0.10.0')).toBe(false);
    expect(isPreviewVersion('0.10.0-rc.1')).toBe(false);
    expect(normalizeChannel('preview')).toBe('preview');
    expect(normalizeChannel(' Beta ')).toBe('preview');
    expect(normalizeChannel('stable')).toBe('stable');
    expect(normalizeChannel(undefined)).toBe('stable');
    expect(normalizeChannel('canary')).toBe('stable');
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
