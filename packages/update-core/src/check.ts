import { DEFAULT_CHANNEL, DEFAULT_FEED_URL, fetchManifest, type UpdateManifest } from './manifest.js';
import { isNewerVersion } from './version.js';

export interface ServerUpdateStatus {
  current?: string;
  available?: string | null;
  lastCheckAt?: string | null;
  notes?: string | null;
  error?: string | null;
  channel?: string;
  autoCheck?: boolean;
  feedConfigured?: boolean;
}

/**
 * Checks for a server update. Prefers the running server's /api/update/status
 * (it caches feed results and works even when the feed is unreachable from the
 * client machine); falls back to fetching the manifest directly.
 */
export async function checkForServerUpdate(o: {
  currentVersion: string;
  /** Base URL of the running server, e.g. http://127.0.0.1:17321. */
  baseUrl?: string;
  feedUrl?: string;
  channel?: string;
  timeoutMs?: number;
  fetchImpl?: typeof fetch;
  userAgent?: string;
}): Promise<{ availableVersion: string | null; notes: string | null; source: 'server' | 'feed'; manifest?: UpdateManifest }> {
  const doFetch = o.fetchImpl ?? globalThis.fetch;
  if (o.baseUrl && doFetch) {
    try {
      const res = await doFetch(`${o.baseUrl.replace(/\/+$/, '')}/api/update/status`, {
        signal: AbortSignal.timeout(o.timeoutMs ?? 3_000),
      });
      if (res.ok) {
        const status = (await res.json()) as ServerUpdateStatus;
        return {
          availableVersion: status.available ?? null,
          notes: status.notes ?? null,
          source: 'server',
        };
      }
    } catch {
      /* fall through to the feed */
    }
  }

  const manifest = await fetchManifest({
    feedUrl: o.feedUrl ?? DEFAULT_FEED_URL,
    channel: o.channel ?? DEFAULT_CHANNEL,
    timeoutMs: o.timeoutMs,
    fetchImpl: o.fetchImpl,
    userAgent: o.userAgent,
  });
  return {
    availableVersion: isNewerVersion(manifest.version, o.currentVersion) ? manifest.version : null,
    notes: manifest.notes ?? null,
    source: 'feed',
    manifest,
  };
}
