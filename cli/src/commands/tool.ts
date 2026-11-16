import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, extname, resolve } from 'node:path';
import { AstraError } from '../errors.js';
import { info, printTable, success } from '../ui.js';
import { apiRequest } from '../lib/http.js';
import { ensureServerRunning } from '../lib/server-lifecycle.js';

interface ToolInfo {
  name: string;
  title: string;
  description?: string | null;
  source: 'builtin' | 'plugin';
  available: boolean;
  reason?: string | null;
}

interface ToolContent {
  type: 'text' | 'image';
  text?: string | null;
  mimeType?: string | null;
  data?: string | null;
}

interface ToolRun {
  isError: boolean;
  content: ToolContent[];
}

/** The parameter a bare positional argument means for each built-in tool; a plugin's tool takes --arg / --json. */
const PRIMARY: Record<string, string> = { web_search: 'query', web_fetch: 'url', generate_image: 'prompt' };

async function api<T>(path: string, method: 'GET' | 'POST' = 'GET', body?: unknown): Promise<T> {
  const { base, runtime, started } = await ensureServerRunning();
  if (started) console.log('Server was not running — started it in the background.');
  return apiRequest<T>({ base, path, method, body, runtimeToken: runtime?.runtimeToken, timeoutMs: 180_000 });
}

export async function runToolList(opts: { json?: boolean }): Promise<void> {
  const tools = await api<ToolInfo[]>('/api/tools');
  if (opts.json) {
    console.log(JSON.stringify(tools, null, 2));
    return;
  }
  printTable(
    tools.map((t) => [t.name, t.source, t.available ? 'yes' : `no (${t.reason ?? 'not available'})`, t.description ?? '']),
    ['Tool', 'Source', 'Available', 'Description'],
  );
}

/** `key=value` pairs as a JSON object; a value that parses as JSON (a number, true, an array) is that, anything else a string. */
export function parseKeyValues(pairs: readonly string[]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const pair of pairs) {
    const at = pair.indexOf('=');
    if (at <= 0) throw new AstraError(`"${pair}" is not key=value`);
    const key = pair.slice(0, at);
    const raw = pair.slice(at + 1);
    try {
      out[key] = JSON.parse(raw);
    } catch {
      out[key] = raw;
    }
  }
  return out;
}

/** What a tool is called with: --json's object, then --arg pairs, then the positional words as the tool's main parameter. */
export function buildArgs(name: string, words: readonly string[], opts: { json?: string; arg?: string[] }): Record<string, unknown> {
  let args: Record<string, unknown> = {};
  if (opts.json) {
    let parsed: unknown;
    try {
      parsed = JSON.parse(opts.json);
    } catch {
      throw new AstraError('--json must be a JSON object');
    }
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new AstraError('--json must be a JSON object');
    args = { ...(parsed as Record<string, unknown>) };
  }
  Object.assign(args, parseKeyValues(opts.arg ?? []));
  if (words.length > 0) {
    const primary = PRIMARY[name];
    if (!primary) throw new AstraError(`${name} takes its arguments as --arg key=value or --json '{...}'`);
    args[primary] = words.join(' ');
  }
  return args;
}

const EXTENSIONS: Record<string, string> = { 'image/png': '.png', 'image/jpeg': '.jpg', 'image/webp': '.webp', 'image/gif': '.gif' };

/** Where image number `index` goes: the -o path itself for the first, `name-2.ext` for the rest. */
export function imagePath(output: string, index: number, mimeType: string | null | undefined): string {
  const full = resolve(output);
  const ext = extname(full) || EXTENSIONS[mimeType ?? ''] || '.png';
  const stem = extname(full) ? full.slice(0, -extname(full).length) : full;
  return index === 0 ? stem + ext : `${stem}-${index + 1}${ext}`;
}

export async function runToolRun(
  name: string,
  words: string[],
  opts: { output?: string; json?: string; arg?: string[]; token?: string; raw?: boolean },
): Promise<void> {
  const args = buildArgs(name, words, opts);
  const token = opts.token ?? process.env.ASTRA_API_KEY;
  const result = await api<ToolRun>('/api/tools/run', 'POST', { name, args, ...(token ? { token } : {}) });
  if (opts.raw) {
    console.log(JSON.stringify(result, null, 2));
    if (result.isError) process.exitCode = 1;
    return;
  }
  let images = 0;
  for (const block of result.content) {
    if (block.type === 'text') {
      if (result.isError) console.error(block.text ?? '');
      else console.log(block.text ?? '');
    } else if (block.type === 'image' && block.data) {
      const target = imagePath(opts.output ?? `${name.replace(/\W+/g, '-')}-${Date.now()}`, images++, block.mimeType);
      await mkdir(dirname(target), { recursive: true });
      await writeFile(target, Buffer.from(block.data, 'base64'));
      success(`Saved ${target}`);
    }
  }
  if (opts.output && images === 0 && !result.isError) {
    // -o names a file for a tool that answers with text: it is written there.
    const text = result.content.filter((b) => b.type === 'text').map((b) => b.text ?? '').join('\n');
    await mkdir(dirname(resolve(opts.output)), { recursive: true });
    await writeFile(resolve(opts.output), text);
    info(`Wrote ${resolve(opts.output)}`);
  }
  if (result.isError) process.exitCode = 1;
}
