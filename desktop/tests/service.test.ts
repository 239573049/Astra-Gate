import { spawn, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ServiceManager } from '../src/service';
import { astraPaths } from '../src/shared/paths';
import { isPidAlive } from '../src/shared/pid';
import type { RunCommand } from '../src/shared/listener';

/**
 * A stand-in astra-server: `serve --port N` listens on N (or the next free
 * port, like PortPicker), answers /api/health and /api/version, and writes
 * runtime.json shortly after listening (the real one writes it at
 * ApplicationStarted). `--no-runtime` plays a server whose entry a later
 * start overwrote.
 */
const FAKE_SERVER = `#!/usr/bin/env node
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2);
const version = __VERSION__;
let port = Number(args[args.indexOf('--port') + 1]);
const server = http.createServer((req, res) => {
  res.setHeader('content-type', 'application/json');
  if (req.url === '/api/health') return res.end(JSON.stringify({ status: 'ok' }));
  if (req.url === '/api/version') return res.end(JSON.stringify({ version, apiVersion: '1.0' }));
  res.statusCode = 404;
  res.end('{}');
});
server.on('error', (err) => {
  if (err.code !== 'EADDRINUSE') throw err;
  port += 1;
  server.listen(port, '127.0.0.1');
});
server.on('listening', () => {
  if (args.includes('--no-runtime')) return;
  setTimeout(() => {
    const runtime = { pid: process.pid, port, host: '127.0.0.1', version, apiVersion: '1.0',
      startedBy: 'desktop', startedAt: new Date().toISOString(), runtimeToken: 'token-' + process.pid };
    fs.writeFileSync(path.join(process.env.ASTRA_HOME, 'runtime.json'), JSON.stringify(runtime));
  }, 400);
});
server.listen(port, '127.0.0.1');
`;

function writeFakeServer(dir: string, name: string, version: string): string {
  const file = path.join(dir, name);
  fs.writeFileSync(file, FAKE_SERVER.replace('__VERSION__', JSON.stringify(version)), { mode: 0o755 });
  return file;
}

async function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const srv = net.createServer();
    srv.once('error', reject);
    srv.listen(0, '127.0.0.1', () => {
      const { port } = srv.address() as net.AddressInfo;
      srv.close(() => resolve(port));
    });
  });
}

async function versionOn(port: number): Promise<string | null> {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/api/version`, { signal: AbortSignal.timeout(1000) });
    return ((await res.json()) as { version?: string }).version ?? null;
  } catch {
    return null;
  }
}

async function waitFor(check: () => Promise<boolean> | boolean, timeoutMs = 5000): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await check()) return true;
    await new Promise((r) => setTimeout(r, 100));
  }
  return false;
}

describe.skipIf(process.platform === 'win32')('ServiceManager with a server nothing on disk names', () => {
  let home: string;
  let port: number;
  let orphan: ChildProcess;
  const pids: number[] = [];

  beforeEach(async () => {
    home = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-service-'));
    port = await freePort();
    fs.writeFileSync(path.join(home, 'config.json'), JSON.stringify({ port }));
    // A 0.9.3 server left behind by a failed update: it holds the configured port, and runtime.json does not name it.
    const orphanBin = writeFakeServer(home, 'astra-server-0.9.3', '0.9.3');
    orphan = spawn(orphanBin, ['serve', '--port', String(port), '--no-runtime'], {
      env: { ...process.env, ASTRA_HOME: home },
      stdio: 'ignore',
    });
    pids.push(orphan.pid!);
    expect(await waitFor(async () => (await versionOn(port)) === '0.9.3')).toBe(true);
  });

  afterEach(() => {
    for (const pid of pids.splice(0)) {
      try {
        process.kill(pid, 'SIGKILL');
      } catch {
        // already gone
      }
    }
    try {
      const runtime = JSON.parse(fs.readFileSync(path.join(home, 'runtime.json'), 'utf8')) as { pid: number };
      process.kill(runtime.pid, 'SIGKILL');
    } catch {
      // no server of ours left
    }
    fs.rmSync(home, { recursive: true, force: true });
  });

  function manager(run: RunCommand): ServiceManager {
    return new ServiceManager({
      paths: astraPaths({ ASTRA_HOME: home }, home),
      repoRoot: home,
      bundledServer: { path: writeFakeServer(home, 'astra-server', '0.9.4'), version: '0.9.4' },
      platform: process.platform,
      env: { ...process.env, ASTRA_HOME: home },
      log: () => {},
      runCommand: run,
    });
  }

  it('stops the unregistered server by its port and serves with its own build', async () => {
    const lookups: string[] = [];
    const svc = manager(async (command) => {
      lookups.push(command);
      if (command.endsWith('lsof')) return `${orphan.pid}\n`;
      if (command === 'ps') return `${home}/astra-server-0.9.3\n`;
      return null;
    });

    const running = await svc.ensureRunning();

    expect(lookups.some((c) => c.endsWith('lsof'))).toBe(true);
    expect(await waitFor(() => !isPidAlive(orphan.pid!))).toBe(true);
    expect(running.port).toBe(port);
    expect(await versionOn(running.port)).toBe('0.9.4');
    expect(running.weStarted).toBe(true);
  }, 20_000);

  it('keeps one server when the unregistered one cannot be stopped', async () => {
    const svc = manager(async () => null); // no lsof / not an astra-server

    const running = await svc.ensureRunning();

    // Adopted, not doubled: a second server on the next port would split the UI from the update checks.
    expect(running.port).toBe(port);
    expect(running.weStarted).toBe(false);
    expect(await versionOn(port + 1)).toBeNull();
    expect(isPidAlive(orphan.pid!)).toBe(true);
  }, 20_000);
});
