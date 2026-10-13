import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { AlertEvent, AlertRule, Webhook } from '../api/types';
import { NotificationSettings } from '../components/NotificationSettings';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';

const spies = vi.hoisted(() => ({
  createWebhook: vi.fn(),
  createRule: vi.fn(),
  test: vi.fn(),
  webhooks: [] as unknown[],
  rules: [] as unknown[],
  states: [] as unknown[],
}));

const events: AlertEvent[] = [
  {
    id: 'provider.balance.low', subjectType: 'provider',
    params: [
      { name: 'metric', kind: 'choice', choices: ['remaining', 'usedPercent'], required: false },
      { name: 'value', kind: 'number', required: true },
      { name: 'plan', kind: 'text', required: false },
    ],
  },
  { id: 'account.auth.invalid', subjectType: 'account', params: [] },
];

vi.mock('../api/hooks', () => {
  const idle = { mutate: vi.fn(), isPending: false };
  return {
    keys: { providerAccounts: (id: string) => ['provider-accounts', id] },
    useWebhooks: () => ({ data: spies.webhooks, isLoading: false }),
    useAlertRules: () => ({ data: spies.rules, isLoading: false }),
    useAlertStates: () => ({ data: spies.states, isLoading: false }),
    useAlertEvents: () => ({ data: events }),
    useProviders: () => ({ data: [{ id: 'p1', name: 'DeepSeek', authScheme: 'bearer', enabled: true }] }),
    useTokens: () => ({ data: [] }),
    useCreateWebhook: () => ({ mutate: spies.createWebhook, isPending: false }),
    useCreateAlertRule: () => ({ mutate: spies.createRule, isPending: false }),
    useTestWebhook: () => ({ mutate: spies.test, isPending: false }),
    useUpdateWebhook: () => idle,
    useDeleteWebhook: () => idle,
    useResetWebhookSecret: () => idle,
    useWebhookDeliveries: () => ({ data: [], isLoading: false }),
    useRedeliver: () => idle,
    useUpdateAlertRule: () => idle,
    useDeleteAlertRule: () => idle,
  };
});

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

vi.mock('motion/react', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useReducedMotion: () => true,
}));

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

const hook: Webhook = {
  id: 'w1', name: 'Ops', url: 'https://hooks.test/a', format: 'generic', enabled: true, timeoutSec: 10, useProxy: true, hasSecret: false,
  headerNames: [], createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
};

const show = () => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <NotificationSettings />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  // Radix select needs pointer capture and scrolling, which jsdom lacks.
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
  Element.prototype.scrollIntoView = () => {};
  spies.createWebhook.mockReset();
  spies.createRule.mockReset();
  spies.test.mockReset();
  spies.webhooks = [];
  spies.rules = [];
  spies.states = [];
});
afterEach(cleanup);

// Radix' select trigger only opens on a mouse pointerdown; jsdom never sets pointerType.
const pick = async (name: string, option: string) => {
  fireEvent.pointerDown(screen.getByRole('combobox', { name }), { button: 0, ctrlKey: false, pointerType: 'mouse' });
  fireEvent.click(await screen.findByRole('option', { name: option }));
};

