// @astragate/plugin-sdk/testing inside the sandbox (astra plugin test, design/plugin-v2 06 §4): the same mount / runCommand /
// runHook a plugin's tests use under Node, driving the views and commands through the boot's own operations — so the real
// host calls (storage, secrets, permissions) answer. Seeded storage and secrets go through those calls; settings, messages and
// the answers to confirm / prompt / pickFile are the boot's test host.
import { storage, secrets, crypto, providers } from 'astra';

const internal = () => {
  const i = globalThis.__astraV2;
  if (!i || !i.run) throw new Error('@astragate/plugin-sdk/testing runs inside `astra plugin test` (or under Node with the SDK)');
  return i;
};
let views = 0;
const plugin = { id: 'test', version: '0.0.0' };

async function seed(opts) {
  const i = internal();
  i.testHost.answers = { confirm: opts.confirm, prompt: opts.prompt, pickFile: opts.pickFile, openSheet: opts.openSheet, openDialog: opts.openDialog };
  i.testHost.config = opts.config ?? null;
  i.testHost.messages = opts.messages ?? null;
  i.testHost.data = [];
  i.testHost.flushed = [];
  i.testHost.asked = [];
  for (const [k, v] of Object.entries(opts.storage ?? {})) await storage.set(k, v);
  for (const [k, v] of Object.entries(opts.secrets ?? {})) await secrets.set(k, v);
  return i;
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

export async function mount(view, opts = {}) {
  const i = await seed(opts);
  const name = '__test_view_' + ++views;
  i.adopt(name, view);
  const base = { view: name, session: name, params: opts.params ?? {}, query: opts.query ?? {}, plugin, client: { shell: 'test', ui: '1.0', locale: 'en', width: 'regular', ...(opts.client ?? {}) } };
  const local = {};
  const { state } = await i.run({ ...base, op: 'view.init', local });
  const handle = {
    state,
    tree: null,
    actions: [],
    effects: [],
    warnings: [],
    host: i.testHost,
    get storage() {
      return { get: async (k) => { const v = await storage.get(k); return v === null ? undefined : v; } };
    },
    async render() {
      const r = await i.run({ ...base, op: 'view.render', state: handle.state, local });
      handle.tree = r.tree;
      handle.actions = r.actions;
      return r.tree;
    },
    async press(action, args = {}) {
      const r = await i.run({ ...base, op: 'view.action', action, args, state: handle.state, local });
      handle.state = r.state;
      handle.effects.push(...r.effects);
      await handle.render();
      return r.effects;
    },
    async change(nameOrKey, value) {
      let input = null;
      walk(handle.tree, (n) => { if (!input && (n.props?.name === nameOrKey || n.key === nameOrKey)) input = n; });
      if (input?.props?.name) local[input.props.name] = value;
      const on = input?.props?.onChange;
      if (on) await handle.press(typeof on === 'string' ? on : on.action, { ...(typeof on === 'object' ? on.args : {}), value });
    },
    find(typeOrKey) {
      let hit = null;
      walk(handle.tree, (n) => { if (!hit && (n.type === typeOrKey || n.key === typeOrKey)) hit = n; });
      return hit;
    },
    findAll(type) {
      const all = [];
      walk(handle.tree, (n) => n.type === type && all.push(n));
      return all;
    },
    async close() {
      await i.run({ ...base, op: 'view.dispose' });
    },
  };
  await handle.render();
  return handle;
}

export async function runCommand(def, id, args = {}, opts = {}) {
  const i = await seed(opts);
  i.adopt('default', def);
  await i.run({ op: 'activate', reason: 'test', plugin });
  return i.run({ op: 'command', id, args, plugin });
}

export async function runHook(hooks, setId, hook, input, opts = {}) {
  const set = hooks[setId];
  if (!set) throw new Error(`no hook set ${setId}`);
  const name = 'on' + hook[0].toUpperCase() + hook.slice(1);
  const fn = set[name];
  if (typeof fn !== 'function') throw new Error(`hook set ${setId} has no ${name}`);
  const result = { rejected: null, headers: [], emitted: [], counts: {}, held: 0 };
  const ctx = {
    protocol: opts.protocol ?? 'anthropic', model: opts.model ?? 'test-model', stream: opts.stream ?? false, path: opts.path ?? '/v1/messages',
    client: opts.client ?? null, token: opts.token ?? null, options: opts.config ?? {}, shared: opts.shared ?? {}, state: opts.state ?? {},
    status: opts.status, upstream: opts.upstream,
    reject: (status, message) => { if (!result.rejected) result.rejected = { status: status >= 400 && status <= 599 ? status : 403, message: message ?? 'turned away by a gateway middleware' }; },
    setHeader: (n, v) => result.headers.push([n, String(v)]),
    removeHeader: (n) => result.headers.push([n, null]),
    emit: (n, payload) => result.emitted.push({ name: n, payload }),
    count: (n, k = 1) => (result.counts[n] = (result.counts[n] ?? 0) + k),
    holdBack: (n) => (result.held = n),
    sha256: (text) => crypto.sha256(text),
  };
  result.output = await fn(JSON.parse(JSON.stringify(input)), ctx);
  return result;
}

// 2.1 (design/plugin-v2 08): a provider's prepare / models / refresh / usage, and a client's connect, through the boot's operations.
// Accounts and their secrets go through the real providers.accounts calls (the manifest must declare the provider).
export async function runProvider(def, providerId, what, request = null, account = null, opts = {}) {
  const i = await seed(opts);
  i.adopt('default', def);
  await i.run({ op: 'activate', reason: 'test', plugin });
  if (account && account.secret !== undefined) await providers.accounts.add(providerId, { key: account.key, secret: account.secret });
  return i.run({ op: 'provider.' + what, provider: providerId, request, account: account ? { key: account.key } : null, plugin });
}

export async function runClient(def, clientId, input, opts = {}) {
  const i = await seed(opts);
  i.adopt('default', def);
  await i.run({ op: 'activate', reason: 'test', plugin });
  return i.run({ op: 'client.connect', client: clientId, input, plugin });
}
