import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { X } from 'lucide-react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { RequestQuery } from '../api/hooks';
import type { ClientInfo, Provider, RequestDetail, RequestPage, RequestSummary } from '../api/types';
import { FilterMenu, FilterToolbar, type FilterChip, type FilterField, type FilterMessages } from '../components/arc/filter-toolbar/filter-toolbar';
import { Button, Field, Input, Segmented } from '../components/ui/controls';
import { I18nProvider } from '../i18n';
import { RequestsPage } from '../pages/RequestsPage';

// ---------- service-boundary mocks (FilterToolbar / RequestsPage stay real) ----------

const hookSpies = vi.hoisted(() => ({
  useClients: vi.fn(),
  useProviders: vi.fn(),
  useRequests: vi.fn(),
  useRequest: vi.fn(),
  useRequestRate: vi.fn(),
}));

vi.mock('../api/hooks', () => ({
  keys: { stats: (...args: unknown[]) => ['stats', ...args] },
  useClients: hookSpies.useClients,
  useProviders: hookSpies.useProviders,
  useRequests: hookSpies.useRequests,
  useRequest: hookSpies.useRequest,
  useRequestRate: hookSpies.useRequestRate,
  useTokens: () => ({ data: [] }),
  useTunnel: () => ({ data: undefined }),
}));

// The live request feed opens an event stream; the list tests only cover stored rows.
vi.mock('../api/liveRequests', () => ({
  useLiveRequestFeed: () => {},
  useLiveRequestList: () => [],
  useLiveRequestsConnected: () => false,
  useLiveRequest: () => undefined,
}));

// Page reads the shell layout context that only the real AppShell provides.
vi.mock('../components/layout/AppShell', () => ({
  useShellLayout: () => ({
    sidebarMode: 'expanded',
    sidebarWidth: 224,
    drawerOpen: false,
    setDrawerOpen: () => {},
    toggleSidebar: () => {},
    narrow: false,
  }),
  TOOLBAR_RIGHT_RESERVE: 0,
}));

// Reduced motion keeps every transition at duration 0, so tests never race an animation.
vi.mock('motion/react', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useReducedMotion: () => true,
}));

// The real @lobehub/icons barrel transitively imports JSON with import attributes that Node ESM
// (vitest) rejects; icons.tsx only needs brand components for provider avatars and client glyphs. Same stub as icons.test.tsx.
vi.mock('@lobehub/icons', () => {
  const stub = (name: string) => {
    const mark = (variant: string) => (props: { size?: number }) => (
      <svg data-testid={`lobe-${name}`} data-variant={variant} data-size={props.size} />
    );
    return Object.assign(mark('mono'), { Avatar: mark('avatar'), Color: mark('color') });
  };
  return {
    Anthropic: stub('anthropic'),
    Claude: stub('claude'),
    ClaudeCode: stub('claude-code'),
    Codex: stub('codex'),
    DeepSeek: stub('deepseek'),
    Gemini: stub('gemini'),
    GeminiCLI: stub('gemini-cli'),
    GithubCopilot: stub('github-copilot'),
    Grok: stub('grok'),
    HermesAgent: stub('hermes-agent'),
    Kimi: stub('kimi'),
    LmStudio: stub('lm-studio'),
    Qoder: stub('qoder'),
    Meta: stub('meta'),
    Minimax: stub('minimax'),
    Moonshot: stub('moonshot'),
    Ollama: stub('ollama'),
    OpenAI: stub('openai'),
    OpenCode: stub('opencode'),
    OpenRouter: stub('openrouter'),
    Pi: stub('pi'),
    Qwen: stub('qwen'),
    SiliconCloud: stub('siliconcloud'),
    Volcengine: stub('volcengine'),
    XAI: stub('xai'),
    XiaomiMiMo: stub('xiaomi-mimo'),
    Baichuan: stub('baichuan'),
    ChatGLM: stub('chatglm'),
    Cohere: stub('cohere'),
    Doubao: stub('doubao'),
    Gemma: stub('gemma'),
    Hunyuan: stub('hunyuan'),
    LongCat: stub('longcat'),
    Mistral: stub('mistral'),
    Stepfun: stub('stepfun'),
    Wenxin: stub('wenxin'),
    Yi: stub('yi'),
    Zhipu: stub('zhipu'),
  };
});

