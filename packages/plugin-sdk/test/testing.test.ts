import { describe, expect, it } from 'vitest';
import { definePlugin, commands, gateway, storage, ui } from 'astra';
import type { ReplyOutput } from 'astra';
import { mount, runCommand, runHook } from '../src/testing.mjs';

const counter = ui.defineView<{ n: number }>({
  init: async () => ({ n: ((await storage.get<number>('n')) ?? 0) as number }),
  render: (s) =>
    ui.Stack({ gap: '3' }, [
      ui.Heading({ value: `Count ${s.n}`, level: 2 }),
      ui.Button({ label: 'Add', onPress: 'add' }),
      ui.TextField({ name: 'step' }),
    ]),
  actions: {
    add: async (s, _args, ctx) => {
      const step = Number(ctx.local.step ?? 1);
      s.n += step;
      await storage.set('n', s.n);
      ctx.toast(`now ${s.n}`);
    },
  },
});

describe('the SDK harness', () => {
  it('mounts a view, presses and changes like a shell', async () => {
    const view = await mount(counter, { storage: { n: 2 } });
    expect(view.find('Heading')?.props.value).toBe('Count 2');
    await view.change('step', 3);
    const effects = await view.press('add');
    expect(view.state.n).toBe(5);
    expect(await view.storage.get('n')).toBe(5);
    expect(view.find('Heading')?.props.value).toBe('Count 5');
    expect(effects.some((e) => e.type === 'toast')).toBe(true);
  });

  it('runs a command registered in activate', async () => {
    const plugin = definePlugin({
      activate(ctx) {
        ctx.subscriptions.push(commands.register('double', (args: { x: number }) => args.x * 2));
      },
    });
    expect(await runCommand(plugin, 'double', { x: 21 })).toBe(42);
  });

  it('runs a gateway hook with a gateway-like ctx', async () => {
    const hooks = gateway.defineHooks({
      guard: {
        onRequest(req, ctx) {
          if (JSON.stringify(req).includes('secret')) ctx.reject(403, 'no secrets');
          ctx.count('seen');
          return req;
        },
      },
    });
    const r = await runHook(hooks, 'guard', 'request', { messages: [{ role: 'user', content: 'a secret' }] });
    expect(r.rejected).toEqual({ status: 403, message: 'no secrets' });
    expect(r.counts.seen).toBe(1);
  });
});

describe('streamed fetch in the harness', () => {
  it('reads lines across chunks', async () => {
    const { http, commands, definePlugin } = await import('astra');
    const { runCommand } = await import('../src/testing.mjs');
    const plugin = definePlugin({
      activate() {
        commands.register('lines', async () => {
          const res = await http.fetch('https://api.acme.example/sse', { stream: true });
          const out: string[] = [];
          for await (const line of res.lines()) if (line) out.push(line);
          return out;
        });
      },
    });
    const out = await runCommand(plugin, 'lines', {}, {
      permissions: ['net:https://api.acme.example'],
      fetch: async () => ({ status: 200, chunks: ['data: a\n\nda', 'ta: b\n'] }),
    });
    expect(out).toEqual(['data: a', 'data: b']);
  });
});

describe('providers and clients in the harness', () => {
  it('runs prepare with the account secret, and a client connect', async () => {
    const { providers, clients, definePlugin } = await import('astra');
    const { runProvider, runClient } = await import('../src/testing.mjs');
    const plugin = definePlugin({
      activate() {
        providers.register('acme', { prepare: async (req, account) => ({ url: 'https://api.acme.example/v1', headers: { authorization: 'Bearer ' + (await account!.secret()) } }) });
        clients.register('acme-cli', { connect: (input) => ({ 'gateway.url': input.gateway.v1 }) });
      },
    });
    const r = (await runProvider(plugin, 'acme', 'prepare', { provider: 'acme', protocol: 'chat', model: 'm', path: '/v1/chat/completions', method: 'POST', headers: {}, body: null, stream: false }, { key: 'k', secret: 's3' })) as { headers: Record<string, string> };
    expect(r.headers.authorization).toBe('Bearer s3');
    expect(await runClient(plugin, 'acme-cli', { gateway: { url: 'http://x', v1: 'http://x/v1', key: 'k' } })).toEqual({ 'gateway.url': 'http://x/v1' });
  });
});

