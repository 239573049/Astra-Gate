import { AstraError } from '../errors.js';
import { info, printTable, success, warn } from '../ui.js';
import { apiRequest } from '../lib/http.js';
import { ensureServerRunning } from '../lib/server-lifecycle.js';

interface PluginRow {
  spec: string;
  name: string;
  shortName: string;
  version?: string | null;
  enabled: boolean;
  kinds: string[];
  options?: Record<string, unknown> | null;
  optionsExample?: Record<string, unknown> | null;
  error?: string | null;
  middleware?: { hooks: string[]; calls: number; avgMicros: number; failures: number; error?: string | null; lastError?: string | null } | null;
  agent?: { kind?: string | null; error?: string | null } | null;
}

interface PluginList {
  plugins: PluginRow[];
  runtime: { path?: string | null; version?: string | null; downloading: boolean; error?: string | null };
}

interface PluginJob {
  id: string;
  state: 'running' | 'succeeded' | 'failed';
  log: string[];
  error?: string | null;
  result?: string | null;
}

interface MarketListing {
  package: string;
  name: string;
  kind: string;
  latest?: string | null;
  summary: Record<string, string>;
  /** astra = published on the Astra site, upstream = magpie-community's list. */
  origin?: 'astra' | 'upstream';
  /** Versions the market hosts itself; empty = installed from npm (or a git spec). */
  tarballs?: { version: string }[];
  minAstraVersion?: string | null;
}

interface SearchResult {
  spec: string;
  kind: string;
  version?: string | null;
  /** astra (the market's own entries), npm or github. */
  source: string;
  description?: string | null;
}

async function api<T>(path: string, method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE' = 'GET', body?: unknown): Promise<T> {
  const { base, runtime, started } = await ensureServerRunning();
  if (started) console.log('Server was not running — started it in the background.');
  return apiRequest<T>({ base, path, method, body, runtimeToken: runtime?.runtimeToken, timeoutMs: 60_000 });
}

/** Finds a plugin as magpie does: its exact spec, else its package name, else its short name (word-guard). */
async function find(name: string): Promise<PluginRow> {
  const { plugins } = await api<PluginList>('/api/plugins');
  const hit =
    plugins.find((p) => p.spec === name) ?? plugins.find((p) => p.name === name) ?? plugins.find((p) => p.shortName === name);
  if (!hit) throw new AstraError(`No plugin "${name}".`, 'See `astra plugin list`.');
  return hit;
}

/** Follows a job, printing its log as it comes; fails when the job does. */
async function follow(job: PluginJob): Promise<PluginJob> {
  let printed = 0;
  for (;;) {
    for (const line of job.log.slice(printed)) console.log(`  ${line}`);
    printed = Math.max(printed, job.log.length);
    if (job.state !== 'running') break;
    await new Promise((r) => setTimeout(r, 800));
    job = await api<PluginJob>(`/api/plugins/jobs/${encodeURIComponent(job.id)}`);
  }
  if (job.state === 'failed') throw new AstraError(job.error ?? 'The plugin task failed.');
  return job;
}

const q = (spec: string) => `?spec=${encodeURIComponent(spec)}`;

export async function runPluginList(opts: { json?: boolean }): Promise<void> {
  const list = await api<PluginList>('/api/plugins');
  if (opts.json) {
    console.log(JSON.stringify(list, null, 2));
    return;
  }
  if (list.plugins.length === 0) {
    info('No plugins installed. Try `astra plugin market`.');
  } else {
    printTable(
      list.plugins.map((p) => [p.shortName, p.version ?? '', p.kinds.join(','), p.enabled ? 'on' : 'off', p.spec]),
      ['NAME', 'VERSION', 'KINDS', 'STATE', 'SPEC'],
    );
    for (const p of list.plugins) {
      if (p.error) warn(`${p.shortName}: ${p.error}`);
      if (p.middleware?.error) warn(`${p.shortName}: middleware didn't load: ${p.middleware.error}`);
      else if (p.middleware)
        console.log(
          `  ${p.shortName}: ${p.middleware.hooks.join(', ')} · ${p.middleware.calls} calls · ${p.middleware.avgMicros.toFixed(1)} µs · ${p.middleware.failures} failures`,
        );
      if (p.agent?.kind) console.log(`  ${p.shortName}: adds the client ${p.agent.kind} (astra client enable ${p.agent.kind} …)`);
      else if (p.agent?.error) warn(`${p.shortName}: its client didn't load: ${p.agent.error}`);
    }
  }
  const bun = list.runtime;
  console.log(`Bun: ${bun.path ?? `not downloaded yet (${bun.version ?? '?'}; fetched when a provider plugin needs it)`}`);
}