// jsdom has no ResizeObserver; a noop observer never fires its callback, so nothing resizes recursively.
class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

// ---------- fixtures ----------

const summary = (overrides: Partial<RequestSummary> & Pick<RequestSummary, 'id'>): RequestSummary => ({
  startedAtUtc: '2026-01-15T10:00:00Z',
  clientKind: 'codex',
  providerId: 'prov-anthropic',
  providerName: 'Anthropic',
  inboundProtocol: 'openai-chat',
  passthrough: false,
  requestedModel: 'gpt-a',
  stream: false,
  status: 'success',
  totalInputTokens: 10,
  totalOutputTokens: 5,
  cacheReadTokens: 0,
  cacheWriteTokens: 0,
  reasoningTokens: 0,
  costNanoUsd: 1234,
  usageSource: 'reported',
  ...overrides,
});

const providerRow = (id: string, name: string): Provider =>
  ({ id, name, enabled: true } as Provider);

const pageOf = (items: RequestSummary[]): RequestPage => ({ items, total: items.length, page: 1, pageSize: 50, totalCostNanoUsd: items.reduce((sum, r) => sum + (r.costNanoUsd ?? 0), 0) });

const hookResult = (data: unknown) => ({ data, isLoading: false, isError: false, error: null });

const lastQuery = (): RequestQuery => hookSpies.useRequests.mock.calls.at(-1)![0] as RequestQuery;

// ---------- shared render helpers ----------

const showPage = () => {
  localStorage.setItem('astra.locale', 'en');
  render(
    <QueryClientProvider client={new QueryClient()}>
      <I18nProvider>
        <RequestsPage />
      </I18nProvider>
    </QueryClientProvider>,
  );
};

/** Opens a filter multi-select (keyboard, as Radix opens on Enter) and toggles the option rows named `optionText`. */
async function pickOption(group: HTMLElement, field: string, optionText: string, rowId?: string) {
  const trigger = within(group).getByRole('button', { name: new RegExp(`^${field}:`) });
  if (trigger.getAttribute('data-state') !== 'open') fireEvent.keyDown(trigger, { key: 'Enter' });
  const rows = await screen.findAllByRole('menuitemcheckbox', { name: optionText });
  const target = rowId ? rows.find((row) => row.getAttribute('data-row') === rowId) : rows[0];
  expect(target).toBeDefined();
  fireEvent.click(target!);
  return target!;
}

const waitDebounce = () => act(() => new Promise((resolve) => setTimeout(resolve, 300)));

// ---------- existing component tests (unchanged) ----------

describe('Button (Arc)', () => {
  it('icon-only buttons are square and keep their accessible name', () => {
    render(<Button variant="plain" size="sm" icon={<X />} aria-label="close" />);
    const button = screen.getByRole('button', { name: 'close' });
    expect(button.style.width).toBe('var(--control-height-sm)');
    expect(button.style.paddingInline).toBe('0px');
  });

  it('primary buttons use the system accent; text buttons keep Arc padding', () => {
    render(
      <>
        <Button variant="primary">Save</Button>
        <Button>Cancel</Button>
      </>,
    );
    expect(screen.getByRole('button', { name: 'Save' }).style.background).toBe('var(--accent)');
    const cancel = screen.getByRole('button', { name: 'Cancel' });
    expect(cancel.style.width).toBe('');
    expect(cancel).toHaveAttribute('type', 'button');
  });
});

