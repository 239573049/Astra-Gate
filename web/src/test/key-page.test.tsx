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
});
