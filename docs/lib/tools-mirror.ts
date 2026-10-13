import { createHash, randomUUID } from 'node:crypto';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';
import { Readable } from 'node:stream';

import { dataDir } from './releases';

/**
 * Download mirror for the helper programs Astra fetches for itself — today only cloudflared (remote access), whose
 * GitHub release downloads are slow or unreachable from some networks (mainland China in particular).
 *
 * A file not cached yet is streamed from GitHub straight through to the caller (Astra's client gives up on a source
 * that sends nothing for 20 s, so it must not wait for the whole download) while a copy is written to
 * `${DATA_DIR}/tools/cloudflared/<version>/<asset>`; the copy is kept only when its SHA-256 matches the digest GitHub
 * publishes for that asset, and later requests are served from it. Only the asset names Astra's server knows
 * (`CloudflaredRuntime.Asset` in src/Astra.Server/Tunnel/CloudflaredRuntime.cs) and release-shaped versions are
 * accepted, so this never turns into a general proxy. Astra's client checks its own pinned checksums on top, so the
 * mirror is not trusted either way.
 */

export const CLOUDFLARED_ASSETS = new Set([
  'cloudflared-darwin-arm64.tgz',
  'cloudflared-darwin-amd64.tgz',
  'cloudflared-linux-amd64',
  'cloudflared-linux-arm64',
  'cloudflared-windows-amd64.exe',
]);

const MAX_BYTES = 120 * 1024 * 1024;
const RELEASES = 'https://github.com/cloudflare/cloudflared/releases';
const API = 'https://api.github.com/repos/cloudflare/cloudflared/releases/tags';

/** cloudflared's release versions look like 2026.10.0. */
export function isCloudflaredVersion(v: string): boolean {
  return /^20\d{2}\.\d{1,2}\.\d{1,3}$/.test(v);
}

export function cloudflaredFile(version: string, asset: string): string {
  return path.join(dataDir(), 'tools', 'cloudflared', version, asset);
}

/** Files being cached right now; a second request meanwhile is streamed from GitHub without writing another copy. */
const caching = new Set<string>();

export interface MirrorBody {
  body: ReadableStream<Uint8Array>;
  size: number | null;
}

/**
 * The asset's bytes: from the cache, or streamed from GitHub (caching a verified copy on the way).
 * Null when GitHub has no such release / asset.
 */
export async function openCloudflared(version: string, asset: string): Promise<MirrorBody | null> {
  const target = cloudflaredFile(version, asset);
  try {
    const st = await fsp.stat(target);
    if (st.isFile()) return { body: Readable.toWeb(fs.createReadStream(target)) as ReadableStream<Uint8Array>, size: st.size };
  } catch {
    // not cached yet
  }

  // The digest first: it also proves the release and asset exist before anything is fetched.
  const want = await publishedDigest(version, asset);
  if (!want) return null;
  const res = await fetch(`${RELEASES}/download/${version}/${asset}`, {
    headers: { 'user-agent': 'astra-gate-docs' },
    redirect: 'follow',
    signal: AbortSignal.timeout(10 * 60_000),
  });
  if (!res.ok || !res.body) return null;
  const length = res.headers.get('content-length');
  const size = length ? Number(length) : null;
  if (size !== null && size > MAX_BYTES) return null;
  if (caching.has(target)) return { body: res.body, size };

  // One branch goes to the caller, the other into the cache; the cache copy finishes even if the caller hangs up.
  const [toCaller, toCache] = res.body.tee();
  caching.add(target);
  void writeVerified(toCache, target, want).finally(() => caching.delete(target));
  return { body: toCaller, size };
}

/** GitHub's published sha256 for the asset ("sha256:<hex>" on the release API); null when it can't be had. */
async function publishedDigest(version: string, asset: string): Promise<string | null> {
  const headers: Record<string, string> = { accept: 'application/vnd.github+json', 'user-agent': 'astra-gate-docs' };
  const token = process.env.GITHUB_TOKEN?.trim();
  if (token) headers.authorization = `Bearer ${token}`;
  try {
    const res = await fetch(`${API}/${version}`, { headers, signal: AbortSignal.timeout(20_000) });
    if (!res.ok) return null;
    const release = (await res.json()) as { assets?: { name?: string; digest?: string | null }[] };
    const digest = release.assets?.find((a) => a.name === asset)?.digest;
    return digest?.startsWith('sha256:') ? digest.slice(7).toLowerCase() : null;
  } catch {
    return null;
  }
}

/** Writes the stream to a temporary file and moves it into place only when the whole thing matches `want`. */
async function writeVerified(stream: ReadableStream<Uint8Array>, target: string, want: string): Promise<void> {
  await fsp.mkdir(path.dirname(target), { recursive: true });
  const part = `${target}.${randomUUID()}.part`;
  const hash = createHash('sha256');
  let size = 0;
  try {
    const out = fs.createWriteStream(part);
    const reader = stream.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > MAX_BYTES) throw new Error('too large');
      hash.update(value);
      if (!out.write(value)) await new Promise((resolve) => out.once('drain', resolve));
    }
    await new Promise<void>((resolve, reject) => out.end((err?: Error | null) => (err ? reject(err) : resolve())));
    if (hash.digest('hex') !== want) throw new Error('checksum mismatch');
    await fsp.rename(part, target);
  } catch {
    await fsp.rm(part, { force: true });
  }
}
