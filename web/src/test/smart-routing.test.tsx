import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ApiError } from '../api/client';
import type { SmartRouting, SmartRoutingTestResult, SmartRoutingUpdate } from '../api/types';
import { SmartRoutingSettings } from '../components/SmartRoutingSettings';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';
import { formatTime } from '../lib/format';

const spies = vi.hoisted(() => ({
  update: vi.fn(),
  test: vi.fn(),
  data: undefined as unknown,
  models: [] as unknown[],
}));

vi.mock('../api/hooks', () => ({
  useSmartRouting: () => ({ data: spies.data }),
  useUpdateSmartRouting: () => ({ mutate: spies.update, isPending: false }),
  useTestSmartRouting: () => ({ mutate: spies.test, isPending: false }),
  useRoutingModels: () => ({ data: spies.models }),
}));

// The brand marks behind the icons module do not load in jsdom; only the client names are needed here.
vi.mock('../components/icons', () => ({ clientName: (kind: string) => (kind === 'tkcode' ? 'TKCode' : kind) }));

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

const status = (over: Partial<SmartRouting['status']> = {}): SmartRouting['status'] => ({
  breakerOpen: false,
  pins: 0,
  recent: { total: 0, outcomes: {} },
  unreachable: [],
  ...over,
});

const saved = (over: Partial<SmartRouting> = {}): SmartRouting => ({
  enabled: false,
  virtualModel: 'astra-auto',
  judge: { protocol: 'typesafe', effectiveBaseUrl: 'https://api.typesafe.ai', hasApiKey: false, model: 'jev-latest', timeoutMs: 2000 },
  grades: [
    { id: 'light', name: 'Light', description: 'Simple things.', model: 'small-model', providerId: 'p1' },
    { id: 'standard', name: 'Standard', description: 'Everyday things.', model: 'mid-model', providerId: 'p1' },
    { id: 'heavy', name: 'Heavy', description: 'Hard things.', model: 'big-model', providerId: 'p1' },
  ],
  rules: { mode: 'session', sessionTtlMinutes: 60, fallbackGrade: 'standard', minConfidence: 0.3, contextTurns: 2, maxChars: 6000 },
  status: status(),
  defaultInstructions: 'Which grade is the cheapest one that can complete the request?',
  protocols: [
    { id: 'typesafe', defaultBaseUrl: 'https://api.typesafe.ai', defaultModel: 'jev-latest' },
    { id: 'openai', defaultBaseUrl: 'https://api.openai.com', defaultModel: '' },
  ],
  ...over,
});

const withKey = () => saved({ judge: { ...saved().judge, hasApiKey: true, apiKeyMasked: 'sk-…1234' } });

const show = () => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <SmartRoutingSettings />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

const sent = (call = 0) => spies.update.mock.calls[call]![0] as SmartRoutingUpdate;
const saveButton = () => screen.getByRole('button', { name: t('common.save') });
const names = () => screen.getAllByLabelText(t('smartRouting.grade.name')).map((i) => (i as HTMLInputElement).value);
const pick = async (combobox: string, option: string) => {
  fireEvent.pointerDown(screen.getByRole('combobox', { name: combobox }), { button: 0, ctrlKey: false, pointerType: 'mouse' });
  fireEvent.click(await screen.findByRole('option', { name: option }));
};

beforeEach(() => {
  // Radix select needs pointer capture and scrolling, which jsdom lacks.
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
  Element.prototype.scrollIntoView = () => {};
  spies.update.mockReset();
  spies.test.mockReset();
  spies.models = [
    { id: 'small-model', name: 'Small', providerId: 'p1', providerName: 'Models provider' },
    { id: 'mid-model', name: 'Mid', providerId: 'p1', providerName: 'Models provider' },
    { id: 'big-model', name: 'Big', providerId: 'p1', providerName: 'Models provider' },
  ];
  spies.data = saved();
});
afterEach(cleanup);

