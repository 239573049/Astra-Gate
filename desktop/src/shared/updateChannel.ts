import * as fs from 'node:fs';
import * as path from 'node:path';
import { DEFAULT_CHANNEL, DEFAULT_FEED_URL, type UpdateChannel } from '@aidotnet/update-core';

/**
 * The DMG track's electron-updater feed per release channel. Pure logic plus one file read, no
 * Electron imports, so it stays unit-testable (see tests/updateChannel.test.ts).
 *
 * electron-builder bakes the stable channel's directory into app-update.yml (`ASTRA_FEED_URL` in
 * release.yml, e.g. https://astra-gate.si/api/client-releases/stable). The preview channel is the
 * sibling directory; the docs site serves latest-mac.yml in both.
 */
const CHANNEL_SEGMENT = /\/(stable|preview|beta)\/*$/;

/**
 * The feed URL for `channel`, derived from the baked one by swapping its channel segment. Without a
 * baked URL it falls back to the built-in feed; a baked URL without a channel segment cannot be
 * switched, so only stable gets it back (null for preview).
 */
export function channelFeedUrl(bakedUrl: string | null, channel: UpdateChannel): string | null {
  const base = bakedUrl?.trim() || `${DEFAULT_FEED_URL}/${DEFAULT_CHANNEL}`;
  if (CHANNEL_SEGMENT.test(base)) return base.replace(CHANNEL_SEGMENT, `/${channel}`);
  return channel === DEFAULT_CHANNEL ? base : null;
}

/** The `url:` electron-builder wrote into `<resources>/app-update.yml`, or null when absent/unreadable. */
export function readBakedFeedUrl(resourcesPath: string | undefined): string | null {
  if (!resourcesPath) return null;
  try {
    const text = fs.readFileSync(path.join(resourcesPath, 'app-update.yml'), 'utf8');
    const m = /^url:\s*['"]?([^'"\s]+)['"]?\s*$/m.exec(text);
    return m?.[1] ?? null;
  } catch {
    return null;
  }
}
