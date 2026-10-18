import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { ClientEnvironment } from '@aidotnet/client-config';
import type { ConnectContext, KeyInfo } from '../src/lib/gateway.js';
import type { Io } from '../src/lib/io.js';
import { connectPaths } from '../src/lib/profile.js';
import { createRuntime, type Runtime } from '../src/lib/runtime.js';

export const KEY = 'sk-astra-employee-key-0001';

export const KEY_INFO: KeyInfo = {
  name: '张三',
  enabled: true,
  limits: [
    { metric: 'cost', period: 'month', unit: 'usd', limit: 200, used: 50, remaining: 150, usedPercent: 25, exhausted: false, resetsAt: null },
  ],
  usage: {
    today: { requests: 1, successRequests: 1, costUsd: 0.1, inputTokens: 10, outputTokens: 5 },
    week: { requests: 2, successRequests: 2, costUsd: 0.2, inputTokens: 20, outputTokens: 10 },
    month: { requests: 3, successRequests: 3, costUsd: 0.3, inputTokens: 30, outputTokens: 15 },
    total: { requests: 4, successRequests: 4, costUsd: 0.4, inputTokens: 40, outputTokens: 20 },
  },
};

export const CONTEXT: ConnectContext = {
  name: '张三',
  models: [
    { id: 'claude-sonnet-4-5', name: 'Claude Sonnet 4.5', contextWindow: 200000, protocols: ['anthropic'] },
    { id: 'gpt-5', name: 'GPT-5', contextWindow: 400000, protocols: ['openai-responses'] },
  ],
};

export interface FakeGateway {
  url: string;
  requests: Array<{ path: string; auth: string | undefined }>;
  close(): Promise<void>;
}

/** A gateway that knows one key: serves /v1/astra/key and /v1/astra/connect-context like the real one. */
export async function startGateway(
  options: { key?: string; keyInfo?: KeyInfo; context?: ConnectContext; contextStatus?: number } = {},
): Promise<FakeGateway> {
  const key = options.key ?? KEY;
  const requests: FakeGateway['requests'] = [];
  const server = http.createServer((req, res) => {
    requests.push({ path: req.url ?? '', auth: req.headers.authorization });
    const send = (status: number, body: unknown) => {
      res.writeHead(status, { 'content-type': 'application/json' });
      res.end(JSON.stringify(body));
    };
    if (req.headers.authorization !== `Bearer ${key}`) return send(401, { error: { type: 'authentication_error' } });
    if (req.url === '/v1/astra/key') return send(200, options.keyInfo ?? KEY_INFO);
    if (req.url === '/v1/astra/connect-context') {
      if (options.contextStatus) return send(options.contextStatus, { error: { message: '没有设置默认提供商' } });
      return send(200, options.context ?? CONTEXT);
    }
    return send(404, {});
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const port = (server.address() as { port: number }).port;
  return {
    url: `http://127.0.0.1:${port}`,
    requests,
    close: () => new Promise((resolve) => server.close(() => resolve())),
  };
}

export interface Machine {
  home: string;
  rt: Runtime;
  io: Io;
  cleanup(): void;
}

/** A fake home with the given client config files, and a runtime over it. */
export function machine(files: Record<string, string> = {}, env: NodeJS.ProcessEnv = {}): Machine {
  const home = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'astra-connect-')));
  for (const [rel, text] of Object.entries(files)) {
    const file = path.join(home, rel);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text);
  }
  const variables: Record<string, string | undefined> = { ...env };
  const clientEnv = new ClientEnvironment(home, 'osx', { lookup: (n) => variables[n] });
  const rt = createRuntime({ env: clientEnv, paths: connectPaths({}, home) });
  const io: Io = { env, interactive: false, readStdin: async () => '' };
  return { home, rt, io, cleanup: () => fs.rmSync(home, { recursive: true, force: true }) };
}
