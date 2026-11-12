// The `astra` module a v2 plugin imports (design/plugin-v2 02): every namespace is a thin layer over the host call
// table — async calls return promises the host settles, sync ones return at once. Permissions are checked by the host,
// not here. The UI builders are made from the host's component list, so a component the host adds is a builder here.
const h = globalThis.__astraV2;
const { hostCall, hostSync, AstraError, translate, collectionOps } = h;

const envInfo = hostSync('env');

export const version = envInfo.astraVersion;
export const api = envInfo.api;
export const env = Object.freeze({ ...envInfo });
export { AstraError };

export const has = (what) => hostSync('has', String(what)) === true;

export const definePlugin = (def) => def;

// ---------- i18n
export const t = (key, params) => translate(h.getLocale(), String(key), params);
export const i18n = { t, get locale() { return h.getLocale(); } };

// ---------- log
const line = (level) => (message, fields) => {
  hostSync('log', { level, message: typeof message === 'string' ? message : JSON.stringify(message), fields: fields === undefined ? null : fields });
};
export const log = { debug: line('debug'), info: line('info'), warn: line('warn'), error: line('error') };

// ---------- storage / secrets / config
const locks = new Map();
export const storage = {
  get: (key) => hostCall('storage.get', String(key)),
  set: (key, value) => hostCall('storage.set', { key: String(key), value: value === undefined ? null : value }),
  delete: (key) => hostCall('storage.delete', String(key)),
  list: (o) => hostCall('storage.list', o || {}),
  // read-modify-write, one at a time per key within this plugin (the only writer: hooks read, never write)
  update: (key, fn) => {
    key = String(key);
    const prev = locks.get(key) || Promise.resolve();
    const next = prev.catch(() => {}).then(async () => {
      const value = await storage.get(key);
      const updated = await fn(value);
      if (updated !== undefined) await storage.set(key, updated);
      return updated === undefined ? value : updated;
    });
    locks.set(key, next);
    next.finally(() => { if (locks.get(key) === next) locks.delete(key); });
    return next;
  },
};

export const secrets = {
  get: (name) => hostCall('secrets.get', String(name)),
  set: (name, value) => hostCall('secrets.set', { name: String(name), value: String(value) }),
  delete: (name) => hostCall('secrets.delete', String(name)),
};

const configListeners = new Set();
export const config = {
  get: () => hostSync('config.get'),
  onChange: (fn) => { configListeners.add(fn); return { dispose: () => configListeners.delete(fn) }; },
};
h.listeners.set('plugin.configChanged', [...(h.listeners.get('plugin.configChanged') || []), (v) => { for (const fn of configListeners) fn(v); }]);

