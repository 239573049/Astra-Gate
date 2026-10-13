import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, URL as NodeURL } from 'node:url';
import { cleanup, render } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { CLIENT_ORDER, ClientGlyph, ClientIcon, ModelGlyph, ProviderIcon } from '../components/icons';

// The real @lobehub/icons barrel transitively imports JSON with import attributes that Node ESM
// (vitest) rejects. Stub each brand component: the mono mark itself, `.Color` and `.Avatar`.
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

const assets = fileURLToPath(new NodeURL('../assets/clients/', import.meta.url));

// Provider icon ids served by a bundled vendor image rather than a @lobehub/icons component.
// Kept in sync with LOCAL_PROVIDER_LOGOS in ../components/icons by the assertions below.
const LOCAL_LOGO_ICONS = new Set(['nextcowork', 'routin']);

afterEach(cleanup);

describe('client brand icons', () => {
  // Vendor marks from @lobehub/icons; clients sharing a vendor mark (Claude, Copilot) differ by a corner badge.
  // brand null = no vendor mark in the library: a locally bundled vendor icon instead (data-client-mark="logo").
  const expected: Record<string, { brand: string | null; mark: string; badge: boolean }> = {
    codex: { brand: 'openai', mark: 'mono', badge: false },
    'claude-code': { brand: 'claude', mark: 'color', badge: true },
    'gemini-cli': { brand: 'gemini', mark: 'color', badge: false },
    opencode: { brand: 'opencode', mark: 'mono', badge: false },
    'claude-desktop': { brand: 'claude', mark: 'color', badge: true },
    'grok-build': { brand: 'grok', mark: 'mono', badge: false },
    pi: { brand: 'pi', mark: 'mono', badge: false },
    'hermes-agent': { brand: 'hermes-agent', mark: 'mono', badge: false },
    'minimax-code': { brand: 'minimax', mark: 'color', badge: false },
    'copilot-cli': { brand: 'github-copilot', mark: 'mono', badge: true },
    'vscode-copilot': { brand: 'github-copilot', mark: 'mono', badge: true },
    crush: { brand: null, mark: 'logo', badge: false },
    'qwen-code': { brand: 'qwen', mark: 'color', badge: true },
    droid: { brand: null, mark: 'logo', badge: false },
    'kimi-code': { brand: 'kimi', mark: 'color', badge: true },
    zed: { brand: null, mark: 'logo', badge: false },
    'vscode-insiders': { brand: null, mark: 'logo', badge: false },
    vscodium: { brand: null, mark: 'logo', badge: false },
    omp: { brand: null, mark: 'logo', badge: false },
    'mimo-code': { brand: 'xiaomi-mimo', mark: 'mono', badge: true },
    'deepseek-harness': { brand: 'deepseek', mark: 'color', badge: true },
    workbuddy: { brand: null, mark: 'logo', badge: false },
    'workbuddy-ai': { brand: null, mark: 'logo', badge: true },
    'muse-code': { brand: 'meta', mark: 'color', badge: true },
    nextcowork: { brand: null, mark: 'logo', badge: false },
    paseo: { brand: null, mark: 'logo', badge: false },
  };

  it('renders the vendor mark for every client, colored where the library has one', () => {
    expect(Object.keys(expected).sort()).toEqual([...CLIENT_ORDER].sort());
    for (const kind of CLIENT_ORDER) {
      const { container, unmount } = render(<ClientGlyph kind={kind} size={18} />);
      const brand = expected[kind]!.brand;
      if (brand === null) {
        expect(container.querySelector('svg[data-testid^="lobe-"]'), kind).toBeNull();
        const img = container.querySelector('img[data-client-mark="logo"]');
        expect(img, kind).not.toBeNull();
        expect(img!.getAttribute('src'), kind).toMatch(/\.(png|svg)$|^data:image\//);
        expect(img!.getAttribute('src'), kind).not.toMatch(/^https?:/);
      } else {
        const svg = container.querySelector('svg[data-testid^="lobe-"]');
        expect(svg?.getAttribute('data-testid'), kind).toBe(`lobe-${brand}`);
        expect(svg?.getAttribute('data-variant'), kind).toBe(expected[kind]!.mark);
        expect(svg?.getAttribute('data-size'), kind).toBe('18');
      }
      expect(container.querySelector(`[data-badge="${kind}"]`) !== null, kind).toBe(expected[kind]!.badge);
      if (brand !== null) expect(container.querySelector('img'), kind).toBeNull();
      expect(container.textContent, kind).toBe('');
      unmount();
    }
  });

  it('ships verified provenance for every locally bundled client icon', () => {
    const provenance = JSON.parse(fs.readFileSync(path.join(assets, 'sources.json'), 'utf8')) as {
      sources: Record<string, { file: string; sha256: string }>;
    };
    for (const kind of CLIENT_ORDER.filter((k) => expected[k]!.brand === null)) {
      const source = provenance.sources[kind];
      expect(source, kind).toBeDefined();
      const bytes = fs.readFileSync(path.join(assets, source!.file));
      expect(createHash('sha256').update(bytes).digest('hex'), source!.file).toBe(source!.sha256);
    }
  });

  it('renders the vendor avatar tile for larger client icons', () => {
    for (const kind of CLIENT_ORDER) {
      const { container, unmount } = render(<ClientIcon kind={kind} size={36} />);
      const brand = expected[kind]!.brand;
      if (brand === null) {
        expect(container.querySelector('img[data-client-mark="logo"]'), kind).not.toBeNull();
      } else {
        const svg = container.querySelector('svg[data-testid^="lobe-"]');
        expect(svg?.getAttribute('data-testid'), kind).toBe(`lobe-${brand}`);
        expect(svg?.getAttribute('data-variant'), kind).toBe('avatar');
      }
      expect(container.querySelector(`[data-badge="${kind}"]`) !== null, kind).toBe(expected[kind]!.badge);
      unmount();
    }
  });
});

describe('provider brand icons', () => {
  it('renders the official brand avatar for every catalog icon id', () => {
    const catalog = JSON.parse(
      fs.readFileSync(path.join(assets, '../../../../src/Astra.Providers/Templates/catalog.json'), 'utf8'),
    ) as { id: string; icon: string }[];
    expect(catalog.length).toBeGreaterThan(10);
    for (const tpl of catalog) {
      const { container, unmount } = render(<ProviderIcon name={tpl.id} icon={tpl.icon} />);
      if (tpl.icon === 'custom') {
        expect(container.querySelector('svg[data-testid^="lobe-"]'), tpl.id).toBeNull();
      } else if (LOCAL_LOGO_ICONS.has(tpl.icon)) {
        // Vendors @lobehub/icons does not ship: a locally bundled mark, never a remote URL.
        const img = container.querySelector('img');
        expect(img, tpl.id).not.toBeNull();
        expect(img!.getAttribute('src'), tpl.id).toMatch(/\.(png|svg)$/);
        expect(img!.getAttribute('src'), tpl.id).not.toMatch(/^https?:/);
        expect(container.querySelector('svg[data-testid^="lobe-"]'), tpl.id).toBeNull();
      } else {
        // Every non-custom template must resolve to a brand avatar component (id → component
        // translation itself is covered by the visual mapping in icons.tsx).
        expect(container.querySelector('svg[data-testid^="lobe-"]'), tpl.id).not.toBeNull();
      }
      unmount();
    }
  });

  it('renders plugin providers by magpie icon name or their own picture', () => {
    // plugin providers carry the market listing's icon name (magpie's), or an https/data:image URL of their own
    const workbuddy = render(<ProviderIcon name="WorkBuddy AI" icon="workbuddy-color" />);
    expect(workbuddy.container.querySelector('img')!.getAttribute('src')).toMatch(/workbuddy\.svg$/);
    workbuddy.unmount();
    const qoder = render(<ProviderIcon name="Qoder CN" icon="qoder" />);
    expect(qoder.container.querySelector('svg[data-testid^="lobe-"]')).not.toBeNull();
    qoder.unmount();
    const own = render(<ProviderIcon name="Own" icon="https://example.com/own.png" />);
    expect(own.container.querySelector('img')!.getAttribute('src')).toBe('https://example.com/own.png');
  });

  it('falls back to the colored-initials tile for unknown icon ids', () => {
    const { container } = render(<ProviderIcon name="Acme AI" icon="unknown-brand" colorKey="acme" />);
    expect(container.querySelector('svg')).toBeNull();
    expect(container.textContent).toBe('AA');
  });

  it('ships verified provenance for every locally bundled provider mark', () => {
    const provenance = JSON.parse(
      fs.readFileSync(path.join(assets, '../providers/sources.json'), 'utf8'),
    ) as { sources: Record<string, { file: string; source: string; website: string; sha256: string }> };
    expect(Object.keys(provenance.sources).sort()).toEqual([...LOCAL_LOGO_ICONS].sort());
    for (const icon of LOCAL_LOGO_ICONS) {
      const source = provenance.sources[icon]!;
      // Downloaded from the vendor's own site.
      expect(new NodeURL(source.source).hostname, icon).toBe(new NodeURL(source.website).hostname);
      const bytes = fs.readFileSync(path.join(assets, '../providers', source.file));
      expect(bytes.length, source.file).toBeGreaterThan(100);
      expect(createHash('sha256').update(bytes).digest('hex'), source.file).toBe(source.sha256);
      expect(bytes.subarray(0, 8).toString('hex'), source.file).toBe('89504e470d0a1a0a'); // PNG
    }
  });
});

describe('model glyphs', () => {
  it('guesses the model family from the id and falls back to a neutral box', () => {
    const cases: [string, string | null][] = [
      ['claude-sonnet-4-5', 'claude'],
      ['us.anthropic.claude-opus-4-1', 'claude'],
      ['gpt-5-codex', 'openai'],
      ['o3-mini', 'openai'],
      ['gemini-2.5-pro', 'gemini'],
      ['gemma-3-27b', 'gemma'],
      ['deepseek-chat', 'deepseek'],
      ['qwen3-coder-plus', 'qwen'],
      ['kimi-k2', 'kimi'],
      ['glm-4.6', 'chatglm'],
      ['grok-4', 'grok'],
      ['qfmodel', null],
    ];
    for (const [model, brand] of cases) {
      const { container, unmount } = render(<ModelGlyph model={model} />);
      const mark = container.querySelector('[data-testid^="lobe-"]');
      expect(mark?.getAttribute('data-testid') ?? null, model).toBe(brand && `lobe-${brand}`);
      expect(container.querySelector('[data-model-mark]')?.getAttribute('data-model-mark')).toBe(brand ? 'brand' : 'none');
      unmount();
    }
  });
});

describe('regional provider variants', () => {
  it('badges the suffixed build of a shared mark and leaves the plain one alone', () => {
    const cases: [string, string, string | null][] = [
      ['WorkBuddy', 'workbuddy-color', null],
      ['WorkBuddy AI', 'workbuddy-color', 'intl'],
      ['Qoder', 'qoder', null],
      ['Qoder CN', 'qoder', 'cn'],
      ['Moonshot AI', 'kimi', null],
    ];
    for (const [name, icon, region] of cases) {
      const { container, unmount } = render(<ProviderIcon name={name} icon={icon} />);
      expect(container.querySelector('[data-region-badge]')?.getAttribute('data-region-badge') ?? null, name).toBe(region);
      unmount();
    }
  });
});
