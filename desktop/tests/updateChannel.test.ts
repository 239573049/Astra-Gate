import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { channelFeedUrl, readBakedFeedUrl } from '../src/shared/updateChannel';

describe('channelFeedUrl', () => {
  const baked = 'https://astra-gate.si/api/client-releases/stable';

  it('swaps the channel segment of the baked feed', () => {
    expect(channelFeedUrl(baked, 'stable')).toBe(baked);
    expect(channelFeedUrl(baked, 'preview')).toBe('https://astra-gate.si/api/client-releases/preview');
    expect(channelFeedUrl('https://feed.example.com/astra/preview/', 'stable')).toBe('https://feed.example.com/astra/stable');
  });

  it('falls back to the built-in feed and refuses to guess for a feed without a channel', () => {
    expect(channelFeedUrl(null, 'preview')).toBe('https://astra-gate.si/api/client-releases/preview');
    expect(channelFeedUrl('https://feed.example.com/astra', 'stable')).toBe('https://feed.example.com/astra');
    expect(channelFeedUrl('https://feed.example.com/astra', 'preview')).toBeNull();
  });
});

describe('readBakedFeedUrl', () => {
  it("reads electron-builder's app-update.yml", () => {
    const dir = mkdtempSync(join(tmpdir(), 'astra-feed-'));
    try {
      writeFileSync(
        join(dir, 'app-update.yml'),
        'provider: generic\nurl: https://astra-gate.si/api/client-releases/stable\nupdaterCacheDirName: astra-updater\n',
      );
      expect(readBakedFeedUrl(dir)).toBe('https://astra-gate.si/api/client-releases/stable');
      expect(readBakedFeedUrl(join(dir, 'missing'))).toBeNull();
      expect(readBakedFeedUrl(undefined)).toBeNull();
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