// ---------- http
// a streamed reply (stream: true): the body stays with the host and is read a chunk at a time (http.read); reading it whole
// (text / json / bytes) drains the stream. A plugin should cancel() a stream it stops reading.
const streamed = (r) => {
  let done = false;
  const next = async () => {
    if (done) return null;
    const c = await hostCall('http.read', r.stream);
    if (c.done) { done = true; return null; }
    return h.unb64(c.chunk);
  };
  const all = async () => {
    const parts = [];
    let size = 0;
    for (let c = await next(); c; c = await next()) { parts.push(c); size += c.length; }
    const out = new Uint8Array(size);
    let at = 0;
    for (const p of parts) { out.set(p, at); at += p.length; }
    return out;
  };
  const decoder = (bytes) => h.utf8(bytes);
  return {
    status: r.status, ok: r.status >= 200 && r.status < 300, headers: r.headers || {}, setCookies: r.setCookies || [], url: r.url, streamed: true,
    stream: () => ({ [Symbol.asyncIterator]: () => ({ next: async () => { const v = await next(); return v ? { value: v, done: false } : { value: undefined, done: true }; } }) }),
    // whole lines of text (SSE and NDJSON): split at the newline byte, which never sits inside a UTF-8 character, so a
    // character or a line split across chunks comes out whole
    lines: () => {
      let rest = new Uint8Array(0);
      const pending = [];
      const line = (bytes) => decoder(bytes).replace(/\r$/, '');
      return {
        [Symbol.asyncIterator]: () => ({
          next: async () => {
            while (pending.length === 0) {
              const c = await next();
              if (!c) {
                if (rest.length) { const last = rest; rest = new Uint8Array(0); return { value: line(last), done: false }; }
                return { value: undefined, done: true };
              }
              const buf = new Uint8Array(rest.length + c.length);
              buf.set(rest, 0);
              buf.set(c, rest.length);
              let start = 0;
              for (let i = 0; i < buf.length; i++) if (buf[i] === 10) { pending.push(line(buf.subarray(start, i))); start = i + 1; }
              rest = buf.slice(start);
            }
            return { value: pending.shift(), done: false };
          },
        }),
      };
    },
    cancel: async () => { if (!done) { done = true; await hostCall('http.close', r.stream); } },
    text: async () => decoder(await all()),
    json: async () => JSON.parse(decoder(await all())),
    bytes: async () => all(),
  };
};
const response = (r) => {
  if (r.stream !== undefined && r.stream !== null) return streamed(r);
  let bytes = null;
  const body = () => (bytes ??= h.unb64(r.body || ''));
  const decode = () => h.utf8(body());
  return {
    status: r.status, ok: r.status >= 200 && r.status < 300, headers: r.headers || {}, setCookies: r.setCookies || [], url: r.url,
    text: async () => decode(),
    json: async () => JSON.parse(decode()),
    bytes: async () => body(),
  };
};
export const http = {
  fetch: async (url, init) => {
    init = init || {};
    let body = null, base64 = false;
    if (init.body instanceof Uint8Array) { body = h.b64(init.body); base64 = true; }
    else if (init.body !== undefined && init.body !== null) body = typeof init.body === 'string' ? init.body : JSON.stringify(init.body);
    const r = await hostCall('http.fetch', {
      url: String(url), method: init.method || 'GET', headers: init.headers || {}, body, base64, timeoutMs: init.timeoutMs || 15000,
      stream: init.stream === true, redirect: init.redirect || 'follow',
    });
    return response(r);
  },
};

// ---------- commands / events / jobs / notifications / permissions
export const commands = {
  register: (id, fn) => { h.commands.set(String(id), fn); return { dispose: () => h.commands.delete(String(id)) }; },
  execute: (id, args) => hostCall('commands.execute', { id: String(id), args: args === undefined ? null : args }),
};

export const events = {
  on: (name, fn) => {
    name = String(name);
    const list = h.listeners.get(name) || [];
    list.push(fn);
    h.listeners.set(name, list);
    hostSync('events.subscribe', name);
    return { dispose: () => { const l = h.listeners.get(name) || []; const i = l.indexOf(fn); if (i >= 0) l.splice(i, 1); } };
  },
  emit: (name, payload) => { hostSync('events.emit', { name: String(name), payload: payload === undefined ? null : payload }); },
};

export const jobs = {
  handle: (id, fn) => { h.jobs.set(String(id), fn); return { dispose: () => h.jobs.delete(String(id)) }; },
  runNow: (id) => hostCall('jobs.runNow', String(id)),
};

export const notifications = {
  send: (o) => { hostSync('notifications.send', typeof o === 'string' ? { title: o } : o); },
};

export const permissions = {
  has: (p) => hostSync('permissions.has', String(p)) === true,
  request: (list) => hostCall('permissions.request', Array.isArray(list) ? list.map(String) : [String(list)]),
};

