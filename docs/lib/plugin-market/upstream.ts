import fsp from 'node:fs/promises';
import path from 'node:path';

import fallback from './upstream-fallback.json';
import { cleanMeta } from './meta';
import { isPackageName, marketRoot } from './store';
import { PLUGIN_KINDS, type PluginKind, type RegistryListing } from './types';

/**
 * magpie-community's registry.json, aggregated for the market: each entry gets npm's word on it (newest version,
 * description, license). Fetched at most every six hours; the copy fetched last (upstream.json) and then the
 * snapshot shipped with the site (upstream-fallback.json) stand in when upstream can't be reached.
 */

const DEFAULT_UPSTREAM = 'https://raw.githubusercontent.com/magpie-community/plugins/main/registry.json';
const FRESH_MS = 6 * 60 * 60 * 1000;
const RETRY_MS = 60 * 1000;
const NPM_REGISTRY = 'https://registry.npmjs.org';

interface UpstreamCache {
  fetchedAt: number;
  plugins: RegistryListing[];
}

let memory: UpstreamCache | null = null;
let inflight: Promise<RegistryListing[]> | null = null;

function cacheFile(): string {
  return path.join(marketRoot(), 'upstream.json');
}

function upstreamUrl(): string | null {
  const v = process.env.PLUGIN_MARKET_UPSTREAM?.trim();
  if (v === 'off') return null;
  return v || DEFAULT_UPSTREAM;
}

/** Community entries only, each a valid npm package. */
export function parseUpstream(doc: unknown): RegistryListing[] {
  const plugins = doc && typeof doc === 'object' ? (doc as { plugins?: unknown }).plugins : undefined;
  if (!Array.isArray(plugins)) return [];
  const out: RegistryListing[] = [];
  for (const e of plugins) {
    if (!e || typeof e !== 'object') continue;
    const o = e as Record<string, unknown>;
    if (typeof o.package !== 'string' || !isPackageName(o.package) || o.community !== true) continue;
    const meta = cleanMeta(o);
    const kind: PluginKind = (PLUGIN_KINDS as readonly string[]).includes(String(o.kind)) ? (o.kind as PluginKind) : 'provider';
    out.push({
      ...meta,
      package: o.package,
      name: meta.name ?? o.package,
      kind,
      providers: meta.providers ?? [],
      summary: meta.summary ?? {},
      community: true,
      origin: 'upstream',
    });
  }
  return out;
}

async function fetchJson(url: string, ms: number): Promise<unknown> {
  const res = await fetch(url, { signal: AbortSignal.timeout(ms), headers: { 'user-agent': 'astra-plugin-market' } });
  if (!res.ok) throw new Error(`${url} answered ${res.status}`);
  return res.json();
}

/** npm's latest manifest of each package: six at a time, eight seconds for all. */
async function enrich(plugins: RegistryListing[]): Promise<void> {
  const deadline = Date.now() + 8000;
  let next = 0;
  const worker = async () => {
    while (next < plugins.length) {
      const p = plugins[next++]!;
      const left = deadline - Date.now();
      if (left <= 0) return;
      try {
        const info = (await fetchJson(`${NPM_REGISTRY}/${p.package.replace('/', '%2F')}/latest`, left)) as Record<string, unknown>;
        if (typeof info.version === 'string') p.latest = info.version;
        const m = cleanMeta({ description: info.description, license: typeof info.license === 'string' ? info.license : undefined });
        if (m.description && !p.description) p.description = m.description;
        if (m.license && !p.license) p.license = m.license;
      } catch {
        /* keep what is known */
      }
    }
  };
  await Promise.all(Array.from({ length: 6 }, worker));
}

async function readCache(): Promise<UpstreamCache | null> {
  try {
    const parsed = JSON.parse(await fsp.readFile(cacheFile(), 'utf8')) as UpstreamCache;
    if (typeof parsed.fetchedAt === 'number' && Array.isArray(parsed.plugins)) return parsed;
  } catch {
    /* none yet */
  }
  return null;
}

async function refresh(): Promise<RegistryListing[]> {
  const stale = memory ?? (await readCache());
  if (stale && Date.now() - stale.fetchedAt < FRESH_MS) {
    memory = stale;
    return stale.plugins;
  }
  const url = upstreamUrl();
  if (url) {
    try {
      const plugins = parseUpstream(await fetchJson(url, 6000));
      if (plugins.length > 0) {
        // enrichment mutates entries; carry over what a failed npm call can't refill
        const before = new Map((stale?.plugins ?? []).map((p) => [p.package, p]));
        for (const p of plugins) {
          const old = before.get(p.package);
          if (old) Object.assign(p, { latest: old.latest, description: old.description, license: old.license });
        }
        await enrich(plugins);
        memory = { fetchedAt: Date.now(), plugins };
        try {
          await fsp.mkdir(marketRoot(), { recursive: true });
          await fsp.writeFile(cacheFile(), JSON.stringify(memory));
        } catch {
          /* a read-only volume only costs the persisted copy */
        }
        return plugins;
      }
    } catch {
      /* fall through to the copy fetched last, then the shipped snapshot */
    }
  }
  const plugins = stale?.plugins ?? parseUpstream(fallback);
  // asked again in a minute
  memory = { fetchedAt: Date.now() - FRESH_MS + RETRY_MS, plugins };
  return plugins;
}

/** The upstream entries (a fresh deep copy is not needed: callers copy before changing anything). */
export function loadUpstream(): Promise<RegistryListing[]> {
  if (memory && Date.now() - memory.fetchedAt < FRESH_MS) return Promise.resolve(memory.plugins);
  inflight ??= refresh().finally(() => {
    inflight = null;
  });
  return inflight;
}
