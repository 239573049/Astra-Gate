// The v2 runtime's boot (design/plugin-v2 01 §3, §5): evaluated once per engine, before the plugin's module. It is
// a function the host calls with its three doors — call (an async host call: returns a ticket the host settles later),
// sync (a synchronous host call: returns JSON text) and done (an operation finished) — and it answers the two
// functions the host drives the plugin with: dispatch (start an operation) and settle (a host call's result arrived).
// The `astra` module (astra.js) reads what it needs from the internal object this leaves on globalThis.
(call, sync, done, context, g) => {
  'use strict';
  const stringify = JSON.stringify, parse = JSON.parse;
  // the 'test' context (astra plugin test, design/plugin-v2 06 §4): the plugin's tests run in this sandbox against the real
  // host calls, but what belongs to a shell's session (asks, data channels, flushes) and the settings / messages a test
  // gives are answered here, from what @astragate/plugin-sdk/testing set on `testHost`.
  const testHost = { answers: {}, config: null, messages: null, data: [], flushed: [], asked: [] };
  if (context === 'test') {
    const realCall = call, realSync = sync;
    const local = (name, argsJson) => {
      const args = argsJson ? parse(argsJson) : null;
      switch (name) {
        case 'ui.ask': {
          testHost.asked.push(args.effect);
          const a = testHost.answers[args.effect.type];
          return { value: typeof a === 'function' ? a(args.effect) : a === undefined ? null : a };
        }
        case 'ui.flush': testHost.flushed.push(args.state); return { value: null };
        case 'ui.data': testHost.data.push(args); return { value: null };
        case 'ui.refresh': return { value: null };
        case 'config.get': return testHost.config ? { value: testHost.config } : undefined;
        case 'i18n.messages': return testHost.messages ? { value: testHost.messages } : undefined;
        default: return undefined;
      }
    };
    let nextLocal = -1;
    call = (name, argsJson) => {
      const r = local(name, argsJson);
      if (!r) return realCall(name, argsJson);
      const ticket = nextLocal--;
      Promise.resolve(r.value).then((v) => settle(ticket, true, stringify(v === undefined ? null : v)), (e) => settle(ticket, false, stringify({ code: 'failed', message: String(e && e.message || e) })));
      return ticket;
    };
    sync = (name, argsJson) => {
      const r = local(name, argsJson);
      return r ? stringify({ result: r.value === undefined ? null : r.value }) : realSync(name, argsJson);
    };
  }
  const define = Object.defineProperty;
  const tickets = new Map();
  const views = new Map();
  const exportsByName = new Map();
  const commands = new Map();
  const jobs = new Map();
  const listeners = new Map();
  const sessions = new Map();
  const providerImpls = new Map();
  const replies = new Map(); // in-flight provider replies by host id: the state their transformResponse calls share
  const providerAccount = (op) => op.account ? {
    ...op.account,
    secret: () => hostCall('providers.accounts.secret', { provider: op.provider, key: op.account.key }),
  } : null;
  const clientImpls = new Map();
  let plugin = null;
  let hooks = null;
  let locale = 'en';
  let messages = null;

  const errorOf = (e) => {
    if (e !== null && typeof e === 'object') {
      const out = { name: typeof e.name === 'string' ? e.name : 'Error', message: typeof e.message === 'string' ? e.message : String(e) };
      if (typeof e.code === 'string') out.code = e.code;
      if (e.details !== undefined) { try { out.details = parse(stringify(e.details)); } catch {} }
      return out;
    }
    return { name: 'Error', message: String(e) };
  };

  class AstraError extends Error {
    constructor(code, message, details) { super(message); this.name = 'AstraError'; this.code = code; if (details !== undefined) this.details = details; }
  }

  // An async host call: a promise the host settles through settle(ticket, ok, json).
  const hostCall = (name, args) => context === 'gateway'
    ? Promise.reject(new AstraError('not_supported', name + ' is not available in gateway hooks: read ctx.shared, written by the main context'))
    : new Promise((resolve, reject) => {
    const ticket = call(name, stringify(args === undefined ? null : args));
    tickets.set(ticket, { resolve, reject });
  });

  // A synchronous host call: its JSON result, or the AstraError it failed with.
  const hostSync = (name, args) => {
    const r = parse(sync(name, stringify(args === undefined ? null : args)));
    if (r && r.error) throw new AstraError(r.error.code, r.error.message, r.error.details);
    return r ? r.result : undefined;
  };

  const settle = (ticket, ok, json) => {
    const t = tickets.get(ticket);
    if (!t) return;
    tickets.delete(ticket);
    const value = json === undefined || json === null || json === '' ? undefined : parse(json);
    if (ok) t.resolve(value);
    else t.reject(new AstraError(value && value.code || 'unavailable', value && value.message || 'host call failed', value && value.details));
  };

  // ---------- i18n
  const format = (text, params) => {
    if (!params) return text;
    // {n, plural, one {…} other {…}} and {n, select, a {…} other {…}}, then {name}
    let out = '';
    let i = 0;
    while (i < text.length) {
      const open = text.indexOf('{', i);
      if (open < 0) { out += text.slice(i); break; }
      out += text.slice(i, open);
      let depth = 0, j = open;
      for (; j < text.length; j++) { if (text[j] === '{') depth++; else if (text[j] === '}') { depth--; if (depth === 0) break; } }
      const inner = text.slice(open + 1, j);
      i = j + 1;
      const m = /^\s*([A-Za-z0-9_]+)\s*(?:,\s*(plural|select)\s*,([\s\S]*))?$/.exec(inner);
      if (!m) { out += '{' + inner + '}'; continue; }
      const value = params[m[1]];
      if (!m[2]) { out += value === undefined ? '{' + m[1] + '}' : String(value); continue; }
      const cases = {};
      const re = /\s*(=?[A-Za-z0-9_]+)\s*\{/g;
      let rest = m[3], k = 0;
      while (k < rest.length) {
        re.lastIndex = k;
        const c = re.exec(rest);
        if (!c || c.index !== k && rest.slice(k, c.index).trim() !== '') break;
        let d = 1, e = re.lastIndex;
        for (; e < rest.length && d > 0; e++) { if (rest[e] === '{') d++; else if (rest[e] === '}') d--; }
        cases[c[1]] = rest.slice(re.lastIndex, e - 1);
        k = e;
      }
      let pick;
      if (m[2] === 'plural') {
        const n = Number(value);
        pick = cases['=' + n] ?? (n === 1 ? cases.one : undefined) ?? (n === 0 ? cases.zero : undefined) ?? cases.other ?? '';
        pick = pick.replace(/#/g, String(n));
      } else pick = cases[String(value)] ?? cases.other ?? '';
      out += format(pick, params);
    }
    return out;
  };
  const translate = (lang, key, params) => {
    if (messages === null) messages = hostSync('i18n.messages') || {};
    const base = String(lang || 'en').split(/[-_]/)[0];
    const text = (messages[lang] && messages[lang][key]) ?? (messages[base] && messages[base][key]) ?? (messages.en && messages.en[key]) ?? key;
    return format(String(text), params);
  };

  // ---------- operations
  const ctxOf = (op) => ({ plugin: { id: op.plugin.id, version: op.plugin.version }, reason: op.reason, upgrade: op.upgrade, subscriptions: [] });
  let active = null;

  const effectsCtx = (op, effects, session) => {
    const ask = (effect) => hostCall('ui.ask', { session: op.session, effect });
    const ctx = {
      params: op.params || {}, client: op.client || {}, local: op.local || {}, session: op.session, query: op.query || {},
      t: (key, params) => translate((op.client && op.client.locale) || locale, key, params),
      toast: (o) => { effects.push({ type: 'toast', ...(typeof o === 'string' ? { message: o } : o) }); },
      navigate: (to) => { effects.push({ type: 'navigate', to }); },
      back: () => { effects.push({ type: 'back' }); },
      openExternal: (url) => { effects.push({ type: 'openExternal', url: String(url) }); },
      copy: (text) => { effects.push({ type: 'copy', text: String(text) }); },
      download: (o) => {
        const e = { type: 'download', name: String(o.name || 'download'), mime: o.type || 'application/octet-stream' };
        if (o.bytes instanceof Uint8Array) e.base64 = b64(o.bytes); else e.text = String(o.text ?? '');
        effects.push(e);
      },
      focus: (key) => { effects.push({ type: 'focus', key: String(key) }); },
      scrollTo: (key) => { effects.push({ type: 'scrollTo', key: String(key) }); },
      close: (result) => { effects.push({ type: 'close', result: result === undefined ? null : result }); },
      fail: (errors) => { effects.push({ type: 'fail', errors: errors || {} }); },
      setQuery: (q) => { effects.push({ type: 'setQuery', query: q || {} }); },
      flush: () => { if (session && session.state) call('ui.flush', stringify({ session: op.session, state: session.state })); },
      refresh: () => { sync('ui.refresh', stringify({ session: op.session })); },
      confirm: (o) => ask({ type: 'confirm', ...(typeof o === 'string' ? { title: o } : o) }).then((v) => v === true),
      prompt: (o) => ask({ type: 'prompt', ...(typeof o === 'string' ? { title: o } : o) }).then((v) => (typeof v === 'string' ? v : null)),
      pickFile: (o) => ask({ type: 'pickFile', ...(o || {}) }).then((v) => v || null),
      openSheet: (view, params, opts) => ask({ type: 'openSheet', view: String(view), params: params || {}, ...(opts || {}) }),
      openDialog: (view, params, opts) => ask({ type: 'openDialog', view: String(view), params: params || {}, ...(opts || {}) }),
      collection: (name) => collectionOps(op.session, String(name)),
    };
    return ctx;
  };

  const collectionOps = (session, name) => {
    const send = (kind, payload) => { sync('ui.data', stringify({ session, collection: name, op: kind, ...payload })); };
    return {
      replace: (items, meta) => send('replace', { items: items || [], meta: meta || {} }),
      append: (items) => send('append', { items: Array.isArray(items) ? items : [items] }),
      prepend: (items) => send('prepend', { items: Array.isArray(items) ? items : [items] }),
      update: (key, patch) => send('update', { key, patch }),
      remove: (key) => send('remove', { key }),
      clear: () => send('clear', {}),
      setChildren: (key, nodes) => send('children', { key, items: nodes || [] }),
    };
  };

  // bytes <-> base64 / UTF-8 text: the host does the work (globals.js, encoding.* calls); a bad UTF-8 sequence reads as U+FFFD
  const b64 = (bytes) => g.toText(bytes, 'base64');
  const unb64 = (text) => g.fromText(text, 'base64');
  const utf8 = (b) => g.utf8dec(b, false, true);

  const viewOf = (name) => {
    const v = views.get(name);
    if (!v) throw new AstraError('not_found', 'no view "' + name + '" (a view is an export made by ui.defineView)');
    return v;
  };

  const clone = (v) => (v === undefined ? undefined : parse(stringify(v)));
  const actionNames = (v) => Object.keys(v.actions || {});

  const run = async (op) => {
    switch (op.op) {
      case 'activate': {
        if (op.client) locale = op.client.locale || locale;
        const ctx = ctxOf(op);
        if (plugin && typeof plugin.activate === 'function') await plugin.activate(ctx);
        active = ctx;
        return { commands: [...commands.keys()], jobs: [...jobs.keys()], events: [...listeners.keys()] };
      }
      case 'deactivate': {
        if (plugin && typeof plugin.deactivate === 'function') await plugin.deactivate();
        if (active) for (const s of active.subscriptions) { try { typeof s === 'function' ? s() : s && s.dispose && s.dispose(); } catch {} }
        active = null;
        return null;
      }
      case 'view.init': {
        const v = viewOf(op.view);
        const session = { state: undefined };
        sessions.set(op.session, session);
        const ctx = effectsCtx(op, [], session);
        let state = typeof v.init === 'function' ? await v.init(ctx) : (v.state !== undefined ? clone(v.state) : {});
        if (state === undefined) state = {};
        if (typeof v.subscribe === 'function') {
          const sub = v.subscribe(ctx);
          session.dispose = typeof sub === 'function' ? sub : sub && typeof sub.dispose === 'function' ? () => sub.dispose() : undefined;
        }
        return { state };
      }
      case 'view.render': {
        const v = viewOf(op.view);
        locale = (op.client && op.client.locale) || locale;
        const ctx = effectsCtx(op, [], null);
        if (typeof v.render !== 'function') throw new AstraError('invalid_argument', 'view "' + op.view + '" has no render');
        const tree = v.render(op.state, ctx);
        if (tree && typeof tree.then === 'function') throw new AstraError('invalid_argument', 'render must not be async: load data in init or an action');
        return { tree, actions: actionNames(v) };
      }
      case 'view.action': {
        const v = viewOf(op.view);
        const fn = v.actions && v.actions[op.action];
        if (typeof fn !== 'function') throw new AstraError('not_found', 'view "' + op.view + '" has no action "' + op.action + '"');
        const session = sessions.get(op.session) || { state: undefined };
        sessions.set(op.session, session);
        session.state = op.state;
        const effects = [];
        const ctx = effectsCtx(op, effects, session);
        const r = await fn(session.state, op.args || {}, ctx);
        // an action changes s in place, or returns the new state
        const state = r !== undefined && r !== null && typeof r === 'object' && !Array.isArray(r) ? r : session.state;
        return { state, effects };
      }
      case 'view.refresh': {
        const v = viewOf(op.view);
        if (typeof v.refresh !== 'function') return { state: op.state };
        const session = sessions.get(op.session) || { state: undefined };
        session.state = op.state;
        const effects = [];
        await v.refresh(session.state, effectsCtx(op, effects, session));
        return { state: session.state, effects };
      }
      case 'view.dispose': {
        const s = sessions.get(op.session);
        sessions.delete(op.session);
        if (s && s.dispose) s.dispose();
        return null;
      }
      case 'command': {
        const fn = commands.get(op.id);
        if (!fn) throw new AstraError('not_found', 'no command "' + op.id + '"');
        const r = await fn(op.args === undefined ? {} : op.args, { client: op.client || {} });
        return r === undefined ? null : r;
      }
      case 'job': {
        const fn = jobs.get(op.id);
        if (!fn) throw new AstraError('not_found', 'no handler for job "' + op.id + '" (jobs.handle)');
        await fn({ scheduledAt: op.scheduledAt, attempt: op.attempt || 1 });
        return null;
      }
      case 'event': {
        const fns = listeners.get(op.name) || [];
        for (const fn of [...fns]) await fn(op.payload);
        return null;
      }
      // ---- 2.1: providers and clients (design/plugin-v2 08)
      case 'provider.prepare':
      case 'provider.models':
      case 'provider.refresh':
      case 'provider.usage': {
        const impl = providerImpls.get(op.provider);
        if (!impl) throw new AstraError('not_found', 'provider "' + op.provider + '" is not registered (providers.register in activate)');
        const what = op.op.slice('provider.'.length);
        const fn = impl[what];
        if (typeof fn !== 'function') {
          if (what === 'prepare') throw new AstraError('not_supported', 'provider "' + op.provider + '" has no prepare');
          return null;
        }
        const account = providerAccount(op);
        // prepare gets the request (+ the plugin-local provider id), refresh the failing reply { status, body }
        const r = what === 'prepare' ? await fn({ ...op.request, provider: op.provider }, account)
          : what === 'refresh' ? await fn(account, op.request || {}) : await fn(account);
        // a prepare body of bytes (protobuf, Connect frames, …) crosses as base64; content-type comes from its headers
        if (what === 'prepare' && r && typeof r === 'object' && (r.body instanceof Uint8Array || r.body instanceof ArrayBuffer)) {
          const { body, ...rest } = r;
          return { ...rest, bodyBase64: b64(body instanceof ArrayBuffer ? new Uint8Array(body) : body) };
        }
        return r === undefined ? null : r;
      }
      // ---- 2.1: a provider's reply, frame by frame (design/plugin-v2 08 §2.2); the host frames, the plugin translates
      case 'provider.response.start':
      case 'provider.response.frame':
      case 'provider.response.end':
      case 'provider.response.dispose': {
        const impl = providerImpls.get(op.provider);
        const tr = impl && impl.transformResponse;
        const phase = op.op.slice('provider.response.'.length);
        if (phase === 'dispose') {
          // the reply is over (ended, failed or the client went away): the plugin stops what it runs beside it (timers, side calls)
          const st = replies.get(op.rid);
          replies.delete(op.rid);
          if (st && tr && typeof tr.dispose === 'function') await tr.dispose({ request: op.request || {}, account: providerAccount(op), state: st.state });
          return null;
        }
        if (!tr || typeof tr !== 'object') throw new AstraError('not_supported', 'provider "' + op.provider + '" declares a reply in its manifest but registers no transformResponse');
        let st = replies.get(op.rid);
        if (!st) {
          if (phase !== 'start') throw new AstraError('not_found', 'no reply "' + op.rid + '" in progress');
          if (replies.size >= 256) replies.delete(replies.keys().next().value);
          // prepare's { state } (08 §2.2), so the reply knows what the request was built with
          st = { state: op.state && typeof op.state === 'object' && !Array.isArray(op.state) ? { ...op.state } : {} };
          replies.set(op.rid, st);
        }
        const ctx = { request: op.request || {}, account: providerAccount(op), state: st.state };
        const fn = tr[phase];
        if (typeof fn !== 'function') {
          if (phase === 'frame') throw new AstraError('not_supported', 'transformResponse of "' + op.provider + '" has no frame(frame, ctx)');
          return phase === 'start' ? null : { frames: [] };
        }
        // the state lives until dispose (after end, or instead of it), which the host always sends
        const r = phase === 'start' ? await fn(op.response, ctx) : phase === 'frame' ? await fn(op.frame, ctx) : await fn(ctx);
        if (phase === 'start') return r === undefined ? null : r;
        return { frames: r === undefined || r === null ? [] : Array.isArray(r) ? r : [r] };
      }
      case 'client.connect': {
        const impl = clientImpls.get(op.client);
        if (!impl || typeof impl.connect !== 'function') return null;
        const r = await impl.connect(op.input);
        return r === undefined ? null : r;
      }
      case 'test.run': {
        if (context !== 'test') throw new AstraError('not_supported', 'tests run only in the test context');
        const runTests = globalThis.__astraTestRun;
        return typeof runTests === 'function' ? await runTests(op) : { tests: [] };
      }
      default:
        throw new AstraError('not_supported', 'unknown operation ' + op.op);
    }
  };

  const dispatch = (json) => {
    const op = parse(json);
    let p;
    try { p = Promise.resolve(run(op)); } catch (e) { p = Promise.reject(e); }
    p.then(
      (v) => { let out; try { out = stringify(v === undefined ? null : v); } catch (e) { done(op.opId, false, stringify(errorOf(new AstraError('invalid_argument', 'result is not JSON: ' + errorOf(e).message)))); return; } done(op.opId, true, out); },
      (e) => { done(op.opId, false, stringify(errorOf(e))); });
  };

  const adopt = (name, value) => {
    exportsByName.set(name, value);
    if (value && value.__astraView === true) views.set(name, value);
    if (name === 'default' && value && value.__astraHooks === true) hooks = value;
    else if (name === 'default' && value && typeof value === 'object') plugin = value;
    if (name === 'hooks' && value && value.__astraHooks === true) hooks = value;
  };

  const internal = {
    context, hostCall, hostSync, AstraError, translate, format, b64, unb64, utf8, collectionOps, bytes: g,
    commands, jobs, listeners, getLocale: () => locale, providers: providerImpls, clients: clientImpls,
    // the in-sandbox test harness drives views and commands through the same operations the host does
    ...(context === 'test' ? { run, adopt, testHost } : {}),
  };
  define(globalThis, '__astraV2', { value: internal, enumerable: false, configurable: true, writable: false });
  return { dispatch, settle, adopt, hooks: () => hooks };
}