describe('Segmented (Arc segmented control)', () => {
  it('marks the selected option', () => {
    render(
      <Segmented
        ariaLabel="range"
        value="7d"
        onChange={() => {}}
        items={[
          { value: 'today', label: 'Today' },
          { value: '7d', label: '7 days' },
        ]}
      />,
    );
    expect(screen.getByRole('group', { name: 'range' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '7 days' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Today' })).toHaveAttribute('aria-pressed', 'false');
  });
});

describe('Field + Input (Arc input)', () => {
  it('passes the field label, hint and error to the Arc input', () => {
    render(
      <Field label="Base URL" hint="Must start with https" error="Invalid URL">
        <Input value="x" onChange={() => {}} />
      </Field>,
    );
    const input = screen.getByLabelText('Base URL');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('alert')).toHaveTextContent('Invalid URL');
  });

  it('gives a standalone input a visually hidden label from aria-label', () => {
    const { container } = render(<Input aria-label="Header name" value="" onChange={() => {}} />);
    expect(screen.getByLabelText('Header name')).toBeInTheDocument();
    expect(container.querySelector('.astra-label-hidden')).not.toBeNull();
  });
});

// ---------- FilterMenu ----------

describe('FilterMenu (Arc add-filter menu)', () => {
  const sameNameFields: FilterField[] = [
    { id: 'provider', label: 'Provider', options: [{ value: 'prov-a', label: 'Anthropic' }, { value: 'prov-b', label: 'Anthropic' }] },
  ];

  it('reports the raw option id (not the shared label) plus valueLabel, and marks active by id only', async () => {
    const onSelect = vi.fn();
    const active: FilterChip[] = [{ id: 'provider', label: 'Provider', value: 'prov-b', valueLabel: 'Anthropic' }];
    render(<FilterMenu fields={sameNameFields} onSelect={onSelect} active={active} />);

    fireEvent.click(screen.getByRole('button', { name: 'Add filter' }), { detail: 1 });
    // The active chip's value also shows in the field row meta, so match the name by prefix.
    fireEvent.click((await screen.findAllByRole('menuitem', { name: /Provider/ }))[0]!, { detail: 1 });
    const rows = await screen.findAllByRole('menuitemradio', { name: 'Anthropic' });
    expect(rows).toHaveLength(2);
    // Same label, but only the row whose value matches the active chip id is checked.
    expect(rows[0]).toHaveAttribute('aria-checked', 'false');
    expect(rows[1]).toHaveAttribute('aria-checked', 'true');

    fireEvent.click(rows[1]!, { detail: 1 });
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith({ id: 'provider', label: 'Provider', value: 'prov-b', valueLabel: 'Anthropic' }, sameNameFields[0]);
  });

  it('localizes the back button through messages.back', async () => {
    render(<FilterMenu fields={sameNameFields} onSelect={() => {}} messages={{ back: '返回筛选字段' }} />);
    fireEvent.click(screen.getByRole('button', { name: 'Add filter' }), { detail: 1 });
    fireEvent.click((await screen.findAllByRole('menuitem', { name: 'Provider' }))[0]!, { detail: 1 });
    expect(await screen.findByRole('button', { name: '返回筛选字段' })).toBeInTheDocument();
  });

  it('shows messages.noOptions when a field has no options', async () => {
    render(
      <FilterMenu
        fields={[{ id: 'empty', label: 'Nothing', options: [] }]}
        onSelect={() => {}}
        messages={{ noOptions: '没有匹配项' }}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Add filter' }), { detail: 1 });
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Nothing' }), { detail: 1 });
    expect(await screen.findByText('没有匹配项')).toBeInTheDocument();
  });
});

// ---------- FilterToolbar ----------

describe('FilterToolbar (Arc chips)', () => {
  const onRemove = vi.fn();
  const onClearAll = vi.fn();
  const messages: Partial<FilterMessages> = {
    empty: 'NOTHING',
    clearAll: 'WIPE',
    cleared: 'WIPED',
    added: (filter) => `A ${filter.label}=${filter.valueLabel ?? filter.value}`,
    changed: (filter) => `C ${filter.label}=${filter.valueLabel ?? filter.value}`,
    removed: (filter) => `R ${filter.label}=${filter.valueLabel ?? filter.value}`,
    remove: (filter) => `X ${filter.label}=${filter.valueLabel ?? filter.value}`,
  };
  const toolbar = (filters: FilterChip[]) => <FilterToolbar filters={filters} onRemove={onRemove} onClearAll={onClearAll} messages={messages} />;
  const chipA: FilterChip = { id: 'provider', label: 'Provider', value: 'prov-9f2', valueLabel: 'Anthropic' };

  beforeEach(() => {
    onRemove.mockClear();
    onClearAll.mockClear();
  });

  it('announces added/changed/removed/cleared and empty through the messages overrides', async () => {
    const view = render(toolbar([]));
    expect(screen.getByText('NOTHING')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'WIPE' })).toBeNull();

    view.rerender(toolbar([chipA]));
    expect(screen.getByRole('status')).toHaveTextContent('A Provider=Anthropic');
    expect(screen.getByRole('button', { name: 'WIPE' })).toBeInTheDocument();

    view.rerender(toolbar([{ ...chipA, value: 'prov-1c8' }]));
    expect(screen.getByRole('status')).toHaveTextContent('C Provider=Anthropic');

    view.rerender(toolbar([{ ...chipA, value: 'prov-7ad' }, { id: 'status', label: 'Status', value: 'success' }]));
    expect(screen.getByRole('status')).toHaveTextContent('A Status=success. C Provider=Anthropic');

    view.rerender(toolbar([{ ...chipA, value: 'prov-7ad' }]));
    expect(screen.getByRole('status')).toHaveTextContent('R Status=success');

    // "Cleared" only applies when more than one chip goes away at once.
    view.rerender(toolbar([{ ...chipA, value: 'prov-7ad' }, { id: 'status', label: 'Status', value: 'success' }]));
    view.rerender(toolbar([]));
    expect(screen.getByRole('status')).toHaveTextContent('WIPED');
    expect(screen.getByText('NOTHING')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('button', { name: 'WIPE' })).toBeNull());
  });

  it('shows valueLabel on the chip without leaking the internal id, and localizes remove', () => {
    render(toolbar([chipA]));
    expect(screen.getByTitle('Provider: Anthropic')).toBeInTheDocument();
    expect(screen.queryByText('prov-9f2')).toBeNull();
    const remove = screen.getByRole('button', { name: 'X Provider=Anthropic' });
    fireEvent.click(remove);
    expect(onRemove).toHaveBeenCalledWith('provider');
  });

  it('offers no clear button when onClearAll is absent, even with chips', () => {
    render(<FilterToolbar filters={[chipA]} onRemove={onRemove} messages={messages} />);
    expect(screen.queryByRole('button', { name: 'WIPE' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'X Provider=Anthropic' }));
    expect(onClearAll).not.toHaveBeenCalled();
  });

  it('clicking the localized clear button clears everything', () => {
    render(toolbar([chipA]));
    fireEvent.click(screen.getByRole('button', { name: 'WIPE' }));
    expect(onClearAll).toHaveBeenCalledTimes(1);
  });
});

