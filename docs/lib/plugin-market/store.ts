import { timingSafeEqual } from 'node:crypto';
import fsp from 'node:fs/promises';
import path from 'node:path';

import { compareVersions, dataDir } from '@/lib/releases';
import type { EntriesFile, HostedTarball, PublishedEntry } from './types';

/**
 * Storage for the plugin market under `${DATA_DIR}/plugin-market/`:
 *
 *   entries.json                      entries published to this site
 *   packages/<dirName>/<version>.tgz  hosted tarballs (versions are immutable)
 *   upstream.json                     cached upstream registry (see upstream.ts)
 */

export const MAX_TARBALL_BYTES = 50 * 1024 * 1024;

/** Same rule as PluginSpec.IsPackageName on the client. */
const PACKAGE_NAME = /^(@[a-z0-9][a-z0-9._~-]*\/)?[a-z0-9][a-z0-9._~-]*$/;
const SEMVER = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;

export function isPackageName(name: string): boolean {
  return name.length <= 214 && PACKAGE_NAME.test(name);
}

export function isSemver(version: string): boolean {
  return version.length <= 64 && SEMVER.test(version);
}

export function marketRoot(): string {
  return path.join(dataDir(), 'plugin-market');
}

/** "@a/b" -> "@a__b". A scoped name starts with "@", an unscoped one can't contain it, so names never collide. */
export function packageDirName(pkg: string): string {
  return pkg.replace('/', '__');
}

/** Whether a URL segment is a dir name this module could have produced. */
export function isDirName(dir: string): boolean {
  return /^(@[a-z0-9][a-z0-9._~-]*__)?[a-z0-9][a-z0-9._~-]*$/.test(dir) && dir.length <= 220;
}

export function packageFile(dir: string, version: string): string {
  const root = path.join(marketRoot(), 'packages');
  const file = path.join(root, dir, `${version}.tgz`);
  // Defense in depth: validated segments already can't climb out of the packages dir.
  if (path.relative(root, file).startsWith('..')) throw new Error('invalid package path');
  return file;
}

export function packageUrl(pkg: string, version: string): string {
  return `/api/plugin-market/packages/${encodeURIComponent(packageDirName(pkg))}/${version}.tgz`;
}

/** Constant-time bearer check; the write API is disabled when PLUGIN_UPLOAD_TOKEN is unset. */
export function verifyPublishToken(req: Request): { ok: true } | { ok: false; status: number; error: string } {
  const expected = process.env.PLUGIN_UPLOAD_TOKEN?.trim();
  if (!expected) return { ok: false, status: 503, error: 'Plugin publishing is disabled (PLUGIN_UPLOAD_TOKEN is not configured).' };
  const given = (req.headers.get('authorization') ?? '').replace(/^Bearer\s+/i, '').trim();
  if (!given) return { ok: false, status: 401, error: 'Missing bearer token.' };
  const a = Buffer.from(given);
  const b = Buffer.from(expected);
  return a.length === b.length && timingSafeEqual(a, b)
    ? { ok: true }
    : { ok: false, status: 401, error: 'Invalid bearer token.' };
}

export async function readEntries(): Promise<EntriesFile> {
  try {
    const parsed = JSON.parse(await fsp.readFile(path.join(marketRoot(), 'entries.json'), 'utf8')) as EntriesFile;
    if (parsed && typeof parsed === 'object' && parsed.entries && typeof parsed.entries === 'object') return parsed;
  } catch {
    /* missing or corrupt = nothing published */
  }
  return { entries: {} };
}

async function writeEntries(file: EntriesFile): Promise<void> {
  const root = marketRoot();
  await fsp.mkdir(root, { recursive: true });
  const target = path.join(root, 'entries.json');
  const tmp = `${target}.tmp`;
  await fsp.writeFile(tmp, `${JSON.stringify(file, null, 2)}\n`);
  await fsp.rename(tmp, target);
}

// Read-modify-write of entries.json is serialized in this process (one docs container owns the volume).
let chain: Promise<unknown> = Promise.resolve();

export function updateEntries<T>(fn: (file: EntriesFile) => T | Promise<T>): Promise<T> {
  const run = chain.then(async () => {
    const file = await readEntries();
    const result = await fn(file);
    await writeEntries(file);
    return result;
  });
  chain = run.catch(() => undefined);
  return run;
}

/** Versions newest first. */
export function sortedVersions(versions: HostedTarball[]): HostedTarball[] {
  return [...versions].sort((a, b) => compareVersions(b.version, a.version));
}

export async function removePackageFile(dir: string, version: string): Promise<void> {
  await fsp.rm(packageFile(dir, version), { force: true });
}

export async function removePackageDir(dir: string): Promise<void> {
  const target = path.join(marketRoot(), 'packages', dir);
  if (path.relative(path.join(marketRoot(), 'packages'), target).startsWith('..')) return;
  await fsp.rm(target, { recursive: true, force: true });
}

export function emptyEntry(pkg: string, now: string): PublishedEntry {
  return { package: pkg, publishedAt: now, updatedAt: now, versions: [] };
}
