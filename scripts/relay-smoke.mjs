// Native AOT gate for `astra-server relay`: runs the published binary against a fake upstream on loopback and checks
// what only a run of the binary shows (AOT can break at run time what the JIT never shows): per-request credentials,
// the model list's per-model endpoints, pass-through and converted protocols, the relayed model list / embeddings /
// token count, request compression and the body limit, SSE heartbeats and usage chunks, health checks, and that
// nothing is written to the data directory or logged from a request.
//
// Usage: node scripts/relay-smoke.mjs <path to astra-server>
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { createServer } from 'node:http';
import { createServer as createNetServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { brotliCompressSync, deflateRawSync, deflateSync, gzipSync, zstdCompressSync } from 'node:zlib';

const binary = resolve(process.argv[2] ?? 'out/publish/astra-server');
const tokens = ['relay-smoke-alice', 'relay-smoke-bob', 'relay-smoke-chosen'];
const catalog = {
  data: [
    { id: 'gpt-5', supported_endpoints: ['/chat/completions', '/responses'] },
    { id: 'chat-only', supported_endpoints: ['/chat/completions'] },
    { id: 'messages-only', supported_endpoints: ['/v1/messages'] },
  ],
};
const received = [];
const failures = [];
let releaseHeartbeat;
let holdChatStream = true;

const upstream = createServer(async (request, response) => {
  try {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const text = Buffer.concat(chunks).toString('utf8');
    const body = text ? JSON.parse(text) : null;
    const url = new URL(request.url, 'http://localhost');
    const token = request.headers.authorization?.replace(/^Bearer /, '');
    assert.ok(tokens.includes(token), 'Unexpected credential or authentication scheme');
    for (const header of ['traceparent', 'tracestate', 'baggage', 'x-api-key', 'cookie', 'content-encoding'])
      assert.equal(request.headers[header], undefined, `Unexpected forwarded ${header}`);
    received.push({ path: url.pathname, token, body, text });
    const json = (value, status = 200) => {
      response.writeHead(status, { 'content-type': 'application/json', 'x-request-id': 'smoke-upstream' });
      response.end(JSON.stringify(value));
    };
    if (url.pathname === '/v1/models') return json({ ...catalog, caller: token });
    assert.equal(request.method, 'POST');
    if (body.model === 'rate-limited') {
      response.setHeader('retry-after', '23');
      return json({ error: { message: 'limited' } }, 429);
    }
    if (url.pathname === '/v1/embeddings')
      return json({ model: 'served-model', object: 'list', data: [{ embedding: [0.1, 0.2] }] });
    if (url.pathname === '/anthropic/v1/messages/count_tokens') return json({ input_tokens: 7 });
    if (url.pathname === '/anthropic/v1/messages')
      return json({ id: 'msg_1', type: 'message', role: 'assistant', model: body.model,
        content: [{ type: 'text', text: 'hello' }], stop_reason: 'end_turn', usage: { input_tokens: 2, output_tokens: 1 } });
    if (url.pathname === '/v1/responses')
      return json({ id: 'resp_1', object: 'response', status: 'completed', model: body.model,
        output: [{ type: 'message', id: 'msg_1', role: 'assistant',
          content: [{ type: 'output_text', text: 'hello', annotations: [] }] }] });
    assert.equal(url.pathname, '/v1/chat/completions', 'No other upstream endpoint is expected');
    if (body.stream && body.model !== 'json-fallback') {
      response.writeHead(200, { 'content-type': 'text/event-stream' });
      response.write(': ready\n\n');
      if (holdChatStream) {
        holdChatStream = false;
        await new Promise((resolve) => { releaseHeartbeat = resolve; });
      }
      response.write(`data: ${JSON.stringify({ id: 'chat_1', model: body.model,
        choices: [{ index: 0, delta: { content: 'hello' }, finish_reason: null }] })}\n\n`);
      response.write('data: {"choices":[],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}\n\n');
      return response.end('data: [DONE]\n\n');
    }
    return json({ id: 'chat_1', object: 'chat.completion', model: 'served-model',
      choices: [{ index: 0, message: { role: 'assistant', content: 'hello' }, finish_reason: 'stop' }],
      usage: { prompt_tokens: 2, completion_tokens: 1, total_tokens: 3 } });
  } catch (error) {
    failures.push(error);
    response.writeHead(500);
    response.end('Fake upstream assertion failed');
  }
});
upstream.listen(0, '127.0.0.1');
await once(upstream, 'listening');

const freePort = async () => {
  const probe = createNetServer().listen(0, '127.0.0.1');
  await once(probe, 'listening');
  const { port } = probe.address();
  probe.close();
  await once(probe, 'close');
  return port;
};
const port = await freePort();
const home = mkdtempSync(join(tmpdir(), 'astra-relay-smoke-'));
const env = { ...process.env, ASTRA_HOME: home, HOME: home, USERPROFILE: home, ASTRA_PORT: String(port),
  ASTRA_RELAY_MAX_REQUEST_BODY_BYTES: '16384' };
delete env.ASTRA_HOST;
let log = '';
const relay = spawn(binary, ['relay', '--provider', 'deepseek', '--base-url', `http://127.0.0.1:${upstream.address().port}`],
  { env, stdio: ['ignore', 'pipe', 'pipe'] });
relay.stdout.on('data', (data) => { log += data; });
relay.stderr.on('data', (data) => { log += data; });
const exited = once(relay, 'exit');
let operation = 'startup';
const call = (path, body, token = tokens[0], headers = {}) => {
  operation = `${body === undefined ? 'GET' : 'POST'} ${path}`;
  return fetch(`http://127.0.0.1:${port}${path}`, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { ...(token ? { authorization: `Bearer ${token}` } : {}), 'content-type': 'application/json', ...headers },
    body: body === undefined ? undefined : typeof body === 'string' || Buffer.isBuffer(body) ? body : JSON.stringify(body),
    signal: AbortSignal.timeout(10000),
  });
};
const chat = (model = 'gpt-5') => ({ model, messages: [{ role: 'user', content: 'body-must-not-be-logged' }] });

