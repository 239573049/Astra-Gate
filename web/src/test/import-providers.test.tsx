import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ImportCandidate, ImportSource } from '../api/types';
import { ImportProvidersSheet } from '../components/ImportProvidersSheet';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

function candidate(over: Partial<ImportCandidate> & Pick<ImportCandidate, 'ref' | 'name'>): ImportCandidate {
  return {
    source: 'magpie', endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://relay.example/v1' }], authScheme: 'bearer',
    hasKey: true, keyMasked: 'sk-…1234', keyFingerprint: 'abcd1234', models: [], headers: [], templateId: null,
    status: 'new', existing: null, off: false, skipReason: null, ignoredFields: [], ...over,
  };
}

const SOURCES: ImportSource[] = [
  {
    id: 'cc-switch', name: 'CC Switch', path: '/home/u/.cc-switch/cc-switch.db', found: false, items: [],
  },
  {
    id: 'magpie', name: 'Magpie', path: '/home/u/.config/magpie/providers.json', found: true,
    items: [
      candidate({ ref: 'magpie:new', name: 'Fresh relay', models: ['a', 'b'], templateId: 'deepseek' }),
      candidate({ ref: 'magpie:same', name: 'Known relay', status: 'same', existing: { id: 'p1', name: 'My relay' } }),
      candidate({ ref: 'magpie:host', name: 'Twin relay', status: 'sameHost', existing: { id: 'p2', name: 'Other' } }),
      candidate({ ref: 'magpie:off', name: 'Old relay', status: 'off', off: true }),
      candidate({ ref: 'magpie:login', name: 'Login thing', status: 'skip', skipReason: 'official-login', hasKey: false, keyMasked: null }),
      candidate({ ref: 'magpie:ign', name: 'Odd headers', ignoredFields: ['header:Host'] }),
    ],
  },
  { id: 'alma', name: 'Alma', path: '/x/alma.db', found: true, error: 'Unsupported layout', items: [] },
];

function show(open = true) {
  localStorage.setItem('astra.locale', 'en');
  const posts: unknown[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
    if (url.endsWith('/api/providers/import/sources')) return json(SOURCES);
    if (url.endsWith('/api/providers/import') && init?.method === 'POST') {
      posts.push(JSON.parse(String(init.body)));
      return json({ added: [{ id: 'n1', name: 'Fresh relay' }], skipped: [] });
    }
    return json({});
  });
  vi.stubGlobal('fetch', fetchMock);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <ImportProvidersSheet open={open} onOpenChange={() => {}} />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
  return { posts };
}

describe('import providers sheet', () => {
  it('groups candidates by source and shows what each source read, never a plaintext key', async () => {
    show();
    await screen.findByText('Fresh relay');
    expect(screen.getByText('/home/u/.config/magpie/providers.json')).toBeInTheDocument();
    expect(screen.getByText('Not found')).toBeInTheDocument(); // CC Switch
    expect(screen.getByText(/Could not be read: Unsupported layout/)).toBeInTheDocument(); // Alma
    expect(screen.getAllByText('sk-…1234').length).toBeGreaterThan(0);
    expect(screen.getByText('2 models')).toBeInTheDocument();
    expect(screen.getByText('Template: deepseek')).toBeInTheDocument();
    expect(screen.getByText('Will ignore: header:Host')).toBeInTheDocument();
  });

  it('ticks only new entries, blocks same / skipped ones and explains why', async () => {
    show();
    await screen.findByText('Fresh relay');
    expect(screen.getByRole('checkbox', { name: 'Fresh relay' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Twin relay' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Old relay' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Known relay' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'Login thing' })).toBeDisabled();
    expect(screen.getByText('Official login (tokens are bound to the source app)')).toBeInTheDocument();
    expect(screen.getByText('Already added as "My relay"')).toBeInTheDocument();
    expect(screen.getByText('"Other" already uses this address; this will be imported as a new provider')).toBeInTheDocument();
  });

  it('imports only the selection, by source and ref', async () => {
    const { posts } = show();
    await screen.findByText('Fresh relay');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Twin relay' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Odd headers' })); // untick a default
    const button = screen.getByRole('button', { name: 'Import selected (2)' });
    fireEvent.click(button);
    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0]).toEqual([
      { source: 'magpie', ref: 'magpie:new' },
      { source: 'magpie', ref: 'magpie:host' },
    ]);
  });

  it('filters by name or address', async () => {
    show();
    await screen.findByText('Fresh relay');
    fireEvent.change(screen.getByPlaceholderText('Search by name or address'), { target: { value: 'twin' } });
    expect(screen.queryByText('Fresh relay')).not.toBeInTheDocument();
    const section = screen.getByRole('region', { name: 'Magpie' });
    expect(within(section).getByText('Twin relay')).toBeInTheDocument();
  });

  it('disables the import button when nothing is selected', async () => {
    show();
    await screen.findByText('Fresh relay');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Fresh relay' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Odd headers' }));
    expect(screen.getByRole('button', { name: 'Import selected (0)' })).toBeDisabled();
  });
});
