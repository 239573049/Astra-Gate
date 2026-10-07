import { describe, expect, it } from 'vitest';

import { spawnSync } from 'node:child_process';

import { isPidAlive } from '../src/shared/pid';

describe('isPidAlive', () => {
  it('reports the current process as alive', () => {
    expect(isPidAlive(process.pid)).toBe(true);
  });

  it('reports a finished child as dead', () => {
    const exited = spawnSync('true');
    const pid = exited.pid;
    if (pid === undefined) return;
    expect(isPidAlive(pid)).toBe(false);
  });

  it('rejects invalid pids', () => {
    expect(isPidAlive(0)).toBe(false);
    expect(isPidAlive(-1)).toBe(false);
    expect(isPidAlive(1.5)).toBe(false);
    expect(isPidAlive(Number.NaN)).toBe(false);
  });
});