describe('smart routing settings', () => {
  it('shows the three steps, per session by default, and the switch off', () => {
    show();

    expect(screen.getByText(t('smartRouting.judge.title'))).toBeInTheDocument();
    expect(screen.getByText(t('smartRouting.grades.title'))).toBeInTheDocument();
    expect(screen.getByText(t('smartRouting.rules.title'))).toBeInTheDocument();
    expect(screen.getByRole('switch', { name: t('smartRouting.enable') })).not.toBeChecked();
    expect(screen.getByText(t('smartRouting.state.off'))).toBeInTheDocument();
    expect(screen.getByRole('button', { name: t('smartRouting.rules.mode.session') })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: t('smartRouting.rules.mode.turn') })).toHaveAttribute('aria-pressed', 'false');
    expect(names()).toEqual(['Light', 'Standard', 'Heavy']);
    expect(saveButton()).toBeDisabled(); // nothing changed yet
  });

  it('shows that it is on, and the model to pick in the client', () => {
    spies.data = saved({ enabled: true, status: status({ pins: 3 }) });
    show();

    expect(screen.getByRole('switch', { name: t('smartRouting.enable') })).toBeChecked();
    expect(screen.getByText(`${t('smartRouting.state.on')} · ${t('smartRouting.state.pins', { count: 3 })}`)).toBeInTheDocument();
    expect(screen.getByText('astra-auto')).toBeInTheDocument();
  });

  it('says so when the judge is being left alone', () => {
    spies.data = saved({ enabled: true, status: status({ breakerOpen: true }) });
    show();
    expect(screen.getByText(t('smartRouting.state.breaker'))).toBeInTheDocument();
  });

  it('sums up the recent decisions and warns, with what to check, when most of them fell back', () => {
    const at = new Date().toISOString();
    spies.data = saved({
      enabled: true,
      status: status({ recent: { total: 10, outcomes: { judge: 1, timeout: 9 }, medianJudgeMs: 310, lastFallback: 'timeout', lastFallbackAt: at } }),
    });
    show();

    const summary = [
      t('smartRouting.recent.judged', { count: 1, total: 10 }),
      t('smartRouting.recent.fallback', { reason: t('smartRouting.failure.timeout'), count: 9 }),
      t('smartRouting.recent.median', { ms: 310 }),
    ].join(' · ');
    expect(screen.getByText(summary)).toBeInTheDocument();
    expect(screen.getByText(t('smartRouting.recent.failing', { grade: 'Standard' }))).toBeInTheDocument();
    expect(screen.getByText(t('smartRouting.recent.hint.timeout'))).toBeInTheDocument();
    expect(screen.getByText(t('smartRouting.recent.last', { reason: t('smartRouting.failure.timeout'), time: formatTime(at, 'en') }))).toBeInTheDocument();
  });

  it('does not warn while the judge mostly answers, and says when nothing was decided yet', () => {
    spies.data = saved({ enabled: true, status: status({ recent: { total: 10, outcomes: { judge: 9, timeout: 1 }, medianJudgeMs: 300 } }) });
    const { unmount } = show();
    expect(screen.queryByText(t('smartRouting.recent.failing', { grade: 'Standard' }))).not.toBeInTheDocument();
    unmount();

    spies.data = saved({ enabled: true });
    show();
    expect(screen.getByText(t('smartRouting.recent.none'))).toBeInTheDocument();
  });

  it('says which caller cannot use which grade, why, on which provider, and what to do about it', () => {
    const at = new Date().toISOString();
    spies.data = saved({
      enabled: true,
      status: status({
        unreachable: [
          { caller: 'tkcode', grade: 'light', model: 'small-model', usedGrade: 'standard', count: 3, lastAt: at, reason: 'provider_off', providerName: 'Elsewhere' },
        ],
      }),
    });
    show();

    expect(screen.getByText(t('smartRouting.unreachable.title'))).toBeInTheDocument();
    const where = t('smartRouting.unreachable.where', { provider: 'Elsewhere', model: 'small-model' });
    const item = t('smartRouting.unreachable.item', { caller: 'TKCode', grade: 'Light', where, reason: t('smartRouting.unusable.provider_off') });
    const used = t('smartRouting.unreachable.used', { used: 'Standard', count: 3, time: formatTime(at, 'en') });
    expect(screen.getByText(`${item} ${used}`)).toBeInTheDocument();
    expect(screen.getByText(t('smartRouting.unusable.provider_off.fix'))).toBeInTheDocument();
    expect(screen.queryByText(t('smartRouting.unreachable.hint'))).not.toBeInTheDocument(); // the reason is known: no list of guesses
  });

  it('falls back to the list of possible causes for a report without a reason (an older server)', () => {
    const at = new Date().toISOString();
    spies.data = saved({ enabled: true, status: status({ unreachable: [{ caller: 'tkcode', grade: 'light', model: 'small-model', usedGrade: 'standard', count: 1, lastAt: at }] }) });
    show();

    expect(screen.getByText(t('smartRouting.unreachable.hint'))).toBeInTheDocument();
    expect(screen.getByText(new RegExp(t('smartRouting.unusable.unknown').replace(/[()]/g, '\\$&')))).toBeInTheDocument();
  });

  it('switching on saves the whole page with enabled: true and no key that was never typed', () => {
    show();

    fireEvent.click(screen.getByRole('switch', { name: t('smartRouting.enable') }));

    expect(spies.update).toHaveBeenCalledTimes(1);
    const body = sent();
    expect(body.enabled).toBe(true);
    expect(body.grades?.map((g) => g.id)).toEqual(['light', 'standard', 'heavy']);
    expect(body.rules).toMatchObject({ mode: 'session', fallbackGrade: 'standard', minConfidence: 0.3, contextTurns: 2 });
    expect(body.judge).toMatchObject({ protocol: 'typesafe', model: 'jev-latest', timeoutMs: 2000 });
    expect(body.judge?.apiKey).toBeUndefined(); // the saved key stays where it is
  });

  it('lists every reason a save was refused in the UI language, and the server’s own words for a code it does not know', async () => {
    spies.update.mockImplementation((_body, opts) =>
      opts.onError(
        new ApiError('the judge needs an API key', 400, {
          problems: [
            { code: 'judge_key_missing', message: 'the judge needs an API key', field: 'judge.apiKey' },
            { code: 'grade_model_missing', message: 'grade light needs a model', field: 'grades[0].model' },
            { code: 'something_new', message: 'Words from the server', field: null },
          ],
        }),
      ),
    );
    const scrolled = vi.fn();
    Element.prototype.scrollIntoView = scrolled;
    show();

    fireEvent.click(screen.getByRole('switch', { name: t('smartRouting.enable') }));

    expect(await screen.findByText(t('smartRouting.problem.judge_key_missing'))).toBeInTheDocument();
    expect(screen.getByText(t('smartRouting.problem.grade_model_missing', { grade: 'Light' }))).toBeInTheDocument();
    expect(screen.getByText('Words from the server')).toBeInTheDocument();
    // The Save button is far from the list of reasons: a refusal brings it into view.
    expect(scrolled).toHaveBeenCalled();
  });

  it('sends a typed key once, and the field is empty again after it was saved', () => {
    spies.update.mockImplementation((_body, opts) => opts.onSuccess(withKey()));
    show();
    const key = screen.getByLabelText(t('smartRouting.judge.apiKey'));

    fireEvent.change(key, { target: { value: 'sk-or-v1-abcdefghijklmnop' } });
    expect(saveButton()).toBeEnabled();
    fireEvent.click(saveButton());

    expect(sent().judge?.apiKey).toBe('sk-or-v1-abcdefghijklmnop');
    expect(key).toHaveValue('');
  });

  it('can discard what was changed', () => {
    show();
    fireEvent.change(screen.getByLabelText(t('smartRouting.judge.model')), { target: { value: 'jev-1.14.0' } });
    expect(saveButton()).toBeEnabled();

    fireEvent.click(screen.getByRole('button', { name: t('smartRouting.discard') }));

    expect(screen.getByLabelText(t('smartRouting.judge.model'))).toHaveValue('jev-latest');
    expect(saveButton()).toBeDisabled();
  });

  it('starts another protocol from its own model and from no key, and says the OpenAI one is a draft', () => {
    spies.data = withKey();
    show();

    fireEvent.click(screen.getByRole('button', { name: t('smartRouting.judge.protocol.openai') }));

    expect(screen.getByText(t('smartRouting.judge.openaiDraft'))).toBeInTheDocument();
    expect(screen.getByLabelText(t('smartRouting.judge.model'))).toHaveValue('');
    expect(screen.queryByText(t('smartRouting.judge.nonEnglish'))).toBeNull(); // TypeSafe's own caveat
    fireEvent.click(saveButton());
    expect(sent().judge).toMatchObject({ protocol: 'openai', baseUrl: '', model: '' });
  });

  it('fills in the address and model of a hosted service, and warns that the saved key will not go there', async () => {
    spies.data = withKey();
    show();

    await pick(t('smartRouting.judge.service'), t('smartRouting.preset.openrouter'));

    expect(screen.getByLabelText(t('smartRouting.judge.model'))).toHaveValue('~typesafe/jev-latest');
    expect(screen.getByText(t('smartRouting.judge.keyDropped'))).toBeInTheDocument();
    // The key row no longer claims a key is saved: the saved one is about to go.
    expect(screen.getByText(t('smartRouting.judge.keyReplace'))).toBeInTheDocument();
    expect(screen.queryByText(t('smartRouting.judge.keySaved', { mask: 'sk-…1234' }))).toBeNull();
    fireEvent.click(saveButton());
    expect(sent().judge).toMatchObject({ baseUrl: 'https://openrouter.ai/api', model: '~typesafe/jev-latest' });
  });

  it('asks for a custom address only when the service is custom', async () => {
    show();
    expect(screen.queryByLabelText(t('smartRouting.judge.baseUrl'))).toBeNull();

    await pick(t('smartRouting.judge.service'), t('smartRouting.preset.custom'));

    expect(screen.getByLabelText(t('smartRouting.judge.baseUrl'))).toHaveValue('https://');
  });

  it('orders, removes and adds grades, keeps at least two and at most six, and the fallback follows', () => {
    show();
    expect(screen.getAllByText(t('smartRouting.grade.fallbackBadge'))).toHaveLength(1);

    fireEvent.click(screen.getAllByRole('button', { name: t('smartRouting.grade.down') })[0]!);
    expect(names()).toEqual(['Standard', 'Light', 'Heavy']);
    expect(screen.getAllByRole('button', { name: t('smartRouting.grade.up') })[0]).toBeDisabled();
    expect(screen.getAllByRole('button', { name: t('smartRouting.grade.down') })[2]).toBeDisabled();

    // The fallback grade (Standard) goes away: another grade takes its place.
    fireEvent.click(screen.getAllByRole('button', { name: t('smartRouting.grade.remove') })[0]!);
    expect(names()).toEqual(['Light', 'Heavy']);
    expect(screen.getAllByText(t('smartRouting.grade.fallbackBadge'))).toHaveLength(1);
    for (const b of screen.getAllByRole('button', { name: t('smartRouting.grade.remove') })) expect(b).toBeDisabled();

    for (let i = 0; i < 4; i++) fireEvent.click(screen.getByRole('button', { name: t('smartRouting.grade.add') }));
    expect(names()).toHaveLength(6);
    expect(screen.getByRole('button', { name: t('smartRouting.grade.add') })).toBeDisabled();

    fireEvent.click(saveButton());
    expect(sent().grades?.map((g) => g.id)).toEqual(['light', 'heavy', 'grade-3', 'grade-4', 'grade-5', 'grade-6']);
    expect(sent().rules?.fallbackGrade).toBe('heavy');
  });

  it('chooses a grade’s model from the models providers serve, and keeps showing one that is no longer served', async () => {
    spies.data = saved({
      grades: [
        { id: 'light', name: 'Light', description: 'Simple things.', model: 'retired-model' },
        { id: 'heavy', name: 'Heavy', description: 'Hard things.', model: 'big-model', providerId: 'p1' },
      ],
      rules: { ...saved().rules, fallbackGrade: 'heavy' },
    });
    show();
    expect(screen.getAllByRole('combobox', { name: t('smartRouting.grade.model') })[0]).toHaveTextContent(t('smartRouting.grade.unavailable', { model: 'retired-model' }));

    fireEvent.pointerDown(screen.getAllByRole('combobox', { name: t('smartRouting.grade.model') })[0]!, { button: 0, ctrlKey: false, pointerType: 'mouse' });
    fireEvent.click(await screen.findByRole('option', { name: 'small-model' }));

    fireEvent.click(saveButton());
    expect(sent().grades?.[0]).toMatchObject({ model: 'small-model', providerId: 'p1' });
  });

  it('offers the same model once per provider and saves the provider that was picked', async () => {
    spies.models = [...spies.models, { id: 'small-model', name: 'Small', providerId: 'p2', providerName: 'Other provider' }];
    show();

    fireEvent.pointerDown(screen.getAllByRole('combobox', { name: t('smartRouting.grade.model') })[0]!, { button: 0, ctrlKey: false, pointerType: 'mouse' });
    expect(await screen.findByRole('option', { name: 'small-model · Models provider' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('option', { name: 'small-model · Other provider' }));

    fireEvent.click(saveButton());
    expect(sent().grades?.[0]).toMatchObject({ model: 'small-model', providerId: 'p2' });
  });

  it('points to Providers when no model can be chosen yet', () => {
    spies.models = [];
    show();
    expect(screen.getByText(t('smartRouting.grade.noModels'))).toBeInTheDocument();
  });

  it('judges every question instead of every session when asked to, and then has no session lifetime', () => {
    show();
    expect(screen.getByLabelText(t('smartRouting.rules.ttl'))).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: t('smartRouting.rules.mode.turn') }));

    expect(screen.queryByLabelText(t('smartRouting.rules.ttl'))).toBeNull();
    expect(screen.getByText(t('smartRouting.rules.mode.turn.hint'))).toBeInTheDocument();
    fireEvent.click(saveButton());
    expect(sent().rules?.mode).toBe('turn');
  });

  it('cannot be saved or switched on while a number is empty', () => {
    show();

    fireEvent.change(screen.getByLabelText(t('smartRouting.judge.timeout')), { target: { value: '' } });

    expect(screen.getByText(t('smartRouting.numberMissing'))).toBeInTheDocument();
    expect(saveButton()).toBeDisabled();
    expect(screen.getByRole('switch', { name: t('smartRouting.enable') })).toBeDisabled();
  });

  it('tells what is sent to the judge, with the numbers from the rules', () => {
    spies.data = saved({ rules: { ...saved().rules, contextTurns: 4, maxChars: 3000 } });
    show();
    expect(screen.getByText(t('smartRouting.privacy.body', { turns: 4, chars: 3000 }))).toBeInTheDocument();
  });

  describe('the tester', () => {
    const ask = (text = 'Redesign the billing module') => {
      fireEvent.change(screen.getByLabelText(t('smartRouting.test.title')), { target: { value: text } });
      fireEvent.click(screen.getByRole('button', { name: t('smartRouting.test.run') }));
    };

    it('needs a text, and judges it with what is on screen, saved or not', () => {
      show();
      expect(screen.getByRole('button', { name: t('smartRouting.test.run') })).toBeDisabled();
      fireEvent.change(screen.getByLabelText(t('smartRouting.judge.model')), { target: { value: 'jev-1.13.0' } });

      ask();

      expect(spies.test).toHaveBeenCalledTimes(1);
      const request = spies.test.mock.calls[0]![0];
      expect(request.text).toBe('Redesign the billing module');
      expect(request.judge).toMatchObject({ protocol: 'typesafe', model: 'jev-1.13.0' }); // the unsaved edit
      expect(request.grades).toHaveLength(3);
    });

    it('shows the grade, its model, how sure the judge was and what it cost', () => {
      const result: SmartRoutingTestResult = {
        ok: true, grade: 'heavy', gradeName: 'Heavy', model: 'big-model', confidence: 0.82, confident: true,
        probabilities: { heavy: 0.88 }, judgeModel: 'jev-1.13.0', latencyMs: 120, inputTokens: 300, outputTokens: 12, limitMs: 2000,
        newConnection: false,
      };
      spies.test.mockImplementation((_req, opts) => opts.onSuccess(result));
      show();

      ask();

      expect(screen.getByText(t('smartRouting.test.result', { grade: 'Heavy' }))).toBeInTheDocument();
      expect(screen.getByText(t('smartRouting.test.model', { model: 'big-model', providerId: 'p1' }))).toBeInTheDocument();
      expect(screen.getByText(/Confidence 82%/)).toBeInTheDocument();
      expect(screen.getByText(t('smartRouting.test.meta', { ms: 120, input: 300, output: 12, model: 'jev-1.13.0' }))).toBeInTheDocument();
    });

    it('says when the answer is too unsure for a request to use that grade', () => {
      spies.test.mockImplementation((_req, opts) =>
        opts.onSuccess({ ok: true, grade: 'light', gradeName: 'Light', model: null, confidence: 0.1, confident: false, latencyMs: 5, inputTokens: 1, outputTokens: 1 }),
      );
      show();

      ask('hmm');

      expect(screen.getByText(/Below the threshold/)).toBeInTheDocument();
      expect(screen.getByText(t('smartRouting.test.noModel'))).toBeInTheDocument();
    });

    it('says when the answer came later than a request would have waited for it', () => {
      spies.test.mockImplementation((_req, opts) =>
        opts.onSuccess({
          ok: true, grade: 'light', gradeName: 'Light', model: 'small-model', confidence: 1, confident: true, latencyMs: 2600, inputTokens: 1,
          outputTokens: 1, limitMs: 2000, newConnection: false,
        }),
      );
      show();

      ask('1+1');

      expect(screen.getByText(t('smartRouting.test.slow', { ms: 2600, limit: 2000 }))).toBeInTheDocument();
    });

    it('says a timeout happened while connecting, and that a second try is the fair one', () => {
      spies.test.mockImplementation((_req, opts) =>
        opts.onSuccess({
          ok: false, failure: 'timeout', message: 'no answer within 8000 ms (including opening the connection)', latencyMs: 8000, inputTokens: 0,
          outputTokens: 0, limitMs: 5000, newConnection: true,
        }),
      );
      show();

      ask('1+1');

      expect(screen.getByText(t('smartRouting.test.failed', { reason: t('smartRouting.failure.timeout') }))).toBeInTheDocument();
      expect(screen.getByText(t('smartRouting.test.connecting'))).toBeInTheDocument();
    });

    it('shows why the judge failed', () => {
      spies.test.mockImplementation((_req, opts) =>
        opts.onSuccess({ ok: false, failure: 'auth', message: 'the judge answered 401: invalid key', latencyMs: 40, inputTokens: 0, outputTokens: 0 }),
      );
      show();

      ask();

      expect(screen.getByText(t('smartRouting.test.failed', { reason: t('smartRouting.failure.auth') }))).toBeInTheDocument();
      expect(screen.getByText('the judge answered 401: invalid key')).toBeInTheDocument();
    });

    it('lists the problems of a refused test in the UI language', () => {
      spies.test.mockImplementation((_req, opts) =>
        opts.onError(new ApiError('the judge needs an API key', 400, { problems: [{ code: 'judge_key_missing', message: 'the judge needs an API key', field: 'judge.apiKey' }] })),
      );
      show();

      ask();

      expect(screen.getByText(t('smartRouting.problem.judge_key_missing'))).toBeInTheDocument();
    });
  });
});
