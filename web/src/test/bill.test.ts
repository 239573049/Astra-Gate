import { describe, expect, it } from 'vitest';

import type { DailyBill } from '../api/types';
import { billFileName, shapeBill } from '../lib/bill';

// The same bill and expectations as tests/Astra.Desktop.Tests/Overview/DailyBillShapingTests.cs: the two clients must print
// identical text.
const bill: DailyBill = {
  date: '2026-11-11',
  tzOffsetMinutes: 480,
  fromUtc: '2026-11-10T16:00:00Z',
  toUtc: '2026-11-11T10:42:07Z',
  generatedAtUtc: '2026-11-11T10:42:07Z',
  costUsd: 32.4,
  requests: 878,
  successRequests: 872,
  inputTokens: 8_728_100,
  outputTokens: 648_220,
  providers: [
    {
      providerId: 'anthropic', name: 'Anthropic', costUsd: 29.45, requests: 540, inputTokens: 6_041_500, outputTokens: 372_520,
      models: [
        { model: 'claude-opus-5-5', costUsd: 18.42, requests: 42, inputTokens: 1_284_300, outputTokens: 96_420 },
        { model: 'claude-haiku-5-5', costUsd: 0.0123, requests: 312, inputTokens: 8_452, outputTokens: 61_300 },
      ],
    },
    {
      providerId: 'openai', name: 'OpenAI 中转', costUsd: 2.95, requests: 338, inputTokens: 2_686_600, outputTokens: 275_700,
      models: [{ model: 'gpt-5', costUsd: 2.95, requests: 338, inputTokens: 2_686_600, outputTokens: 275_700 }],
    },
  ],
};

describe('shapeBill', () => {
  it('prints the day, window and totals in the bill’s own offset', () => {
    const v = shapeBill(bill, 'zh');
    expect(v.number).toBe('AG-20261111');
    expect(v.weekday).toBe('周三');
    expect(v.dateShort).toBe('11.11');
    expect(v.window).toBe('00:00 – 18:42');
    expect(v.until).toBe('18:42');
    expect(v.generatedAt).toBe('2026-11-11 18:42');
    expect(v.total).toBe('$32.40');
    expect(v.requests).toBe('878');
    expect(v.successRate).toBe('99.3%');
    expect(v.inputTokens).toBe('8,728,100');
    expect(v.outputTokens).toBe('648,220');
    expect(v.tokensCompact).toBe('9.4M');
    expect(shapeBill(bill, 'en').weekday).toBe('Wed');
  });

  it('lists providers and models with compact token counts', () => {
    const [a, o] = shapeBill(bill, 'zh').providers;
    expect(a).toMatchObject({ id: 'anthropic', upper: 'ANTHROPIC', cost: '$29.45', requests: '540' });
    expect(a.models).toEqual([
      { name: 'claude-opus-5-5', cost: '$18.42', requests: '42', input: '1.3M', output: '96.4K' },
      { name: 'claude-haiku-5-5', cost: '$0.0123', requests: '312', input: '8,452', output: '61.3K' },
    ]);
    expect(o.upper).toBe('OPENAI 中转');
    expect(a.share).toBeCloseTo(29.45 / 32.4, 6);
  });

  it('names the leading provider with a whole percent', () => {
    expect(shapeBill(bill, 'zh').top).toEqual({ name: 'Anthropic', share: '91%' });
  });

  it('ends a finished day at 24:00 and shares by requests when nothing cost anything', () => {
    const free: DailyBill = {
      ...bill,
      toUtc: '2026-11-11T16:00:00Z',
      costUsd: 0,
      providers: bill.providers.map((p) => ({ ...p, costUsd: 0, models: p.models.map((m) => ({ ...m, costUsd: 0 })) })),
    };
    const v = shapeBill(free, 'zh');
    expect(v.window).toBe('00:00 – 24:00');
    expect(v.total).toBe('$0');
    expect(v.top).toEqual({ name: 'Anthropic', share: '62%' });
  });

  it('has no leader and a dash for the success rate on an empty day', () => {
    const v = shapeBill({ ...bill, costUsd: 0, requests: 0, successRequests: 0, inputTokens: 0, outputTokens: 0, providers: [] }, 'en');
    expect(v.providers).toEqual([]);
    expect(v.top).toBeNull();
    expect(v.successRate).toBe('—');
    expect(v.tokensCompact).toBe('0');
  });

  it('names the saved file after the day and the style', () => {
    expect(billFileName('2026-11-11', 'manga')).toBe('astra-bill-2026-11-11-manga.png');
  });
});
