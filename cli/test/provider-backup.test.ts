import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

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

vi.mock('../src/lib/prompt.js', () => ({
  promptHidden: vi.fn(async () => 'typed passphrase'),
  confirmPrompt: vi.fn(async () => true),
}));

import { runProviderExport, runProviderImportFile } from '../src/commands/admin';
import { AstraError } from '../src/errors';

const FILE = { format: 'astra-providers', version: 1, secrets: 'none', providers: [{ name: 'Alpha' }, { name: 'Beta' }] };
const PREVIEW = {
  secrets: 'none',
  items: [
    { index: 0, name: 'Alpha', status: 'new', modelCount: 2, hasKey: true, keyMasked: 'sk-…1234', ignored: [], endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://a.example/v1' }] },
    { index: 1, name: 'Beta', status: 'same', modelCount: 1, hasKey: false, ignored: [], existing: { name: 'My beta' } },
  ],
};

let dir: string;
let log: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  dir = mkdtempSync(path.join(tmpdir(), 'astra-backup-'));
  calls.length = 0;
  responses.clear();
  responses.set('POST /api/providers/backup/export', FILE);
  responses.set('POST /api/providers/backup/preview', PREVIEW);
  responses.set('POST /api/providers/backup/import', { added: [{ id: 'n1', name: 'Alpha' }], skipped: [] });
  delete process.env.ASTRA_BACKUP_PASSPHRASE;
  log = vi.spyOn(console, 'log').mockImplementation(() => {});
});

afterEach(() => {
  log.mockRestore();
  rmSync(dir, { recursive: true, force: true });
  delete process.env.ASTRA_BACKUP_PASSPHRASE;
});

describe('provider export', () => {
  it('writes a key-less backup by default, owner-only, and asks the server for no keys', async () => {
    const out = path.join(dir, 'b.json');
    await runProviderExport({ out });
    expect(calls[0]?.body).toEqual({ secrets: 'none', passphrase: undefined, ids: undefined });
    expect(JSON.parse(readFileSync(out, 'utf8'))).toEqual(FILE);
    if (process.platform !== 'win32') expect(statSync(out).mode & 0o777).toBe(0o600);
  });

  it('refuses to replace a file without --force, before asking the server', async () => {
    const out = path.join(dir, 'b.json');
    writeFileSync(out, 'old');
    await expect(runProviderExport({ out })).rejects.toBeInstanceOf(AstraError);
    expect(calls).toHaveLength(0);
    expect(readFileSync(out, 'utf8')).toBe('old');
    await runProviderExport({ out, force: true });
    expect(JSON.parse(readFileSync(out, 'utf8')).format).toBe('astra-providers');
  });

  it('plaintext keys need --include-keys', async () => {
    await runProviderExport({ out: path.join(dir, 'p.json'), includeKeys: true });
    expect(calls[0]?.body).toMatchObject({ secrets: 'plaintext' });
    expect(log.mock.calls.map((c) => String(c[0])).join('\n')).toContain('plain text');
  });

  it('takes the passphrase from stdin or the environment, never from argv, and not together with --include-keys', async () => {
    await runProviderExport({ out: path.join(dir, 's.json'), passphraseStdin: true });
    expect(calls[0]?.body).toMatchObject({ secrets: 'passphrase', passphrase: 'typed passphrase' });

    process.env.ASTRA_BACKUP_PASSPHRASE = 'from the environment';
    await runProviderExport({ out: path.join(dir, 'e.json') });
    expect(calls[1]?.body).toMatchObject({ secrets: 'passphrase', passphrase: 'from the environment' });

    await expect(runProviderExport({ out: path.join(dir, 'x.json'), includeKeys: true })).rejects.toBeInstanceOf(AstraError);
  });

  it('passes --id through as the provider filter', async () => {
    await runProviderExport({ out: path.join(dir, 'i.json'), id: ['p1', 'p2'] });
    expect(calls[0]?.body).toMatchObject({ ids: ['p1', 'p2'] });
  });
});

describe('provider import-file', () => {
  const write = (doc: unknown = FILE) => {
    const file = path.join(dir, 'in.json');
    writeFileSync(file, JSON.stringify(doc));
    return file;
  };

  it('previews by default, prints masks only and writes nothing', async () => {
    await runProviderImportFile(write(), {});
    expect(calls.map((c) => c.path)).toEqual(['/api/providers/backup/preview']);
    const output = log.mock.calls.map((c) => String(c[0])).join('\n');
    expect(output).toContain('Alpha');
    expect(output).toContain('sk-…1234');
    expect(output).toContain('already added as "My beta"');
    expect(output).toContain('Run again with --yes to import the 1 new entry');
  });

  it('with --yes imports the new rows by index', async () => {
    await runProviderImportFile(write(), { yes: true });
    expect(calls.at(-1)).toMatchObject({ path: '/api/providers/backup/import', body: { indexes: [0] } });
  });

  it('--only needs --yes and refuses rows that cannot be imported', async () => {
    const file = write();
    await expect(runProviderImportFile(file, { only: ['0'] })).rejects.toBeInstanceOf(AstraError);
    await expect(runProviderImportFile(file, { yes: true, only: ['1'] })).rejects.toBeInstanceOf(AstraError);
    await expect(runProviderImportFile(file, { yes: true, only: ['7'] })).rejects.toBeInstanceOf(AstraError);
    await expect(runProviderImportFile(file, { yes: true, only: ['x'] })).rejects.toBeInstanceOf(AstraError);
  });

  it('an encrypted file gets its passphrase from stdin, and only the server ever sees it', async () => {
    await runProviderImportFile(write({ ...FILE, secrets: 'passphrase' }), { yes: true });
    expect(calls[0]?.body).toMatchObject({ passphrase: 'typed passphrase' });
    expect(log.mock.calls.map((c) => String(c[0])).join('\n')).not.toContain('typed passphrase');
  });

  it('rejects a file that is not a backup, without calling the server', async () => {
    await expect(runProviderImportFile(write({ hello: 1 }), {})).rejects.toBeInstanceOf(AstraError);
    await expect(runProviderImportFile(path.join(dir, 'missing.json'), {})).rejects.toBeInstanceOf(AstraError);
    expect(calls).toHaveLength(0);
  });
});
