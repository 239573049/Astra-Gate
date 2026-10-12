/**
 * Plugin market contract shared by the docs site's API routes and pages.
 * The client side of the same contract lives in src/Astra.Plugins/Market/PluginMarket.cs,
 * web/src/api/types.ts and cli/src/commands/plugin.ts — change them together (AGENTS.md).
 */

export type PluginKind = 'provider' | 'middleware' | 'agent' | 'pi';
export const PLUGIN_KINDS: readonly PluginKind[] = ['provider', 'middleware', 'agent', 'pi'];

/** One version of a package hosted by this site. */
export interface HostedTarball {
  version: string;
  /** Path relative to the registry document's URL, e.g. /api/plugin-market/packages/%40a__b/1.0.0.tgz */
  url: string;
  sha256: string;
  /** base64 */
  sha512: string;
  size: number;
  publishedAt: string;
}

/** The metadata an entry carries (published, or taken from upstream). */
export interface EntryMeta {
  name?: string;
  icon?: string;
  kind?: PluginKind;
  providers?: string[];
  replaces?: string;
  summary?: Record<string, string>;
  /** install spec when it is not the package name, e.g. github:owner/repo */
  spec?: string;
  description?: string;
  license?: string;
  author?: string;
  homepage?: string;
  repository?: string;
  tags?: string[];
  minAstraVersion?: string;
}

/** An entry published to this site (entries.json), keyed by package. */
export interface PublishedEntry extends EntryMeta {
  package: string;
  publishedAt: string;
  updatedAt: string;
  /** hosted versions, any order; the registry sorts them */
  versions: HostedTarball[];
}

export interface EntriesFile {
  entries: Record<string, PublishedEntry>;
}

/** What GET /api/plugin-market/registry.json lists per plugin. */
export interface RegistryListing extends EntryMeta {
  package: string;
  name: string;
  kind: PluginKind;
  providers: string[];
  summary: Record<string, string>;
  community?: boolean;
  origin: 'upstream' | 'astra';
  latest?: string;
  publishedAt?: string;
  updatedAt?: string;
  tarballs?: HostedTarball[];
}

export interface RegistryDocument {
  version: 2;
  generatedAt: string;
  plugins: RegistryListing[];
}

export interface SearchResult {
  spec: string;
  name: string;
  description?: string | null;
  version?: string | null;
  source: 'astra' | 'npm' | 'github';
  kind: PluginKind;
  stars: number;
}
