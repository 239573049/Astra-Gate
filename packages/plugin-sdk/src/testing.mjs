// Testing helpers for v2 plugins (design/plugin-v2 06 §4): mount a view and drive it like a shell does, run gateway
// hooks with a ctx like the gateway's. Runs the real astra module through the SDK's in-memory host.
import '../runtime/install.mjs';
import { validateTree } from './validate-tree.mjs';

export { validateTree } from './validate-tree.mjs';

const host = () => {
  const h = globalThis.__astraTestHost;
  if (!h) throw new Error('the astra test host is not installed: alias `astra` to @astragate/plugin-sdk/runtime');
  return h;
};

const DEFAULT_PERMISSIONS = ['storage', 'secrets', 'ui', 'notifications', 'jobs', 'events', 'providers', 'clients'];

function reset(opts = {}) {
  const h = host();
  h.storage = new Map(Object.entries(opts.storage ?? {}).map(([k, v]) => [k, JSON.stringify(v)]));
  h.secrets = new Map(Object.entries(opts.secrets ?? {}));
  h.config = opts.config ?? {};
  h.messages = opts.messages ?? {};
  // each run starts from the default grants unless it names its own (a previous test's never leak in)
  h.granted = new Set(opts.permissions ?? DEFAULT_PERMISSIONS);
  if (opts.fetch) h.fetch = opts.fetch;
  h.answers = { confirm: opts.confirm, prompt: opts.prompt, pickFile: opts.pickFile, openSheet: opts.openSheet, openDialog: opts.openDialog, files: opts.files };
  h.accounts = new Map();
  h.logs = [];
  h.notifications = [];
  h.emitted = [];
  h.data = [];
  h.asked = [];
  h.flushed = [];
  return h;
}

const walk = (node, fn) => {
  if (!node || typeof node !== 'object') return;
  fn(node);
  for (const c of node.children ?? []) walk(c, fn);
  for (const v of Object.values(node.props ?? {})) {
    if (Array.isArray(v)) v.forEach((x) => x && typeof x === 'object' && 'type' in x && walk(x, fn));
    else if (v && typeof v === 'object' && 'type' in v) walk(v, fn);
  }
};

/** Opens a view (init + render) and returns a handle that presses, changes and finds like a shell. */
export async function mount(view, opts = {}) {
  const h = reset(opts);
  const name = opts.name ?? 'view';
  h.adopt(name, view);
  const session = 's' + Math.random().toString(36).slice(2);
  const base = { view: name, session, params: opts.params ?? {}, query: opts.query ?? {}, client: { shell: 'test', ui: '1.0', locale: 'en', width: 'regular', ...(opts.client ?? {}) } };
  const local = {};
  const { state: initial } = await h.run({ ...base, op: 'view.init', local });
  const handle = {
    state: initial,
    tree: null,
    actions: [],
    effects: [],
    warnings: [],
    host: h,
    get storage() {
      return { get: async (k) => (h.storage.has(k) ? JSON.parse(h.storage.get(k)) : undefined) };
    },
    async render() {
      const r = await h.run({ ...base, op: 'view.render', state: handle.state, local });
      handle.tree = r.tree;
      handle.actions = r.actions;
      // the checks the host's normalizer makes, as failures (design/plugin-v2 06 §3); strict: false only collects them
      const checked = validateTree(r.tree, { actions: r.actions, slot: opts.slot ?? null, state: handle.state });
      for (const w of checked.warnings) if (!handle.warnings.includes(w)) handle.warnings.push(w);
      if (checked.errors.length > 0) {
        if (opts.strict === false) {
          for (const e of checked.errors) if (!handle.warnings.includes(e)) handle.warnings.push(e);
        } else throw Object.assign(new Error(`the view "${name}" rendered a tree the host would change:\n  ` + checked.errors.join('\n  ')), { errors: checked.errors });
      }
      return r.tree;
    },
    /** Runs an action as an event would: its args, the local bag; the state and effects come back. */
    async press(action, args = {}) {
      const r = await h.run({ ...base, op: 'view.action', action, args, state: handle.state, local });
      handle.state = r.state;
      handle.effects.push(...r.effects);
      await handle.render();
      return r.effects;
    },
    /** Sets an input's local value (what the shell holds), and fires its onChange when it has one. */
    async change(nameOrKey, value) {
      let input = null;
      walk(handle.tree, (n) => {
        if (n.props?.name === nameOrKey || n.key === nameOrKey) input ??= n;
      });
      if (input?.props?.name) local[input.props.name] = value;
      const on = input?.props?.onChange;
      if (on) await handle.press(typeof on === 'string' ? on : on.action, { ...(typeof on === 'object' ? on.args : {}), value });
    },
    /** The first node of a type, or with a key. */
    find(typeOrKey) {
      let hit = null;
      walk(handle.tree, (n) => {
        if (!hit && (n.type === typeOrKey || n.key === typeOrKey)) hit = n;
      });
      return hit;
    },
    findAll(type) {
      const all = [];
      walk(handle.tree, (n) => n.type === type && all.push(n));
      return all;
    },
    async close() {
      await h.run({ ...base, op: 'view.dispose' });
    },
  };
  await handle.render();
  return handle;
}

