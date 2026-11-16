import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const calls = vi.hoisted(() => [] as { path: string; method: string; body?: any }[]);
const responses = vi.hoisted(() => new Map<string, unknown>());
const prompts = vi.hoisted(() => ({ hidden: vi.fn(async (_q: string) => 'typed'), confirm: vi.fn(async (_q: string) => true) }));

vi.mock('../src/lib/server-lifecycle.js', () => ({
  ensureServerRunning: async () => ({ base: 'http://127.0.0.1:17321', runtime: undefined, started: false }),
}));

vi.mock('../src/lib/http.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/lib/http.js')>();
  return {
    ...actual,
    apiRequest: async (req: { path: string; method: string; body?: unknown }) => {
      calls.push({ path: req.path, method: req.method, body: req.body });
      const r = responses.get(`${req.method} ${req.path}`) ?? {};
      if (r instanceof Error) throw r;
      return r;
    },
  };
});

vi.mock('../src/lib/prompt.js', () => ({ promptHidden: prompts.hidden, confirmPrompt: prompts.confirm }));

import {
  buildSyncTarget,
  formatPlan,
  formatSyncStatus,
  isEncryptedBackup,
  mapApiError,
  parsePositiveInt,
  parseSections,
  resetStdinForTests,
  runBackupAuto,
  runBackupCreate,
  runBackupRestore,
  runSyncNow,
  runSyncSetup,
  type BackupPlan,
} from '../src/commands/backup';
import { AstraError } from '../src/errors';
import { ApiError } from '../src/lib/http';

const PLAN: BackupPlan = {
  createdAt: '2026-11-16T00:00:00Z',
  astraVersion: '1.2.3',
  hasKeys: true,
  mode: 'merge',
  available: ['providers', 'tokens'],
  sections: [
    { section: 'providers', added: 1, changed: 1, removed: 0, unchanged: 3, truncated: true, items: [
      { id: 'a', label: 'Alpha', status: 'added' },
      { id: 'b', label: 'Beta', status: 'changed' },
      { id: 'c', label: 'Same', status: 'unchanged' },
    ] },
    { section: 'tokens', added: 0, changed: 0, removed: 2, unchanged: 0, truncated: false, items: [{ id: 't', label: '', status: 'removed' }] },
  ],
  notes: ['keys are encrypted'],
  legacy: false,
};

let dir: string;
let log: ReturnType<typeof vi.spyOn>;
const ENVS = ['ASTRA_BACKUP_PASSPHRASE', 'ASTRA_SYNC_PASSPHRASE', 'ASTRA_SYNC_SECRET'];

beforeEach(() => {
  dir = mkdtempSync(path.join(tmpdir(), 'astra-backup2-'));
  calls.length = 0;
  responses.clear();
  resetStdinForTests();
  prompts.hidden.mockClear();
  prompts.confirm.mockClear();
  prompts.confirm.mockResolvedValue(true);
  for (const e of ENVS) delete process.env[e];
  process.exitCode = undefined;
  log = vi.spyOn(console, 'log').mockImplementation(() => {});
  vi.spyOn(console, 'error').mockImplementation(() => {});
});

afterEach(() => {
  vi.restoreAllMocks();
  rmSync(dir, { recursive: true, force: true });
  for (const e of ENVS) delete process.env[e];
  process.exitCode = undefined;
});

describe('argument validation', () => {
  it('parses sections and numbers', () => {
    expect(parseSections('a, b')).toEqual(['a', 'b']);
    expect(parseSections(undefined)).toBeUndefined();
    expect(() => parseSections(',')).toThrow(AstraError);
    expect(() => parseSections('a;b')).toThrow(AstraError);
    expect(parsePositiveInt('6', '--hours')).toBe(6);
    expect(() => parsePositiveInt('0', '--hours')).toThrow(AstraError);
    expect(() => parsePositiveInt('x', '--keep')).toThrow(AstraError);
  });

  it('validates sync targets per kind', () => {
    expect(() => buildSyncTarget({ kind: 'ftp' })).toThrow(AstraError);
    expect(() => buildSyncTarget({ kind: 'webdav' })).toThrow(/--url/);
    expect(() => buildSyncTarget({ kind: 's3' })).toThrow(/--bucket/);
    expect(buildSyncTarget({ kind: 's3', bucket: 'b', region: 'eu' }).bucket).toBe('b');
    expect(buildSyncTarget({ kind: 'folder', url: '/x' }).url).toBe('/x');
  });

  it('rejects --on with --off before any request', async () => {
    await expect(runBackupAuto({ on: true, off: true })).rejects.toBeInstanceOf(AstraError);
    expect(calls).toHaveLength(0);
  });
});

