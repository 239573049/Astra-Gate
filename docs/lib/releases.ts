import { createHash, timingSafeEqual } from 'node:crypto';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';

/**
 * Client-release storage for the official website (plan: docs 改造).
 *
 * Uploads land under `${DATA_DIR}/client-releases/<channel>/<version>/` and an
 * `index.json` beside the version directories records every file's hashes and
 * metadata. The directory is designed to be a single mounted volume in the
 * docs container (`-v …:/app/data`), so releases survive image upgrades.
 */

export const DEFAULT_CHANNEL = 'stable';
const INDEX_NAME = 'index.json';
const MAX_NOTES_BYTES = 64 * 1024;

export function dataDir(): string {
  return process.env.DATA_DIR?.trim() || '/app/data';
}

export function releasesRoot(channel: string): string {
  return path.join(dataDir(), 'client-releases', channel);
}

export function uploadToken(): string | null {
  return process.env.CLIENT_UPLOAD_TOKEN?.trim() || null;
}

/** Constant-time bearer-token check; uploads are disabled when no token is configured. */
export function verifyUploadToken(req: Request): { ok: boolean; status: number; error: string } {
  const expected = uploadToken();
  if (!expected) return { ok: false, status: 503, error: 'Client uploads are disabled (CLIENT_UPLOAD_TOKEN is not configured).' };
  const given = (req.headers.get('authorization') ?? '').replace(/^Bearer\s+/i, '').trim();
  if (!given) return { ok: false, status: 401, error: 'Missing bearer token.' };
  const a = Buffer.from(given);
  const b = Buffer.from(expected);
  const ok = a.length === b.length && timingSafeEqual(a, b);
  return ok
    ? { ok: true, status: 200, error: '' }
    : { ok: false, status: 401, error: 'Invalid bearer token.' };
}

export interface ReleaseFile {
  /** Canonical file name on disk, e.g. "Astra-0.2.0-darwin-arm64.dmg". */
  name: string;
  /** Free-form platform tag from the uploader, e.g. "darwin-arm64". */
  platform: string;
  /** Artifact kind: dmg | zip | exe … (drives the file extension). */
  kind: string;
  size: number;
  sha256: string;
  /** base64 — electron-updater's latest-mac.yml wants sha512 in base64. */
  sha512: string;
}

export interface ReleaseVersion {
  version: string;
  releasedAt: string;
  notes?: string | null;
  files: ReleaseFile[];
}

export interface ReleaseIndex {
  channel: string;
  versions: Record<string, ReleaseVersion>;
}

const EXT_BY_KIND: Record<string, string> = { dmg: 'dmg', zip: 'zip', exe: 'exe', appimage: 'AppImage', deb: 'deb', json: 'json' };

/** Only [A-Za-z0-9._-] survives; everything else becomes '-'. */
export function sanitizeToken(value: string, maxLength = 128): string {
  return value.replace(/[^A-Za-z0-9._-]+/g, '-').replace(/^[-.]+|[-.]+$/g, '').slice(0, maxLength);
}

export function isValidVersion(version: string): boolean {
  return /^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/.test(version);
}

export function canonicalFileName(version: string, platform: string, kind: string): string {
  const ext = EXT_BY_KIND[kind] ?? 'bin';
  return `Astra-${version}-${platform}.${ext}`;
}

export function resolveVersionDir(channel: string, version: string): string {
  const dir = path.join(releasesRoot(channel), sanitizeToken(version));
  // Defense in depth: the version segment must stay inside the channel dir.
  if (path.relative(releasesRoot(channel), dir).startsWith('..')) throw new Error('invalid version');
  return dir;
}

export async function readIndex(channel: string): Promise<ReleaseIndex> {
  try {
    const raw = await fsp.readFile(path.join(releasesRoot(channel), INDEX_NAME), 'utf8');
    const parsed = JSON.parse(raw) as ReleaseIndex;
    if (parsed && typeof parsed === 'object' && parsed.versions && typeof parsed.versions === 'object') return parsed;
  } catch {
    /* missing or corrupt index = empty channel */
  }
  return { channel, versions: {} };
}

export async function writeIndex(channel: string, index: ReleaseIndex): Promise<void> {
  const root = releasesRoot(channel);
  await fsp.mkdir(root, { recursive: true });
  const target = path.join(root, INDEX_NAME);
  const tmp = `${target}.tmp`;
  await fsp.writeFile(tmp, `${JSON.stringify(index, null, 2)}\n`);
  await fsp.rename(tmp, target);
}

