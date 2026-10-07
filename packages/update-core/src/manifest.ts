/**
 * The update feed manifest (latest.json). Same contract the Astra server
 * consumes in UpdateCheckService — camelCase JSON at
 * `<feedUrl>/<channel>/latest.json`.
 */
export interface UpdatePlatformInfo {
  /** npm package carrying the self-contained server binary for this platform. */
  server?: string;
  /** npm package carrying the desktop app (npm-track installs) for this platform. */
  desktop?: string;
  /** sha256 of the server binary inside the server package (hex). */
  serverSha256?: string;
  serverSize?: number;
}

export interface UpdateManifest {
  version: string;
  apiVersion?: string;
  releasedAt?: string;
  notes?: string | null;
  minUpdateable?: string;
  platforms?: Record<string, UpdatePlatformInfo>;
}

/** Built-in feed location; overridable via the server's updateFeedUrl setting. */
export const DEFAULT_FEED_URL = 'https://astra-gate.si/api/client-releases';

export const DEFAULT_CHANNEL = 'stable';

export interface FetchManifestOptions {
  feedUrl?: string;
  channel?: string;
  /** Identifies the caller without leaking anything else, e.g. "Astra/0.2.0". */
  userAgent?: string;
  timeoutMs?: number;
  fetchImpl?: typeof fetch;
}

export class UpdateFeedError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'UpdateFeedError';
  }
}

export async function fetchManifest(o: FetchManifestOptions = {}): Promise<UpdateManifest> {
  const feed = (o.feedUrl ?? DEFAULT_FEED_URL).trim();
  const channel = (o.channel ?? DEFAULT_CHANNEL).trim() || DEFAULT_CHANNEL;
  const url = `${feed.replace(/\/+$/, '')}/${channel}/latest.json`;
  const doFetch = o.fetchImpl ?? globalThis.fetch;
  if (!doFetch) throw new UpdateFeedError('fetch is unavailable (Node.js 18+ required).');
  let response: Response;
  try {
    response = await doFetch(url, {
      headers: {
        Accept: 'application/json',
        ...(o.userAgent ? { 'User-Agent': o.userAgent } : {}),
      },
      signal: AbortSignal.timeout(o.timeoutMs ?? 15_000),
    });
  } catch (err) {
    const reason = err instanceof Error ? (err.name === 'TimeoutError' || err.name === 'AbortError' ? 'timed out' : err.message) : String(err);
    throw new UpdateFeedError(`Cannot reach the update feed at ${url} (${reason}).`);
  }
  if (!response.ok) {
    throw new UpdateFeedError(`Update feed responded ${response.status} ${response.statusText} for ${url}.`);
  }
  let manifest: UpdateManifest;
  try {
    manifest = (await response.json()) as UpdateManifest;
  } catch (err) {
    throw new UpdateFeedError(`Update feed returned invalid JSON (${err instanceof Error ? err.message : String(err)}).`);
  }
  if (!manifest || typeof manifest.version !== 'string' || manifest.version.trim() === '') {
    throw new UpdateFeedError('Update manifest has no version.');
  }
  return manifest;
}
