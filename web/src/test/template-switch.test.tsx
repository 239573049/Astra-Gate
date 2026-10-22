import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { Provider, ProviderTemplate } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

// The real @lobehub/icons barrel imports JSON with import attributes that Node ESM (vitest) rejects; these pages only
// need brand marks, so this stubs the same set as icons.test.tsx.
vi.mock('@lobehub/icons', () => {
  const stub = (name: string) => {
    const mark = (variant: string) => (props: { size?: number }) => <svg data-testid={`lobe-${name}`} data-variant={variant} data-size={props.size} />;
    return Object.assign(mark('mono'), { Avatar: mark('avatar'), Color: mark('color') });
  };
  return {
    Anthropic: stub('anthropic'), Claude: stub('claude'), ClaudeCode: stub('claude-code'), Codex: stub('codex'),
    DeepSeek: stub('deepseek'), Gemini: stub('gemini'), GeminiCLI: stub('gemini-cli'), GithubCopilot: stub('github-copilot'),
    Grok: stub('grok'), HermesAgent: stub('hermes-agent'), Kimi: stub('kimi'), LmStudio: stub('lm-studio'), Meta: stub('meta'), Qoder: stub('qoder'), Minimax: stub('minimax'),
    Moonshot: stub('moonshot'), Ollama: stub('ollama'), OpenAI: stub('openai'), OpenCode: stub('opencode'),
    OpenRouter: stub('openrouter'), Pi: stub('pi'), Qwen: stub('qwen'), SiliconCloud: stub('siliconcloud'),
    Volcengine: stub('volcengine'), XAI: stub('xai'), XiaomiMiMo: stub('xiaomi-mimo'), ZAI: stub('zai'), Zhipu: stub('zhipu'),
    Baichuan: stub('baichuan'), ChatGLM: stub('chatglm'), Cohere: stub('cohere'), Doubao: stub('doubao'), Gemma: stub('gemma'), Hunyuan: stub('hunyuan'), LongCat: stub('longcat'), Mistral: stub('mistral'), Stepfun: stub('stepfun'), Wenxin: stub('wenxin'), Yi: stub('yi'),
  };
});

vi.mock('../components/layout/AppShell', () => ({
  useShellLayout: () => ({ sidebarMode: 'expanded', sidebarWidth: 224, drawerOpen: false, setDrawerOpen: () => {}, toggleSidebar: () => {}, narrow: false }),
  TOOLBAR_RIGHT_RESERVE: 0,
}));

// Reduced motion keeps every transition at duration 0, so tests never race an animation.
vi.mock('motion/react', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useReducedMotion: () => true,
}));

// jsdom has no ResizeObserver (menus and sheets measure themselves); a noop one never fires.
class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const { KeyAlternativeHint } = await import('../components/TemplateSwitch');
const { ProvidersPage } = await import('../pages/ProvidersPage');

const template = (id: string, name: string, over: Partial<ProviderTemplate> = {}): ProviderTemplate =>
  ({
    id, version: 1, name, category: 'aggregator', icon: null, endpoints: [], preferredUpstreamProtocols: [],
    auth: { scheme: 'bearer' }, authScheme: 'bearer', requiresApiKey: true, models: [], defaultHeaders: {}, ...over,
  }) as ProviderTemplate;

const templates: ProviderTemplate[] = [
  template('routin', 'Routin 订阅（Plan）', {
    endpoints: [{ protocol: 'anthropic', baseUrl: 'https://api.routin.ai/plan/v1' }], preferredUpstreamProtocols: ['anthropic'],
    keyAlternatives: ['routin-ai'],
  }),
  template('routin-ai', 'RoutinAI', {
    endpoints: [{ protocol: 'anthropic', baseUrl: 'https://api.routin.ai/v1' }], preferredUpstreamProtocols: ['anthropic'],
    keyAlternatives: ['routin'],
  }),
  template('deepseek', 'DeepSeek', {
    category: 'cn', endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://api.deepseek.com/v1' }], preferredUpstreamProtocols: ['openai-chat'],
    models: ['deepseek-chat'],
  }),
];

const provider = (over: Partial<Provider> = {}): Provider =>
  ({
    id: 'p1', name: 'Routin', icon: null, category: 'aggregator', templateId: 'routin', authScheme: 'bearer',
    endpoints: [{ protocol: 'anthropic', baseUrl: 'https://api.routin.ai/plan/v1' }], preferredUpstreamProtocols: ['anthropic'],
    hasApiKey: true, extraHeaders: {}, priceMultiplier: 1, settings: {}, enabled: true, boundClients: [], ...over,
  }) as unknown as Provider;

