import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { CacheUsageCell, TokenTypeLabel, TokenUsageSummary } from '../components/TokenUsage';
import { I18nProvider } from '../i18n';
import { cacheHitRatio, splitUsage, type UsageCounts } from '../lib/usage';

const usage: UsageCounts = {
  totalInputTokens: 120_000,
  totalOutputTokens: 3_000,
  cacheReadTokens: 90_000,
  cacheWriteTokens: 20_000,
  reasoningTokens: 1_000,
  usageSource: 'reported',
};

afterEach(() => {
  cleanup();
  localStorage.clear();
});

const show = (ui: React.ReactNode) => {
  localStorage.setItem('astra.locale', 'en');
  return render(<I18nProvider>{ui}</I18nProvider>);
};

describe('usage counts', () => {
  it('divides existing input/output totals, rather than counting cache/reasoning twice', () => {
    const u = splitUsage(usage);
    expect(u.uncachedInput + u.read + u.write).toBe(usage.totalInputTokens);
    expect(u.regularOutput + u.reasoning).toBe(usage.totalOutputTokens);
    expect(u.uncachedInput).toBe(10_000);
    expect(u.regularOutput).toBe(2_000);
    expect(u.hitRatio).toBe(0.75);
  });

  it('computes a token-weighted hit rate and handles empty/missing data', () => {
    expect(cacheHitRatio(6_000 + 90_000, 10_000 + 120_000)).toBeCloseTo(0.7384615);
    expect(cacheHitRatio(0, 100)).toBe(0);
    expect(cacheHitRatio(100, 0)).toBeNull();
    expect(cacheHitRatio(-1, 100)).toBeNull();
    expect(cacheHitRatio(NaN, 100)).toBeNull();
    expect(cacheHitRatio(200, 100)).toBe(1);
    expect(splitUsage({ ...usage, usageSource: 'missing' }).hitRatio).toBeNull();
  });
});

describe('token display', () => {
  it('shows cache hit amount/rate separately from cache writes in the list cell', () => {
    show(<CacheUsageCell usage={usage} />);
    expect(screen.getByText(/Hit 90K/)).toHaveTextContent('75.0%');
    expect(screen.getByText('Write 20K')).toBeInTheDocument();
    expect(screen.getByTitle('Cache hits 90,000 / total input 120,000 tokens')).toBeInTheDocument();
  });

  it('shows unknown (not 0%) when upstream usage is missing', () => {
    show(<CacheUsageCell usage={{ ...usage, usageSource: 'missing' }} />);
    expect(screen.getByTitle('No usage reported by upstream; the cache hit rate is unknown.')).toHaveTextContent('—');
    expect(screen.queryByText(/0%/)).not.toBeInTheDocument();
  });

  it('labels cache writes by lifetime while retaining the exact token type', () => {
    show(<><TokenTypeLabel type="cache_write_5m" /><TokenTypeLabel type="cache_write_1h" /></>);
    expect(screen.getByText('Cache write · 5 minutes')).toBeInTheDocument();
    expect(screen.getByText('Cache write · 1 hour')).toBeInTheDocument();
    expect(screen.getByText('cache_write_1h')).toBeInTheDocument();
  });

  it('explains input/output composition in the detail summary', () => {
    show(<TokenUsageSummary usage={usage} />);
    expect(screen.getByText('Uncached input')).toBeInTheDocument();
    expect(screen.getByText('10,000')).toBeInTheDocument();
    expect(screen.getByText('90,000')).toBeInTheDocument();
    expect(screen.getByText('20,000')).toBeInTheDocument();
    expect(screen.getByText('Non-reasoning output')).toBeInTheDocument();
    expect(screen.getByText('2,000')).toBeInTheDocument();
    expect(screen.getByText(/not added twice/)).toBeInTheDocument();
  });
});
