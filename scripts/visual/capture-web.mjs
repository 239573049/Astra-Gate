#!/usr/bin/env node
// Visual-regression capture of the web admin UI from FIXTURE data (no astra-server): the built SPA (web/dist) is loaded in
// headless Chromium and every /api/** call is answered by this script's own HTTP server (plan avalonia-client §23).
// Output: artifacts/visual/web/<page>__<state>__<theme>.png   (artifacts/ is gitignored)
//
//   node scripts/visual/capture-web.mjs [--only overview,tokens__empty] [--theme light|dark|both] [--scale 1|2]
//                                       [--recordings artifacts/visual/fixtures] [--legacy] [--set default]
//                                       [--out artifacts/visual/web] [--list] [--verbose]
//
// Two modes:
//  * replay (default when artifacts/visual/fixtures/ holds recordings): the desktop scene run with ASTRA_VISUAL_RECORD=1
//    (tests/Astra.Desktop.UiTests/VisualRecorder.cs) wrote, per scene, every API answer the desktop page was served, its route
//    and its clock. Each scene the step table (scripts/visual/web-steps.mjs) mirrors is served EXACTLY those answers
//    (same status/body, hangs stay pending, event streams send the same frames and stay open when they were open), falling
//    back to the default fixture set for a call the desktop never made; the SPA opens the equivalent route, the scene's
//    web-side steps run (open a sheet, switch a tab, type, scroll) and the frame is captured under the desktop scene's name.
//    Scenes the web cannot show are web-steps.mjs DESKTOP_ONLY (with the reason) and are skipped.
//  * --legacy: the original fixed list below (LEGACY), from tests/fixtures/ui/<set> only.
//
// Prerequisites: playwright's chromium headless shell cached (see docs/scripts/snap.mjs), docs/node_modules installed
// (playwright-core), and web/dist built (`pnpm --filter @aidotnet/web build`). Scale default 1 (1440x900 pixels);
// ASTRA_VISUAL_SCALE or --scale 2 gives snap.mjs's @2x. The Avalonia side reads the same variable.
import { createServer } from 'node:http';
import { readFileSync, existsSync, statSync, mkdirSync, readdirSync } from 'node:fs';
import { join, dirname, extname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchBrowser, settle } from '../../docs/scripts/lib/snap-core.mjs';
import { STEPS, DESKTOP_ONLY, NOT_MIRRORED, webPathFor, helpers } from './web-steps.mjs';

const repo = dirname(dirname(dirname(fileURLToPath(import.meta.url))));
const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : def; };
const has = (name) => args.includes(`--${name}`);
const scale = Number(opt('scale', process.env.ASTRA_VISUAL_SCALE ?? '1'));
const set = opt('set', 'default');
const outDir = resolve(repo, opt('out', 'artifacts/visual/web'));
const only = opt('only', '')?.split(',').filter(Boolean) ?? [];
const themeArg = opt('theme', 'both');
const themes = themeArg === 'both' ? ['light', 'dark'] : [themeArg];
const recDir = resolve(repo, opt('recordings', 'artifacts/visual/fixtures'));
const verbose = has('verbose');
const replay = !has('legacy') && existsSync(recDir) && readdirSync(recDir).some((f) => f.endsWith('.json'));
const fixDir = join(repo, 'tests', 'fixtures', 'ui', set);
const VIEW = { width: 1440, height: 900 };

const index = JSON.parse(readFileSync(join(fixDir, 'index.json'), 'utf8'));

/** Legacy list: [page, state, path, { sidebar?, override? }] — override: { '<api path>': body | { status, body } }. */
const emptyPage = { items: [], total: 0, page: 1, pageSize: 50, totalCostNanoUsd: 0 };
const LEGACY = [
  ['overview', 'data', '/'],
  ['tokens', 'data', '/tokens'],
  ['tokens', 'empty', '/tokens', { override: { '/api/tokens': [] } }],
  ['providers', 'data', '/providers'],
  ['providers', 'empty', '/providers', { override: { '/api/providers': [] } }],
  ['models', 'data', '/models'],
  ['requests', 'data', '/requests'],
  ['requests', 'empty', '/requests', { override: { '/api/requests': emptyPage } }],
  ['settings', 'data', '/settings'],
  ['shell', 'expanded', '/tokens', { sidebar: 'expanded' }],
  ['shell', 'collapsed', '/tokens', { sidebar: 'collapsed' }],
];