describe('backup create', () => {
  const EXPORT = { name: 'astra-backup.json', content: '{"x":1}', encrypted: true, hasKeys: true, documents: 4, warnings: ['w1'] };

  it('includes keys by default, takes the passphrase from env and writes 0600', async () => {
    process.env.ASTRA_BACKUP_PASSPHRASE = 'pw';
    responses.set('POST /api/backup/export', EXPORT);
    const out = path.join(dir, 'b.json');
    await runBackupCreate({ out, sections: 'providers', includeUsage: true });
    expect(calls[0]?.body).toEqual({ sections: ['providers'], includeKeys: true, includeUsage: true, passphrase: 'pw' });
    expect(readFileSync(out, 'utf8')).toBe('{"x":1}');
    if (process.platform !== 'win32') expect(statSync(out).mode & 0o777).toBe(0o600);
    expect(prompts.hidden).not.toHaveBeenCalled();
  });

  it('--no-keys needs no passphrase and sends none', async () => {
    responses.set('POST /api/backup/export', { ...EXPORT, encrypted: false, hasKeys: false });
    await runBackupCreate({ keys: false, out: path.join(dir, 'p.json') });
    expect(calls[0]?.body.includeKeys).toBe(false);
    expect(calls[0]?.body.passphrase).toBeUndefined();
    expect(prompts.hidden).not.toHaveBeenCalled();
  });

  it('reads the passphrase from stdin when there is no env, and fails on an empty one', async () => {
    responses.set('POST /api/backup/export', EXPORT);
    prompts.hidden.mockResolvedValueOnce('line1\nline2\n');
    await runBackupCreate({ out: path.join(dir, 'c.json'), passphraseStdin: true });
    expect(calls[0]?.body.passphrase).toBe('line1');
    calls.length = 0;
    resetStdinForTests();
    prompts.hidden.mockResolvedValueOnce('');
    await expect(runBackupCreate({ out: path.join(dir, 'd.json') })).rejects.toBeInstanceOf(AstraError);
    expect(calls).toHaveLength(0);
  });

  it('defaults the file name to the one the server returns', async () => {
    process.env.ASTRA_BACKUP_PASSPHRASE = 'pw';
    responses.set('POST /api/backup/export', { ...EXPORT, name: path.join(dir, 'suggested.json') });
    await runBackupCreate({});
    expect(readFileSync(path.join(dir, 'suggested.json'), 'utf8')).toBe('{"x":1}');
  });
});

describe('backup restore', () => {
  const RESTORE = { plan: PLAN, written: 3, removed: 1, skipped: ['x'], errors: [], safetyBackup: 'safety-1.json' };

  function file(text: string): string {
    const f = path.join(dir, 'in.json');
    writeFileSync(f, text);
    return f;
  }

  it('detects encrypted files', () => {
    expect(isEncryptedBackup('{"kdf":"pbkdf2-sha256","x":1}')).toBe(true);
    expect(isEncryptedBackup('{"kdf": "pbkdf2-sha256"}')).toBe(true);
    expect(isEncryptedBackup('{"format":"astra"}')).toBe(false);
  });

  it('formats the preview table and item lines', () => {
    const text = formatPlan(PLAN).join('\n');
    expect(text).toMatch(/SECTION\s+ADDED\s+CHANGED\s+REMOVED\s+UNCHANGED/);
    expect(text).toMatch(/providers\s+1\s+1\s+0\s+3/);
    expect(text).toContain('+ Alpha');
    expect(text).toContain('~ Beta');
    expect(text).not.toContain('Same');
    expect(text).toContain('- t');
    expect(text).toContain('list truncated');
    expect(text).toContain('Note: keys are encrypted');
  });

  it('--dry-run previews and never restores, and asks nothing', async () => {
    responses.set('POST /api/backup/preview', PLAN);
    await runBackupRestore(file('{"a":1}'), { dryRun: true });
    expect(calls.map((c) => c.path)).toEqual(['/api/backup/preview']);
    expect(prompts.confirm).not.toHaveBeenCalled();
  });

  it('asks the passphrase only for encrypted files and sends the file content', async () => {
    responses.set('POST /api/backup/preview', PLAN);
    await runBackupRestore(file('{"a":1}'), { dryRun: true });
    expect(prompts.hidden).not.toHaveBeenCalled();
    await runBackupRestore(file('{"kdf":"pbkdf2-sha256"}'), { dryRun: true, sections: 'tokens' });
    expect(prompts.hidden).toHaveBeenCalledTimes(1);
    expect(calls[1]?.body).toMatchObject({ source: 'file', passphrase: 'typed', sections: ['tokens'], mode: 'merge' });
  });

  it('declining the confirmation cancels without calling restore', async () => {
    responses.set('POST /api/backup/preview', PLAN);
    prompts.confirm.mockResolvedValueOnce(false);
    await expect(runBackupRestore(file('{"a":1}'), {})).rejects.toBeInstanceOf(AstraError);
    expect(calls.map((c) => c.path)).toEqual(['/api/backup/preview']);
  });

  it('--yes restores without asking and reports the result', async () => {
    responses.set('POST /api/backup/preview', PLAN);
    responses.set('POST /api/backup/restore', RESTORE);
    await runBackupRestore(file('{"a":1}'), { yes: true, replace: true });
    expect(calls.map((c) => c.path)).toEqual(['/api/backup/preview', '/api/backup/restore']);
    expect(calls[1]?.body.mode).toBe('replace');
    expect(prompts.confirm).not.toHaveBeenCalled();
    const out = log.mock.calls.map((c) => String(c[0])).join('\n');
    expect(out).toContain('safety-1.json');
  });

  it('errors from the restore set a failing exit code', async () => {
    responses.set('POST /api/backup/preview', PLAN);
    responses.set('POST /api/backup/restore', { ...RESTORE, errors: ['boom'] });
    await runBackupRestore(file('{"a":1}'), { yes: true });
    expect(process.exitCode).toBe(1);
  });

  it('refuses an unreadable file before any request', async () => {
    await expect(runBackupRestore(path.join(dir, 'missing.json'), {})).rejects.toBeInstanceOf(AstraError);
    expect(calls).toHaveLength(0);
  });
});

