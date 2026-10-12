import { kindOfKeywords } from './meta';
import { buildListings } from './registry';
import type { SearchResult } from './types';

const FRESH_MS = 10 * 60 * 1000;
const cache = new Map<string, { at: number; results: SearchResult[] }>();

async function searchNpm(query: string): Promise<SearchResult[]> {
  try {
    const url = `https://registry.npmjs.org/-/v1/search?size=25&text=${encodeURIComponent(`${query} opencode`)}`;
    const res = await fetch(url, { signal: AbortSignal.timeout(8000) });
    if (!res.ok) return [];
    const body = (await res.json()) as { objects?: { package?: Record<string, unknown> }[] };
    return (body.objects ?? []).flatMap((o) => {
      const p = o.package;
      if (!p || typeof p.name !== 'string' || p.name.length === 0) return [];
      return [{
        spec: p.name,
        name: p.name,
        description: typeof p.description === 'string' ? p.description : null,
        version: typeof p.version === 'string' ? p.version : null,
        source: 'npm' as const,
        kind: kindOfKeywords(p.keywords),
        stars: 0,
      }];
    });
  } catch {
    return [];
  }
}

async function searchGitHub(query: string): Promise<SearchResult[]> {
  try {
    const q = encodeURIComponent(`${query} topic:magpie-plugin archived:false fork:false`);
    const headers: Record<string, string> = { accept: 'application/vnd.github+json', 'user-agent': 'astra-plugin-market' };
    const token = process.env.GITHUB_TOKEN?.trim();
    if (token) headers.authorization = `Bearer ${token}`;
    const res = await fetch(`https://api.github.com/search/repositories?q=${q}&sort=stars&per_page=30`, {
      headers,
      signal: AbortSignal.timeout(8000),
    });
    if (!res.ok) return [];
    const body = (await res.json()) as { items?: Record<string, unknown>[] };
    return (body.items ?? []).flatMap((r) => {
      if (typeof r.full_name !== 'string' || r.full_name.length === 0) return [];
      return [{
        spec: `github:${r.full_name}`,
        name: typeof r.name === 'string' ? r.name : r.full_name,
        description: typeof r.description === 'string' ? r.description : null,
        version: null,
        source: 'github' as const,
        kind: kindOfKeywords(r.topics),
        stars: typeof r.stargazers_count === 'number' ? r.stargazers_count : 0,
      }];
    });
  } catch {
    return [];
  }
}

/** Entries of this market (published or community) whose name, package, tags or summary contain the query. */
async function searchMarket(query: string): Promise<SearchResult[]> {
  const q = query.toLowerCase();
  const listings = await buildListings();
  return listings
    .filter((l) =>
      [l.package, l.name, l.description ?? '', ...(l.tags ?? []), ...Object.values(l.summary)].some((s) => s.toLowerCase().includes(q)),
    )
    .map((l) => ({
      spec: l.spec ?? l.package,
      name: l.name,
      description: l.summary.en ?? l.description ?? null,
      version: l.latest ?? null,
      source: 'astra' as const,
      kind: l.kind,
      stars: 0,
    }));
}

/** Search for the client: this market's entries first, then npm and GitHub's magpie-plugin topic. 10 minutes cached. */
export async function searchPlugins(query: string): Promise<SearchResult[]> {
  const key = query.trim().toLowerCase();
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < FRESH_MS) return hit.results;
  const [own, npm, github] = await Promise.all([searchMarket(key), searchNpm(query.trim()), searchGitHub(query.trim())]);
  const seen = new Set(own.map((r) => r.spec));
  const results = [...own, ...npm.filter((r) => !seen.has(r.spec)), ...github];
  if (cache.size > 200) cache.clear();
  cache.set(key, { at: Date.now(), results });
  return results;
}
