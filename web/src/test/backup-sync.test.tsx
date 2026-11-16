import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { AutoBackup, FullBackupPlan, SyncStatus } from '../api/types';
import { BackupSyncSettings } from '../components/backup/BackupSyncSettings';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

const PLAN: FullBackupPlan = {
  createdAt: '2026-11-16T00:00:00Z', astraVersion: '1.2.3', hasKeys: false, mode: 'merge', available: ['providers', 'tokens'], notes: [], legacy: false,
  sections: [
    { section: 'providers', added: 3, changed: 2, removed: 1, unchanged: 7, items: [{ id: 'p1', label: 'Alpha provider', status: 'added' }], truncated: false },
    { section: 'tokens', added: 0, changed: 0, removed: 0, unchanged: 4, items: [], truncated: false },
  ],
};
const AUTO: AutoBackup = {
  enabled: false, hours: 24, keep: 7, hasPassphrase: false, dir: '/home/x/backups',
  files: [{ name: 'auto-1.astra-backup', kind: 'auto', at: '2026-11-16T00:00:00Z', size: 2048, encrypted: false }],
};
const SYNC: SyncStatus = {
  enabled: false, configured: true, hasPassphrase: true, intervalMinutes: 15, sections: [], includeKeys: false, includeSignins: false, deviceId: 'dev-1',
  lastSyncAt: null, lastError: null, lastPushed: 0, lastPulled: 0, running: false, tracked: 5,
  target: { kind: 's3', url: 'https://s3.example', username: 'AKIA', hasSecret: true, folder: 'astra', bucket: 'b', region: 'r', pathStyle: false, branch: '' },
  conflicts: [{ section: 'providers', id: 'p9', label: 'Conflicted one', winner: 'remote', at: '2026-11-16T00:00:00Z', device: 'dev-2' }],
};

function show(opts: { remote?: boolean } = {}) {
  localStorage.setItem('astra.locale', 'en');
  const calls: { method: string; url: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const body = init?.body ? JSON.parse(String(init.body)) : undefined;
      calls.push({ method, url, body });
      const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url.endsWith('/api/auth/status')) return json({ required: !!opts.remote, signedIn: true, remote: false, adminBlocked: false });
      if (url.endsWith('/api/backup/info')) return json({ sections: { providers: 12, tokens: 4 }, version: '1.2.3' });
      if (url.endsWith('/api/backup/export')) return json({ name: 'astra-backup.json', content: '{"x":1}', encrypted: !!body?.passphrase, hasKeys: !!body?.includeKeys, documents: 16, warnings: [] });
      if (url.endsWith('/api/backup/preview')) return json(PLAN);
      if (url.endsWith('/api/backup/restore')) return json({ plan: PLAN, written: 5, removed: 1, skipped: ['skip-me'], errors: [], safetyBackup: 'restore-1.astra-backup' });
      if (url.endsWith('/api/backup/auto')) return json(AUTO);
      if (url.endsWith('/api/sync/status')) return json(SYNC);
      if (url.endsWith('/api/sync/config')) return json(SYNC);
      if (url.endsWith('/api/sync/test')) return json({ ok: true });
      return json({});
    }),
  );
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:x'), revokeObjectURL: vi.fn() }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <BackupSyncSettings />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
  return { calls, last: (suffix: string) => calls.filter((c) => c.url.endsWith(suffix) && c.method !== 'GET').at(-1) };
}

const pickFile = (text: string) => {
  const input = screen.getByLabelText('Choose a backup file') as HTMLInputElement;
  const file = new File([text], 'b.json', { type: 'application/json' });
  Object.defineProperty(file, 'text', { value: async () => text });
  fireEvent.change(input, { target: { files: [file] } });
};

