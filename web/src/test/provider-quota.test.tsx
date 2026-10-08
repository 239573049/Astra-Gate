import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ProviderQuotaLine, ProviderQuotaSection } from '../components/ProviderQuotaSection';
import type { Provider, ProviderQuotaConfig, ProviderQuotaSnapshot, QuotaTemplate } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';
import { formatAmount, planPhrase, quotaFailure, quotaSummary, usedPercentOf } from '../lib/quota';
import { providerQuotaPlans, tightestQuota } from '../lib/tray';

const spies = vi.hoisted(() => ({
  save: vi.fn(),
  test: vi.fn(),
  fetch: vi.fn(),
}));

const templates: QuotaTemplate[] = [
  {
    id: 'deepseek', name: 'DeepSeek', kind: 'balance', appliesTo: ['deepseek'], hosts: ['api.deepseek.com'], params: [],
    request: { method: 'GET', url: '{{origin}}/user/balance', auth: 'provider' },
    extract: { plans: [{ each: 'balance_infos', unit: 'currency', remaining: 'total_balance' }] },
  },
  {
    id: 'newapi', name: 'New API / One API', kind: 'balance', appliesTo: ['custom-chat'], hosts: [],
    params: [{ name: 'userId', secret: false, required: true }, { name: 'accessToken', secret: true, required: true }],
    request: { method: 'GET', url: '{{origin}}/api/user/self', auth: 'none', headers: { 'New-Api-User': '{{userId}}' } },
    extract: { remaining: { $div: ['data.quota', 500000] } },
  },
];

vi.mock('../api/hooks', () => ({
  useQuotaTemplates: () => ({ data: templates }),
  useSaveProviderQuotaConfig: () => ({ mutate: spies.save, isPending: false }),
  useTestProviderQuota: () => ({ mutate: spies.test, isPending: false }),
  useFetchProviderQuota: () => ({ mutate: spies.fetch, isPending: false }),
}));

// jsdom has no ResizeObserver (the test sheet's code block measures itself); a noop one never fires.
class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

const config = (over: Partial<ProviderQuotaConfig> = {}): ProviderQuotaConfig => ({
  enabled: false, template: null, suggestedTemplate: 'deepseek', effectiveTemplate: 'deepseek', intervalMinutes: null,
  effectiveIntervalMinutes: 30, timeoutSec: 10, baseUrl: null, params: {}, secrets: [], request: null, extract: null, ...over,
});

const provider = (over: Partial<Provider> = {}): Provider =>
  ({
    id: 'p1', name: 'DeepSeek', authScheme: 'bearer', enabled: true, templateId: 'deepseek',
    endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://api.deepseek.com/v1' }],
    quotaConfig: config(), quota: null, quotaCheckedAtUtc: null, ...over,
  }) as unknown as Provider;

const balance = (over: Partial<ProviderQuotaSnapshot> = {}): ProviderQuotaSnapshot => ({
  fetchedAtUtc: '2026-10-08T12:00:00Z', template: 'deepseek', kind: 'balance', isValid: true,
  plans: [{ name: 'CNY', unit: 'CNY', remaining: 110 }], ...over,
});

const show = (ui: React.ReactNode) => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>{ui}</FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  spies.save.mockReset();
  spies.test.mockReset();
  spies.fetch.mockReset();
});

afterEach(() => {
  cleanup();
  localStorage.clear();
});

