import fsp from 'node:fs/promises';
import path from 'node:path';

import fallback from './upstream-fallback.json';
import { cleanMeta } from './meta';
import { isPackageName, marketRoot } from './store';
import { PLUGIN_KINDS, type HostedTarball, type PluginKind, type RegistryListing } from './types';

/**
 * The community registry (astragate-community/astra-gate-plugins' registry.json), aggregated for the market. An
 * entry lists the tarballs of its versions (GitHub Release assets, with their hashes), which the client downloads
 * itself; an entry without any gets npm's word on it (newest version, description, license). Fetched at most every
 * six hours; the copy fetched last (upstream.json) and then the snapshot shipped with the site
 * (upstream-fallback.json) stand in when upstream can't be reached.
 */

/** The one source of the community list: fixed, not configurable (no environment variable can point the site elsewhere). */
const UPSTREAM = 'https://raw.githubusercontent.com/astragate-community/astra-gate-plugins/main/registry.json';
const FRESH_MS = 6 * 60 * 60 * 1000;
const RETRY_MS = 60 * 1000;
const NPM_REGISTRY = 'https://registry.npmjs.org';

interface UpstreamCache {
  fetchedAt: number;
  /** the address the plugins came from: a copy of another list's is never served as this one's */
  source: string;
  plugins: RegistryListing[];
}

let memory: UpstreamCache | null = null;
let inflight: Promise<RegistryListing[]> | null = null;

function cacheFile(): string {
  return path.join(marketRoot(), 'upstream.json');
}

/** A tarball the registry lists: an https address, the hash it must have; anything else is left out. */
function parseTarballs(value: unknown): HostedTarball[] {
  if (!Array.isArray(value)) return [];
  const out: HostedTarball[] = [];
  for (const t of value) {
    if (!t || typeof t !== 'object') continue;
    const o = t as Record<string, unknown>;
    if (typeof o.version !== 'string' || typeof o.url !== 'string' || !/^https:\/\/\S+$/.test(o.url)) continue;
    if (typeof o.sha256 !== 'string' || !/^[0-9a-f]{64}$/i.test(o.sha256)) continue;
    out.push({
      version: o.version,
      url: o.url,
      sha256: o.sha256.toLowerCase(),
      sha512: typeof o.sha512 === 'string' ? o.sha512 : '',
      size: typeof o.size === 'number' && Number.isFinite(o.size) ? o.size : 0,
    });
  }
  return out;
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
    const tarballs = parseTarballs(o.tarballs);
    out.push({
      ...meta,
      ...(tarballs.length > 0 ? { tarballs, latest: typeof o.latest === 'string' ? o.latest : tarballs[0]!.version } : {}),
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

/** npm's latest manifest of each package without tarballs of its own: six at a time, eight seconds for all. */
async function enrich(all: RegistryListing[]): Promise<void> {
  const plugins = all.filter((p) => !p.tarballs?.length);
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
    // a copy without a source (written before it was recorded) or from another address is the old list's
    if (typeof parsed.fetchedAt === 'number' && Array.isArray(parsed.plugins) && parsed.source === UPSTREAM) return parsed;
  } catch {
    /* none yet */
  }
  return null;
}

async function refresh(): Promise<RegistryListing[]> {
  const stale = memory?.source === UPSTREAM ? memory : await readCache();
  if (stale && Date.now() - stale.fetchedAt < FRESH_MS) {
    memory = stale;
    return stale.plugins;
  }
  try {
    const plugins = parseUpstream(await fetchJson(UPSTREAM, 6000));
    if (plugins.length > 0) {
      // enrichment mutates entries; carry over what a failed npm call can't refill
      const before = new Map((stale?.plugins ?? []).map((p) => [p.package, p]));
      for (const p of plugins) {
        const old = before.get(p.package);
        if (old) Object.assign(p, { latest: old.latest, description: old.description, license: old.license });
      }
      await enrich(plugins);
      memory = { fetchedAt: Date.now(), source: UPSTREAM, plugins };
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
  const plugins = stale?.plugins ?? parseUpstream(fallback);
  // asked again in a minute
  memory = { fetchedAt: Date.now() - FRESH_MS + RETRY_MS, source: UPSTREAM, plugins };
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