// ---------- crypto / encoding
// Pure computation in the host (CryptoCalls.cs, System.Security.Cryptography): no permission, and hooks may call it. Data is
// a string (UTF-8), a Uint8Array / ArrayBuffer or { base64 } / { hex }; a key is the same (PEM text for RSA / ECDSA).
// `encoding` picks the result: 'hex' | 'base64' | 'base64url' | 'utf8' | 'bytes' (a Uint8Array). MD5 and SHA-1 are for
// speaking to vendors that use them, not for new designs.
const spec = (v, what) => {
  if (typeof v === 'string') return v;
  if (v instanceof Uint8Array || v instanceof ArrayBuffer || ArrayBuffer.isView(v)) return { bin: h.bytes.bin(h.bytes.toBytes(v)) };
  if (v !== null && typeof v === 'object' && (typeof v.base64 === 'string' || typeof v.hex === 'string' || typeof v.text === 'string')) return v;
  throw new AstraError('invalid_argument', what + ' must be a string, a Uint8Array or { base64 | hex }');
};
const cryptoResult = (r) => (r !== null && typeof r === 'object' ? h.bytes.unbin(r.bin) : r);
const cryptoCall = (name, args) => cryptoResult(hostSync(name, args));
const enc = (o) => (o && o.encoding !== undefined ? { encoding: String(o.encoding) } : {});
export const crypto = {
  /** md5 | sha1 | sha256 | sha384 | sha512 → hex by default. */
  digest: (alg, data, o) => cryptoCall('crypto.digest', { alg: String(alg), data: spec(data, 'data'), ...enc(o) }),
  sha256: (data, o) => cryptoCall('crypto.digest', { alg: 'sha256', data: spec(data, 'data'), ...enc(o) }),
  /** hmac(key, data, 'sha1') or hmac(key, data, { algorithm, encoding }). */
  hmac: (key, data, o) => {
    const opts = typeof o === 'string' ? { algorithm: o } : o || {};
    return cryptoCall('crypto.hmac', { alg: String(opts.algorithm || 'sha256'), key: spec(key, 'key'), data: spec(data, 'data'), ...enc(opts) });
  },
  aes: {
    /** { mode: 'cbc' | 'ecb' | 'gcm', key (16/24/32 bytes), iv, data, aad?, padding?: 'pkcs7' | 'none' } → base64 by default; gcm appends the 16-byte tag. */
    encrypt: (o) => cryptoCall('crypto.aes', { op: 'encrypt', mode: o.mode || 'cbc', key: spec(o.key, 'key'), data: spec(o.data, 'data'),
      ...(o.iv !== undefined ? { iv: spec(o.iv, 'iv') } : {}), ...(o.aad !== undefined ? { aad: spec(o.aad, 'aad') } : {}), ...(o.padding ? { padding: o.padding } : {}), ...enc(o) }),
    /** the same options; gcm data ends with its tag (or pass tag) → a Uint8Array by default (encoding: 'utf8' for text). */
    decrypt: (o) => cryptoCall('crypto.aes', { op: 'decrypt', mode: o.mode || 'cbc', key: spec(o.key, 'key'), data: spec(o.data, 'data'),
      ...(o.iv !== undefined ? { iv: spec(o.iv, 'iv') } : {}), ...(o.aad !== undefined ? { aad: spec(o.aad, 'aad') } : {}),
      ...(o.tag !== undefined ? { tag: spec(o.tag, 'tag') } : {}), ...(o.padding ? { padding: o.padding } : {}), ...enc(o) }),
  },
  rsa: {
    /** { key: PEM (SPKI / PKCS#1) or base64 DER, data, padding: 'pkcs1' | 'oaep', hash? (oaep: sha1 by default) } → base64 by default. */
    encrypt: (o) => cryptoCall('crypto.rsa.encrypt', { key: String(o.key), data: spec(o.data, 'data'), ...(o.padding ? { padding: o.padding } : {}), ...(o.hash ? { hash: o.hash } : {}), ...enc(o) }),
  },
  /** { algorithm: 'rsa-sha1|sha256|sha384|sha512' | 'rsa-pss-sha256' | 'ecdsa-p256-sha256' | 'ecdsa-p384-sha384', key: private PEM (PKCS#8, PKCS#1, SEC1), data, format?: 'der' | 'raw' (ecdsa) } → base64 by default. */
  sign: (o) => cryptoCall('crypto.sign', { algorithm: String(o.algorithm), key: String(o.key), data: spec(o.data, 'data'), ...(o.format ? { format: o.format } : {}), ...enc(o) }),
  /** The same with a public key and { signature } (a string is base64 only when you pass { base64 }) → boolean. */
  verify: (o) => hostSync('crypto.verify', { algorithm: String(o.algorithm), key: String(o.key), data: spec(o.data, 'data'), signature: spec(o.signature, 'signature'), ...(o.format ? { format: o.format } : {}) }),
  /** { password, salt, iterations = 100000 (≤ 1e6), length = 32, hash = 'sha256' } → hex by default. */
  pbkdf2: (o) => cryptoCall('crypto.pbkdf2', { password: spec(o.password, 'password'), salt: spec(o.salt, 'salt'), iterations: o.iterations || 100000, length: o.length || 32, ...(o.hash ? { hash: o.hash } : {}), ...enc(o) }),
  /** { key, salt?, info?, length = 32, hash = 'sha256' } → hex by default. */
  hkdf: (o) => cryptoCall('crypto.hkdf', { key: spec(o.key, 'key'), ...(o.salt !== undefined ? { salt: spec(o.salt, 'salt') } : {}), ...(o.info !== undefined ? { info: spec(o.info, 'info') } : {}), length: o.length || 32, ...(o.hash ? { hash: o.hash } : {}), ...enc(o) }),
  randomUUID: () => hostSync('crypto.uuid'),
  randomBytes: (n) => h.unb64(hostSync('crypto.random', Number(n) || 16)),
};