describe('quota formatting', () => {
  it('formats currencies, credits and percentages without inventing numbers', () => {
    expect(formatAmount(110, 'CNY', 'en')).toBe('CN¥110.00');
    expect(formatAmount(0.0042, 'USD', 'en')).toBe('$0.0042');
    expect(formatAmount(1200, 'credits', 'en')).toBe('1,200 credits');
    expect(usedPercentOf({ used: 5, total: 20 })).toBe(25);
    expect(usedPercentOf({ remaining: 3 })).toBeNull();
    expect(planPhrase({ remaining: 10, unit: 'USD', used: 5, total: 15 }, t, 'en')).toBe('Balance $10.00 · 33% used');
    expect(planPhrase({ windowMinutes: 300, usedPercent: 12 }, t, 'en')).toBe('5h: 12% used');
    expect(planPhrase({}, t, 'en')).toBeNull();
  });

  it('shows a failure only when it is newer than the last good snapshot', () => {
    const failed = balance({ error: 'HTTP 401', errorCode: 'unauthorized', errorAtUtc: '2026-10-08T13:00:00Z', failures: 1 });
    expect(quotaFailure(failed)).toEqual({ code: 'unauthorized', message: 'HTTP 401' });
    expect(quotaFailure({ ...failed, errorAtUtc: '2026-10-08T11:00:00Z' })).toBeNull();
    expect(quotaSummary(failed, t, 'en')).toBe('Balance CN¥110.00');
    expect(quotaSummary(balance({ isValid: false, plans: [] }), t, 'en')).toBe('Account unavailable');
  });

  it('turns provider snapshots into tray rows; balances without a total have no meter', () => {
    const plans = providerQuotaPlans([
      provider({ quotaConfig: config({ enabled: true }), quota: balance() }),
      provider({
        id: 'p2', name: 'GLM', quotaConfig: config({ enabled: true }),
        quota: { plans: [{ windowMinutes: 300, usedPercent: 92, resetsAtUtc: '2026-10-08T13:00:00Z' }, { windowMinutes: 10080, usedPercent: 40 }] },
      }),
      provider({ id: 'p3', quotaConfig: config({ enabled: false }), quota: balance() }),
      provider({ id: 'p4', enabled: false, quotaConfig: config({ enabled: true }), quota: balance() }),
      provider({ id: 'p5', authScheme: 'oauth-subscription', quotaConfig: config({ enabled: true }), quota: balance() }),
    ]);
    expect(plans.map((p) => p.providerId)).toEqual(['p1', 'p2']);
    expect(plans[0]!.rows).toEqual([
      { kind: 'balance', windowMinutes: null, remaining: null, resetsAt: null, amount: { value: 110, unit: 'CNY' }, label: 'CNY' },
    ]);
    expect(plans[1]!.rows.map((r) => [r.kind, r.windowMinutes, r.remaining])).toEqual([
      ['window', 300, 8],
      ['window', 10080, 60],
    ]);
    // The meter-less balance never wins "tightest": the 5h window does.
    expect(tightestQuota(plans)?.row).toMatchObject({ kind: 'window', remaining: 8 });
    expect(tightestQuota([plans[0]!])).toBeNull();
  });
});

describe('ProviderQuotaLine', () => {
  it('renders nothing while the usage query is off', () => {
    show(<ProviderQuotaLine provider={provider()} />);
    expect(screen.queryByRole('button', { name: 'Refresh balance' })).toBeNull();
    expect(screen.queryByText('Not queried yet')).toBeNull();
  });

  it('shows the balance, a newer failure, and refreshes on demand', () => {
    show(
      <ProviderQuotaLine
        provider={provider({
          quotaConfig: config({ enabled: true }),
          quota: balance({ error: 'x', errorCode: 'rate_limited', errorAtUtc: '2026-10-08T13:00:00Z' }),
        })}
      />,
    );
    expect(screen.getByText('Balance CN¥110.00')).toBeInTheDocument();
    expect(screen.getByText('Usage query failed: Rate limited upstream; use a longer interval')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh balance' }));
    expect(spies.fetch).toHaveBeenCalledWith('p1', expect.anything());
  });
});

describe('ProviderQuotaSection', () => {
  it('saves the toggle and queries right away when the query is turned on', async () => {
    show(<ProviderQuotaSection provider={provider()} />);
    expect(screen.getByText('Not queried yet')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('switch', { name: 'Enable usage query' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(spies.save).toHaveBeenCalled());
    const [body, options] = spies.save.mock.calls[0]!;
    expect(body).toMatchObject({ enabled: true, template: null, intervalMinutes: null, timeoutSec: 10, baseUrl: null, params: {}, secrets: {} });
    expect(body.request).toBeUndefined();
    options.onSuccess();
    expect(spies.fetch).toHaveBeenCalledWith('p1', expect.anything());
  });

  it('asks for New API parameters and only sends a secret that was typed', async () => {
    show(
      <ProviderQuotaSection
        provider={provider({
          templateId: 'custom-chat',
          quotaConfig: config({ enabled: true, template: 'newapi', suggestedTemplate: 'newapi', params: { userId: '7' }, secrets: ['accessToken'] }),
        })}
      />,
    );
    expect(screen.getByText('Saved — leave empty to keep')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('User ID'), { target: { value: '42' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(spies.save).toHaveBeenCalled());
    expect(spies.save.mock.calls[0]![0]).toMatchObject({ template: 'newapi', params: { userId: '42' }, secrets: {} });

    fireEvent.change(screen.getByLabelText('System access token'), { target: { value: 'tok-new' } });
    fireEvent.click(screen.getByRole('button', { name: 'Test query' }));
    await waitFor(() => expect(spies.test).toHaveBeenCalled());
    expect(spies.test.mock.calls[0]![0]).toMatchObject({ secrets: { accessToken: 'tok-new' } });
  });

  it('shows the redacted raw response and the extracted result of a test run', async () => {
    spies.test.mockImplementation((_body, options) =>
      options.onSuccess({ ok: true, httpStatus: 200, url: 'https://api.deepseek.com/user/balance', raw: { balance_infos: [] }, snapshot: balance() }),
    );
    show(<ProviderQuotaSection provider={provider()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Test query' }));
    expect(await screen.findByText('Query succeeded')).toBeInTheDocument();
    expect(screen.getByText('https://api.deepseek.com/user/balance')).toBeInTheDocument();
    expect(screen.getByText('Raw response (redacted)')).toBeInTheDocument();
  });
});