describe('runResponse', () => {
  it('feeds frames through start / frame / end like the host', async () => {
    const { providers, definePlugin } = await import('astra');
    const { runResponse } = await import('../src/testing.mjs');
    const plugin = definePlugin({
      activate() {
        providers.register('acme', {
          prepare: async () => ({ url: 'https://api.acme.example/v1' }),
          transformResponse: {
            start: (res, ctx) => { ctx.state.n = 0; if (res.status === 402) return { error: { status: 429, message: 'quota' } }; },
            frame: (f, ctx) => { ctx.state.n++; const inner = JSON.parse((f as { json: { body: string } }).json.body); return { choices: [{ delta: { content: inner.t } }] }; },
            end: (ctx) => ({ choices: [{ delta: {}, finish_reason: 'stop' }], n: ctx.state.n }),
          },
        });
      },
    });
    const frames = [{ json: { body: '{"t":"a"}' } }, { json: { body: '{"t":"b"}' } }];
    const r = await runResponse(plugin, 'acme', { framing: 'ndjson', frames });
    expect(r.frames).toHaveLength(3);
    expect((r.frames[2] as { n: number }).n).toBe(2);
    const quota = await runResponse(plugin, 'acme', { framing: 'ndjson', frames, response: { status: 402 } });
    expect(quota.error).toEqual({ status: 429, message: 'quota' });
    const whole = await runResponse(plugin, 'acme', { framing: 'json', body: { body: '{"t":"x"}' }, request: { stream: false } });
    expect(whole.failure?.message).toMatch(/exactly one body/);
  });
});

describe('a bidirectional provider (connect framing)', () => {
  it('carries a byte body, hands prepare state over, stops at $end and disposes', async () => {
    const { providers, definePlugin, encoding } = await import('astra');
    const { runProvider, runResponse } = await import('../src/testing.mjs');
    const disposed: string[] = [];
    const plugin = definePlugin({
      activate() {
        providers.register('bidi', {
          prepare: async () => ({ url: 'https://api.acme.example/run', headers: { 'content-type': 'application/connect+proto' }, body: new Uint8Array([0, 0, 0, 0, 1, 7]), state: { id: 'r1' } }),
          transformResponse: {
            frame: (f, ctx) => {
              const text = new TextDecoder().decode(encoding.base64.decode((f as { bytes: string }).bytes));
              const out: ReplyOutput[] = [{ id: ctx.state.id, choices: [{ delta: { content: text } }] }, { $end: true }];
              return out;
            },
            end: (ctx) => ({ id: ctx.state.id, choices: [{ delta: {}, finish_reason: 'stop' }] }),
            dispose: (ctx) => { disposed.push(ctx.state.id); },
          },
        });
      },
    });
    const prepared = (await runProvider(plugin, 'bidi', 'prepare', { provider: 'bidi', protocol: 'chat', model: 'm', path: '/v1/chat/completions', method: 'POST', headers: {}, body: null, stream: true })) as Record<string, unknown>;
    expect(prepared.body).toBeUndefined();
    expect(prepared.bodyBase64).toBe('AAAAAAEH');
    const frames = [{ flags: 0, bytes: encoding.base64.encode('hi') }, { flags: 0, bytes: encoding.base64.encode('never') }];
    const r = await runResponse(plugin, 'bidi', { framing: 'connect', frames, state: prepared.state as Record<string, unknown> });
    expect(r.ended).toBe(true);
    expect(r.consumed).toBe(1);
    expect(r.frames).toEqual([{ id: 'r1', choices: [{ delta: { content: 'hi' } }] }, { id: 'r1', choices: [{ delta: {}, finish_reason: 'stop' }] }]);
    expect(disposed).toEqual(['r1']);
  });
});
