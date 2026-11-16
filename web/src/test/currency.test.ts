import { afterEach, describe, expect, it } from 'vitest';

import { fillTone, formatFill, widthPercent, windowSegments } from '../lib/context';
import { formatMoney, formatMoneyAxis, parseRate, resolveCurrency, setDisplayCurrency, toUsd, usdToInputValue } from '../lib/currency';
import { formatNanos, formatPlainUsd, formatUsd } from '../lib/format';
import { formatCostShort } from '../lib/tray';

const RATES = { CNY: '7.2', JPY: '150' };

afterEach(() => setDisplayCurrency(resolveCurrency('USD', undefined)));

describe('display currency', () => {
  it('stays USD without a rate or for an unknown code', () => {
    expect(resolveCurrency('EUR', RATES).code).toBe('USD');
    expect(resolveCurrency('XYZ', RATES).code).toBe('USD');
    expect(resolveCurrency('CNY', undefined).code).toBe('USD');
    expect(resolveCurrency('cny', RATES).code).toBe('CNY');
  });

  it('accepts only plain positive decimals as rates', () => {
    expect(parseRate('7.2')).toBe(7.2);
    for (const bad of ['0', '-1', 'abc', '', '1e3', null, undefined]) expect(parseRate(bad as string)).toBeNull();
  });

  it('formats in USD exactly as before by default', () => {
    expect(formatUsd(12.345)).toBe('$12.35');
    expect(formatUsd(0.0213)).toBe('$0.0213');
    expect(formatUsd(0)).toBe('$0');
    expect(formatUsd(null)).toBe('—');
    expect(formatNanos(2_500_000_000)).toBe('$2.50');
  });

  it('converts and formats with the currency\'s symbol and decimals', () => {
    setDisplayCurrency(resolveCurrency('CNY', RATES));
    expect(formatUsd(10)).toBe('¥72.00');
    expect(formatNanos(1_000_000)).toBe('¥0.0072');
    expect(formatMoneyAxis(1000)).toBe('¥7.2K');
    expect(formatCostShort(0.001)).toBe('¥<0.01');
    expect(formatCostShort(1)).toBe('¥7.20');
    setDisplayCurrency(resolveCurrency('JPY', RATES));
    expect(formatMoney(10)).toBe('JP¥1,500'); // yen has no decimals
    expect(formatCostShort(1)).toBe('JP¥150');
  });

  it('turns typed amounts back into USD and shows a plain dollar equivalent', () => {
    setDisplayCurrency(resolveCurrency('CNY', RATES));
    expect(toUsd(72)).toBe(10);
    expect(usdToInputValue(10)).toBe(72);
    expect(formatPlainUsd(10)).toBe('$10.00');
  });
});

describe('context window fill', () => {
  it('formats and tones the fill', () => {
    expect(formatFill(0.5)).toBe('50%');
    expect(formatFill(0.0123)).toBe('1.2%');
    expect(formatFill(0.0004)).toBe('<0.1%');
    expect(formatFill(null)).toBe('—');
    expect(fillTone(0.5)).toBe('ok');
    expect(fillTone(0.75)).toBe('warn');
    expect(fillTone(0.95)).toBe('danger');
  });

  it('splits the window bar into cached, fresh and output and caps overflow', () => {
    const segments = windowSegments({ promptTokens: 50_000, window: 200_000, fill: 0.25, cacheReadTokens: 40_000, outputTokens: 800 });
    expect(segments).toEqual([
      { key: 'cacheRead', tokens: 40_000 },
      { key: 'fresh', tokens: 10_000 },
      { key: 'output', tokens: 800 },
    ]);
    expect(windowSegments({ promptTokens: 1, window: null, cacheReadTokens: 0, outputTokens: 0 })).toEqual([]);
    expect(widthPercent(300_000, 200_000)).toBe(100);
  });
});
