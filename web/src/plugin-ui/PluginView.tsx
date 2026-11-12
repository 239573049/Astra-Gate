import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router';

import { useI18n, useT } from '../i18n';
import { Button, Spinner } from '../components/ui/controls';
import { Sheet, useFeedback } from '../components/ui/overlays';
import { widthClass, type UiEffect, type WidthClass } from './protocol';
import { NodeView, RenderProvider, type RenderContext } from './render';
import { useViewSession, type ClientContext, type ViewTarget } from './session';
import { usePreviewTheme } from './Gallery';
import { InspectorButton, useInspectable, type Preview } from './Inspector';

/** Where a view sits: a page, a sheet / dialog opened by another view, a slot of a host page, the tray panel. */
export type Surface = ClientContext['surface'];

function useTheme(): 'light' | 'dark' {
  const [dark, setDark] = useState(
    () =>
      document.documentElement.classList.contains('dark') ||
      matchMedia('(prefers-color-scheme: dark)').matches,
  );
  useEffect(() => {
    const mq = matchMedia('(prefers-color-scheme: dark)');
    const update = () => setDark(document.documentElement.classList.contains('dark') || mq.matches);
    mq.addEventListener('change', update);
    const mo = new MutationObserver(update);
    mo.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['class', 'data-theme'],
    });
    return () => {
      mq.removeEventListener('change', update);
      mo.disconnect();
    };
  }, []);
  return dark ? 'dark' : 'light';
}

function useWidth(ref: React.RefObject<HTMLElement | null>): WidthClass {
  const [w, setW] = useState<WidthClass>('regular');
  useEffect(() => {
    if (!ref.current) return;
    const ro = new ResizeObserver((e) => setW(widthClass(e[0]!.contentRect.width)));
    ro.observe(ref.current);
    return () => ro.disconnect();
  }, [ref]);
  return w;
}

/**
 * One plugin view, rendered: opens its session, follows its stream and maps its tree onto the console's controls.
 * Effects (toast, navigate, download, …) run here; questions (confirm, prompt, pick a file, open a sheet) are answered
 * here; a sheet or dialog a view opens is another PluginView with this one as its parent.
 */
export function PluginView({
  target,
  surface = 'page',
  onClose,
  onQuery,
}: {
  target: ViewTarget;
  surface?: Surface;
  /** A sheet / dialog view called ctx.close(result). */
  onClose?: (result: unknown) => void;
  /** ctx.setQuery: the page keeps it in its address. */
  onQuery?: (query: Record<string, unknown>) => void;
}) {
  const t = useT();
  const { locale } = useI18n();
  const feedback = useFeedback();
  const navigate = useNavigate();
  const shellTheme = useTheme();
  const box = useRef<HTMLDivElement>(null);
  const measured = useWidth(box);
  const [, setVersion] = useState(0);
  // a dev plugin's view can be previewed at another width class, theme or surface from its inspector (06 §2)
  const [preview, setPreview] = useState<Preview>({});
  usePreviewTheme(preview.theme ?? null);
  const width = preview.width ?? measured;
  const theme = preview.theme ?? shellTheme;
  const shownSurface = preview.surface ?? surface;
  const client = useMemo<ClientContext>(
    () => ({ locale, theme, width, surface: shownSurface }),
    [locale, theme, width, shownSurface],
  );

  const link = useCallback(
    (href: string) => {
      if (href.startsWith('https://')) {
        void feedback
          .confirm({
            title: t('pluginUi.openExternal'),
            detail: href,
            confirmLabel: t('pluginUi.open'),
          })
          .then((ok) => ok && window.open(href, '_blank', 'noopener'));
      } else if (href.startsWith('/')) navigate(href);
      else if (href.startsWith('astra://plugin/'))
        navigate('/plugins/v2/' + href.slice('astra://plugin/'.length));
    },
    [feedback, navigate, t],
  );

  const onEffect = useCallback(
    (e: UiEffect) => {
      switch (e.type) {
        case 'toast':
          feedback.toast(
            String(e.message ?? ''),
            e.tone === 'danger' || e.error ? 'error' : e.tone === 'success' ? 'success' : 'info',
          );
          break;
        case 'navigate': {
          const to = e.to as
            | string
            | { host?: string; page?: string; path?: string; query?: Record<string, string> };
          if (typeof to === 'string')
            navigate(to.startsWith('/') ? to : `/plugins/v2/${target.plugin}/${to}`);
          else if (to?.host)
            navigate(
              `/${to.host}${to.query ? '?' + new URLSearchParams(to.query).toString() : ''}`,
            );
          else if (to?.page)
            navigate(
              `/plugins/v2/${target.plugin}/${to.page}${to.path ? '/' + to.path : ''}${to.query ? '?' + new URLSearchParams(to.query).toString() : ''}`,
            );
          break;
        }
        case 'back':
          navigate(-1);
          break;
        case 'openExternal':
          link(String(e.url));
          break;
        case 'copy':
          void navigator.clipboard?.writeText(String(e.text ?? ''));
          feedback.toast(t('pluginUi.copied'), 'success');
          break;
        case 'download': {
          const data =
            typeof e.base64 === 'string'
              ? Uint8Array.from(atob(e.base64), (c) => c.charCodeAt(0))
              : String(e.text ?? '');
          const url = URL.createObjectURL(
            new Blob([data], { type: String(e.mime ?? 'application/octet-stream') }),
          );
          const a = document.createElement('a');
          a.href = url;
          a.download = String(e.name ?? 'download');
          a.click();
          setTimeout(() => URL.revokeObjectURL(url), 1000);
          break;
        }
        case 'focus':
        case 'scrollTo': {
          const el = box.current?.querySelector<HTMLElement>(
            `[data-key="${CSS.escape(String(e.key))}"]`,
          );
          if (e.type === 'focus')
            (el?.querySelector<HTMLElement>('input,textarea,button,[tabindex]') ?? el)?.focus();
          else el?.scrollIntoView({ behavior: 'smooth', block: 'center' });
          break;
        }
        case 'close':
          onClose?.(e.result ?? null);
          break;
        case 'setQuery':
          onQuery?.((e.query as Record<string, unknown>) ?? {});
          break;
      }
    },
    [feedback, navigate, link, onClose, onQuery, t, target.plugin],
  );

  const s = useViewSession(target, client, onEffect);
  const inspectable = useInspectable(s.sessionId);

  const ctx: RenderContext = {
    locale,
    width,
    weekStart: locale.startsWith('zh') ? 1 : 0,
    local: s.local,
    editedAt: s.editedAt,
    ack: s.ack,
    collections: s.collections,
    fieldErrors: s.fieldErrors,
    busyKey: s.busyKey,
    localVersion: 0,
    bump: () => setVersion((v) => v + 1),
    send: s.send,
    setLocal: s.setLocal,
    link,
    upload: s.uploadFile,
    pluginId: target.plugin,
  };

  if (s.status === 'error' || s.status === 'closed')
    return (
      <div
        ref={box}
        className="flex flex-col items-start gap-2 rounded-[var(--radius-control)] border border-[var(--red)] p-3 text-[13px]"
      >
        <span className="font-medium">{t('pluginUi.failed')}</span>
        <span className="text-[12px] text-[var(--text-secondary)]">{s.error}</span>
      </div>
    );

  return (
    <div ref={box} className="relative min-w-0" style={preview.surface === 'tray' ? { maxWidth: 340 } : preview.surface === 'sheet' ? { maxWidth: 520 } : undefined}>
      {inspectable && s.sessionId && <InspectorButton sessionId={s.sessionId} preview={preview} onPreview={setPreview} />}
      {!s.tree ? (
        <Spinner lines={3} />
      ) : (
        <RenderProvider value={ctx}>
          <NodeView node={s.tree} />
        </RenderProvider>
      )}
      {s.asks.map((a) => (
        <Ask
          key={a.id}
          ask={a}
          target={target}
          sessionId={s.sessionId}
          onAnswer={(v) => s.answer(a.id, v)}
          upload={s.uploadFile}
        />
      ))}
    </div>
  );
}