// bytes <-> text, so a plugin needs no Buffer: encode takes a Uint8Array (a string is its UTF-8), decode returns a Uint8Array
const toBytes = (v) => (typeof v === 'string' ? h.bytes.utf8enc(v) : h.bytes.toBytes(v));
const codec = (name) => ({
  encode: (data) => h.bytes.toText(toBytes(data), name),
  decode: (text) => { try { return h.bytes.fromText(String(text), name); } catch (e) { throw new AstraError('invalid_argument', e.message); } },
});
export const encoding = {
  base64: codec('base64'),
  base64url: codec('base64url'),
  hex: codec('hex'),
  utf8: { encode: (text) => h.bytes.utf8enc(String(text)), decode: (data) => h.bytes.utf8dec(h.bytes.toBytes(data), false, true) },
};

let nextTimer = 1;
const cancelled = new Set();
export const timers = {
  sleep: (ms) => hostCall('timers.sleep', Math.max(0, Number(ms) || 0)),
  setTimeout: (fn, ms, ...args) => {
    const id = nextTimer++;
    timers.sleep(ms).then(() => { if (!cancelled.delete(id)) fn(...args); });
    return id;
  },
  clearTimeout: (id) => { cancelled.add(id); },
  setInterval: (fn, ms, ...args) => {
    const id = nextTimer++;
    const tick = () => timers.sleep(Math.max(1000, Number(ms) || 1000)).then(() => { if (cancelled.has(id)) { cancelled.delete(id); return; } fn(...args); tick(); });
    tick();
    return id;
  },
  clearInterval: (id) => { cancelled.add(id); },
};
if (typeof globalThis.setTimeout !== 'function') {
  globalThis.setTimeout = timers.setTimeout; globalThis.clearTimeout = timers.clearTimeout;
  globalThis.setInterval = timers.setInterval; globalThis.clearInterval = timers.clearInterval;
}

// ---------- data (read-only views)
export const data = {
  requests: {
    query: (o) => hostCall('data.requests.query', o || {}),
    stats: (o) => hostCall('data.requests.stats', o || {}),
  },
  providers: { list: () => hostCall('data.providers.list') },
  models: { list: () => hostCall('data.models.list') },
  tokens: { list: () => hostCall('data.tokens.list') },
};

// ---------- gateway (hooks run in the gateway context; this only marks the definition)
export const gateway = {
  defineHooks: (sets) => Object.defineProperty({ ...sets }, '__astraHooks', { value: true, enumerable: false }),
};

// ---------- providers / clients (2.1, design/plugin-v2 08)
// A provider's code: prepare(req, account) shapes each upstream request the host sends (req.provider is its local id);
// models / refresh(account, { status, body }) are optional, and usage() is not called by the host yet. A provider whose manifest
// has a `reply` block also registers transformResponse { start(res, ctx), frame(frame, ctx), end(ctx) } (08 §2.2).
// An account handed to them carries its key and reads its credential with account.secret().
export const providers = {
  register: (id, impl) => {
    if (!impl || typeof impl.prepare !== 'function') throw new AstraError('invalid_argument', 'a provider needs prepare(req, account)');
    if (impl.transformResponse !== undefined && (impl.transformResponse === null || typeof impl.transformResponse !== 'object' || typeof impl.transformResponse.frame !== 'function'))
      throw new AstraError('invalid_argument', 'transformResponse is { start?, frame(frame, ctx), end? }');
    h.providers.set(String(id), impl);
    return { dispose: () => h.providers.delete(String(id)) };
  },
  accounts: {
    list: (provider) => hostCall('providers.accounts.list', { provider: String(provider) }),
    /** { key, displayName, email?, plan?, secret? }: the secret is kept by the host for this account only. */
    add: (provider, account) => hostCall('providers.accounts.add', { provider: String(provider), account }),
    update: (provider, key, patch) => hostCall('providers.accounts.update', { provider: String(provider), key: String(key), patch: patch || {} }),
    remove: (provider, key) => hostCall('providers.accounts.remove', { provider: String(provider), key: String(key) }),
  },
};
// A client's code: connect(input) returns the keys to write beside the model (the same input as a v1 agent plugin's).
export const clients = {
  register: (id, impl) => {
    h.clients.set(String(id), impl || {});
    return { dispose: () => h.clients.delete(String(id)) };
  },
};

