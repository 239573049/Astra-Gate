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
  /** astra = published on the Astra site, upstream = the community registry's list. */
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

/** Where a listing comes from: the Astra site (hosted = it serves the package too), or the community registry's list. */
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

// ---------- plugins v2 (design/plugin-v2): /api/plugins/v2

interface PluginV2Row {
  id: string;
  spec: string;
  version: string;
  name: string;
  permissions: string[];
  granted: string[];
  missing: string[];
  sensitive: string[];
  enabled: boolean;
  running: boolean;
  problem?: string | null;
  lastError?: string | null;
  contributes: { commands: { plugin: string; id: string; title: string }[]; pages: { id: string; title: string }[]; hooks: string[] };
}

interface PluginSuccessor {
  spec: string;
  v1Name: string;
  name: string;
  version: string;
  permissions: string[];
  sensitive: string[];
  notes?: string | null;
}

async function findV2(id: string): Promise<PluginV2Row> {
  const { plugins } = await api<{ plugins: PluginV2Row[] }>('/api/plugins/v2');
  const hit = plugins.find((p) => p.id === id || p.spec === id || p.name === id);
  if (!hit) throw new AstraError(`No v2 plugin "${id}".`, 'See `astra plugin v2`.');
  return hit;
}

/** Lists v2 plugins: state, permissions waiting for a grant, what they add. */
export async function runPluginV2List(opts: { json?: boolean }): Promise<void> {
  const list = await api<{ plugins: PluginV2Row[] }>('/api/plugins/v2');
  if (opts.json) {
    console.log(JSON.stringify(list, null, 2));
    return;
  }
  if (list.plugins.length === 0) {
    info('No v2 plugins installed.');
    return;
  }
  printTable(
    list.plugins.map((p) => [p.id, p.version, p.running ? 'running' : p.enabled ? 'idle' : 'off', p.missing.length ? `needs ${p.missing.join(', ')}` : '', p.spec]),
    ['ID', 'VERSION', 'STATE', 'PERMISSIONS', 'SPEC'],
  );
  for (const p of list.plugins) {
    if (p.problem) warn(`${p.id}: ${p.problem}`);
    if (p.lastError) warn(`${p.id}: ${p.lastError}`);
    if (p.contributes.commands.length) console.log(`  ${p.id} commands: ${p.contributes.commands.map((c) => c.id).join(', ')}`);
  }
}

/** Grants a v2 plugin the permissions it asks for, after showing them (--yes skips the question). */
export async function runPluginGrant(id: string, opts: { yes?: boolean; permission?: string[] }): Promise<void> {
  const p = await findV2(id);
  const asked = opts.permission?.length ? opts.permission : p.missing;
  if (asked.length === 0) {
    info(`${p.id} has every permission it asks for: ${p.granted.join(', ') || '(none)'}.`);
    return;
  }
  console.log(`${p.name} asks for:`);
  for (const perm of asked) console.log(`  ${p.sensitive.includes(perm) ? '!' : '-'} ${perm}`);
  if (!opts.yes && !(await confirm('Grant these permissions?'))) return;
  await api(`/api/plugins/v2/${encodeURIComponent(p.id)}/grant`, 'POST', { permissions: asked });
  success(`Granted ${asked.length} permission(s) to ${p.id}.`);
}

/** Runs a v2 plugin's command; args is JSON. */
export async function runPluginRun(id: string, command: string, args?: string): Promise<void> {
  let parsed: unknown = null;
  if (args) {
    try {
      parsed = JSON.parse(args);
    } catch (e) {
      throw new AstraError(`The arguments aren't JSON: ${e instanceof Error ? e.message : String(e)}`);
    }
  }
  const p = await findV2(id);
  const r = await api<{ result: unknown }>(`/api/plugins/v2/${encodeURIComponent(p.id)}/commands/${encodeURIComponent(command)}`, 'POST', { args: parsed });
  console.log(typeof r.result === 'string' ? r.result : JSON.stringify(r.result, null, 2));
}

