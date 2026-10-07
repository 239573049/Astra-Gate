import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, URL as NodeURL } from 'node:url';
import { cleanup, render } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { CLIENT_ORDER, ClientGlyph, ClientIcon, ProviderIcon } from '../components/icons';

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
    LmStudio: stub('lm-studio'),
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
    Zhipu: stub('zhipu'),
  };
});

const assets = fileURLToPath(new NodeURL('../assets/clients/', import.meta.url));

// Provider icon ids served by a bundled vendor image rather than a @lobehub/icons component.
// Kept in sync with LOCAL_PROVIDER_LOGOS in ../components/icons by the assertions below.
const LOCAL_LOGO_ICONS = new Set(['routin']);

afterEach(cleanup);

describe('client brand icons', () => {
  // Vendor marks from @lobehub/icons; the two Claude clients share a mark and differ by a corner badge.
  const expected: Record<string, { brand: string; mark: string; badge: boolean }> = {
    codex: { brand: 'openai', mark: 'mono', badge: false },
    'claude-code': { brand: 'claude', mark: 'color', badge: true },
    'gemini-cli': { brand: 'gemini', mark: 'color', badge: false },
    opencode: { brand: 'opencode', mark: 'mono', badge: false },
    'claude-desktop': { brand: 'claude', mark: 'color', badge: true },
    'grok-build': { brand: 'grok', mark: 'mono', badge: false },
    pi: { brand: 'pi', mark: 'mono', badge: false },
    'hermes-agent': { brand: 'hermes-agent', mark: 'mono', badge: false },
    'minimax-code': { brand: 'minimax', mark: 'color', badge: false },
    'copilot-cli': { brand: 'github-copilot', mark: 'mono', badge: false },
  };

  it('renders the vendor mark for every client, colored where the library has one', () => {
    expect(Object.keys(expected).sort()).toEqual([...CLIENT_ORDER].sort());
    for (const kind of CLIENT_ORDER) {
      const { container, unmount } = render(<ClientGlyph kind={kind} size={18} />);
      const svg = container.querySelector('svg[data-testid^="lobe-"]');
      expect(svg?.getAttribute('data-testid'), kind).toBe(`lobe-${expected[kind]!.brand}`);
      expect(svg?.getAttribute('data-variant'), kind).toBe(expected[kind]!.mark);
      expect(svg?.getAttribute('data-size'), kind).toBe('18');
      expect(container.querySelector(`[data-badge="${kind}"]`) !== null, kind).toBe(expected[kind]!.badge);
      expect(container.querySelector('img'), kind).toBeNull();
      expect(container.textContent, kind).toBe('');
      unmount();
    }
  });

  it('renders the vendor avatar tile for larger client icons', () => {
    for (const kind of CLIENT_ORDER) {
      const { container, unmount } = render(<ClientIcon kind={kind} size={36} />);
      const svg = container.querySelector('svg[data-testid^="lobe-"]');
      expect(svg?.getAttribute('data-testid'), kind).toBe(`lobe-${expected[kind]!.brand}`);
      expect(svg?.getAttribute('data-variant'), kind).toBe('avatar');
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

  it('falls back to the colored-initials tile for unknown icon ids', () => {
    const { container } = render(<ProviderIcon name="Acme AI" icon="unknown-brand" colorKey="acme" />);
    expect(container.querySelector('svg')).toBeNull();
    expect(container.textContent).toBe('AA');
  });

  it('ships verified provenance for every locally bundled provider mark', () => {
    const provenance = JSON.parse(
      fs.readFileSync(path.join(assets, '../providers/sources.json'), 'utf8'),
    ) as { sources: Record<string, { file: string; source: string; sha256: string }> };
    expect(Object.keys(provenance.sources).sort()).toEqual([...LOCAL_LOGO_ICONS].sort());
    for (const icon of LOCAL_LOGO_ICONS) {
      const source = provenance.sources[icon];
      expect(new NodeURL(source.source).hostname).toBe('routin.ai');
      const bytes = fs.readFileSync(path.join(assets, '../providers', source.file));
      expect(bytes.length, source.file).toBeGreaterThan(100);
      expect(createHash('sha256').update(bytes).digest('hex'), source.file).toBe(source.sha256);
      expect(bytes.subarray(0, 8).toString('hex'), source.file).toBe('89504e470d0a1a0a'); // PNG
    }
  });
});