type Call = { method: string; path: string; body: unknown };
let calls: Call[] = [];

/** Answers the admin API the pages ask for; everything else is an empty list. */
function serve() {
  calls = [];
  vi.stubGlobal('ResizeObserver', NoopResizeObserver);
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      calls.push({ method, path, body: init?.body ? JSON.parse(String(init.body)) : null });
      const json = (data: unknown) => new Response(JSON.stringify(data), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (path === '/api/provider-templates') return json(templates);
      if (path.endsWith('/template-switch')) return json(provider({ templateId: 'routin-ai' }));
      if (method === 'POST' && path === '/api/providers') return json(provider({ id: 'p2', name: 'DeepSeek', templateId: 'deepseek' }));
      return json([]);
    }),
  );
}

const show = (ui: React.ReactNode) => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <MemoryRouter initialEntries={['/providers?new=1']}>{ui}</MemoryRouter>
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(serve);

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

describe('key alternatives (issue #9)', () => {
  it('offers the other template when the upstream refuses the key, and switches after confirming', async () => {
    show(<KeyAlternativeHint provider={provider()} httpStatus={401} />);
    const offer = await screen.findByRole('button', { name: 'Switch to "RoutinAI"' });
    expect(screen.getByText(/Keys for "Routin 订阅（Plan）" and "RoutinAI" are not interchangeable/)).toBeTruthy();

    fireEvent.click(offer);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/https:\/\/api\.routin\.ai\/v1/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Switch to "RoutinAI"' }));

    await waitFor(() => expect(calls.some((c) => c.path === '/api/providers/p1/template-switch')).toBe(true));
    expect(calls.find((c) => c.path === '/api/providers/p1/template-switch')).toMatchObject({ method: 'POST', body: { templateId: 'routin-ai' } });
  });

  it('stays silent for other failures, templates without alternatives and account providers', async () => {
    show(
      <>
        <KeyAlternativeHint provider={provider()} httpStatus={500} />
        <KeyAlternativeHint provider={provider({ authScheme: 'oauth-subscription' })} httpStatus={401} />
        <KeyAlternativeHint provider={provider({ templateId: 'deepseek' })} httpStatus={401} />
      </>,
    );
    await waitFor(() => expect(calls.some((c) => c.path === '/api/provider-templates')).toBe(true));
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByRole('button', { name: /Switch to/ })).toBeNull();
  });
});

describe('add provider wizard (issue #8)', () => {
  it('sends the more options with the new provider', async () => {
    show(<ProvidersPage />);
    fireEvent.click(await screen.findByRole('radio', { name: /DeepSeek/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));

    fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'sk-wizard-key-0001' } });
    fireEvent.click(screen.getByRole('button', { name: 'More options' }));
    fireEvent.change(await screen.findByLabelText('HTTP proxy'), { target: { value: ' http://127.0.0.1:7890 ' } });
    fireEvent.change(screen.getByLabelText('Notes'), { target: { value: 'team key' } });
    fireEvent.change(screen.getByLabelText('Initial models'), { target: { value: 'deepseek-chat, deepseek-reasoner' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add header' }));
    fireEvent.change(screen.getByLabelText('Name', { selector: 'input[placeholder="Header"]' }), { target: { value: 'X-Team' } });
    fireEvent.change(screen.getByLabelText('Value'), { target: { value: 'blue' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));

    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/providers')).toBe(true));
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/providers')!.body).toEqual({
      templateId: 'deepseek', variantId: null, name: 'DeepSeek', apiKey: 'sk-wizard-key-0001', priceKey: 'deepseek',
      endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://api.deepseek.com/v1' }], authScheme: 'bearer',
      preferredUpstreamProtocols: ['openai-chat'], extraHeaders: { 'X-Team': 'blue' }, httpProxy: 'http://127.0.0.1:7890',
      priceMultiplier: 1, notes: 'team key', models: ['deepseek-chat', 'deepseek-reasoner'],
    });
  });

  it('points at the other template when a key may belong to it', async () => {
    show(<ProvidersPage />);
    fireEvent.click(await screen.findByRole('radio', { name: /Routin 订阅/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Choose "RoutinAI" instead' }));
    expect(await screen.findByDisplayValue('RoutinAI')).toBeTruthy();
  });
});
