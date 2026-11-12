import { useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';

import { api } from '../api/client';
import { Spinner } from '../components/ui/controls';
import { useI18n } from '../i18n';
import type { UiNode, WidthClass } from './protocol';
import { NodeView, RenderProvider, type RenderContext } from './render';

/** One scene of the UI scene set (tests/fixtures/plugin-ui), as GET /api/plugins/v2/ui/scenes serves it (normalised). */
export interface GalleryScene {
  id: string;
  component: string | null;
  state: string;
  tree: UiNode;
  warnings: string[];
}

type Theme = 'light' | 'dark';
type Surface = 'page' | 'sheet' | 'tray';

const SURFACE_WIDTH: Record<Surface, string> = { page: '100%', sheet: '520px', tray: '340px' };

/**
 * A scene rendered with no session: the same NodeView a plugin view uses, over a context whose actions only report what
 * they would have sent (06 §2). What the visual comparison captures (`?scene=<id>` shows one alone).
 */
export function SceneView({
  scene,
  width,
  onSend,
}: {
  scene: GalleryScene;
  width: WidthClass;
  onSend?: (line: string) => void;
}) {
  const { locale } = useI18n();
  const local = useRef<Record<string, unknown>>({});
  const editedAt = useRef<Record<string, number>>({});
  const ack = useRef(0);
  const [version, setVersion] = useState(0);
  const ctx: RenderContext = {
    locale,
    width,
    weekStart: locale.startsWith('zh') ? 1 : 0,
    local,
    editedAt,
    ack,
    collections: {},
    fieldErrors: {},
    busyKey: null,
    localVersion: version,
    bump: () => setVersion((v) => v + 1),
    send: (action, args) => onSend?.(`${action} ${JSON.stringify(args ?? null)}`),
    setLocal: (name, value) => {
      local.current[name] = value;
      setVersion((v) => v + 1);
    },
    link: (href) => onSend?.(`link ${href}`),
    upload: async () => null,
    pluginId: 'gallery',
  };
  return (
    <RenderProvider value={ctx}>
      <NodeView node={scene.tree} />
    </RenderProvider>
  );
}

/** Sets the console's theme while the gallery previews the other one, and puts it back after. */
export function usePreviewTheme(theme: Theme | null) {
  useEffect(() => {
    if (!theme) return;
    const root = document.documentElement;
    const before = root.getAttribute('data-theme');
    root.setAttribute('data-theme', theme);
    return () => {
      if (before === null) root.removeAttribute('data-theme');
      else root.setAttribute('data-theme', before);
    };
  }, [theme]);
}

/**
 * The component gallery (/plugins/dev/gallery): every component of the current UI version in each of its states, with the
 * tree that produces it, at a chosen width class, theme and surface. The scenes are the ones both shells are compared on.
 */
export function GalleryPage() {
  const { t } = useI18n();
  const [params, setParams] = useSearchParams();
  const [scenes, setScenes] = useState<GalleryScene[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [width, setWidth] = useState<WidthClass>('regular');
  const [theme, setTheme] = useState<Theme | null>((params.get('theme') as Theme | null) ?? null);
  const [surface, setSurface] = useState<Surface>('page');
  const [filter, setFilter] = useState('');
  const [log, setLog] = useState<string[]>([]);
  usePreviewTheme(theme);

  useEffect(() => {
    api<GalleryScene[]>('GET', '/api/plugins/v2/ui/scenes').then(setScenes, (e: unknown) =>
      setError(String((e as Error)?.message ?? e)),
    );
  }, []);

  const only = params.get('scene');
  const groups = useMemo(() => {
    const m = new Map<string, GalleryScene[]>();
    for (const s of scenes ?? []) {
      const g = s.component ?? t('pluginUi.gallery.patterns');
      m.set(g, [...(m.get(g) ?? []), s]);
    }
    return [...m.entries()];
  }, [scenes, t]);
  const selected = params.get('component') ?? groups[0]?.[0];

  if (error) return <div className="p-4 text-[13px] text-[var(--red)]">{error}</div>;
  if (!scenes) return <Spinner lines={6} />;

  // a single scene, nothing around it: what scripts/visual captures
  if (only) {
    const scene = scenes.find((s) => s.id === only);
    return (
      <div
        data-scene={only}
        className="p-4"
        style={{ width: params.get('surface') === 'tray' ? 340 : undefined }}
      >
        {scene ? (
          <SceneView
            scene={scene}
            width={(params.get('width') as WidthClass | null) ?? 'regular'}
          />
        ) : (
          <span>{only}</span>
        )}
      </div>
    );
  }

  const shown = groups.filter(([g]) => g.toLowerCase().includes(filter.toLowerCase()));
  const current = groups.find(([g]) => g === selected)?.[1] ?? [];
  const seg = <T extends string>(label: string, value: T, values: T[], set: (v: T) => void) => (
    <div className="flex items-center gap-1 text-[12px]" role="group" aria-label={label}>
      <span className="text-[var(--text-muted)]">{label}</span>
      {values.map((v) => (
        <button
          key={v}
          type="button"
          aria-pressed={v === value}
          onClick={() => set(v)}
          className={`rounded px-2 py-0.5 ${v === value ? 'bg-[var(--surface-muted)] font-medium' : ''}`}
        >
          {v}
        </button>
      ))}
    </div>
  );

  return (
    <div className="flex min-h-0 gap-4">
      <aside className="w-48 shrink-0">
        <input
          className="mb-2 w-full rounded border border-[var(--border-subtle)] bg-transparent px-2 py-1 text-[12px]"
          placeholder={t('pluginUi.gallery.filter')}
          aria-label={t('pluginUi.gallery.filter')}
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
        <nav className="flex max-h-[75vh] flex-col overflow-y-auto text-[12px]">
          {shown.map(([g, list]) => (
            <button
              key={g}
              type="button"
              onClick={() => setParams({ component: g })}
              className={`flex justify-between rounded px-2 py-1 text-left ${g === selected ? 'bg-[var(--surface-muted)] font-medium' : ''}`}
            >
              <span>{g}</span>
              <span className="text-[var(--text-muted)]">{list.length}</span>
            </button>
          ))}
        </nav>
      </aside>
      <main className="min-w-0 flex-1">
        <div className="mb-4 flex flex-wrap items-center gap-4">
          {seg(
            t('pluginUi.gallery.width'),
            width,
            ['compact', 'regular', 'wide'] as WidthClass[],
            setWidth,
          )}
          {seg(
            t('pluginUi.gallery.theme'),
            theme ?? 'light',
            ['light', 'dark'] as Theme[],
            setTheme,
          )}
          {seg(
            t('pluginUi.gallery.surface'),
            surface,
            ['page', 'sheet', 'tray'] as Surface[],
            setSurface,
          )}
        </div>
        <div className="grid gap-4">
          {current.map((scene) => (
            <section
              key={scene.id}
              className="rounded-[var(--radius-panel)] border border-[var(--border-subtle)]"
            >
              <header className="flex items-center justify-between border-b border-[var(--border-subtle)] px-3 py-1.5 text-[12px]">
                <span className="font-medium">{scene.state}</span>
                <code className="text-[11px] text-[var(--text-muted)]">{scene.id}</code>
              </header>
              <div className="p-4" style={{ maxWidth: SURFACE_WIDTH[surface] }}>
                <SceneView
                  scene={scene}
                  width={width}
                  onSend={(line) => setLog((l) => [`${scene.id}: ${line}`, ...l].slice(0, 20))}
                />
              </div>
              <details className="border-t border-[var(--border-subtle)] px-3 py-1.5 text-[11px]">
                <summary className="cursor-pointer text-[var(--text-muted)]">
                  {t('pluginUi.gallery.code')}
                </summary>
                <pre className="mt-1 max-h-64 overflow-auto font-mono">
                  {JSON.stringify(scene.tree, null, 2)}
                </pre>
              </details>
            </section>
          ))}
        </div>
        {log.length > 0 && (
          <div
            className="mt-4 rounded border border-[var(--border-subtle)] p-2 font-mono text-[11px]"
            aria-label={t('pluginUi.gallery.events')}
          >
            {log.map((l, i) => (
              <div key={i}>{l}</div>
            ))}
          </div>
        )}
      </main>
    </div>
  );
}
