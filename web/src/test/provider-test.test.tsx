import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { createSseParser } from '../api/client';
import type { Provider, ProviderModel } from '../api/types';
import { I18nProvider } from '../i18n';

// The real @lobehub/icons barrel imports JSON with import attributes that Node ESM (vitest) rejects; the dialog only
// needs a brand mark, so this stubs the same set as icons.test.tsx.
vi.mock('@lobehub/icons', () => {
  const stub = (name: string) => {
    const mark = (variant: string) => (props: { size?: number }) => <svg data-testid={`lobe-${name}`} data-variant={variant} data-size={props.size} />;
    return Object.assign(mark('mono'), { Avatar: mark('avatar'), Color: mark('color') });
  };
  return {
    Anthropic: stub('anthropic'), Claude: stub('claude'), ClaudeCode: stub('claude-code'), Codex: stub('codex'),
    DeepSeek: stub('deepseek'), Gemini: stub('gemini'), GeminiCLI: stub('gemini-cli'), GithubCopilot: stub('github-copilot'),
    Grok: stub('grok'), HermesAgent: stub('hermes-agent'), LmStudio: stub('lm-studio'), Minimax: stub('minimax'),
    Moonshot: stub('moonshot'), Ollama: stub('ollama'), OpenAI: stub('openai'), OpenCode: stub('opencode'),
    OpenRouter: stub('openrouter'), Pi: stub('pi'), Qwen: stub('qwen'), SiliconCloud: stub('siliconcloud'),
    Volcengine: stub('volcengine'), XAI: stub('xai'), Zhipu: stub('zhipu'),
  };
});

const { ProviderTestDialog } = await import('../components/ProviderTestDialog');

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

// ---------- SSE parsing ----------

describe('provider test event stream', () => {
  it('parses frames split across chunks, multi-line data and comment keep-alives', () => {
    const parse = createSseParser();
    expect(parse('event: headers\ndata: {"httpMs":12}\n')).toEqual([]);
    expect(parse('\n: keep-alive\n\nevent: done\ndata: {"ok":true,\ndata: "text":"OK"}\n\n')).toEqual([
      { event: 'headers', data: { httpMs: 12 } },
      { event: 'done', data: { ok: true, text: 'OK' } },
    ]);
    expect(parse('data: not json\n\n')).toEqual([{ event: '', data: 'not json' }]);
  });
});

// ---------- dialog ----------

const provider = {
  id: 'p1', name: 'Test provider', icon: null, category: 'custom', templateId: null,
  endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://example.invalid/v1', fullUrl: false }],
  preferredUpstreamProtocols: ['openai-chat'], authScheme: 'bearer', hasApiKey: true, apiKeyMasked: 'sk-…0000',
  extraHeaders: {}, httpProxy: null, priceMultiplier: 1, priceKey: null, settings: {}, enabled: true,
  sortOrder: 0, modelCount: 0, boundClients: [], createdAt: '', updatedAt: '',
} as unknown as Provider;

function model(id: string): ProviderModel {
  return { id: 1, providerId: 'p1', modelId: id, systemModelId: null, enabled: true, sortOrder: 0, overrides: {}, effective: { displayName: id, capabilities: {} }, origins: {}, pricingSource: 'none', priceKey: null, multiplier: 1 } as unknown as ProviderModel;
}

const DONE = {
  ok: true, error: null, httpStatus: 200, httpMs: 150, ttftMs: 320, totalMs: 400,
  text: 'OK', reasoning: '', raw: 'data: {"choices":[]}', rawTruncated: false,
  contentType: 'application/json', responseModel: 'model-x', requestId: 'r1', usage: null,
  headers: { 'x-request-id': 'req-1', 'openai-processing-ms': '120' },
};

/** A test-stream response that stays open until `release()` is called. */
function makeStream(done: unknown) {
  const encoder = new TextEncoder();
  let release!: () => void;
  let close!: () => void;
  const gate = new Promise<void>((resolve) => (release = resolve));
  const closed = new Promise<void>((resolve) => (close = resolve));
  const stream = new ReadableStream<Uint8Array>({
    async start(controller) {
      controller.enqueue(encoder.encode('event: headers\ndata: {"httpStatus":200,"httpMs":150,"contentType":"application/json","headers":{"x-request-id":"req-1"}}\n\n'));
      controller.enqueue(encoder.encode('event: delta\ndata: {"kind":"reasoning","text":"thinking"}\n\n'));
      controller.enqueue(encoder.encode('event: delta\ndata: {"kind":"text","text":"O"}\n\n'));
      await gate;
      controller.enqueue(encoder.encode(`event: done\ndata: ${JSON.stringify(done)}\n\n`));
      controller.close();
      close();
    },
  });
  return { response: new Response(stream, { status: 200, headers: { 'Content-Type': 'text/event-stream' } }), release, closed };
}