describe('error mapping', () => {
  it('explains a 403 and keeps the server message otherwise', async () => {
    const forbidden = mapApiError(new ApiError('nope', 403)) as AstraError;
    expect(forbidden).toBeInstanceOf(AstraError);
    expect(forbidden.hint).toMatch(/this machine|local/);
    const other = mapApiError(new ApiError('bad passphrase', 400)) as AstraError;
    expect(other.message).toBe('bad passphrase');
    responses.set('POST /api/sync/now', new ApiError('denied', 403));
    await expect(runSyncNow({})).rejects.toBeInstanceOf(AstraError);
  });
});

describe('auto and sync', () => {
  const AUTO = { enabled: true, hours: 6, keep: 5, hasPassphrase: false, dir: '/d', files: [] };

  it('auto --run posts the run endpoint; flags go to PUT', async () => {
    responses.set('PUT /api/backup/auto', AUTO);
    responses.set('POST /api/backup/auto/run', AUTO);
    await runBackupAuto({ on: true, hours: '6', keep: '5', run: true });
    expect(calls[0]).toMatchObject({ method: 'PUT', body: { enabled: true, hours: 6, keep: 5 } });
    expect(calls[1]?.path).toBe('/api/backup/auto/run');
  });

  it('sync now --dry-run uses the query flag', async () => {
    responses.set('POST /api/sync/now?dryRun=true', { pushed: 1, pulled: 2, dryRun: true, firstSync: false, conflicts: [], items: [], errors: [] });
    await runSyncNow({ dryRun: true });
    expect(calls[0]?.path).toBe('/api/sync/now?dryRun=true');
  });

  it('sync setup takes secret and passphrase from env, never argv, and puts them only in the body', async () => {
    process.env.ASTRA_SYNC_SECRET = 'sekret';
    process.env.ASTRA_SYNC_PASSPHRASE = 'zq9-pw';
    responses.set('PUT /api/sync/config', { target: { kind: 'webdav' } });
    await runSyncSetup({ kind: 'webdav', url: 'https://dav.example', username: 'me', interval: '30', keys: false, signins: true });
    expect(calls[0]?.body).toMatchObject({
      target: { kind: 'webdav', url: 'https://dav.example', username: 'me', secret: 'sekret' },
      passphrase: 'zq9-pw',
      intervalMinutes: 30,
      includeKeys: false,
      includeSignins: true,
    });
    const out = log.mock.calls.map((c) => String(c[0])).join('\n');
    expect(out).not.toContain('sekret');
    expect(out).not.toContain('zq9-pw');
  });

  it('sync setup reads secret then passphrase from consecutive stdin lines', async () => {
    prompts.hidden.mockResolvedValueOnce('s3cret\nthe phrase\n');
    responses.set('PUT /api/sync/config', { target: { kind: 'github' } });
    await runSyncSetup({ kind: 'github', url: 'https://github.com/a/b', secretStdin: true, passphraseStdin: true });
    expect(calls[0]?.body.target.secret).toBe('s3cret');
    expect(calls[0]?.body.passphrase).toBe('the phrase');
  });

  it('folder setup sends no secret; invalid flags fail before any request', async () => {
    process.env.ASTRA_SYNC_PASSPHRASE = 'zq9-pw';
    responses.set('PUT /api/sync/config', { target: { kind: 'folder' } });
    await runSyncSetup({ kind: 'folder', url: '/mnt/x' });
    expect(calls[0]?.body.target.secret).toBeUndefined();
    calls.length = 0;
    await expect(runSyncSetup({ kind: 'webdav' })).rejects.toBeInstanceOf(AstraError);
    expect(calls).toHaveLength(0);
  });

  it('formats the sync status with conflicts', () => {
    const text = formatSyncStatus({
      enabled: true, configured: true, running: false, hasPassphrase: true, intervalMinutes: 15, sections: [], includeKeys: true, includeSignins: false,
      deviceId: 'dev1', lastSyncAt: null, lastError: 'oops', lastPushed: 1, lastPulled: 2, tracked: 9,
      target: { kind: 'folder', url: '/x', username: '', hasSecret: false, folder: 'astra', bucket: '', region: '', pathStyle: false, branch: '' },
      conflicts: [{ section: 'providers', id: 'p', label: 'P', winner: 'remote', at: 't', device: 'd' }],
    }).join('\n');
    expect(text).toContain('Sync: on');
    expect(text).toContain('Last error: oops');
    expect(text).toContain('providers/P: kept remote');
  });
});