export interface SaveUploadOptions {
  channel: string;
  version: string;
  platform: string;
  kind: string;
  notesB64?: string | null;
  body: ReadableStream<Uint8Array>;
}

export interface SaveUploadResult {
  file: ReleaseFile;
  version: ReleaseVersion;
}

/**
 * Streams the upload to a temp file while computing both hashes, then renames
 * it into place and merges the file into the channel index. A failed upload
 * never leaves a partial file or a mutated index behind.
 */
export async function saveUpload(o: SaveUploadOptions): Promise<SaveUploadResult> {
  const dir = resolveVersionDir(o.channel, o.version);
  await fsp.mkdir(dir, { recursive: true });
  const name = canonicalFileName(o.version, sanitizeToken(o.platform, 64), sanitizeToken(o.kind, 16));
  const target = path.join(dir, name);
  const tmp = `${target}.tmp`;

  const sha256 = createHash('sha256');
  const sha512 = createHash('sha512');
  const source = Readable.fromWeb(o.body as Parameters<typeof Readable.fromWeb>[0]);
  try {
    await pipeline(source, async function* (chunks) {
      for await (const chunk of chunks) {
        const buf = chunk as Buffer;
        sha256.update(buf);
        sha512.update(buf);
        yield buf;
      }
    }, fs.createWriteStream(tmp));
  } catch (err) {
    await fsp.rm(tmp, { force: true });
    throw err;
  }
  await fsp.rename(tmp, target);
  const stat = await fsp.stat(target);

  const file: ReleaseFile = {
    name,
    platform: sanitizeToken(o.platform, 64),
    kind: sanitizeToken(o.kind, 16),
    size: stat.size,
    sha256: sha256.digest('hex'),
    sha512: sha512.digest('base64'),
  };

  const index = await readIndex(o.channel);
  const notes = o.notesB64 ? Buffer.from(o.notesB64, 'base64').toString('utf8').slice(0, MAX_NOTES_BYTES) : undefined;
  const existing = index.versions[o.version];
  const version: ReleaseVersion = {
    version: o.version,
    releasedAt: existing?.releasedAt ?? new Date().toISOString(),
    notes: notes ?? existing?.notes ?? null,
    files: [...(existing?.files ?? []).filter((f) => f.name !== name), file].sort((a, b) => a.name.localeCompare(b.name)),
  };
  index.versions[o.version] = version;
  await writeIndex(o.channel, index);
  return { file, version };
}

export function latestVersion(index: ReleaseIndex): ReleaseVersion | null {
  const versions = Object.values(index.versions);
  if (versions.length === 0) return null;
  return versions.sort((a, b) => compareVersions(b.version, a.version))[0] ?? null;
}

/** Numeric semver-ish ordering; suffixes ignored, unparsable entries sink. */
export function compareVersions(a: string, b: string): number {
  const parse = (v: string): [number, number, number] => {
    const m = /^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?/.exec(v.trim());
    if (!m) return [0, 0, 0];
    return [Number(m[1]), Number(m[2] ?? 0), Number(m[3] ?? 0)];
  };
  const pa = parse(a);
  const pb = parse(b);
  for (let i = 0; i < 3; i++) {
    if (pa[i] !== pb[i]) return (pa[i] ?? 0) > (pb[i] ?? 0) ? 1 : -1;
  }
  return a < b ? -1 : a > b ? 1 : 0;
}

/** electron-updater generic feed for macOS: latest-mac.yml next to the feed URL. */
export function renderLatestMacYml(version: ReleaseVersion): string | null {
  const zips = version.files.filter((f) => f.kind === 'zip');
  if (zips.length === 0) return null;
  const first = zips[0]!;
  const lines: string[] = [
    `version: ${version.version}`,
    'files:',
    ...zips.map(
      (f) =>
        `  - url: ${f.name}\n    sha512: ${f.sha512}\n    size: ${f.size}`,
    ),
    `path: ${first.name}`,
    `sha512: ${first.sha512}`,
    `releaseDate: ${version.releasedAt}`,
  ];
  if (version.notes) {
    lines.push('releaseNotes: |-');
    for (const line of version.notes.replace(/\r\n/g, '\n').split('\n')) {
      lines.push(`  ${line}`);
    }
  }
  return `${lines.join('\n')}\n`;
}