describe('backup and sync settings', () => {
  it('exports without keys and without a passphrase by default', async () => {
    const { last } = show();
    await screen.findByText('12');
    fireEvent.click(screen.getByRole('button', { name: 'Export backup' }));
    await waitFor(() => expect(last('/api/backup/export')).toBeDefined());
    const body = last('/api/backup/export')!.body as { includeKeys: boolean; passphrase?: string; sections: string[] };
    expect(body.includeKeys).toBe(false);
    expect(body.passphrase).toBeUndefined();
    expect(body.sections).toContain('providers');
  });

  it('keys need a passphrase of at least 8 characters', async () => {
    const { last } = show();
    await screen.findByText('12');
    fireEvent.click(screen.getByLabelText('Include keys (encrypted with a passphrase)'));
    const run = screen.getByRole('button', { name: 'Export backup' });
    expect(run).toBeDisabled();
    expect(screen.getAllByText(/cannot be recovered/).length).toBeGreaterThan(0);
    const pass = screen.getByLabelText('Passphrase (at least 8 characters)');
    fireEvent.change(pass, { target: { value: 'short' } });
    expect(run).toBeDisabled();
    fireEvent.change(pass, { target: { value: 'long-enough-pass' } });
    expect(run).not.toBeDisabled();
    fireEvent.click(run);
    await waitFor(() => expect(last('/api/backup/export')).toBeDefined());
    expect(last('/api/backup/export')!.body).toMatchObject({ includeKeys: true, passphrase: 'long-enough-pass' });
  });

  it('previews with counts, then restores the chosen sections and mode', async () => {
    const { last } = show();
    await screen.findByText('12');
    pickFile('{"backup":true}');
    await screen.findByText('b.json');
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));
    const counts = await screen.findByTestId('plan-providers');
    expect(counts.textContent).toBe('3 added · 2 changed · 1 removed · 7 unchanged');
    expect(screen.getByText('Alpha provider')).toBeTruthy();
    expect(last('/api/backup/preview')!.body).toMatchObject({ source: 'file', content: '{"backup":true}', mode: 'merge' });

    const restorePanel = screen.getByTestId('plan-tokens').closest('.p-4') as HTMLElement;
    fireEvent.click(within(restorePanel).getByLabelText('Tokens')); // leave tokens out
    fireEvent.click(screen.getByRole('button', { name: 'Restore' }));
    await screen.findByTestId('restore-result');
    expect(last('/api/backup/restore')!.body).toMatchObject({ source: 'file', mode: 'merge', sections: ['providers'] });
    expect(screen.getByText(/restore-1\.astra-backup/)).toBeTruthy();
    expect(screen.getByText('skip-me')).toBeTruthy();
  });

  it('asks for confirmation before a replace restore', async () => {
    const { calls } = show();
    await screen.findByText('12');
    pickFile('{}');
    await screen.findByText('b.json');
    fireEvent.click(screen.getByRole('button', { name: 'Replace' }));
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));
    await screen.findByTestId('plan-providers');
    fireEvent.click(screen.getByRole('button', { name: 'Restore' }));
    await screen.findByText('Replace what you have with this backup?');
    expect(calls.some((c) => c.url.endsWith('/api/backup/restore'))).toBe(false);
    const dialogButtons = screen.getAllByRole('button', { name: 'Restore' });
    fireEvent.click(dialogButtons.at(-1)!);
    await screen.findByTestId('restore-result');
    expect(calls.find((c) => c.url.endsWith('/api/backup/restore'))!.body).toMatchObject({ mode: 'replace' });
  });

  it('sends the sync target, keeps the saved secret out of the page and omits it when untouched', async () => {
    const { last } = show();
    const secret = await screen.findByLabelText('Secret');
    expect((secret as HTMLInputElement).value).toBe('');
    expect((secret as HTMLInputElement).placeholder).toBe('Saved (leave empty to keep)');
    expect(screen.getByLabelText('Bucket')).toHaveProperty('value', 'b');

    fireEvent.change(screen.getByLabelText('Bucket'), { target: { value: 'new-bucket' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Save' }).at(-1)!); // the sync form's
    await waitFor(() => expect(last('/api/sync/config')).toBeDefined());
    const body = last('/api/sync/config')!.body as { target: Record<string, unknown> };
    expect(body.target).toMatchObject({ kind: 's3', bucket: 'new-bucket', region: 'r' });
    expect(body.target.secret).toBeUndefined();
    expect(document.body.textContent).not.toContain('hunter2');
  });

  it('shows sync status and conflicts', async () => {
    show();
    expect(await screen.findByText('Conflicted one')).toBeTruthy();
    expect(screen.getByText('Kept remote')).toBeTruthy();
    expect(screen.getByText('dev-1')).toBeTruthy();
  });

  it('is read-only off this machine', async () => {
    show({ remote: true });
    await screen.findAllByText(/only be used on the machine running Astra/);
    expect(screen.getByLabelText('Include keys (encrypted with a passphrase)')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Sync now' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Preview' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Export backup' })).not.toBeDisabled();
  });
});
