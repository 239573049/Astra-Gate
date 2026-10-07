import process from 'node:process';
import { checkForServerUpdate } from '@aidotnet/update-core';
import { fail, info, style, success } from '../ui.js';
import { astraHome } from '../lib/paths.js';
import { openInBrowser } from '../lib/browser.js';
import { findNewestLog, followFile, tailLines } from '../lib/logs.js';
import { serverStatus, startServer, stopServer } from '../lib/server-lifecycle.js';
import { VERSION } from '../version.js';

export interface StartArgs {
  port?: string;
  host?: string;
  foreground?: boolean;
  open?: boolean;
}

export async function runStart(opts: StartArgs): Promise<void> {
  console.log('Starting Astra…');
  const result = await startServer(opts);
  if (result.alreadyRunning) {
    success(`Astra is already running at ${style.cyan(result.base)} (pid ${result.pid ?? '?'}).`);
    info(style.dim('Use `astra restart` to reload it.'));
    if (opts.open) openInBrowser(result.base);
    return;
  }
  console.log(`  Binary   ${result.command.serverPath} ${style.dim(`(${result.command.source})`)}`);
  console.log(`  Address  ${style.cyan(result.base)}`);
  console.log(`  Log      ${style.dim(result.logFile)}`);
  if (opts.foreground) return; // foreground start returns when the server exits
  success(`Astra is running at ${style.cyan(result.base)}`);
  if (opts.open) openInBrowser(result.base);
}

export async function runStop(): Promise<void> {
  const result = await stopServer();
  if (!result.stopped) {
    console.log('Astra is not running.');
    return;
  }
  success(`Astra stopped (pid ${result.pid ?? '?'}).`);
}

export async function runRestart(opts: StartArgs): Promise<void> {
  const status = await serverStatus();
  if (status.running || status.runtime) {
    console.log('Stopping Astra…');
    await stopServer();
  }
  await runStart(opts);
}

export async function runStatus(): Promise<void> {
  const status = await serverStatus();
  if (!status.running) {
    if (status.staleRuntime) {
      console.log(style.dim('Removed a stale runtime.json (its process is gone).'));
    }
    console.log(`Astra is not running. Start it with ${style.cyan('astra start')}.`);
    process.exitCode = 1;
    return;
  }
  const rt = status.runtime;
  if (!rt) return; // unreachable: running implies runtime
  console.log('Astra is running');
  console.log(`  URL      ${style.cyan(status.base ?? '')}`);
  console.log(`  PID      ${rt.pid}`);
  const versionLine = [rt.version, rt.apiVersion ? `API ${rt.apiVersion}` : undefined]
    .filter(Boolean)
    .join(' · ');
  if (versionLine) console.log(`  Version  ${versionLine}`);
  if (rt.startedBy) {
    console.log(`  Started  by ${rt.startedBy}${rt.startedAt ? ` at ${rt.startedAt}` : ''}`);
  }
  // Update hint: prefer the server's cached feed status; any failure stays silent.
  if (status.base) {
    try {
      const r = await checkForServerUpdate({ currentVersion: VERSION, baseUrl: status.base, timeoutMs: 2000 });
      if (r.availableVersion) {
        console.log(`  Update   ${style.cyan(r.availableVersion)} available — run ${style.cyan('astra update')}`);
      }
    } catch {
      /* offline or feed not configured — not a status failure */
    }
  }
}

export async function runLogs(opts: { follow?: boolean }): Promise<void> {
  const home = astraHome();
  const logFile = findNewestLog(home);
  console.error(style.dim(`# ${logFile}`));
  process.stdout.write(`${tailLines(logFile, 100)}\n`);
  if (opts.follow) {
    console.error(style.dim('--- following (Ctrl+C to stop) ---'));
    const stop = followFile(logFile, (chunk) => process.stdout.write(chunk));
    process.on('SIGINT', () => {
      stop();
      process.exit(0);
    });
    await new Promise(() => {
      /* follow forever */
    });
  }
}

export async function runOpen(): Promise<void> {
  const status = await serverStatus();
  if (!status.running || !status.base) {
    fail('Astra is not running.');
    info(`Start it with ${style.cyan('astra start --open')}.`);
    process.exitCode = 1;
    return;
  }
  openInBrowser(status.base);
  console.log(`Opened ${status.base}`);
}