/** Sets v2 settings from JSON (secret settings with --secret name=value). */
export async function runPluginSettings(id: string, values?: string, opts: { secret?: string[] } = {}): Promise<void> {
  const p = await findV2(id);
  let parsed: Record<string, unknown> | null = null;
  if (values) {
    try {
      parsed = JSON.parse(values) as Record<string, unknown>;
    } catch (e) {
      throw new AstraError(`The settings aren't JSON: ${e instanceof Error ? e.message : String(e)}`);
    }
  }
  const secrets: Record<string, string | null> = {};
  for (const s of opts.secret ?? []) {
    const at = s.indexOf('=');
    if (at <= 0) throw new AstraError(`--secret takes name=value, not "${s}".`);
    secrets[s.slice(0, at)] = s.slice(at + 1) || null;
  }
  if (!parsed && Object.keys(secrets).length === 0) {
    const detail = await api<{ plugin: { settings: Record<string, unknown>; secretsSet: string[] } }>(`/api/plugins/v2/${encodeURIComponent(p.id)}`);
    console.log(JSON.stringify(detail.plugin.settings, null, 2));
    if (detail.plugin.secretsSet.length) info(`Secrets set: ${detail.plugin.secretsSet.join(', ')}`);
    return;
  }
  await api(`/api/plugins/v2/${encodeURIComponent(p.id)}/settings`, 'PUT', { values: parsed, secrets });
  success(`Saved ${p.id}'s settings.`);
}

/** Lists v1 plugins with a v2 version, or upgrades one (design/plugin-v2 07 §3). */
export async function runPluginUpgrade(name: string | undefined, opts: { yes?: boolean }): Promise<void> {
  const successors = await api<PluginSuccessor[]>('/api/plugins/v2/successors');
  if (!name) {
    if (successors.length === 0) {
      info('No installed plugin has a v2 version in the market.');
      return;
    }
    printTable(
      successors.map((s) => [s.v1Name, `${s.name} ${s.version}`, s.permissions.join(', ')]),
      ['V1 PLUGIN', 'V2 VERSION', 'PERMISSIONS'],
    );
    info('Upgrade one with `astra plugin upgrade <name>`.');
    return;
  }
  const s = successors.find((x) => x.v1Name === name || x.spec === name);
  if (!s) throw new AstraError(`"${name}" has no v2 version in the market.`, 'See `astra plugin upgrade`.');
  if (s.notes) console.log(s.notes);
  console.log(`${s.name} ${s.version} asks for:`);
  for (const perm of s.permissions) console.log(`  ${s.sensitive.includes(perm) ? '!' : '-'} ${perm}`);
  if (!opts.yes && !(await confirm('Grant these and upgrade?'))) return;
  await follow(await api<PluginJob>('/api/plugins/v2/upgrade', 'POST', { spec: s.spec, grant: s.permissions }));
  success(`Upgraded ${s.v1Name} to ${s.name} ${s.version}.`);
}

/**
 * Mounts a v2 plugin folder as a dev plugin (design/plugin-v2 06 §2): Astra reloads it on save and keeps its open views'
 * state; this prints its log until Ctrl+C, then unmounts it (unless --keep). This machine only.
 */
export async function runPluginDev(dir: string, opts: { yes?: boolean; keep?: boolean }): Promise<void> {
  const { readFileSync } = await import('node:fs');
  const { resolve } = await import('node:path');
  const root = resolve(dir);
  let id: string;
  try {
    id = (JSON.parse(readFileSync(resolve(root, 'astra.plugin.json'), 'utf8')) as { id?: string }).id ?? '';
  } catch {
    throw new AstraError(`${root} has no readable astra.plugin.json.`, 'Start one with `npx @astragate/plugin-sdk init`.');
  }
  if (!id) throw new AstraError('astra.plugin.json has no id.');
  const list = await api<{ plugins: PluginV2Row[] }>('/api/plugins/v2');
  const mounted = list.plugins.some((p) => p.spec === root);
  if (!mounted) await follow(await api<PluginJob>('/api/plugins', 'POST', { spec: root }));
  // the host picks the folder up on its next refresh
  let p: PluginV2Row | undefined;
  for (let i = 0; i < 20 && !p; i++) {
    p = (await api<{ plugins: PluginV2Row[] }>('/api/plugins/v2')).plugins.find((x) => x.id === id);
    if (!p) await new Promise((r) => setTimeout(r, 500));
  }
  if (!p) throw new AstraError(`Astra didn't load ${id}.`, 'Run `astra-plugin validate` in the folder, or see `astra plugin v2`.');
  if (p.missing.length > 0) await runPluginGrant(id, { yes: opts.yes });
  info(`Developing ${id} from ${root}: it reloads on save. Ctrl+C to stop${opts.keep || mounted ? '' : ' (and unmount it)'}.`);

  let stop = false;
  const done = new Promise<void>((resolveDone) =>
    process.once('SIGINT', () => {
      stop = true;
      resolveDone();
    }),
  );
  let last = '';
  let lastProblem: string | null | undefined;
  while (!stop) {
    try {
      const detail = await api<{ plugin: PluginV2Row; lines: { at: string; level: string; text: string }[] }>(`/api/plugins/v2/${encodeURIComponent(id)}`);
      for (const l of detail.lines.filter((l) => l.at > last)) {
        const line = `${l.at.slice(11, 19)} ${l.level.padEnd(5)} ${l.text}`;
        if (l.level === 'error') warn(line);
        else console.log(line);
      }
      if (detail.lines.length > 0) last = detail.lines[detail.lines.length - 1]!.at > last ? detail.lines[detail.lines.length - 1]!.at : last;
      const problem = detail.plugin.problem ?? detail.plugin.lastError;
      if (problem !== lastProblem && problem) warn(problem);
      lastProblem = problem;
    } catch (e) {
      if (e instanceof AstraError) warn(e.message);
      else throw e;
    }
    await Promise.race([done, new Promise((r) => setTimeout(r, 1000))]);
  }
  if (!opts.keep && !mounted) {
    await follow(await api<PluginJob>(`/api/plugins${q(root)}`, 'DELETE'));
    success(`Unmounted ${id}.`);
  }
}