/** The default fixture set's answer (query-aware), or null. */
function fixtureFor(method, url) {
  for (const r of index.routes) {
    if (r.method !== method || r.path !== url.pathname) continue;
    if (r.query && !Object.entries(r.query).every(([k, v]) => url.searchParams.get(k) === v)) continue;
    return { status: 200, body: readFileSync(join(fixDir, r.file), 'utf8') };
  }
  return null;
}

const sortedQuery = (sp) => [...sp.entries()].sort(([a, x], [b, y]) => (a === b ? (x < y ? -1 : 1) : a < b ? -1 : 1)).map(([k, v]) => `${k}=${v}`).join('&');

/**
 * The per-scene answer table built from a recording. Lookup order for one call:
 *  1. the scene's per-path override (FixtureSet.Override, GET only, query ignored — as on the desktop)
 *  2. a recorded call with the same method, path and query (query order ignored); repeated calls replay in order, then stick on the last
 *  3. a recorded call with the same method and path whose query agrees on every key both have
 *  4. the default fixture set (the desktop never asked for this exact URL)
 *  5. any recorded call with the same method and path
 *  6. 404 { error: 'no fixture' }
 * A recorded hang matches by method + path (any query) and never answers.
 */
function answerTable(rec) {
  const calls = (rec?.calls ?? []).filter((c) => !c.cancelled);
  const parsed = calls.map((c) => ({ ...c, u: new URL(c.url, 'http://x') }));
  const counters = new Map();
  const overrides = rec?.overrides ?? {};
  const hangs = parsed.filter((c) => c.hang);
  return function answer(method, url) {
    if (hangs.some((c) => c.method === method && c.u.pathname === url.pathname)) return { kind: 'hang' };
    if (method === 'GET' && url.pathname in overrides) {
      // An override answers what the desktop's handler chain answered: a recorded exchange that differs (a scene interceptor in
      // front of the fixtures) wins, so look for one first.
      const exact = parsed.find((c) => !c.hang && c.method === method && c.u.pathname === url.pathname && sortedQuery(c.u.searchParams) === sortedQuery(url.searchParams));
      if (!exact) return { kind: 'override', status: overrides[url.pathname].status, body: overrides[url.pathname].body };
    }
    const done = parsed.filter((c) => !c.hang && c.method === method && c.u.pathname === url.pathname);
    const key = `${method} ${url.pathname}?${sortedQuery(url.searchParams)}`;
    const same = done.filter((c) => sortedQuery(c.u.searchParams) === sortedQuery(url.searchParams));
    const pick = (list, k) => {
      const n = counters.get(k) ?? 0;
      counters.set(k, n + 1);
      return list[Math.min(n, list.length - 1)];
    };
    if (same.length) return { kind: 'recorded', ...asAnswer(pick(same, key)) };
    const compatible = done.filter((c) => [...c.u.searchParams.keys()].every((k) => !url.searchParams.has(k) || url.searchParams.get(k) === c.u.searchParams.get(k))
      && [...url.searchParams.keys()].every((k) => !c.u.searchParams.has(k) || url.searchParams.get(k) === c.u.searchParams.get(k)));
    if (compatible.length) return { kind: 'compatible', ...asAnswer(pick(compatible, key)) };
    const fx = fixtureFor(method, url);
    if (fx) return { kind: 'fixture', ...fx };
    if (done.length) return { kind: 'path-only', ...asAnswer(done[done.length - 1]) };
    // a mutation the desktop never sent (the web scene's step submits something): accept it
    if (method !== 'GET') return { kind: 'unrecorded-mutation', status: 200, body: '{}' };
    return { kind: 'miss', status: 404, body: JSON.stringify({ error: 'no fixture' }) };
  };
}

function asAnswer(c) {
  if (c.stream) return { status: c.status, stream: true, frames: c.frames ?? '', open: !!c.open, contentType: c.contentType };
  return { status: c.status, body: c.body ?? '', contentType: c.contentType };
}