/** Runs a command the plugin registered in activate (activate runs first). */
export async function runCommand(plugin, id, args = {}, opts = {}) {
  const h = reset(opts);
  h.adopt('default', plugin);
  await h.run({ op: 'activate', reason: 'test' });
  return h.run({ op: 'command', id, args });
}

/**
 * Runs one gateway hook of a hook set the way the gateway does: input in, what it returned out, plus what it did
 * through ctx (reject, headers, emitted events, counters). `view` is the input as the hook gets it (raw or ir).
 */
export async function runHook(hooks, setId, hook, input, opts = {}) {
  const set = hooks[setId];
  if (!set) throw new Error(`no hook set ${setId}`);
  const name = 'on' + hook[0].toUpperCase() + hook.slice(1);
  const fn = set[name];
  if (typeof fn !== 'function') throw new Error(`hook set ${setId} has no ${name}`);
  const result = { rejected: null, headers: [], emitted: [], counts: {}, held: 0 };
  const ctx = {
    protocol: opts.protocol ?? 'anthropic',
    model: opts.model ?? 'test-model',
    stream: opts.stream ?? false,
    path: opts.path ?? '/v1/messages',
    client: opts.client ?? null,
    token: opts.token ?? null,
    options: opts.config ?? {},
    shared: opts.shared ?? {},
    state: opts.state ?? {},
    status: opts.status,
    upstream: opts.upstream,
    reject: (status, message) => {
      result.rejected ??= { status: status >= 400 && status <= 599 ? status : 403, message: message ?? 'turned away by a gateway middleware' };
    },
    setHeader: (name, value) => result.headers.push([name, String(value)]),
    removeHeader: (name) => result.headers.push([name, null]),
    emit: (name, payload) => result.emitted.push({ name, payload }),
    count: (name, n = 1) => (result.counts[name] = (result.counts[name] ?? 0) + n),
    holdBack: (n) => (result.held = n),
    sha256: (text) => require_('node:crypto').createHash('sha256').update(text).digest('hex'),
  };
  const copy = structuredClone(input);
  result.output = await fn(copy, ctx);
  return result;
}

import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);

/**
 * Runs a provider's prepare / models / refresh / usage the way the host does (2.1, design/plugin-v2 08 §2): activate first,
 * then the call. `account` is { key, secret? } — a secret given here is what account.secret() answers.
 */
