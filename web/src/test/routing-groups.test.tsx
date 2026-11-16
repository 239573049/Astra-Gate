import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ApiError } from '../api/client';
import type { RoutingGroup, RoutingGroupStatus, RoutingModel } from '../api/types';
import { draftOf, draftValid, emptyDraft, memberKey, problemMessages, reasonText, RoutingGroups, slugFrom, writeOf } from '../components/RoutingGroups';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', NoopResizeObserver);
  // Radix' select probes pointer capture and scrolls the current item into view; jsdom implements neither.
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
  Element.prototype.scrollIntoView = () => {};
});

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

const group = (over: Partial<RoutingGroup> = {}): RoutingGroup => ({
  id: 'g1', slug: 'opus', modelId: 'group/opus', name: 'Opus anywhere', mode: 'smart', stays: 'auto', enabled: true, listed: true,
  members: [{ provider: 'p1', model: 'claude-opus', group: null, weight: 1 }, { provider: 'p2', model: 'opus-5', group: null, weight: 1 }],
  createdAt: '2026-11-16T00:00:00Z', updatedAt: '2026-11-16T00:00:00Z', ...over,
});

const models: RoutingModel[] = [
  { id: 'claude-opus', name: 'Claude Opus', providerId: 'p1', providerName: 'Claude' },
  { id: 'opus-5', name: 'Opus 5', providerId: 'p2', providerName: 'Copilot' },
  { id: 'haiku', name: 'Haiku', providerId: 'p1', providerName: 'Claude' },
];

const status = (): RoutingGroupStatus => ({
  id: 'g1', slug: 'opus', lastDecisionAt: '2026-11-16T10:00:00Z', lastReason: 'soonest_reset',
  members: [
    { key: 'p1/claude-opus', providerId: 'p1', providerName: 'Claude', model: 'claude-opus', weight: 1, health: 'healthy', usedPercent: 42, window: 'weekly', resetsAt: '2026-11-18T00:00:00Z', recent: 3, lastUsed: '2026-11-16T10:00:00Z' },
    { key: 'p2/opus-5', providerId: 'p2', providerName: 'Copilot', model: 'opus-5', weight: 1, health: 'cooling', coolingUntil: '2026-11-16T12:00:00Z', recent: 0, problem: 'model_missing' },
  ],
});

function show(groups: RoutingGroup[]) {
  localStorage.setItem('astra.locale', 'en');
  const calls: { method: string; url: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      calls.push({ method, url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
      const json = (value: unknown, code = 200) => new Response(JSON.stringify(value), { status: code, headers: { 'Content-Type': 'application/json' } });
      if (url.endsWith('/api/routing/groups/status')) return json([status()]);
      if (url.endsWith('/api/smart-routing/models')) return json(models);
      if (url.endsWith('/api/routing/groups') && method === 'GET') return json(groups);
      return json(group());
    }),
  );
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <I18nProvider>
        <FeedbackProvider>
          <RoutingGroups />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
  return { calls };
}

describe('routing group helpers', () => {
  it('makes a slug from a name the way the server does', () => {
    expect(slugFrom('Opus anywhere')).toBe('opus-anywhere');
    expect(slugFrom('  A -- B! ')).toBe('a-b');
    expect(slugFrom('！！')).toBe('');
    expect(slugFrom('x'.repeat(60))).toHaveLength(48);
  });

  it('keys members like the server', () => {
    expect(memberKey({ provider: 'p1', model: 'vendor/m' })).toBe('p1/vendor/m');
    expect(memberKey({ group: 'inner' })).toBe('group/inner');
  });

  it('needs a name, a valid slug and a member before it saves', () => {
    const d = { ...emptyDraft(), name: 'X', slug: 'x', members: [{ provider: 'p1', model: 'm', group: null, weight: 1 }] };
    expect(draftValid(d)).toBe(true);
    expect(draftValid({ ...d, name: ' ' })).toBe(false);
    expect(draftValid({ ...d, slug: 'Bad Slug' })).toBe(false);
    expect(draftValid({ ...d, members: [] })).toBe(false);
  });

  it('writes the slug only on create and clamps weights', () => {
    const d = { ...draftOf(group()), members: [{ provider: 'p1', model: 'm', group: null, weight: 5000 }, { provider: 'p2', model: 'n', group: null, weight: 0 }] };
    expect(writeOf(d, true).slug).toBe('opus');
    expect('slug' in writeOf(d, false)).toBe(false);
    expect(writeOf(d, true).members.map((m) => m.weight)).toEqual([1000, 1]);
  });

  it('turns a refusal into the console’s words and the reasons into text', () => {
    const t = ((key: string) => key) as never;
    const err = new ApiError('x', 400, { problems: [{ code: 'member_cycle', message: 'cycle' }, { code: 'brand_new', message: 'a new thing' }] });
    expect(problemMessages(err, t)).toEqual(['routingGroups.problem.member_cycle', 'a new thing']);
    expect(problemMessages(new Error('boom'), t)).toEqual(['boom']);
    expect(reasonText('soonest_reset', t)).toBe('routingGroups.reason.soonest_reset');
    expect(reasonText('mystery', t)).toBe('mystery');
    expect(reasonText(null, t)).toBe('—');
  });
});

describe('routing groups page', () => {
  it('lists a group with its model id, mode and every member’s live state', async () => {
    show([group()]);
    expect(await screen.findByText('Opus anywhere')).toBeTruthy();
    expect(screen.getByText('group/opus')).toBeTruthy();
    expect(screen.getByText('Smart')).toBeTruthy();
    expect(await screen.findByText('Claude · claude-opus')).toBeTruthy();
    expect(screen.getByText('Healthy')).toBeTruthy();
    expect(screen.getByText(/42% of quota used/)).toBeTruthy();
    expect(screen.getByText(/Cooling until/)).toBeTruthy();
    expect(screen.getByText('provider does not serve this model')).toBeTruthy();
    expect(screen.getByText(/Last choice: allowance renews soonest/)).toBeTruthy();
  });

  it('shows the empty state', async () => {
    show([]);
    expect(await screen.findByText('No routing groups yet')).toBeTruthy();
  });

  it('switches a group off from its card', async () => {
    const { calls } = show([group()]);
    fireEvent.click(await screen.findByRole('switch', { name: 'Enabled' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH' && c.url.endsWith('/api/routing/groups/g1'))).toBe(true));
    expect(calls.find((c) => c.method === 'PATCH')!.body).toEqual({ enabled: false });
  });

  it('creates a group: the slug follows the name and members come from the model list', async () => {
    const { calls } = show([]);
    fireEvent.click(await screen.findByRole('button', { name: 'New group' }));
    fireEvent.change(await screen.findByLabelText('Name'), { target: { value: 'Fast pair' } });
    expect((screen.getByLabelText('Model id suffix') as HTMLInputElement).value).toBe('fast-pair');

    fireEvent.pointerDown(screen.getByRole('combobox', { name: 'Add a model…' }), { button: 0, ctrlKey: false, pointerType: 'mouse' });
    fireEvent.click(await screen.findByRole('option', { name: 'Copilot · opus-5' }));
    expect(await screen.findByText('Copilot · opus-5')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    expect(calls.find((c) => c.method === 'POST')!.body).toMatchObject({
      name: 'Fast pair', slug: 'fast-pair', mode: 'smart', stays: 'auto', enabled: true, listed: true,
      members: [{ provider: 'p2', model: 'opus-5', group: null, weight: 1 }],
    });
  });
});