/** One question a view is waiting on (03 §3.4): answered by the shell's own dialogs, never the plugin's. */
function Ask({
  ask,
  target,
  sessionId,
  onAnswer,
  upload,
}: {
  ask: { id: string; effect: UiEffect };
  target: ViewTarget;
  sessionId: string | null;
  onAnswer: (v: unknown) => void;
  upload: (f: File) => Promise<unknown>;
}) {
  const t = useT();
  const feedback = useFeedback();
  const e = ask.effect;
  const started = useRef(false);
  const [text, setText] = useState(String(e.value ?? ''));

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    if (e.type === 'confirm')
      void feedback
        .confirm({
          title: String(e.title ?? ''),
          detail: e.message ? String(e.message) : undefined,
          confirmLabel: e.confirmLabel ? String(e.confirmLabel) : undefined,
          destructive: e.tone === 'danger',
        })
        .then((ok) => onAnswer(ok));
    if (e.type === 'pickFile') {
      const input = document.createElement('input');
      input.type = 'file';
      if (e.accept) input.accept = String(e.accept);
      input.multiple = Boolean(e.multiple);
      input.onchange = async () => {
        const files = Array.from(input.files ?? []);
        const refs = [];
        for (const f of files) refs.push(await upload(f));
        onAnswer(e.multiple ? refs : (refs[0] ?? null));
      };
      input.oncancel = () => onAnswer(null);
      input.click();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (e.type === 'prompt')
    return (
      <Sheet
        open
        onOpenChange={(o) => !o && onAnswer(null)}
        title={String(e.title ?? '')}
        description={e.message ? String(e.message) : undefined}
        footer={
          <>
            <Button onClick={() => onAnswer(null)}>{t('common.cancel')}</Button>
            <Button variant="primary" onClick={() => onAnswer(text)}>
              {t('common.confirm')}
            </Button>
          </>
        }
      >
        <input
          autoFocus
          type={e.secret ? 'password' : 'text'}
          aria-label={String(e.label ?? e.title ?? '')}
          placeholder={e.label ? String(e.label) : undefined}
          className="h-8 w-full rounded-[var(--radius-control)] border border-[var(--border)] bg-[var(--surface)] px-2 text-[13px]"
          value={text}
          onChange={(ev) => setText(ev.target.value)}
          onKeyDown={(ev) => ev.key === 'Enter' && onAnswer(text)}
        />
      </Sheet>
    );

  if (e.type === 'openSheet' || e.type === 'openDialog')
    return (
      <Sheet
        open
        width={e.size === 'lg' ? 720 : e.size === 'sm' ? 380 : 520}
        onOpenChange={(o) => !o && onAnswer(null)}
        title={String(e.title ?? '')}
      >
        <PluginView
          target={{
            plugin: target.plugin,
            view: String(e.view),
            params: (e.params as Record<string, unknown>) ?? {},
            parent: sessionId ?? undefined,
          }}
          surface={e.type === 'openDialog' ? 'dialog' : 'sheet'}
          onClose={(result) => onAnswer(result)}
        />
      </Sheet>
    );

  return null;
}
