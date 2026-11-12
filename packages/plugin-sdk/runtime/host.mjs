// An in-memory Astra host for running v2 plugins in Node (tests, local previews): it answers the same host calls the
// real host does — storage, secrets, config, i18n, http (stubbed), events, timers, ui questions — and drives the same
// boot.js the server runs, so a plugin behaves here as it does in Astra (design/plugin-v2 06 §4).
import fs from 'node:fs';
import { answer as cryptoAnswer, names as cryptoNames } from './node-crypto.mjs';

// the host composes the boot like V2Assets.cs does: globals.js (given the sync door) hands the boot its byte helpers
const bootSource = `(call, sync, done, context) => (${fs.readFileSync(new URL('./boot.js', import.meta.url), 'utf8')})(call, sync, done, context, (${fs.readFileSync(new URL('./globals.js', import.meta.url), 'utf8')})(sync))`;
const schema = JSON.parse(fs.readFileSync(new URL('./components.json', import.meta.url), 'utf8'));

export function createHost(options = {}) {
  const host = {
    plugin: { id: options.id ?? 'test.plugin', version: options.version ?? '2.0.0' },
    storage: new Map(Object.entries(options.storage ?? {}).map(([k, v]) => [k, JSON.stringify(v)])),
    secrets: new Map(Object.entries(options.secrets ?? {})),
    config: options.config ?? {},
    messages: options.messages ?? {},
    granted: new Set(options.permissions ?? ['storage', 'secrets', 'ui', 'notifications', 'jobs', 'events', 'providers', 'clients']),
    fetch: options.fetch ?? (async (req) => ({ status: 404, headers: {}, body: '' })),
    answers: options.answers ?? {},
    logs: [],
    notifications: [],
    emitted: [],
    data: [],
    asked: [],
    flushed: [],
    accounts: new Map(),
  };
  const ops = new Map();
  const streams = new Map();
  let nextStream = 1;
  let nextTicket = 1;
  let nextOp = 1;
  let api;

  const ok = (value) => ({ ok: true, json: JSON.stringify(value === undefined ? null : value) });
  const fail = (code, message) => ({ ok: false, json: JSON.stringify({ code, message }) });
  const need = (permission) => {
    if (!host.granted.has(permission)) throw Object.assign(new Error(`needs the permission "${permission}"`), { code: 'permission_denied' });
  };

  const syncCall = (name, args) => {
    if (cryptoNames.has(name)) return cryptoAnswer(name, args);
    switch (name) {
      case 'env':
        return { astraVersion: options.astraVersion ?? '0.15.0', api: '2.0.0', ui: schema.version, platform: 'test', plugin: host.plugin, dev: true };
      case 'has':
        return host.granted.has(args) || name in {};
      case 'config.get':
        return host.config;
      case 'i18n.messages':
        return host.messages;
      case 'log':
        host.logs.push(args);
        return null;
      case 'ui.components':
        return Object.keys(schema.components);
      case 'ui.refresh':
        return null;
      case 'ui.data':
        host.data.push(args);
        return null;
      case 'events.emit':
        need('events');
        host.emitted.push(args);
        return null;
      case 'events.subscribe':
        return null;
      case 'notifications.send':
        need('notifications');
        host.notifications.push(args);
        return null;
      case 'permissions.has':
        return host.granted.has(args);
      case 'crypto.uuid':
        return globalThis.crypto.randomUUID();
      case 'crypto.random':
        return Buffer.from(globalThis.crypto.getRandomValues(new Uint8Array(Math.min(4096, args || 16)))).toString('base64');
      case 'crypto.sha256': {
        const data = args.base64 ? Buffer.from(args.base64, 'base64') : Buffer.from(args.text ?? '', 'utf8');
        return require_('node:crypto').createHash('sha256').update(data).digest('hex');
      }
    }
    throw Object.assign(new Error(`${name} is not a synchronous call`), { code: 'not_supported' });
  };

  const asyncCall = async (name, args) => {
    switch (name) {
      case 'storage.get':
        need('storage');
        return host.storage.has(args) ? JSON.parse(host.storage.get(args)) : null;
      case 'storage.set':
        need('storage');
        host.storage.set(args.key, JSON.stringify(args.value));
        return null;
      case 'storage.delete':
        need('storage');
        return host.storage.delete(args);
      case 'storage.list': {
        need('storage');
        const prefix = args.prefix ?? '';
        const keys = [...host.storage.keys()].filter((k) => k.startsWith(prefix) && (!args.cursor || k > args.cursor)).sort();
        const page = keys.slice(0, args.limit ?? 100);
        return { items: page.map((key) => ({ key, value: JSON.parse(host.storage.get(key)) })), ...(keys.length > page.length ? { cursor: page.at(-1) } : {}) };
      }
      case 'secrets.get':
        need('secrets');
        return host.secrets.get(args) ?? null;
      case 'secrets.set':
        need('secrets');
        host.secrets.set(args.name, args.value);
        return null;
      case 'secrets.delete':
        need('secrets');
        return host.secrets.delete(args);
      // 2.1 provider accounts: kept in memory, their secrets with the account
      case 'providers.accounts.list':
        need('providers');
        return [...(host.accounts.get(args.provider)?.values() ?? [])].map(({ secret, ...a }) => a);
      case 'providers.accounts.add':
      case 'providers.accounts.update': {
        need('providers');
        if (!host.accounts.has(args.provider)) host.accounts.set(args.provider, new Map());
        const list = host.accounts.get(args.provider);
        const key = args.account?.key ?? args.key;
        list.set(key, { ...(list.get(key) ?? { status: 'active', enabled: true }), ...(args.account ?? args.patch), key });
        return null;
      }
      case 'providers.accounts.remove':
        need('providers');
        return host.accounts.get(args.provider)?.delete(args.key) ?? false;
      case 'providers.accounts.secret':
        need('providers');
        return host.accounts.get(args.provider)?.get(args.key)?.secret ?? null;
      case 'http.fetch': {
        const origin = new URL(args.url).origin;
        if (![...host.granted].includes('net:' + origin)) throw Object.assign(new Error(`${origin} is not an origin this plugin may reach`), { code: 'permission_denied' });
        const r = await host.fetch(args);
        const raw = typeof r.body === 'string' ? Buffer.from(r.body) : r.body instanceof Uint8Array ? Buffer.from(r.body) : Buffer.from(JSON.stringify(r.body ?? null));
        if (args.stream) {
          // like the host: the body is handed over in chunks (a test's `chunks` option splits it where it likes)
          const chunks = Array.isArray(r.chunks) ? r.chunks.map((c) => (typeof c === 'string' ? Buffer.from(c) : Buffer.from(c))) : [raw];
          const id = nextStream++;
          streams.set(id, chunks);
          return { status: r.status ?? 200, url: args.url, headers: r.headers ?? {}, setCookies: r.setCookies ?? [], stream: id };
        }
        return { status: r.status ?? 200, url: args.url, headers: r.headers ?? {}, setCookies: r.setCookies ?? [], body: raw.toString('base64') };
      }
      case 'http.read': {
        const chunks = streams.get(args);
        if (!chunks) throw Object.assign(new Error('no such stream'), { code: 'not_found' });
        const next = chunks.shift();
        if (next === undefined) {
          streams.delete(args);
          return { done: true };
        }
        return { chunk: next.toString('base64') };
      }
      case 'http.close': {
        streams.delete(args);
        return null;
      }
      case 'timers.sleep':
        await new Promise((r) => setTimeout(r, Math.min(args, 50)));
        return null;
      case 'ui.ask': {
        host.asked.push(args.effect);
        const answer = host.answers[args.effect.type];
        return typeof answer === 'function' ? await answer(args.effect) : answer ?? null;
      }
      case 'ui.flush':
        host.flushed.push(args.state);
        return null;
      case 'permissions.request':
        for (const p of args) host.granted.add(p);
        return true;
      case 'commands.execute':
      case 'jobs.runNow':
        return null;
      case 'ui.files.read':
        return host.answers.files?.[args] ?? null;
    }
    throw Object.assign(new Error(`${name} is not a call this host has`), { code: 'not_supported' });
  };

  const call = (name, argsJson) => {
    const ticket = nextTicket++;
    const args = argsJson ? JSON.parse(argsJson) : null;
    asyncCall(name, args).then(
      (v) => queueMicrotask(() => api.settle(ticket, true, JSON.stringify(v === undefined ? null : v))),
      (e) => queueMicrotask(() => api.settle(ticket, false, JSON.stringify({ code: e.code ?? 'failed', message: e.message }))),
    );
    return ticket;
  };
  const sync = (name, argsJson) => {
    try {
      return JSON.stringify({ result: syncCall(name, argsJson ? JSON.parse(argsJson) : null) ?? null });
    } catch (e) {
      return JSON.stringify({ error: { code: e.code ?? 'failed', message: e.message } });
    }
  };
  const done = (id, okFlag, json) => {
    const op = ops.get(id);
    if (!op) return;
    ops.delete(id);
    if (okFlag) op.resolve(JSON.parse(json));
    else {
      const e = JSON.parse(json);
      op.reject(Object.assign(new Error(e.message), e));
    }
  };

  // the boot sets globalThis.__astraV2, which the astra module reads when it is first imported
  const boot = new Function(`return (${bootSource})`)();
  api = boot(call, sync, done, options.context ?? 'main');

  host.adopt = (name, value) => api.adopt(name, value);
  host.run = (op) =>
    new Promise((resolve, reject) => {
      const opId = nextOp++;
      ops.set(opId, { resolve, reject });
      api.dispatch(JSON.stringify({ ...op, opId, plugin: host.plugin }));
    });
  return host;
}

// node:crypto without a top-level import (keeps this module loadable where crypto is shimmed)
import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);