/**
 * Runs a v2 plugin folder's tests (test/**\/*.test.js|mjs) in Astra's own sandbox (design/plugin-v2 06 §4): the same files
 * vitest runs in Node with the SDK, here against the real `astra` module and host calls, over a throwaway store. A folder
 * that isn't mounted yet is mounted for the run and unmounted after. This machine only.
 */
export async function runPluginTest(dir: string, opts: { json?: boolean }): Promise<void> {
  const { readFileSync } = await import('node:fs');
  const { resolve } = await import('node:path');
  const root = resolve(dir);
  let id: string;
  try {
    id = (JSON.parse(readFileSync(resolve(root, 'astra.plugin.json'), 'utf8')) as { id?: string }).id ?? '';
  } catch {
    throw new AstraError(`${root} has no readable astra.plugin.json.`);
  }
  const mounted = (await api<{ plugins: PluginV2Row[] }>('/api/plugins/v2')).plugins.some((p) => p.spec === root);
  if (!mounted) await follow(await api<PluginJob>('/api/plugins', 'POST', { spec: root }));
  try {
    const results = await api<{ file: string; name: string; status: string; ms?: number | null; error?: string | null }[]>(
      `/api/plugins/v2/${encodeURIComponent(id)}/test`,
      'POST',
      {},
    );
    if (opts.json) console.log(JSON.stringify(results, null, 2));
    else {
      let file = '';
      for (const r of results) {
        if (r.file !== file) console.log((file = r.file));
        const mark = r.status === 'passed' ? '✓' : r.status === 'skipped' ? '-' : '✗';
        console.log(`  ${mark} ${r.name}${r.ms != null ? ` (${Math.round(r.ms)} ms)` : ''}`);
        if (r.error) console.log(`      ${r.error}`);
      }
    }
    const failed = results.filter((r) => r.status === 'failed').length;
    const passed = results.filter((r) => r.status === 'passed').length;
    if (results.length === 0) warn('No tests: put them in test/**/*.test.js (or .mjs).');
    else if (failed) {
      warn(`${failed} failed, ${passed} passed (in Astra's sandbox).`);
      process.exitCode = 1;
    } else success(`${passed} passed in Astra's sandbox.`);
  } finally {
    if (!mounted) await follow(await api<PluginJob>(`/api/plugins${q(root)}`, 'DELETE'));
  }
}

/** Goes back to the v1 plugin a v2 one replaced (within 30 days of the upgrade). */
export async function runPluginRollback(id: string, opts: { yes?: boolean }): Promise<void> {
  if (!opts.yes && !(await confirm(`Remove ${id} and reinstall the v1 plugin it replaced?`))) return;
  await follow(await api<PluginJob>(`/api/plugins/v2/${encodeURIComponent(id)}/rollback`, 'POST', {}));
  success(`${id} is back on its v1 plugin.`);
}

async function confirm(question: string): Promise<boolean> {
  if (!process.stdin.isTTY) throw new AstraError(`${question} (not a terminal: pass --yes)`);
  const { createInterface } = await import('node:readline/promises');
  const rl = createInterface({ input: process.stdin, output: process.stdout });
  try {
    return /^y(es)?$/i.test((await rl.question(`${question} [y/N] `)).trim());
  } finally {
    rl.close();
  }
}
