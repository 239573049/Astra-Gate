import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ProviderAccount, StatsSummary, TimeseriesPoint } from '../api/types';
import { TrayPanelView } from '../components/tray/TrayPanelView';
import { DEFAULT_TRAY_PREFS, withSection } from '../components/tray/useTrayBridge';
import type { TrayData } from '../components/tray/useTrayData';
import { I18nProvider } from '../i18n';
import {
  changeRatio,
  failedRequests,
  formatCostShort,
  hourlyRequests,
  quotaPlans,
  quotaTone,
  resetsIn,
  tightestQuota,
  tightestRows,
  topModelShare,
  trayTitle,
  updatedAgo,
  windowLength,
  yesterdayTokens,
  type TrayTitleLabels,
} from '../lib/tray';
import type { TrayPanelState } from '../shell/bridge';

// @lobehub/icons pulls JSON that vitest's Node ESM rejects (see icons.test.tsx); the panel only needs the glyphs.
vi.mock('../components/icons', () => ({
  CLIENT_META: {},
  ClientGlyph: () => null,
  ProviderIcon: () => null,
}));

const summary = (over: Partial<StatsSummary> = {}): StatsSummary => ({
  range: 'today',
  costUsd: 0.004,
  requests: 7,
  successRate: 6 / 7,
  inputTokens: 60_000,
  outputTokens: 8_800,
  cacheReadTokens: 30_000,
  cacheWriteTokens: 0,
  reasoningTokens: 0,
  avgTtftMs: null,
  ...over,
});

const account = (over: Partial<ProviderAccount> = {}): ProviderAccount => ({
  id: 'acc-1',
  providerId: 'p1',
  displayName: 'me@example.com',
  status: 'active',
  createdAt: '2026-10-01T00:00:00Z',
  enabled: true,
  isCurrent: false,
  sortOrder: 0,
  quota: {
    session: { usedPercent: 1, windowMinutes: 300, resetsAtUtc: '2026-10-08T14:00:00Z' },
    weekly: { usedPercent: 36, windowMinutes: 10_080, resetsAtUtc: '2026-10-12T00:00:00Z' },
  },
  ...over,
});

const glm = { id: 'p1', name: 'GLM Coding Plan', icon: 'zhipu' };

const labels: TrayTitleLabels = {
  requests: (c) => `${c} calls`,
  tokens: (c) => `${c} tokens`,
  cost: (a) => `${a} cost`,
  quota: (plan, window, remaining) => `${plan} ${window} ${remaining}`,
  failures: (n) => `${n} failed`,
  stopped: 'stopped',
  windowName: (row) => row.kind,
};

