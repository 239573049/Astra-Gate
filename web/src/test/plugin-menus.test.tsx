import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { PluginV2Contributions } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';
import { ContributionsContext } from '../plugin-ui/contributions';
import { PluginMenu } from '../plugin-ui/menus';

// jsdom has no ResizeObserver (the menu's popper wants one)
class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.unstubAllGlobals();
});

const contributions = (menus: PluginV2Contributions['menus']): PluginV2Contributions => ({
  pages: [], slots: [], menus, commands: [], settings: [], hooks: [], jobs: [],
});

function show(c: PluginV2Contributions | undefined) {
  localStorage.setItem('astra.locale', 'en');
  vi.stubGlobal('ResizeObserver', NoopResizeObserver);
  const calls: { url: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({ url: String(input), body: init?.body ? JSON.parse(String(init.body)) : undefined });
      return new Response(JSON.stringify({ result: 'flagged' }), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
  render(
    <I18nProvider>
      <FeedbackProvider>
        <ContributionsContext.Provider value={c}>
          <PluginMenu menu="request.row" args={{ requestId: 'r1' }} />
        </ContributionsContext.Provider>
      </FeedbackProvider>
    </I18nProvider>,
  );
  return calls;
}

describe('plugin menus (ui.menus)', () => {
  it('shows nothing when no plugin adds to the menu, or outside the shell', () => {
    show(undefined);
    expect(screen.queryByRole('button', { name: 'Plugin actions' })).toBeNull();
    cleanup();
    show(contributions([{ plugin: 'acme.x', menu: 'token.row', command: 'c', title: 'Other menu' }]));
    expect(screen.queryByRole('button', { name: 'Plugin actions' })).toBeNull();
  });

  it("runs the plugin's command with the row's args and shows what it returned", async () => {
    const calls = show(contributions([{ plugin: 'acme.x', menu: 'request.row', command: 'flag', title: 'Flag request' }]));
    const trigger = screen.getByRole('button', { name: 'Plugin actions' });
    fireEvent.keyDown(trigger, { key: 'Enter' });
    fireEvent.click(await screen.findByText('Flag request'));
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0].url).toContain('/api/plugins/v2/acme.x/commands/flag');
    expect(calls[0].body).toEqual({ args: { requestId: 'r1' } });
    expect(await screen.findByText('flagged')).toBeTruthy();
  });
});

describe('command palette', () => {
  it('lists console pages, plugin pages, palette commands and bound commands', async () => {
    const { paletteItems, filterPalette } = await import('../plugin-ui/palette');
    const items = paletteItems(
      {
        pages: [{ plugin: 'acme.x', id: 'home', title: 'Acme home', nav: 'tools', routes: [] }],
        slots: [],
        menus: [{ plugin: 'acme.x', menu: 'command.palette', command: 'sync', title: 'Sync now' }],
        commands: [
          { plugin: 'acme.x', id: 'sync', title: 'Sync now', keybinding: 'mod+shift+s' },
          { plugin: 'acme.x', id: 'clear', title: 'Clear', keybinding: 'alt+c' },
          { plugin: 'acme.x', id: 'hidden', title: 'Hidden' },
        ],
        settings: [], hooks: [], jobs: [],
      },
      (k) => k,
    );
    expect(items.find((i) => i.id === 'nav:tokens')?.run).toEqual({ kind: 'path', path: '/tokens' });
    expect(items.find((i) => i.id === 'page:acme.x/home')?.run).toEqual({ kind: 'path', path: '/plugins/v2/acme.x/home' });
    expect(items.find((i) => i.id === 'cmd:acme.x:sync')?.keybinding).toBe('mod+shift+s');
    expect(items.some((i) => i.id === 'cmd:acme.x:clear')).toBe(true);
    expect(items.some((i) => i.id === 'cmd:acme.x:hidden')).toBe(false);
    expect(filterPalette(items, 'sync acme').map((i) => i.id)).toEqual(['cmd:acme.x:sync']);
  });

  it('parses and matches keybindings per platform', async () => {
    const { parseKeybinding, matchesChord, chordLabel } = await import('../plugin-ui/palette');
    const chord = parseKeybinding('mod+shift+g')!;
    const ev = (o: Partial<KeyboardEvent>) => ({ key: 'g', code: 'KeyG', metaKey: false, ctrlKey: false, altKey: false, shiftKey: false, ...o });
    expect(matchesChord(ev({ metaKey: true, shiftKey: true }), chord, true)).toBe(true);
    expect(matchesChord(ev({ ctrlKey: true, shiftKey: true }), chord, true)).toBe(false);
    expect(matchesChord(ev({ ctrlKey: true, shiftKey: true }), chord, false)).toBe(true);
    expect(matchesChord(ev({ ctrlKey: true }), chord, false)).toBe(false);
    expect(parseKeybinding('g')).toBeNull(); // needs a modifier
    expect(parseKeybinding('hyper+g')).toBeNull();
    expect(chordLabel('mod+shift+g', true)).toBe('⌘⇧G');
    expect(chordLabel('mod+shift+g', false)).toBe('Ctrl+Shift+G');
  });
});
