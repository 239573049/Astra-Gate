import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const calls = vi.hoisted(() => [] as { path: string; method: string; body?: unknown }[]);
const responses = vi.hoisted(() => new Map<string, unknown>());

vi.mock('../src/lib/server-lifecycle.js', () => ({
  ensureServerRunning: async () => ({ base: 'http://127.0.0.1:17321', runtime: undefined, started: false }),
}));

vi.mock('../src/lib/http.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/lib/http.js')>();
  return {
    ...actual,
    apiRequest: async (req: { path: string; method: string; body?: unknown }) => {
      calls.push({ path: req.path, method: req.method, body: req.body });
      return responses.get(`${req.method} ${req.path}`) ?? {};
    },
  };
});

import { buildArgs, imagePath, parseKeyValues, runToolList, runToolRun } from '../src/commands/tool';
import { AstraError } from '../src/errors';

describe('astra tool', () => {
  let log: ReturnType<typeof vi.spyOn>;
  let err: ReturnType<typeof vi.spyOn>;
  let dir: string;

  beforeEach(async () => {
    calls.length = 0;
    responses.clear();
    process.exitCode = undefined;
    log = vi.spyOn(console, 'log').mockImplementation(() => {});
    err = vi.spyOn(console, 'error').mockImplementation(() => {});
    dir = await mkdtemp(join(tmpdir(), 'astra-tool-'));
  });

  afterEach(async () => {
    log.mockRestore();
    err.mockRestore();
    process.exitCode = undefined;
    await rm(dir, { recursive: true, force: true });
  });

  it("maps the positional words to each built-in tool's main parameter and refuses them for a plugin tool", () => {
    expect(buildArgs('web_search', ['magpie', 'facts'], {})).toEqual({ query: 'magpie facts' });
    expect(buildArgs('web_fetch', ['https://example.com'], {})).toEqual({ url: 'https://example.com' });
    expect(buildArgs('generate_image', ['a magpie, watercolor'], { arg: ['size=1024x1024'] })).toEqual({ prompt: 'a magpie, watercolor', size: '1024x1024' });
    expect(buildArgs('web_search', [], { json: '{"query":"x","count":3}' })).toEqual({ query: 'x', count: 3 });
    expect(() => buildArgs('demo.tool', ['x'], {})).toThrow(AstraError);
    expect(() => buildArgs('web_search', [], { json: '[1]' })).toThrow(AstraError);
    expect(() => buildArgs('web_search', [], { json: 'nope' })).toThrow(AstraError);
  });

  it('reads key=value pairs as JSON when they are, else as text', () => {
    expect(parseKeyValues(['n=3', 'on=true', 'name=hero', 'list=[1,2]'])).toEqual({ n: 3, on: true, name: 'hero', list: [1, 2] });
    expect(() => parseKeyValues(['novalue'])).toThrow(AstraError);
  });

  it('numbers the extra images and picks an extension', () => {
    expect(imagePath(join(dir, 'hero.png'), 0, 'image/png')).toBe(join(dir, 'hero.png'));
    expect(imagePath(join(dir, 'hero.png'), 1, 'image/png')).toBe(join(dir, 'hero-2.png'));
    expect(imagePath(join(dir, 'hero'), 0, 'image/jpeg')).toBe(join(dir, 'hero.jpg'));
  });

  it('lists the tools with why one is not available', async () => {
    responses.set('GET /api/tools', [
      { name: 'web_search', title: 'Web search', source: 'builtin', available: false, reason: 'no search API is on' },
      { name: 'web_fetch', title: 'Web fetch', source: 'builtin', available: true },
    ]);
    await runToolList({});
    const text = log.mock.calls.map((c: unknown[]) => String(c[0])).join('\n');
    expect(text).toContain('web_search');
    expect(text).toContain('no (no search API is on)');
  });

  it('runs a tool on the server and saves an image where -o says', async () => {
    const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 1, 2, 3]);
    responses.set('POST /api/tools/run', { isError: false, content: [{ type: 'image', mimeType: 'image/png', data: png.toString('base64') }] });
    const out = join(dir, 'sub', 'hero.png');
    await runToolRun('generate_image', ['a magpie, watercolor'], { output: out, token: 'sk-astra-key' });
    expect(calls.at(-1)).toEqual({
      path: '/api/tools/run',
      method: 'POST',
      body: { name: 'generate_image', args: { prompt: 'a magpie, watercolor' }, token: 'sk-astra-key' },
    });
    expect(await readFile(out)).toEqual(png);
    expect(process.exitCode).toBeUndefined();
  });

  it('prints text results and exits non-zero on a tool error', async () => {
    responses.set('POST /api/tools/run', { isError: false, content: [{ type: 'text', text: 'Search results' }] });
    await runToolRun('web_search', ['magpie'], {});
    expect(log).toHaveBeenCalledWith('Search results');
    expect(process.exitCode).toBeUndefined();

    responses.set('POST /api/tools/run', { isError: true, content: [{ type: 'text', text: 'that address is private' }] });
    await runToolRun('web_fetch', ['http://127.0.0.1/'], {});
    expect(err).toHaveBeenCalledWith('that address is private');
    expect(process.exitCode).toBe(1);
  });

  it('writes a text result to -o', async () => {
    responses.set('POST /api/tools/run', { isError: false, content: [{ type: 'text', text: 'page text' }] });
    const out = join(dir, 'page.txt');
    await runToolRun('web_fetch', ['https://example.com'], { output: out });
    expect(await readFile(out, 'utf8')).toBe('page text');
  });
});
