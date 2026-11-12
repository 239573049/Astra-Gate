import { cleanup, render } from '@testing-library/react';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';
import { SceneView, type GalleryScene } from '../plugin-ui/Gallery';

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

// the scene set both shells are compared on (tests/fixtures/plugin-ui, bundled for the server)
const scenes = (
  JSON.parse(
    readFileSync(join(process.cwd(), '../src/Astra.Plugins/V2/Ui/scenes.json'), 'utf8'),
  ) as { scenes: GalleryScene[] }
).scenes;

describe('component gallery scenes', () => {
  it('covers the scene set', () => {
    expect(scenes.length).toBeGreaterThan(200);
  });

  it.each(scenes.map((s) => [s.id, s] as const))('%s renders', (_id, scene) => {
    vi.stubGlobal('ResizeObserver', NoopResizeObserver);
    vi.stubGlobal(
      'IntersectionObserver',
      class {
        observe() {}
        disconnect() {}
        unobserve() {}
      },
    );
    const { container } = render(
      <MemoryRouter>
        <QueryClientProvider
          client={
            new QueryClient({ defaultOptions: { queries: { retry: false, enabled: false } } })
          }
        >
          <I18nProvider>
            <FeedbackProvider>
              <SceneView scene={{ ...scene, warnings: [] }} width="regular" />
            </FeedbackProvider>
          </I18nProvider>
        </QueryClientProvider>
      </MemoryRouter>,
    );
    // closed overlays (Sheet / Dialog with open: false) render nothing; everything else shows something
    if (!['Sheet', 'Dialog', 'Spacer'].includes(scene.component ?? ''))
      expect(container.innerHTML.length).toBeGreaterThan(0);
  });
});
