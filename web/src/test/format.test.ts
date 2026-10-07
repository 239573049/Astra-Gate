import { describe, expect, it } from 'vitest';

import { formatContext, formatNanos, formatPercent, formatTps, formatUnitPrice, formatUsd, formatUsdAxis, priceSummary } from '../lib/format';

describe('formatUsd', () => {
  it('uses 2 decimals from $1 and 3 significant digits below', () => {
    expect(formatUsd(4.66984508)).toBe('$4.67');
    expect(formatUsd(0.009)).toBe('$0.009');
    expect(formatUsd(0.01994)).toBe('$0.0199');
    expect(formatUsd(0.0000001)).toBe('$0.0000001');
    expect(formatUsd(0)).toBe('$0');
    expect(formatUsd(null)).toBe('—');
  });

  it('compacts large amounts on request', () => {
    expect(formatUsd(1234, { compact: true })).toBe('$1.2K');
    expect(formatUsd(1234)).toBe('$1,234.00');
  });

  it('formats axis ticks short', () => {
    expect(formatUsdAxis(0.0075)).toBe('$0.0075');
    expect(formatUsdAxis(0.3)).toBe('$0.3');
    expect(formatUsdAxis(0)).toBe('$0');
  });
});

describe('other formatters', () => {
  it('formats TPS with units and keeps zero distinct from missing or invalid values', () => {
    expect(formatTps(42.56)).toBe('42.6 tok/s');
    expect(formatTps(0)).toBe('0.0 tok/s');
    expect(formatTps(null)).toBe('—');
    expect(formatTps(undefined)).toBe('—');
    expect(formatTps(NaN)).toBe('—');
    expect(formatTps(Infinity)).toBe('—');
    expect(formatTps(-1)).toBe('—');
  });

  it('converts nano-USD', () => {
    expect(formatNanos(19_900_000)).toBe('$0.0199');
    expect(formatNanos(undefined)).toBe('—');
  });

  it('formats context windows, percentages and unit prices', () => {
    expect(formatContext(1_000_000)).toBe('1M');
    expect(formatContext(131_072)).toBe('131K');
    expect(formatPercent(0.909)).toBe('90.9%');
    expect(formatPercent(1)).toBe('100%');
    expect(formatUnitPrice('0.66')).toBe('$0.66');
    expect(formatUnitPrice(3)).toBe('$3');
    expect(priceSummary({ base: { input: 3, output: 15 } })).toBe('$3 / $15');
    expect(priceSummary(null)).toBe('—');
  });
});
