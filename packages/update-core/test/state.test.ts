import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { initialState, MAX_CONSECUTIVE_FAILURES, readUpdateState, writeUpdateState } from '../src/state.js';

describe('update state', () => {
  it('round-trips and tolerates missing or corrupt files', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-state-'));
    try {
      const file = path.join(dir, 'update-state.json');
      expect(readUpdateState(file)).toBeNull();

      const state = { ...initialState(), phase: 'downloading' as const, targetVersion: '0.2.0', failedCount: 1 };
      writeUpdateState(file, state);
      expect(readUpdateState(file)).toMatchObject({ phase: 'downloading', targetVersion: '0.2.0' });

      fs.writeFileSync(file, '{not json');
      expect(readUpdateState(file)).toBeNull();

      fs.writeFileSync(file, '{"phase":"bogus"}');
      expect(readUpdateState(file)).toBeNull();

      expect(MAX_CONSECUTIVE_FAILURES).toBe(2);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });
});
