import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { KeyInfo } from '../api/types';
import { I18nProvider, translate } from '../i18n';
import { KeyPage } from '../pages/KeyPage';

// The real icon module pulls in @lobehub/icons, whose JSON imports Node ESM rejects; the page only needs names.
vi.mock('../components/icons', () => ({ CLIENT_META: { codex: { name: 'Codex' } } }));

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

const usage = { requests: 3, successRequests: 3, costUsd: 1.25, inputTokens: 1000, outputTokens: 200 };
const info: KeyInfo = {
  name: 'Shared', keyPrefix: 'sk-astra-AbC123…', enabled: true, allowedClients: ['codex'], directCall: false, timeZone: 'Asia/Shanghai',
  limits: [{ metric: 'cost', period: 'month', unit: 'usd', limit: 10, used: 9.5, remaining: 0.5, usedPercent: 95, exhausted: false, resetsAt: '2026-11-01T00:00:00+08:00' }],
  rpm: { limit: 30, used: 2 },
  usage: { today: usage, week: usage, month: usage, total: usage },
  daily: [{ day: '2026-10-13', ...usage }],
};

const fetchMock = vi.fn();

beforeEach(() => {
  localStorage.setItem('astra.locale', 'en');
  sessionStorage.clear();
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
  window.history.replaceState(null, '', '/key');
});
afterEach(cleanup);

const show = () =>
  render(
    <I18nProvider>
      <KeyPage />
    </I18nProvider>,
  );

describe('token self-service page', () => {
  it('looks a key up with the key itself and shows only its own figures', async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify(info), { status: 200 }));
    show();
    fireEvent.change(screen.getByLabelText(t('key.title')), { target: { value: ' sk-astra-secret ' } });
    fireEvent.click(screen.getByRole('button', { name: t('key.submit') }));

    await waitFor(() => expect(screen.getByText('Shared')).toBeInTheDocument());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(String(url)).toMatch(/\/v1\/astra\/key$/);
    expect((init as RequestInit).headers).toEqual({ Authorization: 'Bearer sk-astra-secret' });
    expect(screen.getByRole('progressbar', { name: `${t('tokens.period.month')} · ${t('tokens.metric.cost')}` })).toHaveAttribute('aria-valuenow', '95');
    expect(screen.getByText(t('key.rpm', { used: 2, limit: 30 }))).toBeInTheDocument();
    expect(screen.getByText(new RegExp(t('key.timeZone', { zone: 'Asia/Shanghai' })))).toBeInTheDocument();
    expect(sessionStorage.getItem('astra.keyLookup')).toBe('sk-astra-secret');

    fireEvent.click(screen.getByRole('button', { name: t('key.forget') }));
    expect(sessionStorage.getItem('astra.keyLookup')).toBeNull();
    expect(screen.getByRole('button', { name: t('key.submit') })).toBeInTheDocument();
  });

  it('reports an unknown key and takes a ?key= link out of the address bar', async () => {
    fetchMock.mockResolvedValue(new Response('{}', { status: 401 }));
    window.history.replaceState(null, '', '/key?key=sk-astra-wrong');
    show();
    await waitFor(() => expect(screen.getByText(t('key.invalid'))).toBeInTheDocument());
    expect(window.location.search).toBe('');
    expect(sessionStorage.getItem('astra.keyLookup')).toBeNull();
  });

  it('shows the key’s statistics and its own request log, a page at a time', async () => {
    const stats = {
      range: '7d', timeZone: 'Asia/Shanghai', from: '2026-10-09T16:00:00Z', retentionDays: 30,
      summary: { requests: 4, successRequests: 3, successRate: 0.75, costUsd: 2.5, inputTokens: 100, outputTokens: 20, cacheReadTokens: 0, cacheWriteTokens: 0, reasoningTokens: 0, avgTtftMs: 420 },
      series: [{ bucket: '2026-10-15', requests: 4, costUsd: 2.5, inputTokens: 100, outputTokens: 20 }],
      models: [{ model: 'gpt-x', requests: 4, successRequests: 3, costUsd: 2.5, inputTokens: 100, outputTokens: 20 }],
      clients: [{ client: null, requests: 4, successRequests: 3, costUsd: 2.5, inputTokens: 100, outputTokens: 20 }],
      errors: [{ category: 'rate_limited', count: 1 }],
    };
    const request = (id: string, errorCategory: string | null = null) => ({
      id, time: '2026-10-15T06:00:00Z', model: 'gpt-x', client: 'codex', stream: true, status: errorCategory ? 'upstream_error' : 'success',
      httpStatus: errorCategory ? 429 : 200, errorCategory, inputTokens: 10, outputTokens: 2, cacheReadTokens: 0, cacheWriteTokens: 0,
      reasoningTokens: 0, costUsd: 0.5, ttftMs: 300, totalMs: 900,
    });
    fetchMock.mockImplementation((url: string) => {
      const u = String(url);
      if (u.includes('/v1/astra/key/stats')) return Promise.resolve(new Response(JSON.stringify(stats)));
      if (u.includes('/v1/astra/key/requests'))
        return Promise.resolve(new Response(JSON.stringify(u.includes('before=r2')
          ? { items: [request('r3')], nextBefore: null }
          : { items: [request('r1'), request('r2', 'rate_limited')], nextBefore: 'r2' })));
      return Promise.resolve(new Response(JSON.stringify(info)));
    });
    sessionStorage.setItem('astra.keyLookup', 'sk-astra-secret');
    show();
    await waitFor(() => expect(screen.getByText('Shared')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: t('key.tab.stats') }));
    await waitFor(() => expect(screen.getAllByText('75.0%').length).toBeGreaterThan(0));
    expect(screen.getByText(`${t('key.category.rate_limited')} · 1`)).toBeInTheDocument();
    expect(screen.getByText(t('key.stats.direct'))).toBeInTheDocument();
    expect(screen.getByText(t('key.stats.retention', { days: 30 }))).toBeInTheDocument();
    const statsCall = fetchMock.mock.calls.find(([u]) => String(u).includes('/stats'))!;
    expect(String(statsCall[0])).toContain('range=7d');
    expect((statsCall[1] as RequestInit).headers).toEqual({ Authorization: 'Bearer sk-astra-secret' });

    fireEvent.click(screen.getByRole('button', { name: t('key.tab.requests') }));
    await waitFor(() => expect(screen.getByText(t('key.category.rate_limited'))).toBeInTheDocument());
    expect(screen.getAllByText('gpt-x')).toHaveLength(2);
    fireEvent.click(screen.getByRole('button', { name: t('key.requests.more') }));
    await waitFor(() => expect(screen.getAllByText('gpt-x')).toHaveLength(3));
    expect(screen.queryByRole('button', { name: t('key.requests.more') })).not.toBeInTheDocument();

    // Failed only: the server gets every non-success status.
    fireEvent.click(screen.getByRole('button', { name: t('key.requests.failed') }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([u]) => String(u).includes('status=upstream_error'))).toBe(true));
  });
});