// ---------- RequestsPage ----------

describe('RequestsPage filtering', () => {
  beforeEach(() => {
    hookSpies.useClients.mockReset().mockReturnValue(hookResult([{ kind: 'codex', name: 'Codex' } as ClientInfo]));
    hookSpies.useProviders.mockReset().mockReturnValue(hookResult([providerRow('prov-anthropic', 'Anthropic'), providerRow('prov-relay', 'Anthropic')]));
    hookSpies.useRequests.mockReset().mockReturnValue(hookResult(pageOf([summary({ id: 'req-1' })])));
    hookSpies.useRequest.mockReset().mockReturnValue(hookResult(undefined));
    hookSpies.useRequestRate.mockReset().mockReturnValue(hookResult(undefined));
  });

  it('keeps the model search inside the filter group; trim + debounce feed the query', async () => {
    showPage();
    const group = screen.getByRole('group', { name: 'Active filters' });
    fireEvent.change(within(group).getByLabelText('Search model IDs (keyword)'), { target: { value: '  gpt-5  ' } });
    expect(lastQuery().model).toBe('');
    await waitDebounce();
    expect(lastQuery().model).toBe('gpt-5');
  });

  it('shows client icons and joins several picked clients into one query', async () => {
    hookSpies.useClients.mockReturnValue(hookResult([{ kind: 'codex', name: 'Codex' } as ClientInfo, { kind: 'claude-code', name: 'Claude Code' } as ClientInfo]));
    showPage();
    const group = screen.getByRole('group', { name: 'Active filters' });
    const codex = await pickOption(group, 'Client', 'Codex');
    expect(codex.querySelector('[data-testid^="lobe-"]')).not.toBeNull();
    // The menu stays open, so the next pick is in the same pass.
    await pickOption(group, 'Client', 'Claude Code');
    await waitDebounce();
    expect(lastQuery()).toMatchObject({ client: 'codex,claude-code', page: 1 });
    expect(within(group).getByRole('button', { name: 'Client: Codex, Claude Code' })).toHaveTextContent('+1');

    // Unchecking narrows the list again.
    await pickOption(group, 'Client', 'Codex');
    await waitDebounce();
    expect(lastQuery().client).toBe('claude-code');
  });

  it('shows the filtered total spend from the list query in the header', () => {
    hookSpies.useRequests.mockReturnValue(hookResult({ ...pageOf([summary({ id: 'req-1' })]), total: 120, totalCostNanoUsd: 12_340_000_000 }));
    showPage();
    expect(screen.getByText('Total spend').parentElement).toHaveTextContent('$12.34');
  });

  it('returns to page 1 when a filter changes', async () => {
    hookSpies.useRequests.mockReturnValue(hookResult({ ...pageOf([summary({ id: 'req-1' })]), total: 120 }));
    showPage();
    fireEvent.click(screen.getByRole('button', { name: 'Page 2' }));
    expect(lastQuery().page).toBe(2);

    await pickOption(screen.getByRole('group', { name: 'Active filters' }), 'Provider', 'Anthropic', 'prov-relay');
    await waitDebounce();
    expect(lastQuery()).toMatchObject({ provider: 'prov-relay', page: 1 });
  });

  it('clear resets model, providers and statuses and returns to page 1', async () => {
    showPage();
    const group = screen.getByRole('group', { name: 'Active filters' });
    fireEvent.change(within(group).getByLabelText('Search model IDs (keyword)'), { target: { value: 'gpt-5' } });
    await pickOption(group, 'Provider', 'Anthropic', 'prov-anthropic');
    fireEvent.keyDown(screen.getByRole('menu'), { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());
    await pickOption(group, 'Status', 'Success');
    await waitDebounce();
    expect(lastQuery()).toMatchObject({ model: 'gpt-5', provider: 'prov-anthropic', status: 'success' });

    fireEvent.click(within(group).getByRole('button', { name: 'Clear filters' }));
    await waitDebounce();
    expect(lastQuery()).toMatchObject({ model: '', client: '', provider: '', status: '', page: 1 });
    expect(within(group).getByLabelText('Search model IDs (keyword)')).toHaveValue('');
    expect(within(group).getByRole('button', { name: 'Provider: All providers' })).toBeInTheDocument();
  });

  it('picking the second of two same-name providers queries its real id', async () => {
    showPage();
    const group = screen.getByRole('group', { name: 'Active filters' });
    fireEvent.keyDown(within(group).getByRole('button', { name: /^Provider:/ }), { key: 'Enter' });
    expect(await screen.findAllByRole('menuitemcheckbox', { name: 'Anthropic' })).toHaveLength(2);
    await pickOption(group, 'Provider', 'Anthropic', 'prov-relay');
    await waitDebounce();
    expect(lastQuery().provider).toBe('prov-relay');
    // The trigger shows the shared label, never the internal id.
    expect(within(group).queryByText('prov-relay')).toBeNull();
  });
});

describe('Requests list origin', () => {
  beforeEach(() => {
    hookSpies.useClients.mockReset().mockReturnValue(hookResult([{ kind: 'codex', name: 'Codex' } as ClientInfo]));
    hookSpies.useProviders.mockReset().mockReturnValue(hookResult([providerRow('prov-anthropic', 'Anthropic')]));
    hookSpies.useRequest.mockReset().mockReturnValue(hookResult(undefined));
    hookSpies.useRequestRate.mockReset().mockReturnValue(hookResult(undefined));
  });

  it('hides the origin filter until remote access shows up', () => {
    hookSpies.useRequests.mockReturnValue(hookResult(pageOf([summary({ id: 'r-local' })])));
    showPage();
    expect(screen.queryByRole('combobox', { name: 'Origin' })).toBeNull();
    expect(screen.queryByRole('img', { name: /^Remote/ })).toBeNull();
  });

  it('marks remote rows with the visitor address and offers the origin filter', async () => {
    hookSpies.useRequests.mockReturnValue(hookResult(pageOf([summary({ id: 'r-remote', remote: true, clientIp: '203.0.113.7' })])));
    showPage();
    expect(screen.getByRole('img', { name: 'Remote · 203.0.113.7' })).toBeInTheDocument();
    const group = screen.getByRole('group', { name: 'Active filters' });
    await pickOption(group, 'Origin', 'Remote');
    await waitDebounce();
    expect(lastQuery().origin).toBe('remote');
    // Both picked means any origin.
    await pickOption(group, 'Origin', 'Local');
    await waitDebounce();
    expect(lastQuery().origin).toBe('');
  });
});

describe('Requests list model column', () => {
  beforeEach(() => {
    hookSpies.useClients.mockReset().mockReturnValue(hookResult([{ kind: 'codex', name: 'Codex' } as ClientInfo]));
    hookSpies.useProviders.mockReset().mockReturnValue(hookResult([providerRow('prov-anthropic', 'Anthropic')]));
    hookSpies.useRequest.mockReset().mockReturnValue(hookResult(undefined));
    hookSpies.useRequestRate.mockReset().mockReturnValue(hookResult(undefined));
  });

  it('colors matched models green and mismatches orange with visible wording', () => {
    hookSpies.useRequests.mockReturnValue(
      hookResult(
        pageOf([
          summary({ id: 'r-match', requestedModel: 'gpt-a', upstreamModel: 'up-a', responseModel: 'gpt-a' }),
          summary({ id: 'r-mismatch', requestedModel: 'gpt-b', upstreamModel: 'up-b', responseModel: 'up-c' }),
        ]),
      ),
    );
    showPage();

    const match = screen.getByText('Match').parentElement!;
    expect(match).toHaveClass('text-[var(--success)]');
    expect(match).toHaveAttribute('title', 'Requested: gpt-a; sent upstream: up-a; API returned: gpt-a');

    const mismatch = screen.getByText('Mismatch').parentElement!;
    expect(mismatch).toHaveClass('text-[var(--warning)]');
    expect(mismatch).toHaveAttribute('title', 'Requested: gpt-b; sent upstream: up-b; API returned: up-c');
  });

  it('reports a mismatch when the upstream model equals the response but the request differs', () => {
    hookSpies.useRequests.mockReturnValue(
      hookResult(pageOf([summary({ id: 'r-swap', requestedModel: 'gpt-d', upstreamModel: 'up-x', responseModel: 'up-x' })])),
    );
    showPage();

    const mismatch = screen.getByText('Mismatch').parentElement!;
    expect(mismatch).toHaveClass('text-[var(--warning)]');
    expect(mismatch).toHaveAttribute('title', 'Requested: gpt-d; sent upstream: up-x; API returned: up-x');
  });

  it('shows Not recorded (muted) when the response model is null or missing, without guessing', () => {
    hookSpies.useRequests.mockReturnValue(
      hookResult(
        pageOf([
          summary({ id: 'r-null', requestedModel: 'gpt-e', upstreamModel: 'up-e', responseModel: null }),
          summary({ id: 'r-absent', requestedModel: 'gpt-f', upstreamModel: 'up-f' }),
        ]),
      ),
    );
    showPage();

    const hints = screen.getAllByText('Returned: Not recorded');
    expect(hints).toHaveLength(2);
    for (const hint of hints) {
      const tag = hint.parentElement!;
      expect(tag).toHaveClass('text-[var(--text-muted)]');
      expect(tag).not.toHaveClass('text-[var(--success)]');
      expect(tag).not.toHaveClass('text-[var(--warning)]');
      expect(tag.textContent).toBe('Returned: Not recorded');
      expect(tag).toHaveAttribute('title', 'An older log or the API omitted the model ID; the requested model is not used as a substitute.');
    }
    expect(screen.queryByText('Match')).toBeNull();
    expect(screen.queryByText('Mismatch')).toBeNull();
    // The requested model still shows in the cell; it is never passed off as the returned model.
    expect(screen.getByTitle('gpt-e')).toBeInTheDocument();
  });
});

describe('Requests list reasoning badge', () => {
  beforeEach(() => {
    hookSpies.useClients.mockReset().mockReturnValue(hookResult([]));
    hookSpies.useProviders.mockReset().mockReturnValue(hookResult([]));
    hookSpies.useRequest.mockReset().mockReturnValue(hookResult(undefined));
    hookSpies.useRequestRate.mockReset().mockReturnValue(hookResult(undefined));
  });

  it('shows the explicitly set effort, mode and raw budget next to the model name', () => {
    hookSpies.useRequests.mockReturnValue(
      hookResult(
        pageOf([
          summary({ id: 'r-effort', reasoningEffort: 'high' }),
          summary({ id: 'r-adaptive', reasoningEffort: 'xhigh', reasoningMode: 'adaptive' }),
          summary({ id: 'r-budget', reasoningMode: 'enabled', reasoningBudgetTokens: 8192 }),
          summary({ id: 'r-auto', reasoningMode: 'auto' }),
        ]),
      ),
    );
    showPage();

    expect(screen.getByText('Reasoning: high')).toBeInTheDocument();
    expect(screen.getByText('Reasoning: xhigh · adaptive')).toBeInTheDocument();
    // The budget keeps its real value — it is never mapped to a level.
    expect(screen.getByText('Reasoning: enabled · budget 8192')).toBeInTheDocument();
    expect(screen.getByText('Reasoning: auto')).toBeInTheDocument();
  });

  it('bounds long reasoning labels without losing their recorded value', () => {
    const effort = 'custom-effort-'.repeat(100);
    hookSpies.useRequests.mockReturnValue(hookResult(pageOf([
      summary({ id: 'r-long-effort', reasoningEffort: effort }),
    ])));
    showPage();

    const label = screen.getByText(`Reasoning: ${effort}`);
    expect(label).toHaveClass('max-w-40', 'truncate');
    const badge = label.closest('[title]')!;
    expect(badge).toHaveClass('max-w-44');
    expect(badge.getAttribute('title')).toContain(effort);
  });

  it('displays off explicitly (none / disabled) and stays silent for unrecorded rows', () => {
    hookSpies.useRequests.mockReturnValue(
      hookResult(
        pageOf([
          summary({ id: 'r-none', reasoningEffort: 'none' }),
          summary({ id: 'r-disabled', reasoningMode: 'disabled' }),
          summary({ id: 'r-unrecorded' }),
        ]),
      ),
    );
    showPage();

    expect(screen.getByText('Reasoning: none')).toBeInTheDocument();
    expect(screen.getByText('Reasoning: disabled')).toBeInTheDocument();
    // Old rows / unset configs render nothing in the list — no placeholder, no guessed level.
    expect(screen.getAllByText(/^Reasoning: /)).toHaveLength(2);
  });
});

describe('Requests list TTFT and TPS columns', () => {
  beforeEach(() => {
    hookSpies.useClients.mockReset().mockReturnValue(hookResult([]));
    hookSpies.useProviders.mockReset().mockReturnValue(hookResult([]));
    hookSpies.useRequest.mockReset().mockReturnValue(hookResult(undefined));
    hookSpies.useRequestRate.mockReset().mockReturnValue(hookResult(undefined));
  });

  it('gives TTFT and the output speed a column each, with units', () => {
    hookSpies.useRequests.mockReset().mockReturnValue(hookResult(pageOf([
      summary({ id: 'r-speed', ttftMs: 120, outputTps: 42.56 }),
    ])));
    showPage();

    expect(screen.getByRole('columnheader', { name: /TTFT/ })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: /TPS/ })).toBeInTheDocument();
    const ttft = screen.getByTitle('TTFT');
    const speed = screen.getByTitle('Output speed');
    expect(ttft).toHaveTextContent('120 ms');
    expect(speed).toHaveTextContent('42.6 tok/s');
    expect(ttft.closest('[role="cell"]')).toHaveAttribute('data-label', 'TTFT');
    expect(speed.closest('[role="cell"]')).toHaveAttribute('data-label', 'TPS');
    expect(speed.closest('[role="cell"]')).not.toBe(ttft.closest('[role="cell"]'));
  });

  it.each([null, undefined])('shows a missing TPS as a dash, not a guessed speed (%s)', (outputTps) => {
    hookSpies.useRequests.mockReset().mockReturnValue(hookResult(pageOf([
      summary({ id: 'r-missing-speed', ttftMs: 120, outputTps, totalOutputTokens: 50, totalMs: 1000 }),
    ])));
    showPage();

    const speed = screen.getByTitle('Output speed');
    expect(speed).toHaveTextContent('—');
    expect(speed).not.toHaveTextContent('tok/s');
    expect(speed.closest('[role="cell"]')).not.toHaveTextContent('120 ms');
  });

  it('displays zero TPS as a number, even when TTFT is missing', () => {
    hookSpies.useRequests.mockReset().mockReturnValue(hookResult(pageOf([
      summary({ id: 'r-zero-speed', ttftMs: null, outputTps: 0 }),
    ])));
    showPage();

    expect(screen.getByTitle('TTFT')).toHaveTextContent('—');
    expect(screen.getByTitle('Output speed')).toHaveTextContent('0.0 tok/s');
  });
});

