import { Bug } from 'lucide-react';
import { useEffect, useState } from 'react';

import { api } from '../api/client';
import type { PluginV2Detail } from '../api/types';
import { Sheet } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import type { WidthClass } from './protocol';

/** What GET /api/plugins/v2/ui/sessions/{id}/inspect returns for a dev plugin's session. */
export interface Inspection {
  plugin: string;
  view: string;
  rev: number;
  tree: unknown;
  state: unknown;
  local: unknown;
  actions: string[];
  warnings: string[];
  renderMicros: number;
  timeline: {
    at: string;
    action: string;
    seq: number;
    queueMs: number;
    handlerMs: number;
    renderMs: number;
    diffMs: number;
    ops: number;
    effects: number;
  }[];
}

/** The preview a dev plugin's view can be switched to (06 §2): width class, theme and surface, without the other shell. */
export interface Preview {
  width?: WidthClass;
  theme?: 'light' | 'dark';
  surface?: 'page' | 'sheet' | 'tray';
}

/** Whether a session can be inspected (its plugin is a dev plugin): asked once per session. */
export function useInspectable(sessionId: string | null): boolean {
  const [ok, setOk] = useState(false);
  useEffect(() => {
    setOk(false);
    if (!sessionId) return;
    let live = true;
    api<Inspection>('GET', `/api/plugins/v2/ui/sessions/${sessionId}/inspect`).then(
      () => live && setOk(true),
      () => undefined,
    );
    return () => {
      live = false;
    };
  }, [sessionId]);
  return ok;
}

const TABS = ['tree', 'state', 'timeline', 'calls', 'warnings'] as const;
type Tab = (typeof TABS)[number];

/** The inspector button and sheet for a dev plugin's view (06 §2). */
export function InspectorButton({
  sessionId,
  preview,
  onPreview,
}: {
  sessionId: string;
  preview: Preview;
  onPreview: (p: Preview) => void;
}) {
  const { t, locale } = useI18n();
  const [open, setOpen] = useState(false);
  const [tab, setTab] = useState<Tab>('tree');
  const [data, setData] = useState<Inspection | null>(null);
  const [detail, setDetail] = useState<PluginV2Detail | null>(null);

  useEffect(() => {
    if (!open) return;
    let live = true;
    const pull = () => {
      void api<Inspection>('GET', `/api/plugins/v2/ui/sessions/${sessionId}/inspect`).then(
        (d) => {
          if (!live) return;
          setData(d);
          void api<PluginV2Detail>(
            'GET',
            `/api/plugins/v2/${encodeURIComponent(d.plugin)}?locale=${locale}`,
          ).then(
            (x) => live && setDetail(x),
            () => undefined,
          );
        },
        () => undefined,
      );
    };
    pull();
    const timer = setInterval(pull, 1000);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [open, sessionId, locale]);

  const seg = <T extends string>(
    label: string,
    value: T | undefined,
    values: T[],
    set: (v: T | undefined) => void,
  ) => (
    <div className="flex items-center gap-1 text-[12px]" role="group" aria-label={label}>
      <span className="text-[var(--text-muted)]">{label}</span>
      {[undefined, ...values].map((v) => (
        <button
          key={v ?? 'auto'}
          type="button"
          aria-pressed={v === value}
          onClick={() => set(v)}
          className={`rounded px-2 py-0.5 ${v === value ? 'bg-[var(--surface-muted)] font-medium' : ''}`}
        >
          {v ?? t('pluginUi.inspector.auto')}
        </button>
      ))}
    </div>
  );
  const pre = (v: unknown) => (
    <pre className="max-h-[55vh] overflow-auto font-mono text-[11px]">
      {JSON.stringify(v, null, 2)}
    </pre>
  );

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="absolute top-0 right-0 z-10 inline-flex items-center gap-1 rounded border border-[var(--border-subtle)] bg-[var(--surface)] px-1.5 py-0.5 text-[11px] text-[var(--text-muted)]"
        aria-label={t('pluginUi.inspector.title')}
      >
        <Bug className="size-3" /> {t('pluginUi.inspector.title')}
      </button>
      <Sheet
        open={open}
        onOpenChange={setOpen}
        title={t('pluginUi.inspector.title')}
        description={data ? `${data.plugin} · ${data.view} · rev ${data.rev}` : undefined}
      >
        <div className="mb-3 flex flex-wrap gap-3">
          {seg(
            t('pluginUi.gallery.width'),
            preview.width,
            ['compact', 'regular', 'wide'] as WidthClass[],
            (width) => onPreview({ ...preview, width }),
          )}
          {seg(
            t('pluginUi.gallery.theme'),
            preview.theme,
            ['light', 'dark'] as const as ('light' | 'dark')[],
            (theme) => onPreview({ ...preview, theme }),
          )}
          {seg(
            t('pluginUi.gallery.surface'),
            preview.surface,
            ['page', 'sheet', 'tray'] as const as ('page' | 'sheet' | 'tray')[],
            (surface) => onPreview({ ...preview, surface }),
          )}
        </div>
        <div className="mb-2 flex gap-1 text-[12px]" role="tablist">
          {TABS.map((x) => (
            <button
              key={x}
              type="button"
              role="tab"
              aria-selected={x === tab}
              onClick={() => setTab(x)}
              className={`rounded px-2 py-0.5 ${x === tab ? 'bg-[var(--surface-muted)] font-medium' : ''}`}
            >
              {t(`pluginUi.inspector.${x}`)}
              {x === 'warnings' && data?.warnings.length ? ` (${data.warnings.length})` : ''}
            </button>
          ))}
        </div>
        {!data ? null : tab === 'tree' ? (
          pre(data.tree)
        ) : tab === 'state' ? (
          pre({ state: data.state, local: data.local, actions: data.actions })
        ) : tab === 'timeline' ? (
          <table className="w-full text-[11px]">
            <thead className="text-[var(--text-muted)]">
              <tr>
                {['action', 'queueMs', 'handlerMs', 'renderMs', 'diffMs', 'ops'].map((h) => (
                  <th key={h} className="px-1 text-left font-normal">
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody className="font-mono">
              {[...data.timeline].reverse().map((e) => (
                <tr key={`${e.seq}-${e.at}`}>
                  <td className="px-1">{e.action}</td>
                  <td className="px-1">{e.queueMs}</td>
                  <td className="px-1">{e.handlerMs}</td>
                  <td className="px-1">{e.renderMs}</td>
                  <td className="px-1">{e.diffMs}</td>
                  <td className="px-1">{e.ops}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : tab === 'calls' ? (
          pre({ activity: detail?.activity ?? [], log: detail?.lines ?? [] })
        ) : (
          <ul className="text-[12px]">
            {data.warnings.map((w, i) => (
              <li key={i}>{w}</li>
            ))}
          </ul>
        )}
      </Sheet>
    </>
  );
}
