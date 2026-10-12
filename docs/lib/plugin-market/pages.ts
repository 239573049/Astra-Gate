import { findListing, summaryOf } from '@/lib/plugin-market/registry';
import { isPackageName } from '@/lib/plugin-market/store';
import type { RegistryListing } from '@/lib/plugin-market/types';
import { copy, type MarketLang } from '@/components/plugins/market';

/**
 * Shared halves of the market's four pages (/plugins and its detail page, and the /en pair). Server-side only:
 * it reaches the registry store, which uses node:fs — importing it from a client component breaks the build.
 */

/** The package a detail URL names (a catch-all, because a scoped name has a slash in it); null when it is no package. */
export async function packageFromParams(params: Promise<{ pkg: string[] }>): Promise<string | null> {
  const segments = (await params).pkg.map((s) => decodeURIComponent(s));
  const name = segments.join('/');
  return segments.length > 2 || !isPackageName(name) ? null : name;
}

export async function loadListing(params: Promise<{ pkg: string[] }>): Promise<RegistryListing | null> {
  const name = await packageFromParams(params);
  return name === null ? null : findListing(name);
}

/** What a detail page shows as its summary, and what its metadata says about it. */
export function detailText(plugin: RegistryListing, lang: MarketLang): { summary: string; description: string } {
  const summary = summaryOf(plugin, lang);
  return { summary, description: summary || `${plugin.package} — ${copy(lang).metadataTitle}` };
}
