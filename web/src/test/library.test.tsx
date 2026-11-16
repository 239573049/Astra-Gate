import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ResourceItem, ResourceLibrary, SkillLibrary, SkillMarketResult } from '../api/types';
import { ProjectsPanel } from '../components/library/ProjectsPanel';
import { ResourcePanel } from '../components/library/ResourcePanel';
import { SkillMarketTab } from '../components/skills/SkillMarketTab';
import { SkillRouterPanel } from '../components/skills/SkillRouterPanel';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

const item = (over: Partial<ResourceItem> = {}): ResourceItem => ({
  id: 'i1', kind: 'instruction', name: 'style', description: '', body: 'Use tabs.', timeout: 0, enabled: true, updatedAt: '2026-11-16T00:00:00Z',
  targets: [{ client: 'claude-code', project: '', state: 'applied', path: '/h/.claude/CLAUDE.md' }], ...over,
});

const library = (items: ResourceItem[], over: Partial<ResourceLibrary> = {}): ResourceLibrary => ({
  kind: 'instruction',
  clients: [
    { kind: 'claude-code', detected: true, path: '/h/.claude/CLAUDE.md', project: true },
    { kind: 'codex', detected: false, path: '/h/.codex/AGENTS.md', project: true },
  ],
  items,
  projects: [{ path: '/work/app', name: 'app', exists: true }],
  canWrite: true,
  ...over,
});