describe('tray helpers', () => {
  it('derives failures and short costs', () => {
    expect(failedRequests(summary())).toBe(1);
    expect(failedRequests(summary({ requests: 0, successRate: 0 }))).toBe(0);
    expect(formatCostShort(0)).toBe('$0');
    expect(formatCostShort(0.004)).toBe('$<0.01');
    expect(formatCostShort(0.371)).toBe('$0.37');
    expect(formatCostShort(12.4)).toBe('$12.40');
  });

  it('lays today out as 24 hourly buckets and finds yesterday', () => {
    const now = new Date(2026, 9, 8, 15, 30);
    const points: TimeseriesPoint[] = [
      { bucket: '2026-10-08T09:00', key: 'a', costUsd: 0, requests: 3, tokens: 10 },
      { bucket: '2026-10-08T09:00', key: 'b', costUsd: 0, requests: 2, tokens: 10 },
    ];
    const bars = hourlyRequests(points, now);
    expect(bars).toHaveLength(24);
    expect(bars[9]).toEqual({ bucket: '2026-10-08T09:00', value: 5 });
    const daily: TimeseriesPoint[] = [
      { bucket: '2026-10-07', key: 'a', costUsd: 0, requests: 1, tokens: 400 },
      { bucket: '2026-10-07', key: 'b', costUsd: 0, requests: 1, tokens: 100 },
      { bucket: '2026-10-06', key: 'a', costUsd: 0, requests: 1, tokens: 900 },
    ];
    expect(yesterdayTokens(daily, now)).toBe(500);
    expect(changeRatio(750, 500)).toBe(0.5);
    expect(changeRatio(10, 0)).toBeNull();
  });

  it('picks the top model by tokens', () => {
    const top = [
      { model: 'a', costUsd: 0, requests: 9, tokens: 10, inputTokens: 0, cacheReadTokens: 0 },
      { model: 'b', costUsd: 0, requests: 1, tokens: 30, inputTokens: 0, cacheReadTokens: 0 },
    ];
    expect(topModelShare(top, 40)).toEqual({ model: 'b', share: 0.75 });
    expect(topModelShare([], 0)).toBeNull();
  });

  it('turns subscription quotas into remaining-share rows', () => {
    const plans = quotaPlans([
      { provider: glm, account: account() },
      { provider: glm, account: account({ id: 'acc-2', status: 'expired' }) },
      { provider: glm, account: account({ id: 'acc-3', quota: null }) },
      { provider: { id: 'p2', name: 'Grok' }, account: account({ id: 'acc-4', providerId: 'p2', quota: { credits: { usedPercent: 95 } } }) },
    ]);
    expect(plans.map((p) => p.accountId)).toEqual(['acc-1', 'acc-4']);
    expect(plans[0]!.rows.map((r) => [r.kind, r.remaining])).toEqual([
      ['session', 99],
      ['weekly', 64],
    ]);
    expect(tightestQuota(plans)?.row).toMatchObject({ kind: 'credits', remaining: 5 });
    expect(tightestRows(plans, 2).map((r) => [r.row.kind, r.row.remaining])).toEqual([
      ['credits', 5],
      ['weekly', 64],
    ]);
    expect(quotaTone(64)).toBe('green');
    expect(quotaTone(20)).toBe('orange');
    expect(quotaTone(5)).toBe('red');
  });

  it('formats window lengths and relative times', () => {
    expect(windowLength(300, 'en')).toBe('5h');
    expect(windowLength(10_080, 'en')).toBe('7d');
    expect(windowLength(300, 'zh')).toBe('5小时');
    const now = Date.parse('2026-10-08T12:00:00Z');
    expect(resetsIn('2026-10-08T13:00:00Z', 'en', now)).toBe('in 1 hour');
    expect(resetsIn('2026-10-08T11:00:00Z', 'en', now)).toBeNull();
    expect(updatedAgo(now - 5_000, 'en', 'just now', now)).toBe('just now');
    expect(updatedAgo(now - 180_000, 'en', 'just now', now)).toBe('3 minutes ago');
    expect(updatedAgo(0, 'en', 'just now', now)).toBeNull();
  });

  it('computes the menu-bar title for every mode', () => {
    const plans = quotaPlans([{ provider: glm, account: account() }]);
    const input = { running: true, summary: summary(), plans };
    expect(trayTitle('none', input, labels)).toEqual({ title: '', detail: '' });
    expect(trayTitle('requests', input, labels)).toEqual({ title: '7', detail: '7 calls' });
    expect(trayTitle('tokens', input, labels)).toEqual({ title: '68.8K', detail: '68.8K tokens' });
    expect(trayTitle('cost', input, labels)).toEqual({ title: '$<0.01', detail: '$<0.01 cost' });
    expect(trayTitle('quota', input, labels)).toEqual({ title: '64%', detail: 'GLM Coding Plan weekly 64%' });
    expect(trayTitle('alerts', input, labels)).toEqual({ title: '⚠ 1', detail: '1 failed' });
    expect(trayTitle('alerts', { ...input, summary: summary({ successRate: 1 }) }, labels)).toEqual({ title: '', detail: '' });
    expect(trayTitle('alerts', { ...input, running: false }, labels)).toEqual({ title: '!', detail: 'stopped' });
    // No data yet / gateway down: nothing rather than a stale or zero number.
    expect(trayTitle('requests', { ...input, summary: undefined }, labels).title).toBe('');
    expect(trayTitle('quota', { ...input, plans: [] }, labels).title).toBe('');
  });
});

const data = (over: Partial<TrayData> = {}): TrayData => ({
  settings: { gatewayBaseUrl: 'http://localhost:18317' } as TrayData['settings'],
  summary: summary(),
  hourly: [],
  daily: [],
  top: [{ model: 'glm-5.3-flash', costUsd: 0, requests: 7, tokens: 68_800, inputTokens: 60_000, cacheReadTokens: 0 }],
  plans: quotaPlans([{ provider: glm, account: account() }]),
  hasSubscriptions: true,
  clients: [],
  providers: [],
  updatedAt: Date.now(),
  loading: false,
  error: false,
  ...over,
});

const running: TrayPanelState = { running: true, port: 18317, activity: null, apiVersionMismatch: false, updateAvailable: null };