export async function runProvider(plugin, providerId, what, request = null, account = null, opts = {}) {
  const h = reset(opts);
  h.adopt('default', plugin);
  await h.run({ op: 'activate', reason: 'test' });
  if (account?.secret !== undefined) {
    if (!h.accounts.has(providerId)) h.accounts.set(providerId, new Map());
    h.accounts.get(providerId).set(account.key, { key: account.key, secret: account.secret });
  }
  return h.run({ op: 'provider.' + what, provider: providerId, request, account: account ? { key: account.key } : null });
}

/**
 * Runs a provider's transformResponse the way the host does (08 §2.2): start, one frame call per entry of `frames` (given the way
 * the host frames them — sse { event?, data, id?, json? }, ndjson / json { json | text }, aws-eventstream { headers, json | text, bytes? },
 * connect { flags, bytes } / { flags, end: true, json | text }), then end, then dispose. With framing "json", `body` (an object, or a
 * string) is the one frame; `state` is what prepare returned as state. A frame that returns { $end: true } stops it there, as the host
 * does (`ended` says so, `consumed` how many frames were read). Returns the output the host would write:
 * { status, headers, error, failure, frames, body, ended, consumed } — `error` is what start refused with ({ status, message }),
 * `failure` a frame's { "$error" }, `body` the single output of a non-streaming reply (`request.stream: false`).
 */
export async function runResponse(plugin, providerId, input, opts = {}) {
  const { framing = 'sse', frames, body, request = {}, response = {}, account = null, state } = input;
  const h = reset(opts);
  h.adopt('default', plugin);
  await h.run({ op: 'activate', reason: 'test' });
  if (account?.secret !== undefined) {
    if (!h.accounts.has(providerId)) h.accounts.set(providerId, new Map());
    h.accounts.get(providerId).set(account.key, { key: account.key, secret: account.secret });
  }
  const rid = 'test-' + Math.random().toString(36).slice(2);
  const base = { provider: providerId, rid, request: { protocol: 'chat', model: null, stream: true, ...request }, account: account ? { key: account.key } : null };
  const out = { status: response.status ?? 200, headers: { ...(response.headers ?? {}) }, error: null, failure: null, frames: [], body: undefined, ended: false, consumed: 0 };
  // the host always disposes the reply, however it went
  const dispose = () => h.run({ ...base, op: 'provider.response.dispose' });
  const started = await h.run({ ...base, op: 'provider.response.start', response: { status: 200, headers: {}, stream: framing !== 'json', ...response }, ...(state ? { state } : {}) });
  if (started?.error) { out.error = started.error; await dispose(); return out; }
  if (started?.status) out.status = started.status;
  if (started?.headers) Object.assign(out.headers, started.headers);
  const input_ = frames ?? (body === undefined ? [] : [typeof body === 'string' ? { text: body } : { json: body }]);
  const collect = (r) => {
    for (const f of r?.frames ?? []) {
      if (f && typeof f === 'object' && f.$error) out.failure = f.$error;
      else if (f && typeof f === 'object' && f.$end === true) out.ended = true;
      else out.frames.push(f);
    }
  };
  for (const f of input_) {
    out.consumed++;
    collect(await h.run({ ...base, op: 'provider.response.frame', frame: f }));
    if (out.failure) { await dispose(); return out; }
    if (out.ended) break;
  }
  collect(await h.run({ ...base, op: 'provider.response.end' }));
  await dispose();
  if (request.stream === false) {
    if (out.frames.length !== 1) out.failure = { status: 502, message: 'a non-streaming reply must produce exactly one body object, got ' + out.frames.length };
    else out.body = out.frames[0];
  }
  return out;
}

/** Runs a client's connect (2.1, 08 §3) with `input` ({ gateway: { url, v1, key }, model, models, agent }). */
export async function runClient(plugin, clientId, input, opts = {}) {
  const h = reset(opts);
  h.adopt('default', plugin);
  await h.run({ op: 'activate', reason: 'test' });
  return h.run({ op: 'client.connect', client: clientId, input });
}
