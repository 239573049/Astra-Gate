#!/usr/bin/env node
// astra-plugin: the v2 plugin author's tool (design/plugin-v2 06 §5).
//   astra-plugin init <dir> [--id acme.name]   a starter plugin (a page, a command, a hook set)
//   astra-plugin validate [dir]                checks astra.plugin.json the way Astra does (Astra stays the judge), then the
//                                              code: permissions vs. the APIs it uses, translation keys, declared events, size
//   astra-plugin build [dir] [--out dist] [--watch]
//                                              main / gateway bundled into single-file ESM (the sandbox imports only
//                                              relative files), with the manifest, locales and assets, then validated
//   astra-plugin pack [dir] [--out file.tgz]   an npm-style tarball (package/…) with its sha256 and sha512 integrity
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';

const MANIFEST = 'astra.plugin.json';
const API = '2.0.0';
const FIXED = [
  'storage', 'secrets', 'ui', 'notifications', 'jobs', 'events',
  'gateway.request:read', 'gateway.request:rewrite', 'gateway.request:reject',
  'gateway.response:read', 'gateway.response:rewrite', 'gateway.upstream:headers',
  'data.requests:read', 'data.providers:read', 'data.models:read', 'data.tokens:read',
  'providers', 'clients', 'legacy.auth:import',
];
const POINTS = ['configuration', 'commands', 'ui.pages', 'ui.settings', 'ui.slots', 'ui.menus', 'gateway.hooks', 'jobs', 'events', 'providers', 'clients', 'ui.flows'];
const ID = /^[a-z0-9][a-z0-9-]{0,31}\.[a-z0-9][a-z0-9-]{0,63}$/;
const SEMVER = /^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$/;
const ACTIVATION = /^(onStartup|on(View|Command|Event|Job):[A-Za-z0-9._:-]{1,128})$/;
const SKIP = new Set(['node_modules', '.git', '.DS_Store', 'dist']);
const MAX_PACKAGE = 5 * 1024 * 1024;

