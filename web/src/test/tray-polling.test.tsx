import { renderHook, cleanup } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { useTrayData } from '../components/tray/useTrayData';
import { TRAY_SECTIONS, type TrayPrefs, type TrayTitleMode } from '../shell/bridge';

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  api: vi.fn(() => new Promise(() => {})),
}));

afterEach(cleanup);

const allSections = Object.fromEntries(TRAY_SECTIONS.map((s) => [s, true])) as TrayPrefs['sections'];

/** Each enabled tray query (by its first distinguishing key parts) and whether it keeps polling while hidden. */
function polling(title: TrayTitleMode, opts: { titleOnly?: boolean } = {}) {
  const qc = new QueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  renderHook(() => useTrayData({ title, sections: allSections }, { enabled: true, background: true, live: false, ...opts }), { wrapper });
  return Object.fromEntries(
    qc.getQueryCache().findAll().filter((q) => q.isActive()).map((q) => {
      const key = q.queryKey.slice(0, 3).join(':');
      const options = q.observers[0]!.options as { refetchIntervalInBackground?: boolean };
      return [key, options.refetchIntervalInBackground ?? false];
    }),
  );
}

describe('tray data polling', () => {
  it('keeps only the queries the menu-bar title needs polling while hidden', () => {
    const requests = polling('requests');
    expect(requests['stats:summary:today']).toBe(true);
    expect(requests['stats:timeseries:today']).toBe(false);
    expect(requests['stats:top:today']).toBe(false);
    expect(requests['clients']).toBe(false);
    expect(requests['providers']).toBe(false);

    const quota = polling('quota');
    expect(quota['stats:summary:today']).toBe(false);
    expect(quota['providers']).toBe(true);

    expect(Object.values(polling('none')).some(Boolean)).toBe(false);
  });

  it('fetches only the title’s data for the main window', () => {
    expect(Object.keys(polling('requests', { titleOnly: true })).sort()).toEqual(['settings', 'stats:summary:today']);
    expect(Object.keys(polling('alerts', { titleOnly: true })).sort()).toEqual(['providers', 'settings', 'stats:summary:today']);
  });
});
