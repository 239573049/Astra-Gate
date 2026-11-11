import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { BackupFile, BackupPreview } from '../api/types';
import { ProviderBackupSheet } from '../components/ProviderBackupSheet';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

const FILE: BackupFile = { format: 'astra-providers', version: 1, secrets: 'none', providers: [{ name: 'Alpha' }, { name: 'Beta' }] };
const PREVIEW: BackupPreview = {
  secrets: 'none',
  items: [
    { index: 0, name: 'Alpha', status: 'new', modelCount: 2, hasKey: false, ignored: [], endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://a.example/v1' }] },
    { index: 1, name: 'Beta', status: 'same', modelCount: 1, hasKey: true, keyMasked: 'sk-…1234', ignored: [], existing: { id: 'p1', name: 'My beta' } },
  ],
};

function show() {
  localStorage.setItem('astra.locale', 'en');
  const calls: { url: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const body = init?.body ? JSON.parse(String(init.body)) : undefined;
      calls.push({ url, body });
      const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url.endsWith('/api/backup/webdav')) return json({ configured: true, url: 'https://dav.example/x', username: 'me', hasPassword: true, folder: 'astra' });
      if (url.endsWith('/webdav/upload')) return json({ uploaded: 2, name: 'providers-1.json' });
      if (url.endsWith('/webdav/restore')) return json(FILE);
      if (url.endsWith('/backup/export')) return json(FILE);
      if (url.endsWith('/backup/preview')) return json(PREVIEW);
      if (url.endsWith('/backup/import')) return json({ added: [{ id: 'n1', name: 'Alpha' }], skipped: [] });
      return json({});
    }),
  );
  // the download is an anchor click on a blob URL, which jsdom neither creates nor navigates
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:x'), revokeObjectURL: vi.fn() }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <ProviderBackupSheet open onOpenChange={() => {}} />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
  return { calls };
}

describe('provider backup sheet', () => {
  it('exports without keys by default', async () => {
    const { calls } = show();
    fireEvent.click(await screen.findByRole('button', { name: 'Export' }));
    await waitFor(() => expect(calls.some((c) => c.url.endsWith('/backup/export'))).toBe(true));
    expect(calls.find((c) => c.url.endsWith('/backup/export'))?.body).toEqual({ secrets: 'none' });
  });

  it('previews a chosen file and imports only the ticked, new entries', async () => {
    const { calls } = show();
    const input = (await screen.findByLabelText('Choose a backup file')) as HTMLInputElement;
    const file = new File([JSON.stringify(FILE)], 'astra-providers.json', { type: 'application/json' });
    Object.defineProperty(file, 'text', { value: () => Promise.resolve(JSON.stringify(FILE)) });
    fireEvent.change(input, { target: { files: [file] } });

    expect(await screen.findByText('Alpha')).toBeTruthy();
    expect(screen.getByText('Already there, skipped')).toBeTruthy();
    expect((screen.getByLabelText('Beta') as HTMLInputElement).getAttribute('aria-checked') ?? 'false').not.toBe('true');

    fireEvent.click(screen.getByRole('button', { name: 'Import selected (1)' }));
    await waitFor(() => expect(calls.some((c) => c.url.endsWith('/backup/import'))).toBe(true));
    expect(calls.find((c) => c.url.endsWith('/backup/import'))?.body).toMatchObject({ indexes: [0] });
  });

  it('backs up to WebDAV without keys by default and never offers plaintext there', async () => {
    const { calls } = show();
    fireEvent.click(await screen.findByRole('button', { name: 'Back up now' }));
    await waitFor(() => expect(calls.some((c) => c.url.endsWith('/webdav/upload'))).toBe(true));
    expect(calls.find((c) => c.url.endsWith('/webdav/upload'))?.body).toEqual({ secrets: 'none' });
  });

  it('restores from WebDAV into the ordinary preview instead of importing at once', async () => {
    const { calls } = show();
    fireEvent.click(await screen.findByRole('button', { name: 'Restore from the cloud' }));
    expect(await screen.findByText('Already there, skipped')).toBeTruthy();
    expect(calls.some((c) => c.url.endsWith('/backup/preview'))).toBe(true);
    expect(calls.some((c) => c.url.endsWith('/backup/import'))).toBe(false);
  });

  it('refuses a file that is not a backup, without calling the server', async () => {
    const { calls } = show();
    const input = (await screen.findByLabelText('Choose a backup file')) as HTMLInputElement;
    const file = new File(['{"hello":1}'], 'x.json', { type: 'application/json' });
    Object.defineProperty(file, 'text', { value: () => Promise.resolve('{"hello":1}') });
    fireEvent.change(input, { target: { files: [file] } });
    expect(await screen.findByText('This is not a valid backup file.')).toBeTruthy();
    expect(calls.some((c) => c.url.endsWith('/backup/preview'))).toBe(false);
  });
});