// ---------- ui
const componentNames = hostSync('ui.components');
const isNode = (v) => v !== null && typeof v === 'object' && typeof v.type === 'string' && !Array.isArray(v);
const flatten = (items, out) => {
  for (const c of items) {
    if (c === null || c === undefined || c === false || c === true) continue;
    if (Array.isArray(c)) flatten(c, out);
    else if (typeof c === 'string' || typeof c === 'number') out.push({ type: 'Text', props: { value: String(c) } });
    else out.push(c);
  }
  return out;
};
const node = (type, props, children) => {
  const p = {};
  let key;
  for (const k in props) {
    const v = props[k];
    if (v === undefined) continue;
    if (k === 'key') key = String(v);
    else if (k !== 'children') p[k] = v;
  }
  const kids = flatten([children, props && props.children], []);
  const n = { type, props: p };
  if (key !== undefined) n.key = key;
  if (kids.length > 0) n.children = kids;
  return n;
};
const builder = (type) => (a, b) => {
  // (props, children) | (props) | (children[]) | (childNode) | ()
  if (a === undefined || a === null) return node(type, {}, b);
  if (Array.isArray(a) || isNode(a) || typeof a === 'string') return node(type, {}, a);
  return node(type, a, b);
};

const builders = {};
for (const name of componentNames) builders[name] = builder(name);

const supported = new Set(componentNames);
export const ui = {
  ...builders,
  node: (type, props, children) => node(String(type), props || {}, children),
  defineView: (def) => Object.defineProperty({ ...def }, '__astraView', { value: true, enumerable: false }),
  collection: (name) => ({ collection: String(name) }),
  supports: (name, client) => (client && Array.isArray(client.components) ? client.components.includes(name) : supported.has(name)),
  files: { read: (ref) => hostCall('ui.files.read', String(ref)).then((r) => ({ ...r, bytes: h.unb64(r.base64 || ''), text: () => h.utf8(h.unb64(r.base64 || '')) })) },
  // local conditions (03 §6): evaluated by the shell, never sent back
  when: (expr) => ({ when: expr }),
  local: (name) => ['local', String(name)],
  dates: {
    // a YYYY-MM-DD range (both ends included) as UTC instants [fromUtc, toUtc) for a time zone offset in minutes (ctx.client.utcOffset)
    toUtcRange: (range, utcOffsetMinutes) => {
      const off = Number(utcOffsetMinutes) || 0;
      const at = (d, plusDays) => { const [y, m, day] = String(d).split('-').map(Number); return new Date(Date.UTC(y, m - 1, day + plusDays) - off * 60000).toISOString(); };
      return { fromUtc: at(range.from, 0), toUtc: at(range.to, 1) };
    },
  },
  collectionOps,
};

// JSX runtime: <Row label="x"><Switch/></Row> → the same nodes (the SDK's jsx-runtime points here)
export const jsx = (type, props, key) => {
  if (typeof type === 'function') return type({ ...(props || {}), key });
  return node(String(type), key === undefined ? props || {} : { ...props, key }, undefined);
};
export const jsxs = jsx;
export const Fragment = (props) => flatten([props && props.children], []);

export default { version, api, env, has, definePlugin, t, i18n, log, storage, secrets, config, http, commands, events, jobs, notifications, permissions, crypto, timers, data, gateway, providers, clients, ui };
