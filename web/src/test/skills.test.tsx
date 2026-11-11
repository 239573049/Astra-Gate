import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { Skill, SkillTarget } from '../api/types';
import { badgeLook, SkillBadges } from '../components/skills/SkillBadges';
import { useSkillActions } from '../components/skills/useSkillActions';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

const target = (kind: SkillTarget['kind'], over: Partial<SkillTarget> = {}): SkillTarget => ({
  kind, supported: true, detected: true, dir: `/h/.${kind}/skills`, readsShared: false, alsoReads: [], ...over,
});

const skill = (clients: Skill['clients']): Skill => ({
  name: 'pdf', description: 'd', sourceKind: 'local', updatedAt: '2026-11-11T00:00:00Z', clients, placements: [],
});

describe('badge look', () => {
  it('derives each badge from what the server says the client sees', () => {
    const t = target('claude-code');
    expect(badgeLook({ kind: 'claude-code', state: 'own', how: 'symlink', modified: false }, t)).toBe('on');
    expect(badgeLook({ kind: 'codex', state: 'via-shared', how: 'symlink', modified: false }, t)).toBe('shared');
    expect(badgeLook({ kind: 'opencode', state: 'via-claude-code', how: 'symlink', modified: false }, t)).toBe('locked');
    expect(badgeLook({ kind: 'claude-code', state: 'own', how: 'copy', modified: true }, t)).toBe('copy');
    expect(badgeLook({ kind: 'claude-code', state: 'off', how: null, modified: false }, t)).toBe('off');
    expect(badgeLook({ kind: 'claude-code', state: 'off', how: null, modified: false }, target('claude-code', { detected: false }))).toBe('disabled');
  });
});

function Badges({ s, targets }: { s: Skill; targets: SkillTarget[] }) {
  const actions = useSkillActions();
  return <SkillBadges skill={s} targets={targets} actions={actions} />;
}

function show(s: Skill, targets: SkillTarget[], respond: (url: string, body: unknown) => unknown) {
  localStorage.setItem('astra.locale', 'en');
  const calls: { method: string; url: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const body = init?.body ? JSON.parse(String(init.body)) : undefined;
      calls.push({ method: init?.method ?? 'GET', url: String(input), body });
      return new Response(JSON.stringify(respond(String(input), body)), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <I18nProvider>
        <FeedbackProvider>
          <Badges s={s} targets={targets} />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
  return { calls };
}

describe('skill badges', () => {
  it('switches a client on with one PUT', async () => {
    const { calls } = show(
      skill([{ kind: 'claude-code', state: 'off', how: null, modified: false }]),
      [target('claude-code')],
      () => ({ done: true, needsConfirm: false, affected: [] }),
    );
    fireEvent.click(await screen.findByRole('button', { name: /Claude Code/ }));
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true));
    const put = calls.find((c) => c.method === 'PUT')!;
    expect(put.url).toContain('/api/library/skills/pdf/clients/claude-code');
    expect(put.body).toEqual({ on: true, confirm: false });
  });

  it('asks before removing a link other clients share, and only then confirms', async () => {
    let n = 0;
    const { calls } = show(
      skill([{ kind: 'codex', state: 'via-shared', how: 'symlink', modified: false }]),
      [target('codex', { readsShared: true })],
      () => (n++ === 0 ? { done: false, needsConfirm: true, affected: ['gemini-cli', 'droid'] } : { done: true, needsConfirm: false, affected: [] }),
    );
    fireEvent.click(await screen.findByRole('button', { name: /Codex/ }));
    expect(await screen.findByText(/Switching off Codex affects other clients/)).toBeTruthy();
    expect(screen.getByText(/Gemini CLI、Droid/)).toBeTruthy();
    expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(1);

    fireEvent.click(screen.getByRole('button', { name: 'Switch off anyway' }));
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(2));
    expect(calls.filter((c) => c.method === 'PUT')[1]!.body).toEqual({ on: false, confirm: true });
  });

  it('does not switch a client that only sees the Skill through another client', async () => {
    const { calls } = show(
      skill([{ kind: 'opencode', state: 'via-claude-code', how: 'symlink', modified: false }]),
      [target('opencode', { readsShared: true, alsoReads: ['claude-code'] })],
      () => ({}),
    );
    fireEvent.click(await screen.findByRole('button', { name: /OpenCode/ }));
    expect(calls).toHaveLength(0);
  });
});