function show(ui: React.ReactNode) {
  localStorage.setItem('astra.locale', 'en');
  return render(<I18nProvider>{ui}</I18nProvider>);
}

describe('TrayPanelView', () => {
  afterEach(cleanup);

  it('renders status, today, top model and quota windows', () => {
    show(<TrayPanelView prefs={DEFAULT_TRAY_PREFS} state={running} data={data()} onRefresh={() => {}} />);
    expect(screen.getByText('Gateway running')).toBeInTheDocument();
    expect(screen.getByText('localhost:18317')).toBeInTheDocument();
    expect(screen.getByText('1 failed · 14.3% of today')).toBeInTheDocument();
    expect(screen.getByText('68.8K')).toBeInTheDocument();
    expect(screen.getByText('$<0.01')).toBeInTheDocument();
    expect(screen.getByText('glm-5.3-flash')).toBeInTheDocument();
    // The overview lists the tightest windows, one line each.
    expect(screen.getAllByText('GLM Coding Plan')).toHaveLength(2);
    expect(screen.getByText('99%')).toBeInTheDocument();
    expect(screen.getByText('64%')).toBeInTheDocument();
  });

  it('hides switched-off sections', () => {
    let prefs = withSection(DEFAULT_TRAY_PREFS, 'cost', false);
    prefs = withSection(prefs, 'quota', false);
    show(<TrayPanelView prefs={prefs} state={running} data={data()} onRefresh={() => {}} />);
    expect(screen.queryByText('$<0.01')).not.toBeInTheDocument();
    expect(screen.queryByText('GLM Coding Plan')).not.toBeInTheDocument();
    expect(screen.getByText('68.8K')).toBeInTheDocument();
  });

  it('switches between tabs and only offers the ones with content', () => {
    const prefs = withSection(DEFAULT_TRAY_PREFS, 'clients', true);
    const clients = [{ kind: 'codex', name: 'Codex', enabled: true, providerId: null }] as unknown as TrayData['clients'];
    show(<TrayPanelView prefs={prefs} state={running} data={data({ clients })} onRefresh={() => {}} />);
    expect(screen.getAllByRole('tab').map((el) => el.textContent)).toEqual(['Overview', 'Quota', 'Clients']);
    expect(screen.queryByText('Quota & balance')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'Quota' }));
    expect(screen.getByText('Quota & balance')).toBeInTheDocument();
    expect(screen.queryByText('68.8K')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'Clients' }));
    expect(screen.getByText('Codex')).toBeInTheDocument();
    cleanup();

    // Nothing but the overview to show: no tab bar.
    show(<TrayPanelView prefs={DEFAULT_TRAY_PREFS} state={running} data={data({ plans: [], hasSubscriptions: false })} onRefresh={() => {}} />);
    expect(screen.queryByRole('tab')).not.toBeInTheDocument();
  });

  it('warns about a nearly empty quota and links to the quota tab', () => {
    const low = quotaPlans([{ provider: glm, account: account({ quota: { weekly: { usedPercent: 92, windowMinutes: 10_080, resetsAtUtc: null } } }) }]);
    show(<TrayPanelView prefs={DEFAULT_TRAY_PREFS} state={running} data={data({ plans: low })} onRefresh={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: /only 8% left/ }));
    expect(screen.getByText('Quota & balance')).toBeInTheDocument();
  });

  it('forwards footer actions and offers start when stopped', () => {
    const onCommand = vi.fn();
    const { unmount } = show(<TrayPanelView prefs={DEFAULT_TRAY_PREFS} state={running} data={data()} onCommand={onCommand} onRefresh={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: 'Restart gateway' }));
    fireEvent.click(screen.getByRole('button', { name: /Open Astra/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Tray panel settings' }));
    expect(onCommand.mock.calls.map((c) => c[0])).toEqual(['restart', 'open', 'open-settings']);
    unmount();

    show(<TrayPanelView prefs={DEFAULT_TRAY_PREFS} state={{ ...running, running: false }} data={data()} onCommand={onCommand} onRefresh={() => {}} />);
    expect(screen.getByText('Gateway not running', { selector: 'span' })).toBeInTheDocument();
    expect(screen.queryByText('68.8K')).not.toBeInTheDocument();
    fireEvent.click(screen.getAllByRole('button', { name: 'Start service' })[0]!);
    expect(onCommand).toHaveBeenLastCalledWith('start');
  });
});
