import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { DailyBill } from '../api/types';
import { DailyBillSheet } from '../components/bill/DailyBillSheet';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

const BILL: DailyBill = {
  date: '2026-11-11', tzOffsetMinutes: 480, fromUtc: '2026-11-10T16:00:00Z', toUtc: '2026-11-11T10:42:07Z', generatedAtUtc: '2026-11-11T10:42:07Z',
  costUsd: 3.5, requests: 12, successRequests: 12, inputTokens: 120_000, outputTokens: 8_000,
  providers: [
    { providerId: 'a', name: 'Anthropic', costUsd: 3, requests: 10, inputTokens: 100_000, outputTokens: 6_000, models: [
      { model: 'claude-opus-5-5', costUsd: 3, requests: 10, inputTokens: 100_000, outputTokens: 6_000 },
    ] },
    { providerId: 'o', name: 'OpenAI', costUsd: 0.5, requests: 2, inputTokens: 20_000, outputTokens: 2_000, models: [
      { model: 'gpt-5', costUsd: 0.5, requests: 2, inputTokens: 20_000, outputTokens: 2_000 },
    ] },
  ],
};

function show(bill: DailyBill = BILL) {
  localStorage.setItem('astra.locale', 'en');
  const urls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(String(input));
      return new Response(JSON.stringify(bill), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <DailyBillSheet open onOpenChange={() => {}} />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
  return { urls };
}

describe('daily bill sheet', () => {
  it('asks for today in the viewer’s offset and prints a receipt by default', async () => {
    const { urls } = show();
    const preview = await screen.findByTestId('bill-preview');
    expect(await within(preview).findByText('■ ANTHROPIC')).toBeTruthy();
    expect(urls[0]).toContain(`/api/stats/daily-bill?tzOffset=${-new Date().getTimezoneOffset()}`);
    expect(within(preview).getByText('claude-opus-5-5')).toBeTruthy();
    expect(within(preview).getByText('$3.50')).toBeTruthy();
    expect(within(preview).getByText('00:00 – 18:42')).toBeTruthy();
    expect(within(preview).getByText('AG-20261111')).toBeTruthy();
  });

  it('switches to the comic page and remembers the choice', async () => {
    show();
    const preview = await screen.findByTestId('bill-preview');
    await within(preview).findByText('■ ANTHROPIC');
    fireEvent.click(screen.getByRole('button', { name: 'Comic' }));
    expect(await within(preview).findByText('Anthropic alone took 86%~')).toBeTruthy();
    expect(within(preview).getByText('as of 18:42')).toBeTruthy();
    expect(localStorage.getItem('astra.bill.style')).toBe('manga');
  });

  it('opens on the remembered style and says so when nothing was spent', async () => {
    localStorage.setItem('astra.bill.style', 'manga');
    show({ ...BILL, costUsd: 0, requests: 0, successRequests: 0, inputTokens: 0, outputTokens: 0, providers: [] });
    const preview = await screen.findByTestId('bill-preview');
    expect(await within(preview).findByText('Not a cent spent today~')).toBeTruthy();
    expect(within(preview).getByText('— Nothing spent today —')).toBeTruthy();
  });
});