describe('RequestsPage detail sheet', () => {
  beforeEach(() => {
    hookSpies.useClients.mockReset().mockReturnValue(hookResult([{ kind: 'codex', name: 'Codex' } as ClientInfo]));
    hookSpies.useProviders.mockReset().mockReturnValue(hookResult([providerRow('prov-anthropic', 'Anthropic')]));
    hookSpies.useRequests.mockReset().mockReturnValue(hookResult(pageOf([summary({ id: 'req-1', requestedModel: 'gpt-a', upstreamModel: 'up-b', responseModel: 'up-c' })])));
    hookSpies.useRequestRate.mockReset().mockReturnValue(hookResult(undefined));
  });

  it('opens the request detail with all three model values and colors the returned model', async () => {
    const detail: RequestDetail = {
      ...summary({ id: 'req-1', requestedModel: 'gpt-a', upstreamModel: 'up-b', responseModel: 'up-c' }),
      billingTrace: [],
      usageItems: [],
    };
    hookSpies.useRequest.mockReset().mockImplementation((id: string | null) => hookResult(id === 'req-1' ? detail : undefined));
    showPage();

    fireEvent.click(screen.getByRole('button', { name: 'Open request details for gpt-a' }));
    expect(hookSpies.useRequest).toHaveBeenCalledWith('req-1');
    const dialog = await screen.findByRole('dialog', { name: 'gpt-a' });
    expect(within(dialog).getByText('Requested model')).toBeInTheDocument();
    expect(within(dialog).getByText('Sent upstream model')).toBeInTheDocument();
    expect(within(dialog).getByText('API returned model')).toBeInTheDocument();
    expect(within(dialog).getByText('up-b')).toBeInTheDocument();

    const returned = within(dialog).getByText('Mismatch').parentElement!;
    expect(returned).toHaveClass('text-[var(--warning)]');
    expect(returned).toHaveAttribute('title', 'Requested: gpt-a; sent upstream: up-b; API returned: up-c');
  });

  it('adds the recorded reasoning effort, mode and budget to the detail sheet', async () => {
    const detail: RequestDetail = {
      ...summary({ id: 'req-1', reasoningEffort: 'xhigh', reasoningMode: 'adaptive', reasoningBudgetTokens: 4096 }),
      billingTrace: [],
      usageItems: [],
    };
    hookSpies.useRequest.mockReset().mockImplementation((id: string | null) => hookResult(id === 'req-1' ? detail : undefined));
    showPage();

    fireEvent.click(screen.getByRole('button', { name: 'Open request details for gpt-a' }));
    const dialog = await screen.findByRole('dialog', { name: 'gpt-a' });
    const row = within(dialog).getByText('Reasoning').parentElement!;
    expect(within(row).getByText('xhigh · adaptive · budget 4096')).toBeInTheDocument();
  });

  it('marks unrecorded reasoning as Not recorded (muted), never a guessed level', async () => {
    const detail: RequestDetail = {
      ...summary({ id: 'req-1' }),
      billingTrace: [],
      usageItems: [],
    };
    hookSpies.useRequest.mockReset().mockImplementation((id: string | null) => hookResult(id === 'req-1' ? detail : undefined));
    showPage();

    fireEvent.click(screen.getByRole('button', { name: 'Open request details for gpt-a' }));
    const dialog = await screen.findByRole('dialog', { name: 'gpt-a' });
    const reasoningRow = within(dialog).getByText('Reasoning').parentElement!;
    // Scoped to the reasoning row: the returned-model row has its own "Not recorded".
    const value = within(reasoningRow).getByText('Not recorded');
    expect(value).toHaveClass('text-[var(--text-muted)]');
  });

  it.each([
    { outputTps: 42.56, text: '42.6 tok/s' },
    { outputTps: 0, text: '0.0 tok/s' },
    { outputTps: null, text: '—' },
  ])('formats output speed consistently in details ($outputTps)', async ({ outputTps, text }) => {
    const detail: RequestDetail = {
      ...summary({ id: 'req-1', outputTps }),
      billingTrace: [],
      usageItems: [],
    };
    hookSpies.useRequest.mockReset().mockImplementation((id: string | null) => hookResult(id === 'req-1' ? detail : undefined));
    showPage();

    fireEvent.click(screen.getByRole('button', { name: 'Open request details for gpt-a' }));
    const dialog = await screen.findByRole('dialog', { name: 'gpt-a' });
    const speedRow = within(dialog).getByText('Output speed').parentElement!;
    expect(within(speedRow).getByText(text)).toBeInTheDocument();
  });
});

afterEach(() => {
  cleanup();
  localStorage.clear();
});