/** Problems and warnings of a plugin directory's manifest. */
export function validate(dir) {
  const errors = [];
  const warnings = [];
  let m;
  try {
    m = JSON.parse(fs.readFileSync(path.join(dir, MANIFEST), 'utf8'));
  } catch (e) {
    return { errors: [`${MANIFEST}: ${e.code === 'ENOENT' ? 'not found' : e.message}`], warnings };
  }
  if (m.manifestVersion !== 2) errors.push('manifestVersion must be 2');
  if (!ID.test(m.id ?? '')) errors.push('id must be <publisher>.<name> in lower case, e.g. acme.word-guard');
  if (!SEMVER.test(m.version ?? '')) errors.push('version must be a semver x.y.z');
  const name = typeof m.name === 'string' ? m.name : m.name?.en ?? Object.values(m.name ?? {})[0];
  if (!name) errors.push('name is required (a string or { "zh": …, "en": … })');
  const api = m.engines?.api;
  if (api && !/^(\^|~|>=)?2(\.|$)/.test(api.trim())) errors.push(`needs API ${api}; Astra speaks ${API}`);
  const file = (rel, what) => {
    if (rel == null) return false;
    if (typeof rel !== 'string' || path.isAbsolute(rel) || rel.includes('\\')) return errors.push(`${what} must be a relative path with / separators`), false;
    const full = path.resolve(dir, rel);
    const r = path.relative(path.resolve(dir), full);
    if (r.startsWith('..') || path.isAbsolute(r)) return errors.push(`${what} is outside the plugin`), false;
    if (!fs.existsSync(full)) return errors.push(`${what} "${rel}" doesn't exist`), false;
    return true;
  };
  const hasMain = file(m.main, 'main');
  const hasGateway = file(m.gateway, 'gateway');
  const perms = [...(m.permissions ?? []), ...(m.optionalPermissions ?? [])];
  for (const p of perms) {
    if (FIXED.includes(p)) continue;
    if (!p.startsWith('net:')) errors.push(`unknown permission "${p}"`);
    else if (!/^\{config\.[A-Za-z0-9_.-]+\}$/.test(p.slice(4)) && !/^https:\/\/[^/:?#\s]+(:\d+)?$/.test(p.slice(4)))
      errors.push(`"${p}": net: takes one https origin (https://host[:port]) or {config.<setting>}`);
  }
  for (const a of m.activation ?? []) if (!ACTIVATION.test(a)) errors.push(`activation "${a}" is not onStartup, onView:<id>, onCommand:<id>, onEvent:<name> or onJob:<id>`);
  const c = m.contributes ?? {};
  for (const key of Object.keys(c)) if (!POINTS.includes(key)) warnings.push(`contributes.${key}: not an extension point of API ${API}, ignored`);
  const has = (p) => (m.permissions ?? []).some((g) => g === p || (p === 'gateway.request:read' && /^gateway\.request:(rewrite|reject)$/.test(g)) || (p === 'gateway.response:read' && g === 'gateway.response:rewrite'));
  const ui = ['ui.pages', 'ui.settings', 'ui.slots', 'ui.menus'].filter((k) => c[k]);
  for (const k of ui) if (!has('ui')) errors.push(`contributes.${k} needs the permission "ui"`);
  if (c.jobs && !has('jobs')) errors.push('contributes.jobs needs the permission "jobs"');
  if (c.providers && !has('providers')) errors.push('contributes.providers needs the permission "providers"');
  if (c.clients && !has('clients')) errors.push('contributes.clients needs the permission "clients"');
  if (c['ui.flows'] && !has('ui')) errors.push('contributes.ui.flows needs the permission "ui"');
  for (const p of c.providers ?? []) {
    if (p.reply !== undefined) {
      const r = p.reply;
      if (!r || typeof r !== 'object' || Array.isArray(r)) errors.push(`providers.${p.id}.reply: must be an object { framing, output }`);
      else {
        if (!['sse', 'ndjson', 'json', 'aws-eventstream', 'connect'].includes(r.framing)) errors.push(`providers.${p.id}.reply.framing: "${r.framing}" is not one of sse, ndjson, json, aws-eventstream, connect`);
        const protocols = p.protocols ?? [];
        const out = r.output ?? protocols[0] ?? 'chat';
        if (!protocols.includes(out)) errors.push(`providers.${p.id}.reply.output: "${out}" isn't one of the provider's protocols (${protocols.join(', ')})`);
        for (const k of Object.keys(r)) if (k !== 'framing' && k !== 'output') errors.push(`providers.${p.id}.reply: unknown key "${k}"`);
      }
    }
    const flow = p.signIn?.flow;
    if (flow && !(c['ui.flows'] ?? []).some((f) => f.id === flow)) errors.push(`providers.${p.id}: signIn.flow "${flow}" isn't one of its ui.flows`);
  }
  for (const h of c['gateway.hooks'] ?? []) {
    if (h.view && h.view !== 'raw' && h.view !== 'ir') errors.push(`gateway.hooks.${h.id}: view must be raw or ir`);
    if (h.reads?.length && !has('storage')) errors.push(`gateway.hooks.${h.id}: reads needs the permission "storage"`);
    for (const on of h.on ?? []) {
      const need = { request: 'gateway.request:read', upstream: 'gateway.request:read', response: 'gateway.response:read', event: 'gateway.response:read', text: 'gateway.response:read', complete: 'gateway.response:read' }[on];
      if (!need) errors.push(`gateway.hooks.${h.id}: unknown hook "${on}"`);
      else if (!has(need)) errors.push(`contributes.gateway.hooks needs the permission "${need}"`);
      if (on === 'text' && h.view !== 'ir') errors.push(`gateway.hooks.${h.id}: the text hook needs view "ir"`);
    }
  }
  if (!hasMain && (ui.length || c.commands || c.jobs || c.providers || c.clients)) errors.push('main is required: the plugin contributes views, commands, jobs, providers, clients or flows');
  if (!hasGateway && c['gateway.hooks']?.length) errors.push('gateway is required: the plugin contributes gateway.hooks');
  if (errors.length === 0) scanSources(dir, m, errors, warnings);
  return { errors: [...new Set(errors)], warnings: [...new Set(warnings)], manifest: m };
}

// ---------- what the code uses (design/plugin-v2 06 §1 validate)

// a call on the astra module → the permission it needs (HostCalls.Needs); http needs any net: permission
// host events and the permission subscribing to each needs (PluginV2Host.HostEvents)
const HOST_EVENT_PERMISSION = { 'request.completed': 'data.requests:read', 'provider.changed': 'data.providers:read', 'account.changed': 'data.providers:read' };
const API_PERMISSION = {
  storage: 'storage', secrets: 'secrets', notifications: 'notifications', jobs: 'jobs', events: 'events',
  'data.requests': 'data.requests:read', 'data.providers': 'data.providers:read', 'data.models': 'data.models:read', 'data.tokens': 'data.tokens:read',
  providers: 'providers', clients: 'clients',
};
// hook ctx calls in the gateway module → permission
const CTX_PERMISSION = { reject: 'gateway.request:reject', setHeader: 'gateway.upstream:headers', removeHeader: 'gateway.upstream:headers', emit: 'events' };
const isSource = (rel) => /\.(m?js|cjs)$/.test(rel) && !/(^|\/)(tests?|__tests__)\//.test(rel) && !/\.(test|spec)\.m?js$/.test(rel) && !/(^|\/)vitest\.config\./.test(rel);

/** The astra exports a file imports, by local name: { storage: ['storage'], '*': ['astra'] }. */
function astraImports(code) {
  const out = new Map();
  const add = (exp, local) => out.set(exp, [...(out.get(exp) ?? []), local]);
  for (const m of code.matchAll(/import\s+([^;'"]*?)\s+from\s*['"]astra['"]/g)) {
    const clause = m[1];
    const named = /\{([^}]*)\}/.exec(clause);
    if (named)
      for (const part of named[1].split(',')) {
        const [exp, local] = part.trim().split(/\s+as\s+/);
        if (exp) add(exp.trim(), (local ?? exp).trim());
      }
    const ns = /\*\s+as\s+([\w$]+)/.exec(clause);
    if (ns) add('*', ns[1]);
    const def = /^([\w$]+)\s*(,|$)/.exec(clause.trim());
    if (def) add('*', def[1]);
  }
  return out;
}

const esc = (s) => s.replace(/[$]/g, '\\$');

function scanSources(dir, m, errors, warnings) {
  const all = files(dir);
  let size = 0;
  for (const rel of all) size += fs.statSync(path.join(dir, rel)).size;
  if (size > MAX_PACKAGE) warnings.push(`the package is ${(size / 1024 / 1024).toFixed(1)} MB; keep it under ${MAX_PACKAGE / 1024 / 1024} MB (assets the plugin doesn't load, source maps, node_modules copies)`);

  const declared = new Set([...(m.permissions ?? [])]);
  const optional = new Set(m.optionalPermissions ?? []);
  const nets = [...declared, ...optional].filter((p) => p.startsWith('net:'));
  const used = new Set();
  const keys = new Set();
  const emitted = new Set();
  let sawTransform = false;
  const gatewayRel = typeof m.gateway === 'string' ? path.posix.normalize(m.gateway) : null;
  const holds = (p) => declared.has(p) || optional.has(p) || (p === 'gateway.request:read' && (declared.has('gateway.request:rewrite') || declared.has('gateway.request:reject')));

  for (const rel of all.filter(isSource)) {
    const code = fs.readFileSync(path.join(dir, rel), 'utf8');
    if (/\btransformResponse\b/.test(code)) sawTransform = true;
    const imports = astraImports(code);
    const locals = (exp) => [...(imports.get(exp) ?? []), ...(imports.get('*') ?? []).map((ns) => `${ns}.${exp}`)];
    const calls = (local) => [...code.matchAll(new RegExp(`(?<![\\w$.])${esc(local).replace('.', '\\s*\\.\\s*')}\\s*\\.\\s*([\\w$]+)`, 'g'))].map((x) => x[1]);
    for (const api of ['storage', 'secrets', 'notifications', 'jobs', 'events']) if (locals(api).some((l) => calls(l).length > 0)) used.add(API_PERMISSION[api]);
    for (const l of locals('data')) for (const member of calls(l)) if (API_PERMISSION['data.' + member]) used.add(API_PERMISSION['data.' + member]);
    for (const l of locals('http'))
      if (calls(l).includes('fetch')) {
        used.add('net:');
        for (const f of code.matchAll(new RegExp(`${esc(l).replace('.', '\\.')}\\s*\\.\\s*fetch\\(\\s*['"\`](https?:\\/\\/[^'"\`/?#]+)`, 'g'))) {
          const origin = f[1];
          if (!origin.startsWith('https://')) errors.push(`${rel}: http.fetch("${origin}…"): only https is allowed`);
          else if (!nets.some((p) => p === 'net:' + origin || p.startsWith('net:{config.')))
            errors.push(`${rel}: http.fetch reaches ${origin}, which no net: permission names (add "net:${origin}")`);
        }
      }
    for (const l of locals('permissions'))
      for (const r of code.matchAll(new RegExp(`${esc(l).replace('.', '\\.')}\\s*\\.\\s*request\\(\\s*(\\[[^\\]]*\\]|['"][^'"]+['"])`, 'g')))
        for (const p of [...r[1].matchAll(/['"]([^'"]+)['"]/g)].map((x) => x[1]))
          if (!optional.has(p)) errors.push(`${rel}: permissions.request("${p}") — list it in optionalPermissions${declared.has(p) ? ' (it is already a required permission, so the request is not needed)' : ''}`);
    for (const l of locals('events')) for (const e of code.matchAll(new RegExp(`${esc(l).replace('.', '\\.')}\\s*\\.\\s*emit\\(\\s*['"]([^'"]+)['"]`, 'g'))) emitted.add(e[1]);
    // a host event needs its data permission to be subscribed to (PluginV2Host.HostEvents)
    for (const l of locals('events'))
      for (const e of code.matchAll(new RegExp(`${esc(l).replace('.', '\\.')}\\s*\\.\\s*on\\(\\s*['"]([^'"]+)['"]`, 'g')))
        if (HOST_EVENT_PERMISSION[e[1]]) used.add(HOST_EVENT_PERMISSION[e[1]]);
    // translation keys: t('x'), i18n.t('x'), ctx.t('x') with a literal key
    for (const k of code.matchAll(/(?<![\w$])t\(\s*['"]([^'"\n]+)['"]/g)) keys.add(k[1]);
    if (gatewayRel && path.posix.normalize(rel) === gatewayRel) {
      for (const [call, perm] of Object.entries(CTX_PERMISSION))
        if (new RegExp(`\\bctx\\s*\\.\\s*${call}\\s*\\(`).test(code)) used.add(perm);
      for (const e of code.matchAll(/\bctx\s*\.\s*emit\(\s*['"]([^'"]+)['"]/g)) emitted.add(e[1]);
    }
  }
  if ((m.contributes?.['gateway.hooks'] ?? []).some((h) => h.reads?.length)) used.add('storage');
  // a reply block and transformResponse go together (08 §2.2): the host runs one only with the other
  const withReply = (m.contributes?.providers ?? []).filter((p) => p.reply !== undefined);
  if (sawTransform && withReply.length === 0) errors.push('the code registers transformResponse but no provider has a "reply" block ({ framing, output }) in contributes.providers');
  if (!sawTransform) for (const p of withReply) errors.push(`providers.${p.id}.reply is declared but the code registers no transformResponse`);

  for (const p of used) {
    if (p === 'net:') {
      if (nets.length === 0) errors.push('the code calls http.fetch but the plugin has no net: permission');
      continue;
    }
    if (!holds(p)) errors.push(`the code uses ${p === 'events' ? 'events' : p.split(/[.:]/)[0] === 'data' ? p.replace(':read', '') : p} but "${p}" is not in permissions or optionalPermissions`);
  }
  const checkable = ['storage', 'secrets', 'notifications', 'jobs', 'events', 'data.requests:read', 'data.providers:read', 'data.models:read', 'data.tokens:read'];
  for (const p of declared) {
    if (checkable.includes(p) && !used.has(p) && !(p === 'jobs' && m.contributes?.jobs) && !(p === 'events' && m.contributes?.events))
      warnings.push(`"${p}" is declared but the code never uses it — users are asked for every permission, so drop the ones you don't need`);
    if (p.startsWith('net:') && !used.has('net:')) warnings.push(`"${p}" is declared but the code never calls http.fetch`);
  }
  const eventNames = new Set((m.contributes?.events ?? []).map((e) => e.name));
  for (const e of emitted) if (!eventNames.has(e)) errors.push(`the code emits "${e}", which contributes.events does not declare`);

  // translation keys against locales/*.json (when the plugin has any)
  const localeDir = path.join(dir, 'locales');
  if (keys.size > 0 && fs.existsSync(localeDir)) {
    const locales = fs.readdirSync(localeDir).filter((f) => f.endsWith('.json'));
    const tables = locales.map((f) => {
      try {
        return [f, JSON.parse(fs.readFileSync(path.join(localeDir, f), 'utf8'))];
      } catch (e) {
        errors.push(`locales/${f}: ${e.message}`);
        return [f, {}];
      }
    });
    const has = (table, key) => key in table || key.split('.').reduce((o, part) => (o && typeof o === 'object' ? o[part] : undefined), table) !== undefined;
    for (const key of keys) {
      const missing = tables.filter(([, table]) => !has(table, key)).map(([f]) => f);
      if (missing.length === tables.length && tables.length > 0) errors.push(`t("${key}") has no text in any of locales/${locales.join(', ')}`);
      else if (missing.length > 0) warnings.push(`t("${key}") is missing from locales/${missing.join(', ')}`);
    }
  }
}

function files(dir, base = dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    if (SKIP.has(e.name) || e.name.endsWith('.tgz')) continue;
    const full = path.join(dir, e.name);
    if (e.isDirectory()) files(full, base, out);
    else if (e.isFile()) out.push(path.relative(base, full).split(path.sep).join('/'));
  }
  return out;
}

// A ustar header block: name split into prefix/name when longer than 100 bytes.
function header(name, size) {
  const b = Buffer.alloc(512);
  let prefix = '';
  if (Buffer.byteLength(name) > 100) {
    const cut = name.lastIndexOf('/', 155);
    prefix = name.slice(0, cut);
    name = name.slice(cut + 1);
  }
  b.write(name, 0, 100);
  b.write('0000644\0', 100);
  b.write('0000000\0', 108);
  b.write('0000000\0', 116);
  b.write(size.toString(8).padStart(11, '0') + '\0', 124);
  b.write('00000000000\0', 136); // mtime 0: the same tree packs to the same bytes
  b.write('        ', 148);
  b.write('0', 156);
  b.write('ustar\0', 257);
  b.write('00', 263);
  b.write(prefix, 345, 155);
  let sum = 0;
  for (const x of b) sum += x;
  b.write(sum.toString(8).padStart(6, '0') + '\0 ', 148);
  return b;
}

/** Packs a plugin directory; returns where it went and its digests. */
export function pack(dir, out) {
  const { errors, manifest } = validate(dir);
  if (errors.length) throw new Error(errors.join('\n'));
  const parts = [];
  for (const rel of files(dir)) {
    const data = fs.readFileSync(path.join(dir, rel));
    parts.push(header('package/' + rel, data.length), data, Buffer.alloc((512 - (data.length % 512)) % 512));
  }
  parts.push(Buffer.alloc(1024));
  const tgz = zlib.gzipSync(Buffer.concat(parts), { level: 9 });
  out ??= path.join(dir, `${manifest.id}-${manifest.version}.tgz`);
  fs.writeFileSync(out, tgz);
  return {
    file: out,
    size: tgz.length,
    sha256: crypto.createHash('sha256').update(tgz).digest('hex'),
    integrity: 'sha512-' + crypto.createHash('sha512').update(tgz).digest('base64'),
  };
}

/**
 * Bundles a plugin into `out` (default <dir>/dist): main and gateway each become one ESM file with `astra` left as the
 * host's module; the manifest (pointing at them), locales/, assets/ and a relative icon are copied. Returns the
 * validation of the result. `watch` keeps rebuilding and calls `onBuild` after each round.
 */
export async function build(dir, { out, watch = false, onBuild } = {}) {
  const src = validate(dir);
  if (!src.manifest) return src;
  const m = src.manifest;
  out = path.resolve(out ?? path.join(dir, 'dist'));
  const { build: esbuild, context } = await import('esbuild');
  const entries = {};
  if (typeof m.main === 'string') entries.main = path.join(dir, m.main);
  if (typeof m.gateway === 'string') entries.gateway = path.join(dir, m.gateway);
  const finish = () => {
    const manifest = { ...m };
    if (entries.main) manifest.main = 'main.js';
    if (entries.gateway) manifest.gateway = 'gateway.js';
    fs.writeFileSync(path.join(out, MANIFEST), JSON.stringify(manifest, null, 2) + '\n');
    for (const extra of ['locales', 'assets'])
      if (fs.existsSync(path.join(dir, extra))) fs.cpSync(path.join(dir, extra), path.join(out, extra), { recursive: true });
    if (typeof m.icon === 'string' && !/^[a-z0-9-]+$/.test(m.icon) && !/^https:/.test(m.icon) && fs.existsSync(path.join(dir, m.icon))) {
      fs.mkdirSync(path.dirname(path.join(out, m.icon)), { recursive: true });
      fs.copyFileSync(path.join(dir, m.icon), path.join(out, m.icon));
    }
    const result = validate(out);
    onBuild?.(result);
    return result;
  };
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });
  if (Object.keys(entries).length === 0) return finish();
  const options = {
    entryPoints: entries,
    outdir: out,
    bundle: true,
    format: 'esm',
    platform: 'neutral',
    target: 'es2022',
    external: ['astra'],
    // the sandbox has no Node: a bare import of a Node built-in fails the build instead of the plugin at run time
    mainFields: ['module', 'main'],
    legalComments: 'none',
    logLevel: 'silent',
    write: true,
  };
  if (!watch) {
    await esbuild(options);
    return finish();
  }
  let last;
  const ctx = await context({ ...options, plugins: [{ name: 'astra-finish', setup: (b) => b.onEnd((r) => void (r.errors.length === 0 && (last = finish()))) }] });
  await ctx.rebuild(); // the first round written before this returns
  await ctx.watch();
  return { ...(last ?? finish()), dispose: () => ctx.dispose() };
}

/** Writes a starter plugin into an empty (or new) directory. */
export function init(dir, id) {
  if (fs.existsSync(dir) && fs.readdirSync(dir).length) throw new Error(`${dir} is not empty`);
  id ??= 'me.' + path.basename(path.resolve(dir)).toLowerCase().replace(/[^a-z0-9-]/g, '-').replace(/^-+/, '').slice(0, 60) || 'me.plugin';
  const write = (rel, text) => {
    fs.mkdirSync(path.dirname(path.join(dir, rel)), { recursive: true });
    fs.writeFileSync(path.join(dir, rel), text);
  };
  write(MANIFEST, JSON.stringify({
    manifestVersion: 2, id, version: '0.1.0', name: { en: 'My plugin', zh: '我的插件' },
    engines: { api: '^2.0' }, main: 'main.js', gateway: 'gateway.js',
    permissions: ['ui', 'storage', 'gateway.request:read'],
    contributes: {
      'ui.pages': [{ id: 'home', title: { en: 'My plugin', zh: '我的插件' }, icon: 'puzzle', nav: 'tools', routes: [{ path: '', view: 'home' }] }],
      commands: [{ id: 'reset', title: { en: 'Reset the counter', zh: '清零计数' } }],
      'gateway.hooks': [{ id: 'count', on: ['request'] }],
    },
  }, null, 2) + '\n');
  write('main.js', `import { definePlugin, commands, storage, ui } from 'astra';

// A view is a top-level export: its name is what ui.pages routes and ui.slots name.
export const home = ui.defineView({
  init: async () => ({ seen: (await storage.get('seen')) ?? 0 }),
  render: (s) =>
    ui.Stack({ gap: '4' }, [
      ui.Heading({ value: 'Requests seen', level: 2 }),
      ui.MetricCard({ label: 'Total', value: s.seen }),
      ui.Button({ label: 'Refresh', onPress: 'refresh' }),
    ]),
  actions: {
    refresh: async (s) => {
      s.seen = (await storage.get('seen')) ?? 0;
    },
  },
});

export default definePlugin({
  activate(ctx) {
    ctx.subscriptions.push(commands.register('reset', () => storage.set('seen', 0)));
  },
});
`);
  write('gateway.js', `import { gateway } from 'astra';

export default gateway.defineHooks({
  count: {
    onRequest(req, ctx) {
      ctx.count('seen');
    },
  },
});
`);
  write('package.json', JSON.stringify({ private: true, type: 'module', scripts: { test: 'vitest run', validate: 'astra-plugin validate', pack: 'astra-plugin pack' }, devDependencies: { '@astragate/plugin-sdk': '^2.0.0', vitest: '^3.0.0' } }, null, 2) + '\n');
  write('vitest.config.js', `import { defineConfig } from 'vitest/config';

export default defineConfig({ resolve: { alias: { astra: '@astragate/plugin-sdk/runtime' } } });
`);
  write('test/home.test.js', `import { expect, it } from 'vitest';
import { mount } from '@astragate/plugin-sdk/testing';
import { home } from '../main.js';

it('shows what storage holds', async () => {
  const view = await mount(home, { storage: { seen: 3 } });
  expect(view.find('MetricCard').props.value).toBe(3);
});
`);
  return id;
}

async function main(argv) {
  const [cmd, ...rest] = argv;
  const flag = (n) => {
    const i = rest.indexOf(n);
    if (i < 0) return undefined;
    const v = rest[i + 1];
    rest.splice(i, 2);
    return v;
  };
  switch (cmd) {
    case 'init': {
      const id = flag('--id');
      const dir = rest[0] ?? '.';
      console.log(`created ${init(dir, id)} in ${dir}`);
      return 0;
    }
    case 'validate': {
      const { errors, warnings } = validate(rest[0] ?? '.');
      for (const w of warnings) console.warn('warning: ' + w);
      for (const e of errors) console.error('error: ' + e);
      if (!errors.length) console.log('ok');
      return errors.length ? 1 : 0;
    }
    case 'build': {
      const out = flag('--out');
      const watch = rest.includes('--watch');
      const report = (r) => {
        for (const w of r.warnings) console.warn('warning: ' + w);
        for (const e of r.errors) console.error('error: ' + e);
        console.log(r.errors.length ? 'built, with errors' : `built ${out ?? 'dist'}`);
      };
      const r = await build(rest.filter((a) => a !== '--watch')[0] ?? '.', { out, watch, onBuild: watch ? report : undefined });
      if (!watch) report(r);
      if (watch) return new Promise(() => {});
      return r.errors.length ? 1 : 0;
    }
    case 'pack': {
      const out = flag('--out');
      const r = pack(rest[0] ?? '.', out);
      console.log(`${r.file}\n  size      ${r.size}\n  sha256    ${r.sha256}\n  integrity ${r.integrity}`);
      return 0;
    }
    default:
      console.log('usage: astra-plugin <init [dir] [--id x.y] | validate [dir] | build [dir] [--out dist] [--watch] | pack [dir] [--out file]>');
      return cmd ? 1 : 0;
  }
}

if (import.meta.url === `file://${fs.realpathSync(process.argv[1] ?? '')}`) {
  main(process.argv.slice(2)).then(
    (code) => process.exit(code),
    (e) => {
      console.error(e.message);
      process.exit(1);
    },
  );
}