try {
  let ready = false;
  for (let attempt = 0; attempt < 200 && !ready; attempt++) {
    if (relay.exitCode !== null) throw new Error(`The relay exited before it was ready: ${log}`);
    try { ready = (await call('/readyz', undefined, null)).status === 200; }
    catch (error) { if (error.cause?.code !== 'ECONNREFUSED') throw error; }
    if (!ready) await sleep(50);
  }
  assert.ok(ready, 'The relay never became ready');
  execFileSync(binary, ['healthcheck'], { env });
  assert.match(execFileSync(binary, ['version'], { encoding: 'utf8' }), /"version"/);
  assert.equal((await call('/')).status, 404);
  assert.equal((await call('/api/health')).status, 404);
  assert.equal((await call('/v1/models', undefined, null)).status, 401);

  const alice = await (await call('/v1/models')).json();
  const bob = await (await call('/models', undefined, tokens[1])).json();
  assert.equal(alice.caller, tokens[0]);
  assert.equal(bob.caller, tokens[1]);
  for (const token of tokens.slice(0, 2)) {
    const response = await call('/v1/chat/completions', chat(), token, {
      traceparent: '00-11111111111111111111111111111111-2222222222222222-01',
      baggage: 'test=not-for-upstream', cookie: 'test=not-for-upstream',
    });
    assert.equal(response.status, 200);
    assert.equal((await response.json()).choices[0].message.content, 'hello');
    assert.equal(received.at(-1).token, token);
  }
  const chosen = await call('/chat/completions', chat(), null,
    { 'x-api-key': tokens[2], authorization: 'invalid lower-priority header' });
  assert.equal(chosen.status, 200);
  await chosen.arrayBuffer();
  assert.equal(received.at(-1).token, tokens[2]);

  for (const [path, body, field, upstreamPath] of [
    ['/responses', { model: 'gpt-5', input: 'Hi' }, 'output', '/v1/responses'],
    ['/messages', { model: 'messages-only', max_tokens: 32, messages: chat().messages }, 'content', '/anthropic/v1/messages'],
    ['/v1/messages', { model: 'chat-only', max_tokens: 32, messages: chat().messages }, 'content', '/v1/chat/completions'],
    ['/v1beta/models/chat-only:generateContent', { contents: [{ role: 'user', parts: [{ text: 'Hi' }] }] }, 'candidates', '/v1/chat/completions'],
    ['/v1/embeddings', { model: 'embedding-model', input: 'Hi' }, 'data', '/v1/embeddings'],
    ['/messages/count_tokens', { model: 'messages-only', messages: [] }, 'input_tokens', '/anthropic/v1/messages/count_tokens'],
  ]) {
    const response = await call(path, body);
    assert.equal(response.status, 200, path);
    assert.ok(field in await response.json(), path);
    assert.equal(received.at(-1).path, upstreamPath, path);
  }
  const ordinary = await call('/chat/completions', chat('custom-model'));
  assert.equal(ordinary.status, 200);
  assert.equal((await ordinary.json()).model, 'served-model');
  assert.equal(received.at(-1).body.model, 'custom-model');

  const source = JSON.stringify(chat());
  for (const [encoding, compress] of [
    ['gzip', gzipSync], ['br', brotliCompressSync], ['deflate', deflateSync],
    ['deflate', deflateRawSync], ['zstd', zstdCompressSync],
    ['gzip, br', (bytes) => brotliCompressSync(gzipSync(bytes))],
  ]) {
    const response = await call('/v1/chat/completions', compress(Buffer.from(source)), tokens[0], { 'content-encoding': encoding });
    assert.equal(response.status, 200, encoding);
    await response.arrayBuffer();
    assert.equal(received.at(-1).text, source, encoding);
  }
  for (const size of [16384, 16385]) {
    const bytes = gzipSync(source + ' '.repeat(size - Buffer.byteLength(source)));
    const response = await call('/v1/chat/completions', bytes, tokens[0], { 'content-encoding': 'gzip' });
    assert.equal(response.status, size === 16384 ? 200 : 413);
    await response.arrayBuffer();
  }
  const bad = await call('/v1/chat/completions', 'not json');
  assert.equal(bad.status, 400);
  await bad.arrayBuffer();
  const limited = await call('/v1/embeddings', { model: 'rate-limited', input: 'Hi' });
  assert.equal(limited.status, 429);
  assert.equal(limited.headers.get('retry-after'), '23');
  await limited.arrayBuffer();
  const virtual = await call('/v1/chat/completions', chat('astra-auto'));
  assert.equal(virtual.status, 400);
  await virtual.arrayBuffer();

  const stream = await call('/v1/chat/completions', { ...chat(), stream: true, stream_options: { include_usage: true } });
  assert.equal(stream.status, 200);
  const reader = stream.body.getReader();
  const first = await Promise.race([reader.read(),
    sleep(2000, null, { ref: false }).then(() => { throw new Error('The heartbeat was held back'); })]);
  assert.match(Buffer.from(first.value).toString(), /: ready/);
  releaseHeartbeat();
  let streamText = '';
  while (true) {
    const part = await reader.read();
    if (part.done) break;
    streamText += Buffer.from(part.value).toString();
  }
  assert.match(streamText, /\[DONE\]/);
  assert.match(streamText, /"choices":\[\]/);
  const filtered = await call('/v1/chat/completions', { ...chat(), stream: true });
  assert.equal(filtered.status, 200);
  const filteredText = await filtered.text();
  assert.match(filteredText, /hello/);
  assert.doesNotMatch(filteredText, /"choices":\[\]/);
  const fallback = await call('/v1/chat/completions', { ...chat('json-fallback'), stream: true });
  assert.equal(fallback.status, 200);
  assert.match(await fallback.text(), /hello/);

  assert.deepEqual(failures, []);
  assert.deepEqual(readdirSync(home), [], 'The relay must not write to the data directory');
  for (const value of [...tokens, 'body-must-not-be-logged']) assert.ok(!log.includes(value), 'A request input was logged');
  console.log('Relay smoke passed: per-request credentials, protocols, relayed endpoints, compression, limits and SSE.');
} catch (error) {
  console.error(`Relay smoke failed during ${operation}; ${received.length} upstream requests completed.`);
  console.error(log);
  throw error;
} finally {
  releaseHeartbeat?.();
  if (relay.exitCode === null) relay.kill('SIGTERM');
  await Promise.race([exited, sleep(10000, null, { ref: false }).then(() => {
    if (relay.exitCode === null) relay.kill('SIGKILL');
  })]);
  upstream.closeAllConnections();
  upstream.close();
  rmSync(home, { recursive: true, force: true });
}
