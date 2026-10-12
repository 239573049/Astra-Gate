import { createHash } from 'node:crypto';

import { compareVersions } from '@/lib/releases';
import { readEntries, sortedVersions } from './store';
import { loadUpstream } from './upstream';
import type { EntryMeta, PublishedEntry, RegistryDocument, RegistryListing } from './types';

function definedOnly<T extends object>(o: T): Partial<T> {
  return Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined)) as Partial<T>;
}

function fromPublished(entry: PublishedEntry, upstream?: RegistryListing): RegistryListing {
  const { versions, package: pkg, publishedAt, updatedAt, ...meta } = entry;
  const tarballs = sortedVersions(versions);
  const base = upstream ? { ...upstream } : undefined;
  return {
    ...base,
    ...definedOnly<EntryMeta>(meta),
    package: pkg,
    name: meta.name ?? base?.name ?? pkg,
    kind: meta.kind ?? base?.kind ?? 'provider',
    providers: meta.providers ?? base?.providers ?? [],
    summary: { ...base?.summary, ...meta.summary },
    origin: 'astra',
    latest: tarballs[0]?.version ?? base?.latest,
    publishedAt,
    updatedAt,
    ...(tarballs.length > 0 ? { tarballs } : {}),
  };
}

/** Upstream entries, overridden by (and merged with) what is published here; sorted for stable output. */
export async function buildListings(): Promise<RegistryListing[]> {
  const [upstream, published] = await Promise.all([loadUpstream(), readEntries()]);
  const byPackage = new Map<string, RegistryListing>();
  for (const u of upstream) byPackage.set(u.package, { ...u });
  for (const entry of Object.values(published.entries)) {
    byPackage.set(entry.package, fromPublished(entry, byPackage.get(entry.package)));
  }
  return [...byPackage.values()].sort((a, b) => {
    if (a.origin !== b.origin) return a.origin === 'astra' ? -1 : 1;
    return a.package.localeCompare(b.package);
  });
}

export async function buildRegistry(): Promise<{ doc: RegistryDocument; etag: string }> {
  const plugins = await buildListings();
  // the ETag covers content only, so an unchanged registry answers 304 whatever the time is
  const etag = `"${createHash('sha256').update(JSON.stringify(plugins)).digest('hex').slice(0, 32)}"`;
  return { doc: { version: 2, generatedAt: new Date().toISOString(), plugins }, etag };
}

export async function findListing(pkg: string): Promise<RegistryListing | null> {
  return (await buildListings()).find((l) => l.package === pkg) ?? null;
}

/** Whether `a` is a newer version than `b`. */
export function isNewer(a: string, b: string): boolean {
  return compareVersions(a, b) > 0;
}

/** Display text for a listing, Chinese first (the site's pages are Chinese). */
export function summaryOf(l: Pick<RegistryListing, 'summary' | 'description'>, lang: 'zh' | 'en' = 'zh'): string {
  return l.summary[lang] ?? l.summary.en ?? l.summary.zh ?? l.description ?? '';
}
