import { spawn, type ChildProcess } from 'node:child_process';

export interface RunResult {
  code: number | null;
  stdout: string;
  stderr: string;
}

export interface RunCaptureOptions {
  timeoutMs?: number;
  cwd?: string;
  env?: NodeJS.ProcessEnv;
  input?: string;
  shell?: boolean;
}

export function runCapture(
  command: string,
  args: readonly string[],
  options: RunCaptureOptions = {},
): Promise<RunResult> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, [...args], {
      cwd: options.cwd,
      env: options.env,
      windowsHide: true,
      shell: options.shell ?? false,
    });
    let stdout = '';
    let stderr = '';
    let settled = false;
    const timer = options.timeoutMs
      ? setTimeout(() => {
          settled = true;
          try {
            child.kill('SIGKILL');
          } catch {
            /* ignore */
          }
          reject(new Error(`Command timed out after ${options.timeoutMs}ms: ${command} ${args.join(' ')}`));
        }, options.timeoutMs)
      : null;

    child.stdout?.on('data', (d: Buffer) => {
      stdout += d.toString();
    });
    child.stderr?.on('data', (d: Buffer) => {
      stderr += d.toString();
    });
    if (options.input !== undefined) {
      child.stdin?.write(options.input);
    }
    child.stdin?.end();
    child.on('error', (err) => {
      if (timer) clearTimeout(timer);
      if (!settled) reject(err);
    });
    child.on('close', (code) => {
      if (timer) clearTimeout(timer);
      if (!settled) resolve({ code, stdout, stderr });
    });
  });
}

export function spawnDetached(
  command: string,
  args: readonly string[],
  stdio: ('ignore' | number)[],
): ChildProcess {
  const child = spawn(command, [...args], { detached: true, stdio, windowsHide: true });
  child.unref();
  return child;
}

export function exitCodeOf(child: ChildProcess): Promise<number | null> {
  return new Promise((resolve, reject) => {
    child.on('error', reject);
    child.on('close', (code) => resolve(code));
  });
}

export function isPidAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    return (err as NodeJS.ErrnoException).code === 'EPERM';
  }
}

/** Ask a process to terminate: SIGTERM on POSIX, taskkill on Windows. */
export async function terminatePid(pid: number, platform: string = process.platform): Promise<void> {
  if (platform === 'win32') {
    try {
      await runCapture('taskkill', ['/PID', String(pid), '/T', '/F'], { timeoutMs: 10_000 });
    } catch {
      /* ignore */
    }
    return;
  }
  try {
    process.kill(pid, 'SIGTERM');
  } catch {
    /* already dead */
  }
}

/** Force-kill a process: SIGKILL on POSIX, taskkill /F on Windows. */
export async function killPid(pid: number, platform: string = process.platform): Promise<void> {
  if (platform === 'win32') {
    await terminatePid(pid, platform);
    return;
  }
  try {
    process.kill(pid, 'SIGKILL');
  } catch {
    /* already dead */
  }
}

export async function waitForPidExit(
  pid: number,
  timeoutMs: number,
  isAlive: (pid: number) => boolean = isPidAlive,
): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (!isAlive(pid)) return true;
    await delay(300);
  }
  return !isAlive(pid);
}

export function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