describe('notification settings', () => {
  it('lists webhooks, rules and firing alerts', () => {
    spies.webhooks = [hook];
    spies.rules = [{
      id: 'r1', name: 'Low balance', enabled: true, event: 'provider.balance.low', subjects: null, condition: { value: 5 },
      notifyResolved: false, repeatMinutes: null, cooldownMinutes: 60, webhookIds: ['w1'], firing: 1,
      createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
    } satisfies AlertRule];
    spies.states = [{
      ruleId: 'r1', ruleName: 'Low balance', event: 'provider.balance.low', subjectType: 'provider', subjectId: 'p1',
      subjectName: 'DeepSeek', state: 'firing', changedAt: '2026-10-13T00:00:00Z',
    }];
    show();
    expect(screen.getByText('https://hooks.test/a')).toBeInTheDocument();
    expect(screen.getByText(t('alerts.webhooks.unsigned'))).toBeInTheDocument();
    expect(screen.getByText(t('alerts.rules.firing', { count: 1 }))).toBeInTheDocument();
    expect(screen.getByText(`${t('alerts.event.provider.balance.low')} · DeepSeek`)).toBeInTheDocument();
    expect(screen.getByText(`${t('alerts.event.provider.balance.low')} → Ops`)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: t('alerts.webhooks.test') }));
    expect(spies.test).toHaveBeenCalledWith('w1', expect.anything());
  });

  it('creates a webhook with headers and shows the new secret once', async () => {
    spies.createWebhook.mockImplementation((_body, opts: { onSuccess: (r: unknown) => void }) =>
      opts.onSuccess({ webhook: hook, secret: 'whsec_abc' }));
    show();
    expect(screen.getByText(t('alerts.webhooks.empty'))).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: t('alerts.webhooks.add') }));
    fireEvent.change(await screen.findByLabelText(t('alerts.webhooks.name')), { target: { value: 'Ops' } });
    fireEvent.change(screen.getByLabelText(t('alerts.webhooks.url')), { target: { value: ' https://hooks.test/a ' } });
    fireEvent.click(screen.getByRole('button', { name: t('alerts.webhooks.addHeader') }));
    fireEvent.change(screen.getByRole('textbox', { name: t('alerts.webhooks.headerName') }), { target: { value: 'Authorization' } });
    fireEvent.change(screen.getByRole('textbox', { name: t('alerts.webhooks.headerValue') }), { target: { value: 'Bearer x' } });
    fireEvent.click(screen.getByRole('button', { name: t('common.save') }));

    expect(spies.createWebhook.mock.calls[0]![0]).toMatchObject({
      name: 'Ops', url: 'https://hooks.test/a', signed: true, headers: { Authorization: 'Bearer x' },
    });
    await waitFor(() => expect(screen.getByText('whsec_abc')).toBeInTheDocument());
  });

  it('renders the condition form from the event descriptor and saves the rule', async () => {
    spies.webhooks = [hook];
    show();
    fireEvent.click(screen.getByRole('button', { name: t('alerts.rules.add') }));
    fireEvent.change(await screen.findByLabelText(t('alerts.rules.name')), { target: { value: 'Low balance' } });
    // provider.balance.low (the first event): metric choice, threshold number, plan text.
    expect(screen.getByText(t('alerts.event.provider.balance.low.hint'))).toBeInTheDocument();
    fireEvent.change(screen.getByRole('spinbutton', { name: t('alerts.param.value') }), { target: { value: '5' } });
    expect(screen.getByRole('textbox', { name: t('alerts.param.plan') })).toHaveAttribute('placeholder', t('alerts.param.plan.placeholder'));
    fireEvent.click(screen.getByRole('button', { name: t('common.save') }));

    expect(spies.createRule.mock.calls[0]![0]).toMatchObject({
      name: 'Low balance', event: 'provider.balance.low', condition: { metric: 'remaining', value: 5 }, subjects: null,
      webhookIds: ['w1'], repeatMinutes: null, cooldownMinutes: 60,
    });
  });

  it('creates a Feishu bot webhook with its own signing secret', async () => {
    spies.createWebhook.mockImplementation((_body, opts: { onSuccess: (r: unknown) => void }) =>
      opts.onSuccess({ webhook: { ...hook, format: 'feishu', hasSecret: true }, secret: null }));
    show();
    fireEvent.click(screen.getByRole('button', { name: t('alerts.webhooks.add') }));
    fireEvent.change(await screen.findByLabelText(t('alerts.webhooks.name')), { target: { value: '飞书群' } });
    await pick(t('alerts.webhooks.format'), t('alerts.webhooks.format.feishu'));
    expect(screen.getByLabelText(t('alerts.webhooks.url'))).toHaveAttribute('placeholder', expect.stringContaining('open.feishu.cn'));
    fireEvent.change(screen.getByLabelText(t('alerts.webhooks.url')), { target: { value: 'https://open.feishu.cn/open-apis/bot/v2/hook/x' } });
    fireEvent.change(screen.getByLabelText(t('alerts.webhooks.botSecret')), { target: { value: ' fs-secret ' } });
    fireEvent.click(screen.getByRole('button', { name: t('common.save') }));

    const body = spies.createWebhook.mock.calls[0]![0];
    expect(body).toMatchObject({ format: 'feishu', secret: 'fs-secret' });
    expect(body).not.toHaveProperty('signed');
    // No generated secret to show for a bot format.
    await waitFor(() => expect(screen.queryByText(t('alerts.webhooks.secretShown'))).not.toBeInTheDocument());
  });
});
