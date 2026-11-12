import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import zlib from 'node:zlib';
import { describe, expect, it } from 'vitest';
// @ts-expect-error — plain JS tool without typings
import { build, init, pack, validate } from '../bin/astra-plugin.mjs';
import { mount, runHook } from '../src/testing.mjs';

const temp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'astra-plugin-'));

describe('astra-plugin', () => {
  it('init writes a plugin that validates, packs and runs', async () => {
    const dir = path.join(temp(), 'hello');
    expect(init(dir, 'acme.hello')).toBe('acme.hello');
    expect(validate(dir).errors).toEqual([]);

    const r = pack(dir);
    expect(r.sha256).toMatch(/^[0-9a-f]{64}$/);
    expect(r.integrity).toMatch(/^sha512-/);
    const tar = zlib.gunzipSync(fs.readFileSync(r.file)).toString('latin1');
    expect(tar).toContain('package/astra.plugin.json');
    expect(tar).toContain('package/main.js');
    expect(pack(dir).sha256).toBe(r.sha256); // same tree, same bytes

    const main = await import(path.join(dir, 'main.js'));
    const view = await mount(main.home, { storage: { seen: 4 } });
    expect(view.find('MetricCard')?.props.value).toBe(4);
    const hooks = (await import(path.join(dir, 'gateway.js'))).default;
    expect((await runHook(hooks, 'count', 'request', { messages: [] })).counts).toEqual({ seen: 1 });
  });

  it('validate pairs a provider reply block with transformResponse', () => {
    const make = (reply: unknown, code: string) => {
      const dir = temp();
      fs.writeFileSync(path.join(dir, 'main.js'), code);
      fs.writeFileSync(path.join(dir, 'astra.plugin.json'), JSON.stringify({
        manifestVersion: 2, id: 'acme.p', version: '1.0.0', name: 'x', main: 'main.js', permissions: ['providers'],
        contributes: { providers: [{ id: 'p', protocols: ['chat'], ...(reply === undefined ? {} : { reply }) }] },
      }));
      return validate(dir).errors as string[];
    };
    const withT = "import { providers } from 'astra'; providers.register('p', { prepare() {}, transformResponse: { frame: (f) => f.json } });";
    const without = "import { providers } from 'astra'; providers.register('p', { prepare() {} });";
    expect(make({ framing: 'sse', output: 'chat' }, withT)).toEqual([]);
    expect(make(undefined, withT).join()).toMatch(/no provider has a "reply" block/);
    expect(make({ framing: 'ndjson' }, without).join()).toMatch(/registers no transformResponse/);
    expect(make({ framing: 'xml', output: 'gemini' }, withT).join()).toMatch(/reply\.framing[\s\S]*reply\.output/);
  });

  it('validate says what is wrong', () => {
    const dir = temp();
    fs.writeFileSync(path.join(dir, 'astra.plugin.json'), JSON.stringify({
      manifestVersion: 2, id: 'Bad', version: '1', name: 'x',
      permissions: ['everything', 'net:http://x.example'],
      contributes: { 'ui.pages': [{ id: 'p' }], 'gateway.hooks': [{ id: 'h', on: ['text'] }] },
    }));
    const { errors } = validate(dir);
    expect(errors).toEqual(expect.arrayContaining([
      expect.stringContaining('id must be'),
      expect.stringContaining('semver'),
      expect.stringContaining('unknown permission "everything"'),
      expect.stringContaining('https origin'),
      expect.stringContaining('needs the permission "ui"'),
      expect.stringContaining('needs view "ir"'),
      expect.stringContaining('main is required'),
      expect.stringContaining('gateway is required'),
    ]));
  });

  it('validate checks the code against the permissions, events and translations', () => {
    const dir = temp();
    const write = (rel: string, text: string) => {
      fs.mkdirSync(path.dirname(path.join(dir, rel)), { recursive: true });
      fs.writeFileSync(path.join(dir, rel), text);
    };
    write('astra.plugin.json', JSON.stringify({
      manifestVersion: 2, id: 'acme.scan', version: '1.0.0', name: 'Scan', main: 'main.js', gateway: 'gateway.js',
      permissions: ['ui', 'secrets', 'net:https://api.acme.example', 'gateway.request:read'],
      optionalPermissions: ['notifications'],
      contributes: { 'ui.pages': [{ id: 'p' }], 'gateway.hooks': [{ id: 'g', on: ['request'] }], events: [{ name: 'hit' }] },
    }));
    write('main.js', `import * as fs from './fs.js';
import { storage as kv, http, events, permissions, t, data } from 'astra';
export const p = 1;
async function go() {
  await kv.get('x');
  await http.fetch('https://api.acme.example/v1');
  await http.fetch('https://evil.example/steal');
  events.emit('hit'); events.emit('miss');
  await permissions.request(['notifications', 'jobs']);
  await data.requests.query({});
  return t('hello') + t('only.en');
}`);
    write('fs.js', 'export const x = 1;');
    write('gateway.js', `import { gateway } from 'astra';
export default gateway.defineHooks({ g: { onRequest(req, ctx) { ctx.reject(403); ctx.emit('hit'); return req; } } });`);
    write('locales/en.json', JSON.stringify({ only: { en: 'x' } }));
    write('locales/zh.json', '{}');
    write('test/main.test.js', "import { secrets } from 'astra'; secrets.get('ignored in tests');");

    const { errors, warnings } = validate(dir);
    const err = (s: string) => expect(errors.some((e: string) => e.includes(s)), `${s}\n${errors.join('\n')}`).toBe(true);
    const warn = (s: string) => expect(warnings.some((w: string) => w.includes(s)), `${s}\n${warnings.join('\n')}`).toBe(true);
    err('"storage" is not in permissions');
    err('reaches https://evil.example');
    err('emits "miss"');
    err('permissions.request("jobs")');
    err('"data.requests:read" is not in permissions');
    err('"gateway.request:reject" is not in permissions');
    err('"events" is not in permissions');
    err('t("hello") has no text');
    warn('t("only.en") is missing from locales/zh.json');
    warn('"secrets" is declared but the code never uses it');
    expect(errors.some((e: string) => e.includes('api.acme.example'))).toBe(false);
  });

  it('build bundles main and gateway into single files that pack and run', async () => {
    const dir = path.join(temp(), 'b');
    init(dir, 'acme.built');
    fs.mkdirSync(path.join(dir, 'lib'));
    fs.writeFileSync(path.join(dir, 'lib', 'label.js'), "export const label = (n) => 'seen ' + n;");
    const main = fs.readFileSync(path.join(dir, 'main.js'), 'utf8')
      .replace("import { definePlugin, commands, storage, ui } from 'astra';", "import { definePlugin, commands, storage, ui } from 'astra';\nimport { label } from './lib/label.js';")
      .replace("ui.MetricCard({ label: 'Total', value: s.seen })", "ui.MetricCard({ label: label(s.seen), value: s.seen })");
    fs.writeFileSync(path.join(dir, 'main.js'), main);
    fs.mkdirSync(path.join(dir, 'locales'));
    fs.writeFileSync(path.join(dir, 'locales', 'en.json'), '{}');

    const r = await build(dir);
    expect(r.errors).toEqual([]);
    const out = path.join(dir, 'dist');
    const bundled = fs.readFileSync(path.join(out, 'main.js'), 'utf8');
    expect(bundled).not.toContain('./lib/label.js');
    expect(bundled).toMatch(/from ["']astra["']/);
    expect(JSON.parse(fs.readFileSync(path.join(out, 'astra.plugin.json'), 'utf8')).gateway).toBe('gateway.js');
    expect(fs.existsSync(path.join(out, 'locales', 'en.json'))).toBe(true);
    expect(fs.existsSync(path.join(out, 'test'))).toBe(false);
    expect(pack(out).sha256).toMatch(/^[0-9a-f]{64}$/);
    // the source tree's pack leaves dist/ out
    const src = zlib.gunzipSync(fs.readFileSync(pack(dir).file)).toString('latin1');
    expect(src).not.toContain('package/dist/');

    const built = await import(path.join(out, 'main.js'));
    const view = await mount(built.home, { storage: { seen: 2 } });
    expect(view.find('MetricCard')?.props.label).toBe('seen 2');
  });

  it('build --watch rebuilds when a source changes', async () => {
    const dir = path.join(temp(), 'w');
    init(dir, 'acme.watched');
    const rounds: string[][] = [];
    const r = await build(dir, { watch: true, onBuild: (x: { errors: string[] }) => rounds.push(x.errors) });
    try {
      const first = rounds.length;
      fs.appendFileSync(path.join(dir, 'gateway.js'), '\nexport const marker = "changed";\n');
      for (let i = 0; i < 100 && !fs.readFileSync(path.join(dir, 'dist', 'gateway.js'), 'utf8').includes('changed'); i++) await new Promise((res) => setTimeout(res, 50));
      expect(fs.readFileSync(path.join(dir, 'dist', 'gateway.js'), 'utf8')).toContain('changed');
      expect(rounds.length).toBeGreaterThan(first);
    } finally {
      await r.dispose();
    }
  });
});