export async function runPluginAdd(spec: string): Promise<void> {
  const job = await follow(await api<PluginJob>('/api/plugins', 'POST', { spec }));
  success(`Added ${job.result ?? spec}.`);
}

export async function runPluginRemove(name: string): Promise<void> {
  const p = await find(name);
  await follow(await api<PluginJob>(`/api/plugins${q(p.spec)}`, 'DELETE'));
  success(`Removed ${p.spec}.`);
}

export async function runPluginSwitch(name: string, enabled: boolean): Promise<void> {
  const p = await find(name);
  await api<PluginList>(`/api/plugins${q(p.spec)}`, 'PATCH', { enabled });
  success(`${p.shortName} is ${enabled ? 'on' : 'off'}.`);
}

export async function runPluginUpdate(name?: string): Promise<void> {
  const spec = name ? (await find(name)).spec : undefined;
  await follow(await api<PluginJob>('/api/plugins/update', 'POST', { spec }));
  success(name ? `Updated ${name}.` : 'Updated every plugin.');
}

/** Prints a plugin's options (or its package's example), sets them from JSON, or clears them with "off". */
export async function runPluginOptions(name: string, value?: string): Promise<void> {
  const p = await find(name);
  if (value === undefined) {
    if (p.options) console.log(JSON.stringify(p.options, null, 2));
    else if (p.optionsExample) {
      info(`${p.shortName} has no options set; its package suggests:`);
      console.log(JSON.stringify(p.optionsExample, null, 2));
    } else info(`${p.shortName} has no options set.`);
    return;
  }
  let options: unknown = null;
  if (value !== 'off') {
    try {
      options = JSON.parse(value);
    } catch (e) {
      throw new AstraError(`The options aren't JSON: ${e instanceof Error ? e.message : String(e)}`);
    }
    if (options === null || typeof options !== 'object' || Array.isArray(options)) throw new AstraError('The options must be a JSON object.');
  }
  await api<PluginList>(`/api/plugins${q(p.spec)}`, 'PATCH', { options });
  success(options === null ? `Cleared ${p.shortName}'s options.` : `Set ${p.shortName}'s options.`);
}

/** Has the plugin serve a provider id another plugin signs in to as well. */
export async function runPluginUse(name: string, provider: string): Promise<void> {
  const p = await find(name);
  await api<PluginList>('/api/plugins/prefer', 'POST', { spec: p.spec, provider });
  success(`${p.shortName} serves ${provider} now.`);
}

/** Where a listing comes from: the Astra site (hosted = it serves the package too), or magpie-community's list. */
function marketOrigin(l: MarketListing): string {
  if (l.origin !== 'astra') return 'community';
  return l.tarballs && l.tarballs.length > 0 ? 'astra (hosted)' : 'astra';
}

export async function runPluginMarket(query?: string): Promise<void> {
  if (query) {
    const results = await api<SearchResult[]>(`/api/plugins/search?q=${encodeURIComponent(query)}`);
    printTable(
      results.map((r) => [r.spec, r.kind, r.version ?? '', r.source, (r.description ?? '').slice(0, 60)]),
      ['SPEC', 'KIND', 'VERSION', 'FROM', 'DESCRIPTION'],
    );
    return;
  }
  const listings = await api<MarketListing[]>('/api/plugins/market');
  printTable(
    listings.map((l) => [l.package, l.kind, l.latest ?? '', marketOrigin(l), l.summary.en ?? '']),
    ['PACKAGE', 'KIND', 'LATEST', 'FROM', 'SUMMARY'],
  );
  info('Install one with `astra plugin add <package>`.');
}