const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml', '.json': 'application/json', '.woff2': 'font/woff2', '.woff': 'font/woff', '.ttf': 'font/ttf', '.ico': 'image/x-icon', '.webp': 'image/webp' };

/** The one HTTP server: web/dist (SPA fallback) plus /api/** from the active scene's answer table. */
function serve(state) {
  const dist = join(repo, 'web', 'dist');
  if (!existsSync(join(dist, 'index.html'))) throw new Error('web/dist is not built — run `pnpm --filter @aidotnet/web build`');
  const sockets = new Set();
  const server = createServer((req, res) => {
    const url = new URL(req.url, 'http://x');
    if (url.pathname.startsWith('/api/')) {
      let body = '';
      req.on('data', (c) => (body += c));
      req.on('end', () => api(req.method, url, body, res));
      return;
    }
    const p = decodeURIComponent(url.pathname);
    let file = join(dist, p);
    if (!file.startsWith(dist) || !existsSync(file) || statSync(file).isDirectory()) file = join(dist, 'index.html'); // SPA fallback
    res.writeHead(200, { 'content-type': MIME[extname(file)] ?? 'application/octet-stream' });
    res.end(readFileSync(file));
  });
  server.on('connection', (s) => { sockets.add(s); s.on('close', () => sockets.delete(s)); });

  function api(method, url, reqBody, res) {
    const a = state.answer(method, url);
    state.log.push({ method, url: url.pathname + url.search, kind: a.kind, status: a.status, reqBody });
    if (a.kind === 'hang') { state.open.add(res); return; }
    if (url.pathname === '/api/requests/live' && a.kind === 'fixture') a.kind = 'live-default';
    if (a.stream) {
      res.writeHead(a.status, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive' });
      res.write(a.frames || ': fixture\n\n');
      if (a.open) state.open.add(res); else res.end();
      return;
    }
    if (url.pathname === '/api/requests/live' && (a.kind === 'miss' || a.kind === 'live-default')) {
      // the desktop fixture handler refuses the live feed (503); with no recording, do the same
      res.writeHead(503, { 'content-type': 'application/json' });
      res.end('{"error":"no live feed in fixtures"}');
      return;
    }
    const type = a.contentType && a.contentType !== 'application/json' ? a.contentType : 'application/json; charset=utf-8';
    res.writeHead(a.status ?? 200, { 'content-type': type });
    res.end(typeof a.body === 'string' ? a.body : JSON.stringify(a.body));
  }

  return new Promise((ok) => server.listen(0, '127.0.0.1', () => ok({
    url: `http://127.0.0.1:${server.address().port}`,
    endScene() { for (const r of state.open) { try { r.destroy(); } catch { /* gone */ } } state.open.clear(); },
    close() { for (const s of sockets) s.destroy(); server.close(); },
  })));
}

// ------------------------------------------------------------------ scene list
function loadRecordings() {
  const out = new Map();
  for (const f of readdirSync(recDir).filter((f) => f.endsWith('.json')).sort()) {
    const rec = JSON.parse(readFileSync(join(recDir, f), 'utf8'));
    out.set(rec.scene, rec);
  }
  return out;
}

const matches = (name) => !only.length || only.some((s) => name === s || name.startsWith(`${s}__`));

function replayScenes() {
  const recs = loadRecordings();
  const scenes = [];
  const summary = { covered: [], desktopOnly: [], notMirrored: [], noSteps: [] };
  for (const [name, rec] of recs) {
    if (DESKTOP_ONLY[name]) { summary.desktopOnly.push(name); continue; }
    if (NOT_MIRRORED[name]) { summary.notMirrored.push(name); continue; }
    const step = STEPS[name];
    if (!step) { summary.noSteps.push(name); continue; }
    summary.covered.push(name);
    if (matches(name)) scenes.push({ name, rec, step });
  }
  return { scenes, summary };
}

function legacyScenes() {
  return LEGACY.map(([pageName, state, path, o = {}]) => {
    const overrides = Object.fromEntries(Object.entries(o.override ?? {}).map(([k, v]) => [k, { status: 200, body: JSON.stringify(v) }]));
    return { name: `${pageName}__${state}`, rec: { now: index.now, overrides, calls: [], sidebarCollapsed: o.sidebar === 'collapsed' }, step: { path } };
  }).filter((s) => matches(s.name));
}

const { scenes, summary } = replay ? replayScenes() : { scenes: legacyScenes(), summary: null };
if (summary) {
  console.log(`recordings: ${summary.covered.length + summary.desktopOnly.length + summary.notMirrored.length + summary.noSteps.length} | mirrored ${summary.covered.length} | desktop-only ${summary.desktopOnly.length} | not mirrored ${summary.notMirrored.length} | no step entry ${summary.noSteps.length}`);
  if (summary.noSteps.length) console.log(`  no step entry: ${summary.noSteps.join(' ')}`);
}
if (has('list')) process.exit(0);

// ------------------------------------------------------------------ capture
const state = { answer: answerTable(null), log: [], open: new Set() };
const host = await serve(state);
const browser = await launchBrowser(scale);
mkdirSync(outDir, { recursive: true });
let failures = 0;
const misses = new Map();
try {
  for (const theme of themes) {
    for (const { name: scene, rec, step } of scenes) {
      const name = `${scene}__${theme}`;
      state.answer = answerTable(step.patch ? step.patch(structuredClone(rec)) : rec);
      state.log = [];
      const ctx = await browser.newContext({
        viewport: step.viewport ?? VIEW, deviceScaleFactor: scale, locale: 'zh-CN', timezoneId: index.timeZone ?? 'UTC',
        colorScheme: theme, reducedMotion: 'reduce',
      });
      const page = await ctx.newPage();
      try {
        await page.clock.setFixedTime(new Date(rec.now ?? index.now));
        const sidebar = step.sidebar ?? (rec.sidebarCollapsed ? 'collapsed' : 'expanded');
        await page.addInitScript(([th, side, loc, extra]) => {
          try {
            localStorage.setItem('astra.locale', loc);
            localStorage.setItem('astra.theme', th);
            // a CSS colour (appearance.tsx uses the stored value as is): the desktop's fixed accent, macOS blue
            localStorage.setItem('astra.accent', '#007aff');
            localStorage.setItem('astra.sidebar', side);
            for (const [k, v] of Object.entries(extra ?? {})) localStorage.setItem(k, v);
          } catch { /* ignore */ }
        }, [theme, sidebar, index.locale ?? 'zh', step.storage ?? null]);
        if (step.init) await page.addInitScript(step.init);
        const path = step.path ?? webPathFor(rec);
        await page.goto(`${host.url}${path}`, { waitUntil: 'domcontentloaded' });
        // a pending request or open stream never lets the network go idle: wait plainly instead of timing out on it
        const busy = (rec.calls ?? []).some((c) => c.hang || (c.stream && c.open));
        const wait = (ms) => (busy ? page.waitForTimeout(ms + 500) : settle(page, ms));
        await wait(step.settle ?? 1500);
        if (step.run) {
          await step.run(page, helpers(page));
          await wait(step.after ?? 800);
        }
        if (step.isolate) await helpers(page).isolate(step.isolate);
        await page.mouse.move(step.mouse?.[0] ?? 1439, step.mouse?.[1] ?? 899);
        await page.waitForTimeout(150);
        await page.screenshot({ path: join(outDir, `${name}.png`) });
        const bad = state.log.filter((l) => l.kind === 'miss' || l.kind === 'unrecorded-mutation');
        for (const l of bad) misses.set(`${scene}: ${l.method} ${l.url} (${l.kind})`, 1);
        console.log(`✓ ${name}.png${bad.length ? `  (${bad.length} unmatched)` : ''}`);
        if (verbose) for (const l of state.log) console.log(`    ${l.kind.padEnd(12)} ${l.method} ${l.url}`);
      } catch (e) {
        failures++;
        console.error(`✗ ${name}: ${e.message.split('\n')[0]}`);
      } finally {
        await ctx.close();
        host.endScene();
      }
    }
  }
} finally {
  await browser.close();
  host.close();
}
if (misses.size) {
  console.log('\nAPI calls with no recorded or fixture answer (404 / accepted mutation):');
  for (const k of [...misses.keys()].sort()) console.log(`  ${k}`);
}
console.log(`\nDone → ${outDir}${failures ? ` (${failures} failed)` : ''}`);
process.exit(failures ? 1 : 0);
