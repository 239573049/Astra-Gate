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
    Grok: stub('grok'), HermesAgent: stub('hermes-agent'), Kimi: stub('kimi'), LmStudio: stub('lm-studio'), Meta: stub('meta'), Qoder: stub('qoder'), Minimax: stub('minimax'),
    Moonshot: stub('moonshot'), Ollama: stub('ollama'), OpenAI: stub('openai'), OpenCode: stub('opencode'),
    OpenRouter: stub('openrouter'), Pi: stub('pi'), Qwen: stub('qwen'), SiliconCloud: stub('siliconcloud'),
    Volcengine: stub('volcengine'), XAI: stub('xai'), XiaomiMiMo: stub('xiaomi-mimo'), ZAI: stub('zai'), Zhipu: stub('zhipu'),
    Baichuan: stub('baichuan'), ChatGLM: stub('chatglm'), Cohere: stub('cohere'), Doubao: stub('doubao'), Gemma: stub('gemma'), Hunyuan: stub('hunyuan'), LongCat: stub('longcat'), Mistral: stub('mistral'), Stepfun: stub('stepfun'), Wenxin: stub('wenxin'), Yi: stub('yi'),
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
function stubFetch(models: string[], done: unknown = DONE) {
  const pending: { url: string; body: unknown; release: () => void; closed: Promise<void> }[] = [];
  let inFlight = 0;
  let peak = 0;
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.endsWith('/models')) {
      return new Response(JSON.stringify(models.map(model)), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }
    const { response, release, closed } = makeStream(done);
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

  it('offers generate / edit for an image model and sends the chosen ability on the Images API', async () => {
    const fetcher = stubFetch(['gpt-5', 'gpt-image-2.5-flare']);
    show(<ProviderTestDialog provider={provider} open onOpenChange={() => {}} />);
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Test model' }) as HTMLInputElement).value).toBe('gpt-5'));
    // A chat model: no image controls.
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Show model list' }));
    fireEvent.mouseDown(await screen.findByRole('button', { name: 'gpt-image-2.5-flare' }));
    // Picking the image model switches the test to the Images API and shows the ability switch.
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    fireEvent.click(screen.getByRole('button', { name: 'Start test' }));
    await waitFor(() => expect(fetcher.pending).toHaveLength(1));
    expect(fetcher.pending[0]!.body).toMatchObject({ modelId: 'gpt-image-2.5-flare', protocol: 'openai-images', imageOperation: 'edit' });
    expect(fetcher.pending[0]!.body).not.toHaveProperty('stream');
    fetcher.pending[0]!.release();
    await screen.findByText('OK');
    fireEvent.click(screen.getByRole('button', { name: 'Show model list' }));
    // Back to a chat model: a chat request again.
    fireEvent.mouseDown(await screen.findByRole('button', { name: 'gpt-5' }));
    fireEvent.click((await screen.findAllByRole('button', { name: 'Test again' }))[0]!);
    await waitFor(() => expect(fetcher.pending).toHaveLength(2));
    expect(fetcher.pending[1]!.body).toMatchObject({ modelId: 'gpt-5', protocol: 'openai-chat', stream: true });
    fetcher.pending[1]!.release();
  });

  it('uploads the picture picked for an edit test and refuses one it cannot send', async () => {
    const fetcher = stubFetch(['gpt-image-2.5-flare']);
    show(<ProviderTestDialog provider={provider} open onOpenChange={() => {}} />);
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Test model' }) as HTMLInputElement).value).toBe('gpt-image-2.5-flare'));
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    // Nothing picked yet: the built-in picture is described, and the request carries no picture.
    expect(screen.getByText('Built-in red circle (default)')).toBeInTheDocument();
    const input = screen.getByLabelText('Test picture', { selector: 'input' }) as HTMLInputElement;

    // A format the Images API does not edit is refused on the spot.
    fireEvent.change(input, { target: { files: [new File(['x'], 'notes.txt', { type: 'text/plain' })] } });
    expect(await screen.findByText('Only PNG, JPEG and WebP pictures are supported')).toBeInTheDocument();

    fireEvent.change(input, { target: { files: [new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], 'mine.png', { type: 'image/png' })] } });
    expect(await screen.findByText('mine.png')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Start test' }));
    await waitFor(() => expect(fetcher.pending).toHaveLength(1));
    expect(fetcher.pending[0]!.body).toMatchObject({ protocol: 'openai-images', imageOperation: 'edit', imageInput: 'iVBORw==' });
    fetcher.pending[0]!.release();
    await screen.findByText('OK');

    // "Use default" goes back to the built-in picture: no imageInput.
    fireEvent.click(screen.getByRole('button', { name: 'Use default' }));
    fireEvent.click((await screen.findAllByRole('button', { name: 'Test again' }))[0]!);
    await waitFor(() => expect(fetcher.pending).toHaveLength(2));
    expect(fetcher.pending[1]!.body).not.toHaveProperty('imageInput');
    fetcher.pending[1]!.release();
  });

  it('shows the pictures of a single image test, and asks for none in a batch', async () => {
    const fetcher = stubFetch(['gpt-image-2.5-flare'], { ...DONE, text: 'Generated 1 image, png', images: [{ mime: 'image/png', data: 'iVBORw==' }] });
    show(<ProviderTestDialog provider={provider} open onOpenChange={() => {}} />);
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Test model' }) as HTMLInputElement).value).toBe('gpt-image-2.5-flare'));
    fireEvent.click(screen.getByRole('button', { name: 'Start test' }));
    await waitFor(() => expect(fetcher.pending).toHaveLength(1));
    expect(fetcher.pending[0]!.body).toMatchObject({ protocol: 'openai-images', imagePreview: true });
    fetcher.pending[0]!.release();
    const picture = (await screen.findByAltText('Result picture 1')) as HTMLImageElement;
    expect(picture.src).toBe('data:image/png;base64,iVBORw==');
    expect(screen.getByRole('link', { name: 'Save picture' })).toHaveAttribute('download', 'astra-test-1.png');
    expect(screen.getByText('Generated 1 image, png')).toBeInTheDocument();

    // The batch asks for no pictures: a table of many results stays a table.
    fireEvent.click(screen.getByRole('button', { name: 'Batch' }));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'gpt-image-2.5-flare' }));
    fireEvent.click(screen.getByRole('button', { name: 'Test 1 models' }));
    await waitFor(() => expect(fetcher.pending).toHaveLength(2));
    expect(fetcher.pending[1]!.body).toMatchObject({ protocol: 'openai-images', imagePreview: false });
    fetcher.pending[1]!.release();
  });

  it('sends every model of a batch to the interface it needs', async () => {
    const fetcher = stubFetch(['gpt-5', 'gpt-image-2.5-flare', 'dall-e-3']);
    show(<ProviderTestDialog provider={provider} open onOpenChange={() => {}} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Batch' }));
    // Settings: the ability applies to the image models; the batch protocol box has no "OpenAI Images" choice.
    fireEvent.click(await screen.findByRole('button', { name: 'Test configuration' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
    for (const id of ['gpt-5', 'gpt-image-2.5-flare', 'dall-e-3']) fireEvent.click(await screen.findByRole('checkbox', { name: id }));
    fireEvent.click(screen.getByRole('button', { name: 'Test 3 models' }));
    await waitFor(() => expect(fetcher.pending).toHaveLength(3));
    const byModel = Object.fromEntries(fetcher.pending.map((r) => [(r.body as { modelId: string }).modelId, r.body]));
    expect(byModel['gpt-5']).toMatchObject({ protocol: 'openai-chat', stream: true });
    expect(byModel['gpt-5']).not.toHaveProperty('imageOperation');
    expect(byModel['gpt-image-2.5-flare']).toMatchObject({ protocol: 'openai-images', imageOperation: 'edit' });
    expect(byModel['dall-e-3']).toMatchObject({ protocol: 'openai-images', imageOperation: 'edit' });
    fetcher.pending.forEach((r) => r.release());
    await waitFor(() => expect(within(screen.getByRole('table')).getAllByText('Edit')).toHaveLength(2));
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