/** Stubs fetch: model lists answer immediately, test runs answer when their release() is called. */
function stubFetch(models: string[]) {
  const pending: { url: string; body: unknown; release: () => void; closed: Promise<void> }[] = [];
  let inFlight = 0;
  let peak = 0;
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.endsWith('/models')) {
      return new Response(JSON.stringify(models.map(model)), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }
    const { response, release, closed } = makeStream(DONE);
    inFlight += 1;
    peak = Math.max(peak, inFlight);
    void closed.then(() => (inFlight -= 1));
    pending.push({ url, body: JSON.parse(String(init?.body ?? '{}')), release, closed });
    return response;
  });
  vi.stubGlobal('fetch', fetchMock);
  return { pending, peak: () => peak };
}

function show(ui: React.ReactNode) {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><I18nProvider>{ui}</I18nProvider></QueryClientProvider>);
}

describe('provider test dialog', () => {
  it('streams one test and shows its metrics, preview and raw body', async () => {
    const fetcher = stubFetch(['model-x', 'model-y']);
    show(<ProviderTestDialog provider={provider} open onOpenChange={() => {}} />);
    // The picker opens with the provider's models and picking one fills the input.
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Test model' }) as HTMLInputElement).value).toBe('model-x'));
    fireEvent.click(screen.getByRole('button', { name: 'Show model list' }));
    fireEvent.mouseDown(await screen.findByRole('button', { name: 'model-y' }));
    expect(screen.getByRole('combobox', { name: 'Test model' })).toHaveValue('model-y');
    fireEvent.click(screen.getByRole('button', { name: 'Start test' }));
    await waitFor(() => expect(fetcher.pending).toHaveLength(1));
    expect(fetcher.pending[0]!.body).toMatchObject({ modelId: 'model-y', protocol: 'openai-chat', stream: true, maxOutputTokens: 256 });
    fetcher.pending[0]!.release();
    await screen.findByText('OK');
    expect(screen.getByText('HTTP 200')).toBeInTheDocument();
    expect(screen.getByText('150 ms')).toBeInTheDocument();
    expect(screen.getByText('400 ms')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Raw' }));
    expect(await screen.findByText(/data: \{"choices":\[\]\}/)).toBeInTheDocument();
    // The response headers arrive with the "headers" event and live in their own tab next to "Raw".
    fireEvent.click(screen.getByRole('button', { name: 'Response headers' }));
    expect(screen.getByText('x-request-id')).toBeInTheDocument();
    expect(screen.getByText('req-1')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('openai-processing-ms')).toBeInTheDocument());
    expect(screen.getByText('120')).toBeInTheDocument();
  });

  it('runs a batch three at a time and cancels the models still waiting', async () => {
    const fetcher = stubFetch([]);
    show(<ProviderTestDialog provider={provider} open onOpenChange={() => {}} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Batch' }));
    const search = screen.getByPlaceholderText('Search or type a model ID');
    for (const id of ['m1', 'm2', 'm3', 'm4', 'm5']) {
      fireEvent.change(search, { target: { value: id } });
      fireEvent.click(await screen.findByRole('button', { name: `Add "${id}"` }));
    }
    fireEvent.click(screen.getByRole('button', { name: 'Test 5 models' }));
    await waitFor(() => expect(fetcher.pending).toHaveLength(3));
    expect(fetcher.peak()).toBe(3);

    fireEvent.click(screen.getByRole('button', { name: 'Stop the rest' }));
    const rows = () => Array.from(document.querySelectorAll('tbody tr')).map((row) => row.textContent ?? '');
    expect(rows().filter((text) => text.includes('Cancelled'))).toHaveLength(2);

    fetcher.pending.forEach((request) => request.release());
    await waitFor(() => expect(rows().filter((text) => text.includes('Test succeeded'))).toHaveLength(3));
    expect(fetcher.pending.map((request) => request.url)).toEqual([
      '/api/providers/p1/test', '/api/providers/p1/test', '/api/providers/p1/test',
    ]);
    expect(rows()).toHaveLength(5);
    within(screen.getByRole('table')).getByText('m5');
  });

  it('keeps its clicks away from a clickable ancestor', async () => {
    // The card renders the button and opens the dialog itself; a dialog portaled from inside it must not bubble
    // its clicks up React's tree, or "Start test" would also open the provider behind it.
    const fetcher = stubFetch(['model-x']);
    const opened = vi.fn();
    show(
      <div onClick={opened}>
        <ProviderTestDialog provider={provider} open onOpenChange={() => {}} />
      </div>,
    );
    await waitFor(() => expect((screen.getByLabelText('Test model') as HTMLInputElement).value).toBe('model-x'));
    fireEvent.click(screen.getByRole('button', { name: 'Start test' }));
    await waitFor(() => expect(fetcher.pending).toHaveLength(1));
    fetcher.pending[0]!.release();
    await screen.findByText('OK');
    expect(opened).not.toHaveBeenCalled();
  });
});