function show(ui: React.ReactNode, respond: (method: string, url: string, body: unknown) => unknown) {
  localStorage.setItem('astra.locale', 'en');
  const calls: { method: string; url: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const body = init?.body ? JSON.parse(String(init.body)) : undefined;
      const method = init?.method ?? 'GET';
      calls.push({ method, url: String(input), body });
      return new Response(JSON.stringify(respond(method, String(input), body)), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <I18nProvider>
        <FeedbackProvider>{ui}</FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
  return { calls };
}

describe('resource panel', () => {
  it('lists an item with the state of each target and writes it again on request', async () => {
    const drifted = item({ targets: [{ client: 'claude-code', project: '', state: 'drifted' }, { client: 'codex', project: '/work/app', state: 'project-missing' }] });
    const { calls } = show(<ResourcePanel segment="instructions" />, (method, url) => {
      if (method === 'POST' && url.includes('/apply')) return drifted;
      return library([drifted]);
    });
    expect(await screen.findByText('style')).toBeTruthy();
    expect(screen.getByText('Changed')).toBeTruthy();
    expect(screen.getByText('Project is gone')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Write again' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url.endsWith('/api/library/instructions/i1/apply'))).toBe(true));
    // the plain rewrite never overwrites what the user changed
    expect(calls.find((c) => c.url.includes('/apply'))!.url).not.toContain('force');
  });

  it('asks before overwriting what the user changed, then forces', async () => {
    const drifted = item({ targets: [{ client: 'claude-code', project: '', state: 'drifted' }] });
    const { calls } = show(<ResourcePanel segment="instructions" />, (method) => (method === 'POST' ? drifted : library([drifted])));
    fireEvent.click(await screen.findByRole('button', { name: 'Overwrite my changes' }));
    expect(await screen.findByText(/Overwrite what was changed in style/)).toBeTruthy();
    expect(calls.some((c) => c.url.includes('/apply'))).toBe(false);
    const confirmButtons = screen.getAllByRole('button', { name: 'Overwrite my changes' });
    fireEvent.click(confirmButtons[confirmButtons.length - 1]!);
    await waitFor(() => expect(calls.some((c) => c.url.endsWith('/apply?force=true'))).toBe(true));
  });

  it('creates an instruction with the chosen user-wide and project targets', async () => {
    const { calls } = show(<ResourcePanel segment="instructions" />, (method) => (method === 'POST' ? item() : library([])));
    fireEvent.click((await screen.findAllByRole('button', { name: 'Add' }))[0]!);
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getAllByRole('textbox')[0]!, { target: { value: 'rules' } });
    fireEvent.change(within(dialog).getAllByRole('textbox')[1]!, { target: { value: 'Always test.' } });
    const boxes = within(dialog).getAllByRole('checkbox');
    // Claude Code: user-wide, then the project "app"
    fireEvent.click(boxes[0]!);
    fireEvent.click(boxes[1]!);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url === '/api/library/instructions')).toBe(true));
    const post = calls.find((c) => c.method === 'POST')!;
    expect(post.body).toMatchObject({ name: 'rules', body: 'Always test.', enabled: true });
    expect((post.body as { targets: unknown[] }).targets).toEqual([{ client: 'claude-code', project: '' }, { client: 'claude-code', project: '/work/app' }]);
  });

  it('only looks when writing is local-only', async () => {
    show(<ResourcePanel segment="hooks" />, () => library([], { canWrite: false, kind: 'hook' }));
    expect(await screen.findByText(/only possible on this machine/)).toBeTruthy();
    expect((screen.getAllByRole('button', { name: 'Add' })[0] as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('projects panel', () => {
  it('adds a project by path and shows a deleted one as gone', async () => {
    const { calls } = show(<ProjectsPanel />, (method) =>
      method === 'POST' ? { path: '/work/new', name: 'new', exists: true } : [{ path: '/work/app', name: 'app', exists: false }],
    );
    expect(await screen.findByText('Project is gone')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Project directory'), { target: { value: '/work/new' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add project' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url === '/api/library/projects')).toBe(true));
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({ path: '/work/new' });
  });
});

const skills: SkillLibrary = {
  targets: [{ kind: 'claude-code', supported: true, detected: true, dir: '/h/.claude/skills', readsShared: false, alsoReads: [] }],
  skills: [], found: [], libraryDir: '/h/.astra/library/skills', sharedDir: '/h/.agents/skills', canCreate: true, projectDirs: [],
};

describe('skills market', () => {
  it('searches the registry and groups the results by repository, then installs one', async () => {
    const result: SkillMarketResult = {
      registry: 'https://www.skills.sh', error: null,
      skills: [
        { source: 'acme/skills', skillId: 'pdf', name: 'PDF', installs: 42, installed: false },
        { source: 'acme/skills', skillId: 'docx', name: 'DOCX', installs: 7, installed: true },
        { source: 'other/pack', skillId: 'xlsx', name: 'XLSX', installs: 0, installed: false },
      ],
    };
    const { calls } = show(<SkillMarketTab library={skills} />, (method, url) => {
      if (url.includes('/market/source')) return { url: 'https://www.skills.sh', default: 'https://www.skills.sh' };
      if (method === 'POST') return [{ name: 'pdf', ok: true }];
      return result;
    });
    fireEvent.change(screen.getByPlaceholderText('Search Skills (2+ characters)'), { target: { value: 'pdf' } });
    expect(await screen.findByText('acme/skills')).toBeTruthy();
    expect(screen.getByText('other/pack')).toBeTruthy();
    expect(screen.getByText('Installed')).toBeTruthy();
    expect(screen.getAllByText(/third-party registry/).length).toBeGreaterThan(0);

    fireEvent.click(screen.getAllByRole('button', { name: 'Install' })[0]!);
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url.endsWith('/market/install'))).toBe(true));
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({ source: 'acme/skills', skillId: 'pdf', clients: [] });
  });
});

describe('skill router', () => {
  it('switches a client on and explains why a shared reader cannot have it', async () => {
    const state = [
      { kind: 'claude-code', supported: true, enabled: false, listed: 0 },
      { kind: 'codex', supported: false, enabled: false, why: 'shared', listed: 0 },
    ];
    const { calls } = show(<SkillRouterPanel canChange />, (method) => (method === 'PUT' ? state : state));
    expect(await screen.findByText('Skill router')).toBeTruthy();
    expect(screen.getByText(/reads the shared Skills directory/)).toBeTruthy();
    fireEvent.click(screen.getByRole('switch', { name: 'Claude Code' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT' && c.url.endsWith('/router/claude-code'))).toBe(true));
    expect(calls.find((c) => c.method === 'PUT')!.body).toEqual({ on: true });
  });
});
