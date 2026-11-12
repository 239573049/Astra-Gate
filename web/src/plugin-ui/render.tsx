// Renders a v2 plugin view's tree with the console's own controls (design/plugin-v2 03 §5, 04). Each node type maps to
// one component; props are tokens, never styles. Inputs keep their value locally (the local state bag) and reconcile
// with the server's value by sequence number (03 §6.1); events go back as { action, args }.
import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { DynamicIcon, type IconName } from 'lucide-react/dynamic';
import { Check, ChevronDown, Copy, MoreHorizontal } from 'lucide-react';

import { useClients, useModels, useProviderAccounts, useProviders, useTokens } from '../api/hooks';
import { useT, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';
import {
  Badge,
  Button,
  CodeBlock,
  Dot,
  EmptyState,
  Field,
  Spinner,
  Switch as AstraSwitch,
} from '../components/ui/controls';
import { Menu, Select, type SelectOption } from '../components/ui/overlays';
import {
  flag,
  format,
  GAP,
  responsive,
  toneColor,
  WIDTH,
  type UiAction,
  type UiCollectionState,
  type UiNode,
  type WidthClass,
} from './protocol';
import {
  CanvasView,
  ChartView,
  DatePickerView,
  DateRangePickerView,
  GaugeView,
  JsonTree,
  KeyValueEditorView,
  LogViewer,
  QrCodeView,
  RichTextView,
  SparklineView,
  TagInputView,
  TimeInput,
  TreeView,
  type ChartSpec,
  type Span,
  type TreeNodeData,
} from './widgets';

export interface RenderContext {
  locale: string;
  width: WidthClass;
  weekStart: number;
  local: React.MutableRefObject<Record<string, unknown>>;
  editedAt: React.MutableRefObject<Record<string, number>>;
  ack: React.MutableRefObject<number>;
  collections: Record<string, UiCollectionState>;
  fieldErrors: Record<string, string>;
  busyKey: string | null;
  /** Re-renders conditions that read the local state bag. */
  localVersion: number;
  bump: () => void;
  send: (action: string, args: unknown, sourceKey?: string) => void;
  setLocal: (name: string, value: unknown) => void;
  link: (href: string) => void;
  upload: (file: File) => Promise<unknown>;
  pluginId: string;
}

const Ctx = createContext<RenderContext | null>(null);
export const RenderProvider = Ctx.Provider;
const useRender = () => useContext(Ctx)!;

const P = <T,>(n: UiNode, k: string): T | undefined => n.props[k] as T | undefined;
const S = (n: UiNode, k: string): string | undefined =>
  typeof n.props[k] === 'string' ? (n.props[k] as string) : undefined;
const N = (n: UiNode, k: string): number | undefined =>
  typeof n.props[k] === 'number' ? (n.props[k] as number) : undefined;
const B = (n: UiNode, k: string): boolean => n.props[k] === true;

function act(n: UiNode, k: string): UiAction | undefined {
  const v = n.props[k];
  return v && typeof v === 'object' && 'action' in v ? (v as UiAction) : undefined;
}

/** Fires an event prop: its action with its args merged under the event's payload. */
function useFire() {
  const r = useRender();
  return (n: UiNode, k: string, payload?: Record<string, unknown>) => {
    const a = act(n, k);
    if (!a) return false;
    const args =
      a.args !== undefined && typeof a.args === 'object' && !Array.isArray(a.args)
        ? { ...(a.args as object), ...payload }
        : (payload ?? a.args);
    r.send(a.action, args ?? {}, n.key);
    return true;
  };
}

export function NodeView({ node }: { node: UiNode }) {
  const r = useRender();
  void r.localVersion;
  if (!flag(node.props.visible, r.local.current, true)) return null;
  const Comp = RENDERERS[node.type];
  if (!Comp) {
    const fb = node.props.fallback as UiNode | undefined;
    return fb ? <NodeView node={fb} /> : <Unsupported type={node.type} />;
  }
  const w = responsive(node.props.width as string | undefined, r.width);
  const style: React.CSSProperties = {};
  if (w) style.width = WIDTH[w] ?? (typeof w === 'number' ? `${w}px` : undefined);
  if (typeof node.props.grow === 'number') style.flexGrow = node.props.grow;
  if (node.props.minWidth) style.minWidth = WIDTH[String(node.props.minWidth)];
  if (node.props.maxWidth) style.maxWidth = WIDTH[String(node.props.maxWidth)];
  if (node.props.alignSelf) style.alignSelf = ALIGN[String(node.props.alignSelf)];
  if (typeof node.props.span === 'number') style.gridColumn = `span ${node.props.span}`;
  const el = <Comp node={node} />;
  const tooltip = typeof node.props.tooltip === 'string' ? node.props.tooltip : undefined;
  if (!Object.keys(style).length && !tooltip && !node.key) return el;
  return (
    <div
      style={{ ...style, display: style.width || style.flexGrow ? undefined : 'contents' }}
      title={tooltip}
      data-key={node.key}
      data-testid={S(node, 'testId')}
    >
      {el}
    </div>
  );
}

function Kids({ node }: { node: UiNode }) {
  return (
    <>
      {node.children?.map((c, i) => (
        <NodeView key={c.key ?? `${i}:${c.type}`} node={c} />
      ))}
    </>
  );
}

function Nodes({ list }: { list: unknown }) {
  if (!Array.isArray(list)) return null;
  return (
    <>
      {(list as UiNode[]).map((c, i) => (
        <NodeView key={c.key ?? `${i}:${c.type}`} node={c} />
      ))}
    </>
  );
}

function Unsupported({ type }: { type: string }) {
  const t = useT();
  return (
    <div className="rounded border border-dashed border-[var(--border)] px-2 py-1 text-[12px] text-[var(--text-muted)]">
      {t('pluginUi.unsupported', { type })}
    </div>
  );
}

const ALIGN: Record<string, string> = {
  start: 'flex-start',
  center: 'center',
  end: 'flex-end',
  stretch: 'stretch',
  between: 'space-between',
};

export function Icon({
  name,
  className,
  color,
}: {
  name?: unknown;
  className?: string;
  color?: string;
}) {
  if (typeof name !== 'string' || !name) return null;
  return (
    <DynamicIcon
      name={name as IconName}
      className={className ?? 'size-4'}
      style={color ? { color } : undefined}
      fallback={() => <span className={className ?? 'size-4'} />}
    />
  );
}

const tone = (v: unknown): 'neutral' | 'accent' | 'green' | 'orange' | 'red' =>
  v === 'success'
    ? 'green'
    : v === 'warning'
      ? 'orange'
      : v === 'danger'
        ? 'red'
        : v === 'accent'
          ? 'accent'
          : 'neutral';

// ---------------------------------------------------------------- inputs: local value + reconcile

function useInput<T>(node: UiNode, fallback: T): [T, (v: T, commitNow?: boolean) => void, boolean] {
  const r = useRender();
  const fire = useFire();
  const name = S(node, 'name');
  const serverValue = (node.props.value ?? node.props.checked ?? fallback) as T;
  const [value, setValue] = useState<T>(() =>
    name && name in r.local.current ? (r.local.current[name] as T) : serverValue,
  );
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const serverJson = JSON.stringify(serverValue);
  useEffect(() => {
    // the server's value wins unless the user edited after what the server has acknowledged
    if (name && (r.editedAt.current[name] ?? 0) > r.ack.current) return;
    setValue(serverValue);
    if (name) r.local.current = { ...r.local.current, [name]: serverValue };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serverJson]);
  useEffect(() => {
    if (name && !(name in r.local.current))
      r.local.current = { ...r.local.current, [name]: serverValue };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const disabled = flag(node.props.disabled, r.local.current, false) || B(node, 'readOnly');
  const set = (v: T, commitNow = false) => {
    setValue(v);
    if (name) r.setLocal(name, v);
    r.bump();
    if (!act(node, 'onChange')) return;
    clearTimeout(timer.current);
    const commit = S(node, 'commit');
    if (commit === 'blur' && !commitNow) return;
    const debounce = commitNow ? 0 : (N(node, 'debounce') ?? (typeof v === 'string' ? 300 : 0));
    timer.current = setTimeout(() => fire(node, 'onChange', { value: v }), debounce);
  };
  return [value, set, disabled];
}

const inputClass =
  'h-8 w-full rounded-[var(--radius-control)] border border-[var(--border)] bg-[var(--surface)] px-2 text-[13px] outline-none focus:border-[var(--accent)] disabled:opacity-50';

function TextInput({ node, type = 'text', mono }: { node: UiNode; type?: string; mono?: boolean }) {
  const [value, set, disabled] = useInput<string>(node, '');
  const r = useRender();
  const name = S(node, 'name');
  const invalid = B(node, 'invalid') || Boolean(name && r.fieldErrors[name]);
  return (
    <div className="flex items-center gap-1">
      {S(node, 'prefix') && (
        <span className="text-[12px] text-[var(--text-muted)]">{S(node, 'prefix')}</span>
      )}
      <input
        className={cn(inputClass, mono && 'font-mono', invalid && 'border-[var(--red)]')}
        type={type}
        name={name}
        value={value ?? ''}
        placeholder={S(node, 'placeholder')}
        maxLength={N(node, 'maxLength')}
        pattern={S(node, 'pattern')}
        required={B(node, 'required')}
        disabled={disabled}
        autoFocus={B(node, 'autoFocus')}
        aria-label={S(node, 'a11yLabel') ?? S(node, 'placeholder')}
        onChange={(e) => set(e.target.value)}
        onBlur={() => S(node, 'commit') === 'blur' && set(value, true)}
        onKeyDown={(e) => e.key === 'Enter' && S(node, 'commit') === 'blur' && set(value, true)}
      />
      {S(node, 'suffix') && (
        <span className="text-[12px] text-[var(--text-muted)]">{S(node, 'suffix')}</span>
      )}
    </div>
  );
}

function useOptions(
  node: UiNode,
): {
  value: string;
  label: string;
  description?: string;
  icon?: string;
  disabled?: boolean;
  group?: string;
  title?: string;
}[] {
  const r = useRender();
  const source = node.props.source as { collection?: string } | undefined;
  const raw = source?.collection
    ? r.collections[source.collection]?.items
    : (node.props.options as unknown[] | undefined);
  return (raw ?? []).map((o) =>
    typeof o === 'string'
      ? { value: o, label: o }
      : {
          ...(o as object),
          value: String((o as { value: unknown }).value),
          label: String(
            (o as { label?: unknown; title?: unknown }).label ??
              (o as { title?: unknown }).title ??
              (o as { value: unknown }).value,
          ),
        },
  );
}

// ---------------------------------------------------------------- renderers

type R = (p: { node: UiNode }) => ReactNode;

const RENDERERS: Record<string, R> = {
  // ----- page & layout
  Page: ({ node }) => (
    <div
      className="mx-auto flex w-full flex-col gap-5"
      style={{
        maxWidth: { narrow: 640, default: 960, wide: 1280, full: undefined }[
          S(node, 'width') ?? 'default'
        ],
      }}
    >
      {(S(node, 'title') || P(node, 'actions')) && (
        <div className="flex items-start justify-between gap-3">
          <div className="flex min-w-0 items-center gap-2">
            <Icon name={node.props.icon} className="size-5 text-[var(--accent)]" />
            <div className="min-w-0">
              {S(node, 'title') && (
                <h1 className="text-[20px] font-semibold tracking-tight">{S(node, 'title')}</h1>
              )}
              {S(node, 'subtitle') && (
                <p className="text-[13px] text-[var(--text-secondary)]">{S(node, 'subtitle')}</p>
              )}
            </div>
          </div>
          <div className="flex shrink-0 items-center gap-2">
            <Nodes list={node.props.actions} />
          </div>
        </div>
      )}
      <Kids node={node} />
    </div>
  ),
  Stack: ({ node }) => {
    const r = useRender();
    const dir = responsive(S(node, 'direction'), r.width) ?? 'column';
    return (
      <div
        className={cn(
          'flex min-w-0',
          dir === 'row' ? 'flex-row' : 'flex-col',
          B(node, 'wrap') && 'flex-wrap',
        )}
        style={{
          gap: GAP[
            String(responsive(node.props.gap as string, r.width) ?? (dir === 'row' ? '2' : '3'))
          ],
          padding: GAP[String(node.props.padding ?? '0')],
          alignItems: ALIGN[S(node, 'align') ?? (dir === 'row' ? 'center' : 'stretch')],
          justifyContent: ALIGN[S(node, 'justify') ?? 'start'],
        }}
      >
        <Kids node={node} />
      </div>
    );
  },
  Grid: ({ node }) => {
    const r = useRender();
    const cols = responsive(node.props.columns as number | { min?: string }, r.width);
    const template =
      typeof cols === 'number'
        ? `repeat(${cols}, minmax(0, 1fr))`
        : cols && typeof cols === 'object' && 'min' in cols
          ? `repeat(auto-fill, minmax(${WIDTH[String(cols.min)] ?? '200px'}, 1fr))`
          : 'repeat(auto-fill, minmax(240px, 1fr))';
    return (
      <div
        className="grid"
        style={{
          gridTemplateColumns: template,
          gap: GAP[String(node.props.gap ?? '3')],
          rowGap: node.props.rowGap ? GAP[String(node.props.rowGap)] : undefined,
        }}
      >
        <Kids node={node} />
      </div>
    );
  },
  Split: ({ node }) => {
    const r = useRender();
    const below = S(node, 'stackBelow') ?? 'compact';
    const stacked = r.width === 'compact' || (below === 'regular' && r.width !== 'wide');
    const ratio = N(node, 'ratio') ?? 0.35;
    const [a, b] = node.children ?? [];
    if (stacked)
      return (
        <div className="flex flex-col gap-4">
          {a && <NodeView node={a} />}
          {b && <NodeView node={b} />}
        </div>
      );
    return (
      <div className="flex gap-4">
        <div className="min-w-0" style={{ flex: `${ratio} 1 0` }}>
          {a && <NodeView node={a} />}
        </div>
        <div className="min-w-0" style={{ flex: `${1 - ratio} 1 0` }}>
          {b && <NodeView node={b} />}
        </div>
      </div>
    );
  },
  Scroll: ({ node }) => (
    <div
      className="overflow-auto"
      style={{
        maxHeight:
          WIDTH[S(node, 'maxHeight') ?? ''] ??
          (N(node, 'maxHeight') ? `${N(node, 'maxHeight')}px` : '480px'),
      }}
    >
      <Kids node={node} />
    </div>
  ),
  Box: ({ node }) => (
    <div
      className={cn(
        S(node, 'surface') === 'card' && 'card',
        S(node, 'surface') === 'sunken' && 'bg-[var(--surface-muted)]',
        S(node, 'surface') === 'accent' && 'bg-[var(--accent-subtle)]',
        B(node, 'border') && 'border border-[var(--border)]',
        S(node, 'radius') === 'lg'
          ? 'rounded-[var(--radius-panel)]'
          : S(node, 'radius') === 'none'
            ? ''
            : 'rounded-[var(--radius-control)]',
      )}
      style={{ padding: GAP[String(node.props.padding ?? '3')] }}
    >
      <Kids node={node} />
    </div>
  ),
  Divider: ({ node }) =>
    S(node, 'label') ? (
      <div className="flex items-center gap-2 text-[11px] text-[var(--text-muted)]">
        <span className="h-px flex-1 bg-[var(--border)]" />
        {S(node, 'label')}
        <span className="h-px flex-1 bg-[var(--border)]" />
      </div>
    ) : S(node, 'direction') === 'vertical' ? (
      <span className="w-px self-stretch bg-[var(--border)]" />
    ) : (
      <hr className="border-[var(--border)]" />
    ),
  Spacer: ({ node }) => (
    <div
      style={{
        flexGrow: B(node, 'grow') ? 1 : undefined,
        width: GAP[String(node.props.size ?? '4')],
        height: GAP[String(node.props.size ?? '4')],
      }}
    />
  ),
  Sticky: ({ node }) => (
    <div
      className={cn(
        'sticky z-10 bg-[var(--background)]',
        S(node, 'edge') === 'bottom' ? 'bottom-0' : 'top-0',
      )}
    >
      <Kids node={node} />
    </div>
  ),

  // ----- structure
  Group: ({ node }) => (
    <section>
      {(S(node, 'title') || P(node, 'actions')) && (
        <div className="mb-2 flex items-center justify-between px-1">
          <h3 className="text-[12px] font-medium text-[var(--text-secondary)]">
            {S(node, 'title')}
          </h3>
          <div className="flex gap-1">
            <Nodes list={node.props.actions} />
          </div>
        </div>
      )}
      {S(node, 'description') && (
        <p className="mb-2 px-1 text-[12px] text-[var(--text-muted)]">{S(node, 'description')}</p>
      )}
      <div className="card divide-y divide-[var(--border)]">
        <Kids node={node} />
      </div>
      {S(node, 'footer') && (
        <p className="mt-2 px-1 text-[11px] text-[var(--text-muted)]">{S(node, 'footer')}</p>
      )}
    </section>
  ),
  Row: ({ node }) => {
    const fire = useFire();
    const press = act(node, 'onPress');
    return (
      <div
        role={press ? 'button' : undefined}
        tabIndex={press ? 0 : undefined}
        className={cn(
          'flex min-h-11 items-center gap-3 px-4 py-2',
          press && 'cursor-pointer hover:bg-[var(--surface-muted)]',
          B(node, 'selected') && 'bg-[var(--accent-subtle)]',
        )}
        onClick={() => press && fire(node, 'onPress')}
        onKeyDown={(e) => press && (e.key === 'Enter' || e.key === ' ') && fire(node, 'onPress')}
      >
        <Icon name={node.props.icon} className="size-4 text-[var(--text-secondary)]" />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-1 text-[13px]">
            {S(node, 'label')}
            {S(node, 'hint') && (
              <span title={S(node, 'hint')} className="cursor-help text-[var(--text-muted)]">
                ⓘ
              </span>
            )}
          </div>
          {S(node, 'detail') && (
            <div className="text-[12px] text-[var(--text-muted)]">{S(node, 'detail')}</div>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-2" onClick={(e) => e.stopPropagation()}>
          <Kids node={node} />
        </div>
      </div>
    );
  },
  Card: ({ node }) => {
    const fire = useFire();
    const press = act(node, 'onPress');
    return (
      <div
        className={cn(
          'card flex flex-col gap-3 p-4',
          press && 'cursor-pointer hover:border-[var(--accent)]',
        )}
        style={{ borderColor: node.props.tone ? toneColor(node.props.tone) : undefined }}
        onClick={() => press && fire(node, 'onPress')}
      >
        {(S(node, 'title') || P(node, 'actions')) && (
          <div className="flex items-start justify-between gap-2">
            <div className="flex min-w-0 items-center gap-2">
              <Icon name={node.props.icon} className="size-4 text-[var(--accent)]" />
              <div className="min-w-0">
                <div className="truncate text-[14px] font-medium">{S(node, 'title')}</div>
                {S(node, 'subtitle') && (
                  <div className="text-[12px] text-[var(--text-muted)]">{S(node, 'subtitle')}</div>
                )}
              </div>
            </div>
            <div className="flex gap-1" onClick={(e) => e.stopPropagation()}>
              <Nodes list={node.props.actions} />
            </div>
          </div>
        )}
        <Kids node={node} />
        {Array.isArray(node.props.footer) && (
          <div className="flex justify-end gap-2 border-t border-[var(--border)] pt-3">
            <Nodes list={node.props.footer} />
          </div>
        )}
      </div>
    );
  },
  Section: ({ node }) => {
    const [open, setOpen] = useState(node.props.defaultOpen !== false);
    const collapsible = B(node, 'collapsible');
    return (
      <section className="flex flex-col gap-3">
        <div className="flex items-center justify-between">
          <button
            type="button"
            disabled={!collapsible}
            className="flex items-center gap-1 text-left"
            onClick={() => setOpen((o) => !o)}
          >
            {collapsible && (
              <ChevronDown className={cn('size-3.5 transition-transform', !open && '-rotate-90')} />
            )}
            <h2 className="text-[15px] font-semibold">{S(node, 'title')}</h2>
          </button>
          <div className="flex gap-1">
            <Nodes list={node.props.actions} />
          </div>
        </div>
        {S(node, 'description') && (
          <p className="-mt-2 text-[12px] text-[var(--text-muted)]">{S(node, 'description')}</p>
        )}
        {open && <Kids node={node} />}
      </section>
    );
  },
  Tabs: ({ node }) => {
    const r = useRender();
    const fire = useFire();
    const items =
      P<{ value: string; label: string; icon?: string; badge?: string }[]>(node, 'items') ?? [];
    const [value, setValue] = useState<string | undefined>(S(node, 'value') ?? items[0]?.value);
    useEffect(() => {
      const v = S(node, 'value');
      if (v) setValue(v);
      // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [S(node, 'value')]);
    const pane = node.children?.find((c) => c.props.value === value);
    return (
      <div className="flex flex-col gap-3">
        <div
          role="tablist"
          className={cn(
            'flex gap-1',
            S(node, 'variant') === 'pill' ? '' : 'border-b border-[var(--border)]',
          )}
        >
          {items.map((it) => (
            <button
              key={it.value}
              role="tab"
              aria-selected={it.value === value}
              type="button"
              className={cn(
                'flex items-center gap-1.5 px-3 py-1.5 text-[13px]',
                S(node, 'variant') === 'pill'
                  ? cn(
                      'rounded-full',
                      it.value === value
                        ? 'bg-[var(--accent)] text-[var(--accent-foreground)]'
                        : 'hover:bg-[var(--surface-muted)]',
                    )
                  : cn(
                      '-mb-px border-b-2',
                      it.value === value
                        ? 'border-[var(--accent)] text-[var(--foreground)]'
                        : 'border-transparent text-[var(--text-secondary)]',
                    ),
              )}
              onClick={() => {
                setValue(it.value);
                const name = S(node, 'name');
                if (name) r.setLocal(name, it.value);
                fire(node, 'onChange', { value: it.value });
              }}
            >
              <Icon name={it.icon} className="size-3.5" />
              {it.label}
              {it.badge && (
                <span className="rounded-full bg-[var(--surface-muted)] px-1.5 text-[11px]">
                  {it.badge}
                </span>
              )}
            </button>
          ))}
        </div>
        {pane && <NodeView node={pane} />}
      </div>
    );
  },
  Tab: ({ node }) => (
    <div role="tabpanel">
      <Kids node={node} />
    </div>
  ),
  Accordion: ({ node }) => (
    <div className="card divide-y divide-[var(--border)]">
      <Kids node={node} />
    </div>
  ),
  AccordionItem: ({ node }) => {
    const [open, setOpen] = useState(B(node, 'defaultOpen'));
    return (
      <div>
        <button
          type="button"
          className="flex w-full items-center justify-between px-4 py-2.5 text-left text-[13px] font-medium"
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
        >
          {S(node, 'title')}
          <ChevronDown className={cn('size-4 transition-transform', open && 'rotate-180')} />
        </button>
        {open && (
          <div className="px-4 pb-3">
            <Kids node={node} />
          </div>
        )}
      </div>
    );
  },
  Disclosure: ({ node }) => {
    const [open, setOpen] = useState(B(node, 'defaultOpen'));
    return (
      <div>
        <button
          type="button"
          className="flex items-center gap-1 text-[13px] text-[var(--text-secondary)]"
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
        >
          <ChevronDown className={cn('size-3.5 transition-transform', !open && '-rotate-90')} />
          {S(node, 'title')}
        </button>
        {open && (
          <div className="mt-2">
            <Kids node={node} />
          </div>
        )}
      </div>
    );
  },
  Stepper: ({ node }) => {
    const steps =
      P<{ title: string; description?: string; status?: string }[]>(node, 'steps') ?? [];
    const current = N(node, 'current') ?? 0;
    return (
      <ol className={cn('flex gap-4', S(node, 'direction') === 'vertical' && 'flex-col')}>
        {steps.map((s, i) => (
          <li key={i} className="flex items-start gap-2">
            <span
              className={cn(
                'grid size-6 shrink-0 place-items-center rounded-full text-[11px]',
                i < current || s.status === 'done'
                  ? 'bg-[var(--accent)] text-[var(--accent-foreground)]'
                  : i === current
                    ? 'border-2 border-[var(--accent)]'
                    : 'border border-[var(--border)]',
              )}
            >
              {i < current || s.status === 'done' ? <Check className="size-3.5" /> : i + 1}
            </span>
            <div>
              <div className="text-[13px] font-medium">{s.title}</div>
              {s.description && (
                <div className="text-[12px] text-[var(--text-muted)]">{s.description}</div>
              )}
            </div>
          </li>
        ))}
      </ol>
    );
  },
  Timeline: ({ node }) => {
    const r = useRender();
    const items =
      P<{ time?: string; title: string; detail?: string; tone?: string; icon?: string }[]>(
        node,
        'items',
      ) ?? [];
    return (
      <ol className="relative flex flex-col gap-4 border-l border-[var(--border)] pl-4">
        {items.map((it, i) => (
          <li key={i} className="relative">
            <span
              className="absolute -left-[21px] top-1 size-2.5 rounded-full"
              style={{ background: toneColor(it.tone ?? 'accent') }}
            />
            {it.time && (
              <div className="text-[11px] text-[var(--text-muted)]">
                {format('datetime', it.time, r.locale)}
              </div>
            )}
            <div className="text-[13px] font-medium">{it.title}</div>
            {it.detail && (
              <div className="text-[12px] text-[var(--text-secondary)]">{it.detail}</div>
            )}
          </li>
        ))}
      </ol>
    );
  },
  Breadcrumb: ({ node }) => {
    const r = useRender();
    const items = P<{ label: string; action?: string | UiAction }[]>(node, 'items') ?? [];
    return (
      <nav
        aria-label="breadcrumb"
        className="flex items-center gap-1 text-[12px] text-[var(--text-secondary)]"
      >
        {items.map((it, i) => (
          <span key={i} className="flex items-center gap-1">
            {i > 0 && <span>/</span>}
            {it.action ? (
              <button
                type="button"
                className="hover:text-[var(--accent)]"
                onClick={() =>
                  r.send(
                    typeof it.action === 'string' ? it.action : it.action!.action,
                    typeof it.action === 'string' ? {} : (it.action!.args ?? {}),
                  )
                }
              >
                {it.label}
              </button>
            ) : (
              <span className="text-[var(--foreground)]">{it.label}</span>
            )}
          </span>
        ))}
      </nav>
    );
  },
  Toolbar: ({ node }) => (
    <div className="flex flex-wrap items-center justify-between gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <Nodes list={node.props.start} />
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <Nodes list={node.props.end} />
      </div>
    </div>
  ),
  Boundary: ({ node }) =>
    B(node, 'busy') && node.props.loading ? (
      <NodeView node={node.props.loading as UiNode} />
    ) : (
      <Kids node={node} />
    ),

  // ----- text & display
  Text: ({ node }) => {
    const r = useRender();
    const v = node.props.value;
    const text = node.props.format ? format(node.props.format, v, r.locale) : String(v ?? '');
    const variant = S(node, 'variant');
    const lines = N(node, 'truncate');
    return (
      <span
        className={cn(
          'min-w-0',
          variant === 'caption'
            ? 'text-[12px] text-[var(--text-muted)]'
            : variant === 'label'
              ? 'text-[12px] font-medium text-[var(--text-secondary)]'
              : variant === 'mono'
                ? 'font-mono text-[12px]'
                : 'text-[13px]',
          S(node, 'weight') === 'bold'
            ? 'font-bold'
            : S(node, 'weight') === 'semibold'
              ? 'font-semibold'
              : S(node, 'weight') === 'medium' && 'font-medium',
          lines === 1 && 'truncate',
          B(node, 'selectable') ? 'select-text' : '',
        )}
        style={{
          color: node.props.tone ? toneColor(node.props.tone) : undefined,
          ...(lines && lines > 1
            ? {
                display: '-webkit-box',
                WebkitLineClamp: lines,
                WebkitBoxOrient: 'vertical',
                overflow: 'hidden',
              }
            : {}),
        }}
      >
        {text}
      </span>
    );
  },
  Heading: ({ node }) => {
    const level = Math.min(4, Math.max(1, N(node, 'level') ?? 2));
    const cls = [
      'text-[22px] font-semibold',
      'text-[18px] font-semibold',
      'text-[15px] font-semibold',
      'text-[13px] font-semibold',
    ][level - 1];
    const Tag = `h${level}` as 'h1';
    return <Tag className={cls}>{String(node.props.value ?? '')}</Tag>;
  },
  RichText: ({ node }) => {
    const r = useRender();
    return (
      <RichTextView
        spans={P<Span[]>(node, 'spans') ?? []}
        size={S(node, 'variant')}
        onLink={(s) => (s.action ? r.send(s.action, { href: s.link }) : s.link && r.link(s.link))}
      />
    );
  },
  Markdown: ({ node }) => (
    <p className="whitespace-pre-wrap text-[13px]">{String(node.props.value ?? '')}</p>
  ),
  Code: ({ node }) => (
    <CodeBlock
      language={S(node, 'language') ?? 'text'}
      filename={S(node, 'filename')}
      maxHeight={N(node, 'maxHeight') ?? 320}
    >
      {String(node.props.value ?? '')}
    </CodeBlock>
  ),
  JsonView: ({ node }) => (
    <div className="overflow-auto rounded-[var(--radius-control)] bg-[var(--surface-muted)] p-2 font-mono text-[12px]">
      <JsonTree value={node.props.value} expandDepth={N(node, 'expandDepth') ?? 2} />
    </div>
  ),
  DiffView: ({ node }) => {
    const before = String(node.props.before ?? '').split('\n');
    const after = String(node.props.after ?? '').split('\n');
    const rows = diffLines(before, after);
    return (
      <pre className="overflow-auto rounded-[var(--radius-control)] bg-[var(--surface-muted)] p-2 font-mono text-[12px]">
        {rows.map((r, i) => (
          <div
            key={i}
            style={{
              background:
                r.kind === '+'
                  ? 'color-mix(in srgb, var(--green) 15%, transparent)'
                  : r.kind === '-'
                    ? 'color-mix(in srgb, var(--red) 15%, transparent)'
                    : undefined,
            }}
          >
            {r.kind} {r.text}
          </div>
        ))}
      </pre>
    );
  },
  Link: ({ node }) => {
    const r = useRender();
    const fire = useFire();
    return (
      <a
        href={S(node, 'href')}
        className="text-[13px] text-[var(--accent)] hover:underline"
        onClick={(e) => {
          e.preventDefault();
          if (!fire(node, 'onPress') && S(node, 'href')) r.link(S(node, 'href')!);
        }}
      >
        {S(node, 'label') ?? S(node, 'href')}
      </a>
    );
  },
  Icon: ({ node }) => {
    const r = useRender();
    if (S(node, 'src'))
      return <img src={assetUrl(r.pluginId, S(node, 'src')!)} alt="" className="size-4" />;
    const size =
      S(node, 'size') === 'lg' ? 'size-6' : S(node, 'size') === 'sm' ? 'size-3.5' : 'size-4';
    return (
      <Icon
        name={node.props.name ?? node.props.brand}
        className={size}
        color={node.props.tone ? toneColor(node.props.tone) : undefined}
      />
    );
  },
  Image: ({ node }) => {
    const r = useRender();
    const src = node.props.src;
    const url =
      typeof src === 'string'
        ? assetUrl(r.pluginId, src)
        : src && typeof src === 'object'
          ? assetUrl(r.pluginId, String((src as { light?: string }).light ?? ''))
          : '';
    return (
      <img
        src={url}
        alt={S(node, 'alt') ?? ''}
        className={cn(
          'max-w-full',
          S(node, 'radius') === 'lg'
            ? 'rounded-[var(--radius-panel)]'
            : S(node, 'radius') === 'md' && 'rounded-[var(--radius-control)]',
        )}
        style={{
          objectFit: (S(node, 'fit') as 'cover') ?? 'contain',
          aspectRatio: N(node, 'ratio'),
        }}
      />
    );
  },
  Avatar: ({ node }) => {
    const size = S(node, 'size') === 'lg' ? 40 : S(node, 'size') === 'sm' ? 20 : 28;
    const name = S(node, 'name') ?? '';
    return (
      <span
        className="relative grid shrink-0 place-items-center overflow-hidden rounded-full bg-[var(--accent-subtle)] text-[var(--accent)]"
        style={{ width: size, height: size, fontSize: size * 0.4 }}
      >
        {S(node, 'icon') ? (
          <Icon name={S(node, 'icon')} className="size-1/2" />
        ) : (
          name.slice(0, 1).toUpperCase()
        )}
        {node.props.status ? (
          <span
            className="absolute bottom-0 right-0 size-2 rounded-full ring-2 ring-[var(--surface)]"
            style={{ background: toneColor(node.props.status) }}
          />
        ) : null}
      </span>
    );
  },
  Badge: ({ node }) => (
    <Badge tone={tone(node.props.tone)}>
      <span className="inline-flex items-center gap-1">
        <Icon name={node.props.icon} className="size-3" />
        {S(node, 'label')}
      </span>
    </Badge>
  ),
  StatusDot: ({ node }) => (
    <span className="inline-flex items-center gap-1.5 text-[12px]">
      <Dot
        tone={tone(node.props.tone) === 'accent' ? 'neutral' : (tone(node.props.tone) as 'green')}
      />
      {S(node, 'label')}
    </span>
  ),
  Kbd: ({ node }) => (
    <span className="inline-flex gap-0.5">
      {(P<string[]>(node, 'keys') ?? []).map((k) => (
        <kbd
          key={k}
          className="rounded border border-[var(--border)] bg-[var(--surface-muted)] px-1 font-mono text-[11px]"
        >
          {k}
        </kbd>
      ))}
    </span>
  ),
  KV: ({ node }) => {
    const items =
      P<{ label: string; value: unknown; mono?: boolean; copy?: boolean }[]>(node, 'items') ?? [];
    return (
      <dl
        className="grid gap-x-6 gap-y-2"
        style={{ gridTemplateColumns: `repeat(${N(node, 'columns') ?? 1}, auto 1fr)` }}
      >
        {items.map((it, i) => (
          <div key={i} className="contents">
            <dt className="text-[12px] text-[var(--text-muted)]">{it.label}</dt>
            <dd
              className={cn(
                'flex items-center gap-1 text-[13px]',
                it.mono && 'font-mono text-[12px]',
              )}
            >
              {String(it.value ?? '—')}
              {it.copy && <CopyInline value={String(it.value ?? '')} />}
            </dd>
          </div>
        ))}
      </dl>
    );
  },
  Alert: ({ node }) => {
    const fire = useFire();
    const [gone, setGone] = useState(false);
    if (gone) return null;
    return (
      <div
        className="flex items-start gap-3 rounded-[var(--radius-control)] border px-3 py-2.5"
        style={{
          borderColor: toneColor(node.props.tone),
          background: `color-mix(in srgb, ${toneColor(node.props.tone)} 8%, transparent)`,
        }}
      >
        <div className="min-w-0 flex-1">
          {S(node, 'title') && <div className="text-[13px] font-medium">{S(node, 'title')}</div>}
          {S(node, 'message') && (
            <div className="text-[12px] text-[var(--text-secondary)]">{S(node, 'message')}</div>
          )}
          {Array.isArray(node.props.actions) && (
            <div className="mt-2 flex gap-2">
              <Nodes list={node.props.actions} />
            </div>
          )}
        </div>
        {B(node, 'dismissible') && (
          <button
            type="button"
            className="text-[var(--text-muted)]"
            aria-label="dismiss"
            onClick={() => (setGone(true), fire(node, 'onDismiss'))}
          >
            ×
          </button>
        )}
      </div>
    );
  },
  Banner: ({ node }) =>
    RENDERERS.Alert!({
      node: { ...node, props: { ...node.props, dismissible: Boolean(node.props.onDismiss) } },
    }),
  EmptyState: ({ node }) => (
    <EmptyState
      icon={<Icon name={node.props.icon ?? 'inbox'} className="size-6" />}
      title={S(node, 'title') ?? ''}
      detail={S(node, 'detail')}
      action={node.props.action ? <NodeView node={node.props.action as UiNode} /> : undefined}
    />
  ),
  Skeleton: ({ node }) => (
    <Spinner lines={N(node, 'lines') ?? (S(node, 'shape') === 'block' ? 4 : 2)} />
  ),
  Spinner: ({ node }) => (
    <span className="inline-flex items-center gap-2 text-[12px] text-[var(--text-muted)]">
      <span className="size-4 animate-spin rounded-full border-2 border-[var(--accent)] border-t-transparent" />
      {S(node, 'label')}
    </span>
  ),
  Progress: ({ node }) => {
    const v = N(node, 'value');
    if (S(node, 'variant') === 'ring') {
      const r = 14;
      const c = 2 * Math.PI * r;
      return (
        <svg
          width={36}
          height={36}
          role="progressbar"
          aria-valuenow={v === undefined ? undefined : Math.round(v * 100)}
        >
          <circle cx={18} cy={18} r={r} stroke="var(--surface-muted)" strokeWidth={4} fill="none" />
          <circle
            cx={18}
            cy={18}
            r={r}
            stroke={toneColor(node.props.tone ?? 'accent')}
            strokeWidth={4}
            fill="none"
            strokeDasharray={`${c * (v ?? 0.25)} ${c}`}
            transform="rotate(-90 18 18)"
            className={v === undefined ? 'origin-center animate-spin' : ''}
          />
        </svg>
      );
    }
    return (
      <div className="flex flex-col gap-1">
        {S(node, 'label') && (
          <div className="flex justify-between text-[12px] text-[var(--text-secondary)]">
            <span>{S(node, 'label')}</span>
            {v !== undefined && <span>{Math.round(v * 100)}%</span>}
          </div>
        )}
        <div
          className="h-1.5 overflow-hidden rounded-full bg-[var(--surface-muted)]"
          role="progressbar"
          aria-valuenow={v === undefined ? undefined : Math.round(v * 100)}
        >
          <div
            className={cn('h-full rounded-full', v === undefined && 'w-1/3 animate-pulse')}
            style={{
              width: v === undefined ? undefined : `${Math.min(100, v * 100)}%`,
              background: toneColor(node.props.tone ?? 'accent'),
            }}
          />
        </div>
      </div>
    );
  },
  Meter: ({ node }) => {
    const r = useRender();
    const max = N(node, 'max') ?? 100;
    const v = N(node, 'value') ?? 0;
    return RENDERERS.Progress!({
      node: {
        ...node,
        props: {
          label:
            S(node, 'label') ??
            `${format('number', v, r.locale)} / ${format('number', max, r.locale)}`,
          value: v / (max || 1),
          tone: v / (max || 1) > 0.9 ? 'danger' : v / (max || 1) > 0.75 ? 'warning' : 'accent',
        },
      },
    });
  },
  Counter: ({ node }) => {
    const r = useRender();
    return (
      <span className="text-[22px] font-semibold tabular-nums">
        {format(node.props.format ?? 'number', node.props.value, r.locale)}
      </span>
    );
  },
  QRCode: ({ node }) => (
    <QrCodeView
      value={String(node.props.value ?? '')}
      size={S(node, 'size') === 'lg' ? 220 : S(node, 'size') === 'sm' ? 120 : 160}
    />
  ),

  // ----- actions
  Button: ({ node }) => {
    const r = useRender();
    const fire = useFire();
    const variant = S(node, 'variant') ?? 'secondary';
    const busy = B(node, 'loading') || (r.busyKey !== null && r.busyKey === node.key);
    const disabled = flag(node.props.disabled, r.local.current, false);
    if (variant === 'link')
      return (
        <button
          type="button"
          disabled={disabled}
          className="text-[13px] text-[var(--accent)] hover:underline disabled:opacity-50"
          onClick={() => fire(node, 'onPress')}
        >
          {S(node, 'label')}
        </button>
      );
    return (
      <Button
        type={B(node, 'submit') ? 'submit' : 'button'}
        variant={
          variant === 'primary'
            ? 'primary'
            : variant === 'danger'
              ? 'destructive'
              : variant === 'ghost'
                ? 'plain'
                : 'glass'
        }
        size={S(node, 'size') === 'sm' ? 'sm' : 'md'}
        disabled={disabled || busy}
        icon={
          busy ? (
            <span className="size-3.5 animate-spin rounded-full border-2 border-current border-t-transparent" />
          ) : (
            <Icon name={node.props.icon} className="size-3.5" />
          )
        }
        onClick={() => !B(node, 'submit') && fire(node, 'onPress')}
      >
        {S(node, 'label')}
      </Button>
    );
  },
  IconButton: ({ node }) => {
    const r = useRender();
    const fire = useFire();
    const busy = B(node, 'loading') || (r.busyKey !== null && r.busyKey === node.key);
    return (
      <button
        type="button"
        aria-label={S(node, 'a11yLabel')}
        title={S(node, 'a11yLabel')}
        disabled={flag(node.props.disabled, r.local.current, false) || busy}
        className={cn(
          'grid size-8 place-items-center rounded-[var(--radius-control)] hover:bg-[var(--surface-muted)] disabled:opacity-50',
          S(node, 'variant') === 'danger' && 'text-[var(--red)]',
          S(node, 'variant') === 'primary' && 'bg-[var(--accent)] text-[var(--accent-foreground)]',
        )}
        onClick={() => fire(node, 'onPress')}
      >
        {busy ? (
          <span className="size-3.5 animate-spin rounded-full border-2 border-current border-t-transparent" />
        ) : (
          <Icon name={node.props.icon} />
        )}
      </button>
    );
  },
  ButtonGroup: ({ node }) => (
    <div className="flex items-center gap-2">
      <Kids node={node} />
    </div>
  ),
  SplitButton: ({ node }) => {
    const fire = useFire();
    return (
      <div className="flex items-center">
        <Button
          variant={S(node, 'variant') === 'primary' ? 'primary' : 'glass'}
          onClick={() => fire(node, 'onPress')}
        >
          {S(node, 'label')}
        </Button>
        <MenuItems items={P<unknown[]>(node, 'items') ?? []} label={S(node, 'label') ?? ''} />
      </div>
    );
  },
  CopyButton: ({ node }) => (
    <CopyInline value={String(node.props.value ?? '')} label={S(node, 'label')} />
  ),
  ConfirmButton: ({ node }) => {
    const fire = useFire();
    const [armed, setArmed] = useState(false);
    useEffect(() => {
      if (!armed) return;
      const t = setTimeout(() => setArmed(false), 4000);
      return () => clearTimeout(t);
    }, [armed]);
    return (
      <Button
        variant={armed ? 'destructive' : 'glass'}
        onClick={() => (armed ? (setArmed(false), fire(node, 'onPress')) : setArmed(true))}
      >
        {armed ? (S(node, 'confirmLabel') ?? S(node, 'label')) : S(node, 'label')}
      </Button>
    );
  },
  Menu: ({ node }) => (
    <MenuItems
      items={P<unknown[]>(node, 'items') ?? []}
      label={S(node, 'label') ?? ''}
      icon={S(node, 'icon')}
    />
  ),
  ContextMenu: ({ node }) => {
    const [at, setAt] = useState<{ x: number; y: number } | null>(null);
    const r = useRender();
    const items =
      P<{ label?: string; action?: string | UiAction; separator?: boolean; tone?: string }[]>(
        node,
        'items',
      ) ?? [];
    return (
      <div
        onContextMenu={(e) => {
          e.preventDefault();
          setAt({ x: e.clientX, y: e.clientY });
        }}
      >
        <Kids node={node} />
        {at && (
          <div
            className="fixed inset-0 z-50"
            onClick={() => setAt(null)}
            onContextMenu={(e) => (e.preventDefault(), setAt(null))}
          >
            <div
              className="absolute min-w-40 rounded-[var(--radius-control)] border border-[var(--border)] bg-[var(--surface-solid)] py-1 shadow-lg"
              style={{ left: at.x, top: at.y }}
            >
              {items.map((it, i) =>
                it.separator ? (
                  <hr key={i} className="my-1 border-[var(--border)]" />
                ) : (
                  <button
                    key={i}
                    type="button"
                    className="block w-full px-3 py-1.5 text-left text-[13px] hover:bg-[var(--surface-muted)]"
                    style={{ color: it.tone === 'danger' ? 'var(--red)' : undefined }}
                    onClick={() =>
                      it.action &&
                      r.send(
                        typeof it.action === 'string' ? it.action : it.action.action,
                        typeof it.action === 'string' ? {} : (it.action.args ?? {}),
                      )
                    }
                  >
                    {it.label}
                  </button>
                ),
              )}
            </div>
          </div>
        )}
      </div>
    );
  },

  // ----- inputs
  TextField: ({ node }) => <TextInput node={node} type={S(node, 'type') ?? 'text'} />,
  PasswordField: ({ node }) => {
    const t = useT();
    return (
      <TextInput
        node={{
          ...node,
          props: {
            ...node.props,
            placeholder:
              node.props.placeholder ?? (B(node, 'hasValue') ? t('pluginUi.secretSet') : undefined),
          },
        }}
        type="password"
      />
    );
  },
  SearchField: ({ node }) => {
    const fire = useFire();
    const [value, set, disabled] = useInput<string>(node, '');
    const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    return (
      <input
        type="search"
        className={inputClass}
        value={value ?? ''}
        disabled={disabled}
        placeholder={S(node, 'placeholder')}
        aria-label={S(node, 'a11yLabel') ?? S(node, 'placeholder')}
        onChange={(e) => {
          set(e.target.value);
          clearTimeout(timer.current);
          const v = e.target.value;
          timer.current = setTimeout(
            () => fire(node, 'onSearch', { query: v }),
            N(node, 'debounce') ?? 300,
          );
        }}
        onKeyDown={(e) => e.key === 'Enter' && fire(node, 'onSearch', { query: value })}
      />
    );
  },
  TextArea: ({ node }) => {
    const [value, set, disabled] = useInput<string>(node, '');
    return (
      <textarea
        className={cn(inputClass, 'h-auto py-1.5', B(node, 'mono') && 'font-mono text-[12px]')}
        rows={N(node, 'rows') ?? 4}
        value={value ?? ''}
        maxLength={N(node, 'maxLength')}
        placeholder={S(node, 'placeholder')}
        disabled={disabled}
        onChange={(e) => set(e.target.value)}
        onBlur={() => S(node, 'commit') === 'blur' && set(value, true)}
      />
    );
  },
  CodeField: ({ node }) => {
    const t = useT();
    const [value, set, disabled] = useInput<string>(node, '');
    let error: string | undefined;
    if (B(node, 'validate') && S(node, 'language') === 'json' && value) {
      try {
        JSON.parse(value);
      } catch (e) {
        error = (e as Error).message;
      }
    }
    return (
      <div>
        <textarea
          className={cn(
            inputClass,
            'h-auto py-1.5 font-mono text-[12px]',
            error && 'border-[var(--red)]',
          )}
          rows={N(node, 'rows') ?? 8}
          value={value ?? ''}
          disabled={disabled}
          spellCheck={false}
          onChange={(e) => set(e.target.value)}
        />
        {error && (
          <div className="mt-1 text-[11px] text-[var(--red)]">
            {t('pluginUi.invalidJson')}: {error}
          </div>
        )}
      </div>
    );
  },
  NumberField: ({ node }) => {
    const [value, set, disabled] = useInput<number | null>(node, null);
    return (
      <div className="flex items-center gap-1">
        <input
          type="number"
          className={cn(inputClass, 'tabular-nums')}
          value={value ?? ''}
          min={N(node, 'min')}
          max={N(node, 'max')}
          step={N(node, 'step') ?? (B(node, 'integer') ? 1 : 'any')}
          disabled={disabled}
          onChange={(e) =>
            set(
              e.target.value === ''
                ? null
                : B(node, 'integer')
                  ? Math.round(Number(e.target.value))
                  : Number(e.target.value),
            )
          }
        />
        {S(node, 'unit') && (
          <span className="text-[12px] text-[var(--text-muted)]">{S(node, 'unit')}</span>
        )}
      </div>
    );
  },
  MoneyField: ({ node }) => {
    // the value is nanodollars; the user types dollars
    const [value, set, disabled] = useInput<number | null>(node, null);
    return (
      <div className="flex items-center gap-1">
        <span className="text-[12px] text-[var(--text-muted)]">$</span>
        <input
          type="number"
          step="any"
          className={cn(inputClass, 'tabular-nums')}
          disabled={disabled}
          value={value === null || value === undefined ? '' : value / 1e9}
          onChange={(e) =>
            set(e.target.value === '' ? null : Math.round(Number(e.target.value) * 1e9))
          }
        />
      </div>
    );
  },
  Switch: ({ node }) => {
    const [value, set, disabled] = useInput<boolean>(node, false);
    return (
      <AstraSwitch
        checked={Boolean(value)}
        disabled={disabled}
        label={S(node, 'label') ?? S(node, 'a11yLabel')}
        onChange={(v) => set(v, true)}
      />
    );
  },
  Checkbox: ({ node }) => {
    const [value, set, disabled] = useInput<boolean>(node, false);
    return (
      <label className="inline-flex items-center gap-2 text-[13px]">
        <input
          type="checkbox"
          className="accent-[var(--accent)]"
          checked={Boolean(value)}
          disabled={disabled}
          ref={(el) => {
            if (el) el.indeterminate = B(node, 'indeterminate');
          }}
          onChange={(e) => set(e.target.checked, true)}
        />
        {S(node, 'label')}
      </label>
    );
  },
  RadioGroup: ({ node }) => {
    const [value, set, disabled] = useInput<string>(node, '');
    return (
      <div
        role="radiogroup"
        className={cn('flex gap-3', S(node, 'direction') !== 'row' && 'flex-col gap-1.5')}
      >
        {useOptions(node).map((o) => (
          <label key={o.value} className="inline-flex items-center gap-2 text-[13px]">
            <input
              type="radio"
              className="accent-[var(--accent)]"
              checked={value === o.value}
              disabled={disabled || o.disabled}
              onChange={() => set(o.value, true)}
            />
            {o.label}
          </label>
        ))}
      </div>
    );
  },
  RadioCards: ({ node }) => {
    const [value, set, disabled] = useInput<string>(node, '');
    return (
      <div
        role="radiogroup"
        className="grid gap-2"
        style={{ gridTemplateColumns: `repeat(${N(node, 'columns') ?? 2}, minmax(0, 1fr))` }}
      >
        {useOptions(node).map((o) => (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={value === o.value}
            disabled={disabled || o.disabled}
            className={cn(
              'card flex items-start gap-2 p-3 text-left',
              value === o.value && 'border-[var(--accent)] bg-[var(--accent-subtle)]',
            )}
            onClick={() => set(o.value, true)}
          >
            <Icon name={o.icon} className="mt-0.5 size-4 text-[var(--accent)]" />
            <span>
              <span className="block text-[13px] font-medium">{o.title ?? o.label}</span>
              {o.description && (
                <span className="block text-[12px] text-[var(--text-muted)]">{o.description}</span>
              )}
            </span>
          </button>
        ))}
      </div>
    );
  },
  Segmented: ({ node }) => {
    const [value, set, disabled] = useInput<string>(node, '');
    return (
      <div
        role="radiogroup"
        className="inline-flex rounded-[var(--radius-control)] bg-[var(--surface-muted)] p-0.5"
      >
        {useOptions(node).map((o) => (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={value === o.value}
            disabled={disabled}
            className={cn(
              'rounded-[calc(var(--radius-control)-2px)] px-3 py-1 text-[12px]',
              value === o.value
                ? 'bg-[var(--surface-solid)] shadow-sm'
                : 'text-[var(--text-secondary)]',
            )}
            onClick={() => set(o.value, true)}
          >
            {o.label}
          </button>
        ))}
      </div>
    );
  },
  Select: ({ node }) => {
    const [value, set, disabled] = useInput<string | null>(node, null);
    const opts: SelectOption<string>[] = useOptions(node).map((o) => ({
      value: o.value,
      label: o.label,
      detail: o.description,
      disabled: o.disabled,
    }));
    return (
      <Select
        value={value}
        options={opts}
        disabled={disabled}
        placeholder={S(node, 'placeholder')}
        ariaLabel={S(node, 'a11yLabel') ?? S(node, 'placeholder')}
        onChange={(v) => set(v, true)}
      />
    );
  },
  Combobox: ({ node }) => {
    const fire = useFire();
    const [value, set, disabled] = useInput<string>(node, '');
    const id = useMemo(() => 'cb' + Math.random().toString(36).slice(2), []);
    return (
      <>
        <input
          className={inputClass}
          list={id}
          value={value ?? ''}
          disabled={disabled}
          placeholder={S(node, 'placeholder')}
          onChange={(e) => {
            set(e.target.value);
            fire(node, 'onSearch', { query: e.target.value });
          }}
        />
        <datalist id={id}>
          {useOptions(node).map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </datalist>
      </>
    );
  },
  MultiSelect: ({ node }) => {
    const [value, set, disabled] = useInput<string[]>(node, []);
    const max = N(node, 'max');
    const current = Array.isArray(value) ? value : [];
    return (
      <div className="flex flex-wrap gap-1.5">
        {useOptions(node).map((o) => {
          const on = current.includes(o.value);
          return (
            <button
              key={o.value}
              type="button"
              aria-pressed={on}
              disabled={disabled || (!on && max !== undefined && current.length >= max)}
              className={cn(
                'rounded-full border px-2.5 py-0.5 text-[12px]',
                on ? 'border-[var(--accent)] bg-[var(--accent-subtle)]' : 'border-[var(--border)]',
              )}
              onClick={() =>
                set(on ? current.filter((x) => x !== o.value) : [...current, o.value], true)
              }
            >
              {o.label}
            </button>
          );
        })}
      </div>
    );
  },
  ChipGroup: ({ node }) => {
    const multiple = node.props.multiple !== false;
    const [value, set, disabled] = useInput<string[] | string>(node, multiple ? [] : '');
    const current = Array.isArray(value) ? value : value ? [value] : [];
    return (
      <div className="flex flex-wrap gap-1.5">
        {useOptions(node).map((o) => {
          const on = current.includes(o.value);
          return (
            <button
              key={o.value}
              type="button"
              aria-pressed={on}
              disabled={disabled}
              className={cn(
                'rounded-full border px-2.5 py-0.5 text-[12px]',
                on ? 'border-[var(--accent)] bg-[var(--accent-subtle)]' : 'border-[var(--border)]',
              )}
              onClick={() =>
                set(
                  multiple
                    ? on
                      ? current.filter((x) => x !== o.value)
                      : [...current, o.value]
                    : o.value,
                  true,
                )
              }
            >
              {o.label}
            </button>
          );
        })}
      </div>
    );
  },
  TagInput: ({ node }) => {
    const [value, set, disabled] = useInput<string[]>(node, []);
    return (
      <TagInputView
        value={Array.isArray(value) ? value : []}
        onChange={(v) => set(v, true)}
        suggestions={P<string[]>(node, 'suggestions')}
        max={N(node, 'max')}
        pattern={S(node, 'validate')}
        placeholder={S(node, 'placeholder')}
        disabled={disabled}
      />
    );
  },
  Slider: ({ node }) => {
    const [value, set, disabled] = useInput<number>(node, N(node, 'min') ?? 0);
    return (
      <div className="flex items-center gap-2">
        <input
          type="range"
          className="flex-1 accent-[var(--accent)]"
          min={N(node, 'min') ?? 0}
          max={N(node, 'max') ?? 100}
          step={N(node, 'step') ?? 1}
          value={value ?? 0}
          disabled={disabled}
          onChange={(e) => set(Number(e.target.value))}
          onMouseUp={() => set(value, true)}
        />
        <span className="w-10 text-right text-[12px] tabular-nums">{value}</span>
      </div>
    );
  },
  DatePicker: ({ node }) => {
    const r = useRender();
    const [value, set, disabled] = useInput<string | null>(node, null);
    return (
      <DatePickerView
        value={value}
        onChange={(v) => set(v, true)}
        locale={r.locale}
        weekStart={r.weekStart}
        min={node.props.min}
        max={node.props.max}
        withTime={B(node, 'withTime')}
        minuteStep={N(node, 'minuteStep')}
        clearable={B(node, 'clearable')}
        disabled={disabled}
        placeholder={S(node, 'placeholder')}
        label={S(node, 'a11yLabel')}
      />
    );
  },
  DateRangePicker: ({ node }) => {
    const r = useRender();
    const t = useT();
    const [value, set, disabled] = useInput<{ from?: string; to?: string } | null>(node, null);
    return (
      <DateRangePickerView
        value={value}
        onChange={(v) => set(v, true)}
        locale={r.locale}
        weekStart={r.weekStart}
        min={node.props.min}
        max={node.props.max}
        presets={P<unknown[]>(node, 'presets')}
        maxSpanDays={N(node, 'maxSpanDays')}
        disabled={disabled}
        presetLabel={(p) => t(`pluginUi.preset.${p}` as MessageKey)}
        label={S(node, 'a11yLabel')}
      />
    );
  },
  TimePicker: ({ node }) => {
    const [value, set, disabled] = useInput<string>(node, '09:00');
    return (
      <TimeInput
        value={value ?? ''}
        step={N(node, 'minuteStep')}
        disabled={disabled}
        onChange={(v) => set(v, true)}
      />
    );
  },
  KeyValueEditor: ({ node }) => {
    const t = useT();
    const [value, set, disabled] = useInput<{ key: string; value: string }[]>(node, []);
    return (
      <KeyValueEditorView
        value={Array.isArray(value) ? value : []}
        onChange={(v) => set(v)}
        keyPlaceholder={S(node, 'keyPlaceholder')}
        valuePlaceholder={S(node, 'valuePlaceholder')}
        secretValues={B(node, 'secretValues')}
        disabled={disabled}
        addLabel={t('pluginUi.add')}
      />
    );
  },
  ListEditor: ({ node }) => {
    const t = useT();
    const [value, set, disabled] = useInput<Record<string, unknown>[]>(node, []);
    const fields = P<{ name: string; label?: string; type?: string; placeholder?: string }[]>(
      node,
      'item',
    ) ?? [{ name: 'value' }];
    const list = Array.isArray(value) ? value : [];
    const max = N(node, 'max');
    return (
      <div className="flex flex-col gap-1.5">
        {list.map((row, i) => (
          <div key={i} className="flex items-center gap-1.5">
            {fields.map((f) => (
              <input
                key={f.name}
                className={inputClass}
                placeholder={f.placeholder ?? f.label}
                type={f.type === 'number' ? 'number' : 'text'}
                value={String(row[f.name] ?? '')}
                disabled={disabled}
                onChange={(e) =>
                  set(
                    list.map((r, j) =>
                      j === i
                        ? {
                            ...r,
                            [f.name]: f.type === 'number' ? Number(e.target.value) : e.target.value,
                          }
                        : r,
                    ),
                  )
                }
              />
            ))}
            {B(node, 'reorderable') && i > 0 && (
              <button
                type="button"
                className="text-[var(--text-muted)]"
                aria-label="move up"
                onClick={() =>
                  set(list.map((r, j) => (j === i - 1 ? list[i]! : j === i ? list[i - 1]! : r)))
                }
              >
                ↑
              </button>
            )}
            <button
              type="button"
              disabled={disabled}
              className="text-[var(--text-muted)]"
              aria-label="remove"
              onClick={() => set(list.filter((_, j) => j !== i))}
            >
              ×
            </button>
          </div>
        ))}
        {(max === undefined || list.length < max) && (
          <button
            type="button"
            disabled={disabled}
            className="self-start text-[12px] text-[var(--accent)]"
            onClick={() =>
              set([
                ...list,
                Object.fromEntries(fields.map((f) => [f.name, f.type === 'number' ? 0 : ''])),
              ])
            }
          >
            + {S(node, 'addLabel') ?? t('pluginUi.add')}
          </button>
        )}
      </div>
    );
  },
  FileDrop: ({ node }) => {
    const r = useRender();
    const t = useT();
    const fire = useFire();
    const [over, setOver] = useState(false);
    const input = useRef<HTMLInputElement>(null);
    const take = async (files: FileList | null) => {
      if (!files?.length) return;
      const max = N(node, 'maxSize');
      const refs = [];
      for (const f of Array.from(files).slice(0, B(node, 'multiple') ? 20 : 1)) {
        if (max && f.size > max) continue;
        refs.push(await r.upload(f));
      }
      fire(node, 'onDrop', { files: refs });
    };
    return (
      <div
        className={cn(
          'grid cursor-pointer place-items-center rounded-[var(--radius-panel)] border-2 border-dashed px-4 py-6 text-[12px] text-[var(--text-muted)]',
          over ? 'border-[var(--accent)] bg-[var(--accent-subtle)]' : 'border-[var(--border)]',
        )}
        onDragOver={(e) => (e.preventDefault(), setOver(true))}
        onDragLeave={() => setOver(false)}
        onDrop={(e) => {
          e.preventDefault();
          setOver(false);
          void take(e.dataTransfer.files);
        }}
        onClick={() => input.current?.click()}
      >
        {S(node, 'label') ?? t('pluginUi.dropFiles')}
        <input
          ref={input}
          type="file"
          hidden
          accept={S(node, 'accept')}
          multiple={B(node, 'multiple')}
          onChange={(e) => void take(e.target.files)}
        />
      </div>
    );
  },
  Form: ({ node }) => {
    const r = useRender();
    const t = useT();
    const fire = useFire();
    const [baseline] = useState(() => JSON.stringify(r.local.current));
    const dirty = B(node, 'dirtyBar') && JSON.stringify(r.local.current) !== baseline;
    return (
      <form
        className={cn('flex flex-col gap-4', S(node, 'layout') === 'horizontal' && 'gap-3')}
        onSubmit={(e) => {
          e.preventDefault();
          if (!(e.currentTarget as HTMLFormElement).reportValidity()) return;
          fire(node, 'onSubmit', { values: r.local.current });
        }}
      >
        <Kids node={node} />
        {dirty && (
          <div className="sticky bottom-0 flex items-center justify-end gap-2 border-t border-[var(--border)] bg-[var(--background)] py-2">
            <span className="mr-auto text-[12px] text-[var(--text-muted)]">
              {t('pluginUi.unsaved')}
            </span>
            <Button type="submit" variant="primary">
              {t('pluginUi.save')}
            </Button>
          </div>
        )}
      </form>
    );
  },
  Field: ({ node }) => {
    const r = useRender();
    const child = node.children?.[0];
    const name = child ? S(child, 'name') : undefined;
    const error = S(node, 'error') ?? (name ? r.fieldErrors[name] : undefined);
    return (
      <Field
        label={(S(node, 'label') ?? '') + (B(node, 'required') ? ' *' : '')}
        hint={S(node, 'hint') ?? S(node, 'info')}
        error={error}
      >
        <Kids node={node} />
      </Field>
    );
  },
  FieldGroup: ({ node }) => (
    <fieldset className="flex flex-col gap-3">
      {S(node, 'title') && (
        <legend className="mb-1 text-[12px] font-medium text-[var(--text-secondary)]">
          {S(node, 'title')}
        </legend>
      )}
      <div
        className="grid gap-3"
        style={{ gridTemplateColumns: `repeat(${N(node, 'columns') ?? 1}, minmax(0, 1fr))` }}
      >
        <Kids node={node} />
      </div>
    </fieldset>
  ),

  // ----- data
  Table: ({ node }) => <TableView node={node} />,
  List: ({ node }) => <ListView node={node} />,
  Tree: ({ node }) => {
    const r = useRender();
    const t = useT();
    const fire = useFire();
    const source = node.props.source as { collection?: string } | undefined;
    const col = source?.collection ? r.collections[source.collection] : undefined;
    const raw = (col ? col.items : node.props.nodes) as TreeNodeData[] | undefined;
    const [loading, setLoading] = useState<Set<string>>(new Set());
    const withChildren = (list: TreeNodeData[]): TreeNodeData[] =>
      list.map((n) => ({
        ...n,
        icon:
          n.icon && typeof n.icon === 'string' ? (
            <Icon name={n.icon} className="size-3.5 text-[var(--text-secondary)]" />
          ) : undefined,
        children: col?.children[n.key]
          ? withChildren(col.children[n.key] as TreeNodeData[])
          : n.children
            ? withChildren(n.children)
            : undefined,
        lazy: n.lazy && !col?.children[n.key],
      }));
    useEffect(() => setLoading(new Set()), [col]);
    const nodeActions = P<{ label: string; action: string; tone?: string }[]>(node, 'nodeActions');
    return (
      <TreeView
        nodes={withChildren(raw ?? [])}
        expanded={P<string[]>(node, 'expanded')}
        selected={
          P<string[]>(node, 'selected') ??
          (S(node, 'name') ? (r.local.current[S(node, 'name')!] as string[]) : undefined) ??
          []
        }
        selection={(S(node, 'selection') as 'none') ?? 'none'}
        search={B(node, 'search')}
        dense={B(node, 'dense')}
        loadingKeys={loading}
        emptyLabel={t('pluginUi.empty')}
        searchLabel={t('pluginUi.search')}
        onExpand={(key, expanded) => {
          const n = findNode(raw ?? [], key);
          if (expanded && n?.lazy && !col?.children[key]) setLoading((s) => new Set(s).add(key));
          fire(node, 'onExpand', { key, expanded });
        }}
        onSelect={(keys) => {
          const name = S(node, 'name');
          if (name) r.setLocal(name, keys);
          fire(node, 'onSelect', { keys, value: keys[0] });
        }}
        onPress={(key) => fire(node, 'onNodePress', { key })}
        menu={
          nodeActions?.length
            ? (n) => (
                <MenuItems
                  label="…"
                  items={nodeActions.map((a) => ({
                    label: a.label,
                    tone: a.tone,
                    action: { action: a.action, args: { key: n.key } },
                  }))}
                  iconOnly
                />
              )
            : undefined
        }
      />
    );
  },
  Pagination: ({ node }) => {
    const fire = useFire();
    const page = N(node, 'page') ?? 1;
    const pages = Math.max(1, Math.ceil((N(node, 'total') ?? 0) / (N(node, 'pageSize') ?? 20)));
    return (
      <Pager
        page={page}
        pages={pages}
        onPage={(p) => fire(node, 'onPage', { page: p, pageSize: N(node, 'pageSize') ?? 20 })}
      />
    );
  },
  LogView: ({ node }) => {
    const r = useRender();
    const source = node.props.source as { collection?: string } | undefined;
    const lines = (
      source?.collection ? r.collections[source.collection]?.items : node.props.lines
    ) as { level?: string; text?: string; at?: string }[] | undefined;
    const levels = P<string[]>(node, 'levels');
    return (
      <LogViewer
        lines={(lines ?? [])
          .filter((l) => !levels || !l.level || levels.includes(l.level))
          .slice(-2000)}
        follow={node.props.follow !== false}
        wrap={B(node, 'wrap')}
        height={N(node, 'height') ?? 320}
      />
    );
  },
  MetricCard: ({ node }) => {
    const r = useRender();
    const delta = N(node, 'delta');
    return (
      <div className="card flex flex-col gap-1 p-4">
        <div className="flex items-center gap-1.5 text-[12px] text-[var(--text-secondary)]">
          <Icon name={node.props.icon} className="size-3.5" />
          {S(node, 'label')}
        </div>
        <div className="flex items-end justify-between gap-2">
          <span
            className="text-[22px] font-semibold tabular-nums"
            style={{ color: node.props.tone ? toneColor(node.props.tone) : undefined }}
          >
            {format(node.props.format, node.props.value, r.locale)}
          </span>
          {Array.isArray(node.props.trend) && (
            <SparklineView
              data={(node.props.trend as unknown[]).map(Number)}
              tone={S(node, 'tone')}
              area
            />
          )}
        </div>
        {delta !== undefined && (
          <span
            className="text-[12px]"
            style={{ color: delta >= 0 ? 'var(--green)' : 'var(--red)' }}
          >
            {delta >= 0 ? '▲' : '▼'} {format('percent', Math.abs(delta), r.locale)}
          </span>
        )}
      </div>
    );
  },
  Sparkline: ({ node }) => (
    <SparklineView
      data={(P<unknown[]>(node, 'data') ?? []).map(Number)}
      tone={S(node, 'tone')}
      area={B(node, 'area')}
    />
  ),
  Chart: ({ node }) => {
    const r = useRender();
    const t = useT();
    const fire = useFire();
    const source = node.props.source as { collection?: string } | undefined;
    let data = (source?.collection ? r.collections[source.collection]?.items : node.props.data) as
      Record<string, unknown>[] | undefined;
    const win = N(node, 'window');
    if (data && win) data = data.slice(-win);
    if (B(node, 'loading')) return <Spinner lines={4} />;
    const height =
      N(node, 'height') ??
      { xs: 120, sm: 160, md: 240, lg: 320, xl: 420 }[S(node, 'height') ?? 'md'] ??
      240;
    const spec = {
      ...(node.props as unknown as ChartSpec),
      data: data ?? [],
      height,
      series: (node.props.series as ChartSpec['series']) ?? [],
    };
    return (
      <ChartView
        spec={spec}
        locale={r.locale}
        emptyLabel={t('pluginUi.empty')}
        onSelect={
          act(node, 'onSelect')
            ? (datum, series) => fire(node, 'onSelect', { datum, series })
            : undefined
        }
        onRange={
          act(node, 'onRange') ? (from, to) => fire(node, 'onRange', { from, to }) : undefined
        }
      />
    );
  },
  Heatmap: ({ node }) => {
    const days = P<{ date: string; value: number }[]>(node, 'days') ?? [];
    const max = Math.max(1, ...days.map((d) => d.value));
    return (
      <div className="grid grid-flow-col grid-rows-7 gap-0.5">
        {days.map((d) => (
          <span
            key={d.date}
            title={`${d.date}: ${d.value}`}
            className="size-2.5 rounded-[2px]"
            style={{
              background: d.value ? toneColor(node.props.tone ?? 'accent') : 'var(--surface-muted)',
              opacity: d.value ? 0.25 + (0.75 * d.value) / max : 1,
            }}
          />
        ))}
      </div>
    );
  },
  Gauge: ({ node }) => {
    const r = useRender();
    return (
      <GaugeView
        value={N(node, 'value') ?? 0}
        max={N(node, 'max')}
        label={S(node, 'label')}
        thresholds={P(node, 'thresholds')}
        locale={r.locale}
      />
    );
  },
  Canvas: ({ node }) => {
    const r = useRender();
    return (
      <CanvasView
        ops={P(node, 'draw') ?? []}
        viewBox={P(node, 'viewBox')}
        height={
          N(node, 'height') ?? { sm: 160, md: 240, lg: 320 }[S(node, 'height') ?? 'md'] ?? 240
        }
        onHit={(hit) => hit.action && r.send(hit.action, hit.args ?? {})}
      />
    );
  },

  // ----- overlays (declared in the tree; the effect forms open their own views)
  Sheet: ({ node }) => <InlineOverlay node={node} />,
  Dialog: ({ node }) => <InlineOverlay node={node} />,
  Popover: ({ node }) => {
    const [open, setOpen] = useState(false);
    return (
      <span className="relative inline-block">
        <span onClick={() => setOpen((o) => !o)}>
          {node.props.trigger ? (
            <NodeView node={node.props.trigger as UiNode} />
          ) : (
            <MoreHorizontal className="size-4" />
          )}
        </span>
        {open && (
          <div
            className="absolute z-50 mt-1 min-w-48 rounded-[var(--radius-panel)] border border-[var(--border)] bg-[var(--surface-solid)] p-3 shadow-lg"
            onMouseLeave={() => setOpen(false)}
          >
            <Kids node={node} />
          </div>
        )}
      </span>
    );
  },

  // ----- domain
  ModelPicker: ({ node }) => <DomainPicker node={node} kind="model" />,
  ProviderPicker: ({ node }) => <DomainPicker node={node} kind="provider" />,
  AccountPicker: ({ node }) => <DomainPicker node={node} kind="account" />,
  ClientPicker: ({ node }) => <DomainPicker node={node} kind="client" />,
  TokenPicker: ({ node }) => <DomainPicker node={node} kind="token" />,
  ProtocolPicker: ({ node }) => <DomainPicker node={node} kind="protocol" />,
  ModelLabel: ({ node }) => {
    const models = useModels();
    const m = models.data?.find((x) => x.id === node.props.id);
    return (
      <span className="font-mono text-[12px]">{m?.displayName ?? String(node.props.id ?? '')}</span>
    );
  },
  ProviderLabel: ({ node }) => {
    const providers = useProviders();
    return (
      <span className="text-[13px]">
        {providers.data?.find((p) => p.id === node.props.id)?.name ?? String(node.props.id ?? '')}
      </span>
    );
  },
  ClientLabel: ({ node }) => {
    const clients = useClients();
    return (
      <span className="text-[13px]">
        {clients.data?.find((c) => c.kind === node.props.id)?.name ?? String(node.props.id ?? '')}
      </span>
    );
  },
  Money: ({ node }) => {
    const r = useRender();
    return <span className="tabular-nums">{format('money', node.props.value, r.locale)}</span>;
  },
  RequestLink: ({ node }) => {
    const r = useRender();
    return (
      <a
        className="font-mono text-[12px] text-[var(--accent)] hover:underline"
        href={`#/requests?id=${encodeURIComponent(String(node.props.id ?? ''))}`}
        onClick={(e) => (
          e.preventDefault(),
          r.link(`/requests?id=${encodeURIComponent(String(node.props.id ?? ''))}`)
        )}
      >
        {S(node, 'label') ?? String(node.props.id ?? '')}
      </a>
    );
  },
  TraceLink: ({ node }) => {
    const r = useRender();
    return (
      <a
        className="font-mono text-[12px] text-[var(--accent)] hover:underline"
        href={`#/traces/requests/${encodeURIComponent(String(node.props.id ?? ''))}`}
        onClick={(e) => (
          e.preventDefault(),
          r.link(`/traces/requests/${encodeURIComponent(String(node.props.id ?? ''))}`)
        )}
      >
        {S(node, 'label') ?? String(node.props.id ?? '')}
      </a>
    );
  },
  UsageBar: ({ node }) => {
    const r = useRender();
    const used = N(node, 'used') ?? 0;
    const limit = N(node, 'limit') ?? 0;
    return RENDERERS.Progress!({
      node: {
        ...node,
        props: {
          label: `${S(node, 'label') ?? ''} ${format(node.props.format ?? 'number', used, r.locale)}${limit ? ' / ' + format(node.props.format ?? 'number', limit, r.locale) : ''}`,
          value: limit ? used / limit : undefined,
          tone: limit && used / limit > 0.9 ? 'danger' : 'accent',
        },
      },
    });
  },
};

function findNode(list: TreeNodeData[], key: string): TreeNodeData | undefined {
  for (const n of list) {
    if (n.key === key) return n;
    const inner = n.children && findNode(n.children, key);
    if (inner) return inner;
  }
  return undefined;
}

function assetUrl(pluginId: string, src: string): string {
  if (src.startsWith('data:image/')) return src;
  if (src.startsWith('https://')) return src;
  return `/api/plugins/v2/${encodeURIComponent(pluginId)}/assets/${src.replace(/^\.?\//, '')}`;
}

function CopyInline({ value, label }: { value: string; label?: string }) {
  const [done, setDone] = useState(false);
  return (
    <button
      type="button"
      className="inline-flex items-center gap-1 text-[12px] text-[var(--text-secondary)] hover:text-[var(--accent)]"
      aria-label={label ?? 'copy'}
      onClick={() => {
        void navigator.clipboard?.writeText(value);
        setDone(true);
        setTimeout(() => setDone(false), 1200);
      }}
    >
      {done ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
      {label}
    </button>
  );
}

function MenuItems({
  items,
  label,
  icon,
  iconOnly,
}: {
  items: unknown[];
  label: string;
  icon?: string;
  iconOnly?: boolean;
}) {
  const r = useRender();
  const list = items
    .filter(
      (
        i,
      ): i is {
        label: string;
        action?: string | UiAction;
        tone?: string;
        icon?: string;
        disabled?: boolean;
        separator?: boolean;
      } => typeof i === 'object' && i !== null,
    )
    .filter((i) => !i.separator);
  return (
    <Menu
      label={label}
      iconOnly={iconOnly ?? !label}
      icon={icon ? <Icon name={icon} /> : <MoreHorizontal className="size-4" />}
      items={list.map((i, k) => ({
        id: String(k),
        label: i.label,
        icon: i.icon ? <Icon name={i.icon} className="size-3.5" /> : undefined,
        destructive: i.tone === 'danger',
        disabled: i.disabled,
        onSelect: () =>
          i.action &&
          r.send(
            typeof i.action === 'string' ? i.action : i.action.action,
            typeof i.action === 'string' ? {} : (i.action.args ?? {}),
          ),
      }))}
    />
  );
}

function Pager({
  page,
  pages,
  onPage,
}: {
  page: number;
  pages: number;
  onPage: (p: number) => void;
}) {
  if (pages <= 1) return null;
  return (
    <div className="flex items-center justify-end gap-1 text-[12px]">
      <button
        type="button"
        disabled={page <= 1}
        className="rounded px-2 py-1 hover:bg-[var(--surface-muted)] disabled:opacity-40"
        onClick={() => onPage(page - 1)}
      >
        ‹
      </button>
      <span className="tabular-nums text-[var(--text-secondary)]">
        {page} / {pages}
      </span>
      <button
        type="button"
        disabled={page >= pages}
        className="rounded px-2 py-1 hover:bg-[var(--surface-muted)] disabled:opacity-40"
        onClick={() => onPage(page + 1)}
      >
        ›
      </button>
    </div>
  );
}

interface Column {
  key: string;
  title?: string;
  format?: string;
  sortable?: boolean;
  width?: number;
  align?: 'start' | 'center' | 'end';
  mono?: boolean;
  truncate?: boolean;
  priority?: 'low' | 'normal' | 'high';
  cell?: {
    type?: string;
    toneBy?: Record<string, string>;
    labelBy?: Record<string, string>;
    action?: string;
  };
}

function TableView({ node }: { node: UiNode }) {
  const r = useRender();
  const t = useT();
  const fire = useFire();
  const source = node.props.source as { collection?: string } | undefined;
  const col = source?.collection ? r.collections[source.collection] : undefined;
  const rawRows = (col ? col.items : node.props.rows) as Record<string, unknown>[] | undefined;
  const rows = useMemo(() => rawRows ?? [], [rawRows]);
  const rowKey = S(node, 'rowKey') ?? 'id';
  const columns = (P<Column[]>(node, 'columns') ?? []).filter(
    (c) => !(r.width === 'compact' && c.priority === 'low'),
  );
  const sort = P<{ key: string; dir: 'asc' | 'desc' }>(node, 'sort');
  const [localSort, setLocalSort] = useState(sort);
  const selection = S(node, 'selection') ?? 'none';
  const [selected, setSelected] = useState<string[]>(P<string[]>(node, 'selected') ?? []);
  const total = N(node, 'total') ?? (col?.meta.total as number | undefined);
  const page = N(node, 'page') ?? (col?.meta.page as number | undefined) ?? 1;
  const pageSize = N(node, 'pageSize') ?? 20;
  const serverSide = total !== undefined;
  const sorted = useMemo(() => {
    if (serverSide || !localSort) return rows;
    return [...rows].sort((a, b) => {
      const x = a[localSort.key];
      const y = b[localSort.key];
      const c =
        typeof x === 'number' && typeof y === 'number'
          ? x - y
          : String(x ?? '').localeCompare(String(y ?? ''));
      return localSort.dir === 'desc' ? -c : c;
    });
  }, [rows, localSort, serverSide]);
  const rowActions = P<{ label: string; icon?: string; tone?: string; action: string }[]>(
    node,
    'rowActions',
  );
  const bulk = P<{ label: string; action: string }[]>(node, 'bulkActions');
  if (B(node, 'loading') && !rows.length) return <Spinner lines={4} />;
  if (!rows.length)
    return node.props.empty ? (
      <NodeView node={node.props.empty as UiNode} />
    ) : (
      <div className="py-6 text-center text-[12px] text-[var(--text-muted)]">
        {t('pluginUi.empty')}
      </div>
    );
  const cell = (c: Column, row: Record<string, unknown>) => {
    const v = row[c.key];
    const text = c.format
      ? format(c.format, v, r.locale)
      : v === null || v === undefined
        ? ''
        : typeof v === 'object'
          ? JSON.stringify(v)
          : String(v);
    switch (c.cell?.type) {
      case 'badge':
        return (
          <Badge tone={tone(c.cell.toneBy?.[String(v)])}>
            {c.cell.labelBy?.[String(v)] ?? text}
          </Badge>
        );
      case 'status':
        return (
          <span className="inline-flex items-center gap-1.5">
            <span
              className="size-2 rounded-full"
              style={{ background: toneColor(c.cell.toneBy?.[String(v)]) }}
            />
            {c.cell.labelBy?.[String(v)] ?? text}
          </span>
        );
      case 'icon':
        return <Icon name={v} />;
      case 'link':
        return (
          <button
            type="button"
            className="text-[var(--accent)] hover:underline"
            onClick={(e) => (e.stopPropagation(), c.cell?.action && r.send(c.cell.action, { row }))}
          >
            {text}
          </button>
        );
      case 'progress':
        return (
          <div className="h-1.5 w-24 overflow-hidden rounded-full bg-[var(--surface-muted)]">
            <div
              className="h-full bg-[var(--accent)]"
              style={{ width: `${Math.min(100, Number(v) * 100)}%` }}
            />
          </div>
        );
      case 'copy':
        return (
          <span className="inline-flex items-center gap-1 font-mono text-[12px]">
            {text}
            <CopyInline value={String(v ?? '')} />
          </span>
        );
      case 'avatar':
        return (
          <span className="inline-flex items-center gap-1.5">
            <span className="grid size-5 place-items-center rounded-full bg-[var(--accent-subtle)] text-[10px] text-[var(--accent)]">
              {text.slice(0, 1).toUpperCase()}
            </span>
            {text}
          </span>
        );
      case 'tags':
        return (
          <span className="flex flex-wrap gap-1">
            {(Array.isArray(v) ? v : []).map((x) => (
              <Badge key={String(x)}>{String(x)}</Badge>
            ))}
          </span>
        );
      default:
        return (
          <span
            className={cn(
              c.mono && 'font-mono text-[12px]',
              c.truncate && 'block max-w-[24ch] truncate',
            )}
            title={c.truncate ? text : undefined}
          >
            {text}
          </span>
        );
    }
  };
  return (
    <div className="flex flex-col gap-2">
      {bulk?.length && selected.length > 0 && (
        <div className="flex items-center gap-2 text-[12px]">
          <span className="text-[var(--text-secondary)]">
            {t('pluginUi.selected', { count: selected.length })}
          </span>
          {bulk.map((b) => (
            <Button key={b.action} size="sm" onClick={() => r.send(b.action, { keys: selected })}>
              {b.label}
            </Button>
          ))}
        </div>
      )}
      <div className="card overflow-x-auto">
        <table className="w-full text-[13px]">
          <thead
            className={cn(B(node, 'stickyHeader') && 'sticky top-0 bg-[var(--surface-solid)]')}
          >
            <tr className="border-b border-[var(--border)] text-left text-[12px] text-[var(--text-secondary)]">
              {selection === 'multiple' && (
                <th className="w-8 px-3">
                  <input
                    type="checkbox"
                    className="accent-[var(--accent)]"
                    checked={selected.length === sorted.length}
                    onChange={(e) =>
                      setSelected(e.target.checked ? sorted.map((x) => String(x[rowKey])) : [])
                    }
                  />
                </th>
              )}
              {columns.map((c) => (
                <th
                  key={c.key}
                  className={cn(
                    'px-3 font-medium',
                    B(node, 'dense') ? 'py-1.5' : 'py-2',
                    c.align === 'end' && 'text-right',
                    c.align === 'center' && 'text-center',
                    c.sortable && 'cursor-pointer select-none',
                  )}
                  style={{ width: c.width }}
                  aria-sort={
                    localSort?.key === c.key
                      ? localSort.dir === 'asc'
                        ? 'ascending'
                        : 'descending'
                      : undefined
                  }
                  onClick={() => {
                    if (!c.sortable) return;
                    const next = {
                      key: c.key,
                      dir: localSort?.key === c.key && localSort.dir === 'asc' ? 'desc' : 'asc',
                    } as const;
                    setLocalSort(next);
                    fire(node, 'onSort', { sort: next });
                  }}
                >
                  {c.title ?? c.key}
                  {localSort?.key === c.key && (localSort.dir === 'asc' ? ' ↑' : ' ↓')}
                </th>
              ))}
              {rowActions?.length ? <th className="w-10" /> : null}
            </tr>
          </thead>
          <tbody>
            {sorted.map((row, i) => {
              const k = String(row[rowKey] ?? i);
              const isSel = selected.includes(k);
              return (
                <tr
                  key={k}
                  className={cn(
                    'border-b border-[var(--border)] last:border-0',
                    act(node, 'onRowPress') || selection === 'single'
                      ? 'cursor-pointer hover:bg-[var(--surface-muted)]'
                      : '',
                    isSel && 'bg-[var(--accent-subtle)]',
                  )}
                  onClick={() => {
                    if (selection === 'single') {
                      setSelected([k]);
                      fire(node, 'onSelect', { keys: [k], row });
                    }
                    fire(node, 'onRowPress', { row, key: k });
                  }}
                >
                  {selection === 'multiple' && (
                    <td className="px-3" onClick={(e) => e.stopPropagation()}>
                      <input
                        type="checkbox"
                        className="accent-[var(--accent)]"
                        checked={isSel}
                        onChange={() => {
                          const next = isSel ? selected.filter((x) => x !== k) : [...selected, k];
                          setSelected(next);
                          fire(node, 'onSelect', { keys: next });
                        }}
                      />
                    </td>
                  )}
                  {columns.map((c) => (
                    <td
                      key={c.key}
                      className={cn(
                        'px-3',
                        B(node, 'dense') ? 'py-1.5' : 'py-2',
                        c.align === 'end' && 'text-right tabular-nums',
                        c.align === 'center' && 'text-center',
                      )}
                    >
                      {cell(c, row)}
                    </td>
                  ))}
                  {rowActions?.length ? (
                    <td className="px-1" onClick={(e) => e.stopPropagation()}>
                      <MenuItems
                        label=""
                        iconOnly
                        items={rowActions.map((a) => ({
                          label: a.label,
                          icon: a.icon,
                          tone: a.tone,
                          action: { action: a.action, args: { row, key: k } },
                        }))}
                      />
                    </td>
                  ) : null}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {serverSide && (
        <Pager
          page={page}
          pages={Math.max(1, Math.ceil((total ?? 0) / pageSize))}
          onPage={(p) => fire(node, 'onPage', { page: p, pageSize })}
        />
      )}
    </div>
  );
}

function ListView({ node }: { node: UiNode }) {
  const r = useRender();
  const t = useT();
  const fire = useFire();
  const source = node.props.source as { collection?: string } | undefined;
  const items =
    ((source?.collection ? r.collections[source.collection]?.items : node.props.items) as
      | {
          key?: string;
          id?: string;
          title?: string;
          detail?: string;
          icon?: string;
          trailing?: UiNode;
          actions?: { label: string; action: string }[];
        }[]
      | undefined) ?? [];
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!act(node, 'onVisible') || !end.current) return;
    const io = new IntersectionObserver((e) => e[0]?.isIntersecting && fire(node, 'onVisible'));
    io.observe(end.current);
    return () => io.disconnect();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [items.length]);
  if (!items.length)
    return node.props.empty ? (
      <NodeView node={node.props.empty as UiNode} />
    ) : (
      <div className="py-6 text-center text-[12px] text-[var(--text-muted)]">
        {t('pluginUi.empty')}
      </div>
    );
  return (
    <div className="card divide-y divide-[var(--border)]">
      {items.map((it, i) => {
        const key = String(it.key ?? it.id ?? i);
        return (
          <div
            key={key}
            className={cn(
              'flex items-center gap-3 px-4',
              B(node, 'dense') ? 'py-1.5' : 'py-2.5',
              act(node, 'onItemPress') && 'cursor-pointer hover:bg-[var(--surface-muted)]',
            )}
            onClick={() => fire(node, 'onItemPress', { key, item: it })}
          >
            <Icon name={it.icon} className="size-4 text-[var(--text-secondary)]" />
            <div className="min-w-0 flex-1">
              <div className="truncate text-[13px]">{it.title}</div>
              {it.detail && (
                <div className="truncate text-[12px] text-[var(--text-muted)]">{it.detail}</div>
              )}
            </div>
            {it.trailing && <NodeView node={it.trailing} />}
            {it.actions?.length ? (
              <span onClick={(e) => e.stopPropagation()}>
                <MenuItems
                  label=""
                  iconOnly
                  items={it.actions.map((a) => ({
                    label: a.label,
                    action: { action: a.action, args: { key, item: it } },
                  }))}
                />
              </span>
            ) : null}
          </div>
        );
      })}
      <div ref={end} />
    </div>
  );
}

function InlineOverlay({ node }: { node: UiNode }) {
  const fire = useFire();
  if (!B(node, 'open')) return null;
  return (
    <div
      className="fixed inset-0 z-50 grid place-items-center bg-black/30"
      onClick={() => fire(node, 'onOpenChange', { open: false })}
    >
      <div
        className={cn(
          'card max-h-[85vh] w-[min(560px,calc(100vw-2rem))] overflow-auto p-5',
          node.type === 'Sheet' && 'ml-auto h-full max-h-none rounded-none',
        )}
        onClick={(e) => e.stopPropagation()}
      >
        {S(node, 'title') && <h2 className="mb-4 text-[16px] font-semibold">{S(node, 'title')}</h2>}
        <div className="flex flex-col gap-4">
          <Kids node={node} />
        </div>
        {Array.isArray(node.props.footer) && (
          <div className="mt-5 flex justify-end gap-2">
            <Nodes list={node.props.footer} />
          </div>
        )}
      </div>
    </div>
  );
}

function DomainPicker({
  node,
  kind,
}: {
  node: UiNode;
  kind: 'model' | 'provider' | 'account' | 'client' | 'token' | 'protocol';
}) {
  const multiple = B(node, 'multiple');
  const [value, set, disabled] = useInput<string | string[] | null>(node, multiple ? [] : null);
  const models = useModels();
  const providers = useProviders();
  const clients = useClients();
  const tokens = useTokens();
  const accounts = useProviderAccounts(
    kind === 'account' ? S(node, 'providerId') : undefined,
    kind === 'account' && Boolean(S(node, 'providerId')),
  );
  const opts: SelectOption<string>[] =
    kind === 'model'
      ? (models.data ?? []).map((m) => ({
          value: m.id,
          label: m.displayName || m.id,
          detail: m.vendor,
        }))
      : kind === 'provider'
        ? (providers.data ?? []).map((p) => ({ value: p.id, label: p.name }))
        : kind === 'account'
          ? (accounts.data ?? []).map((a) => ({
              value: a.id,
              label: a.displayName,
              detail: a.accountEmail ?? undefined,
            }))
          : kind === 'client'
            ? (clients.data ?? []).map((c) => ({ value: c.kind, label: c.name }))
            : kind === 'token'
              ? (tokens.data ?? []).map((t) => ({
                  value: t.id,
                  label: t.name,
                  detail: t.keyMasked,
                }))
              : ['anthropic', 'chat', 'responses', 'gemini'].map((p) => ({ value: p, label: p }));
  if (multiple) {
    const current = Array.isArray(value) ? value : [];
    return (
      <div className="flex flex-wrap gap-1.5">
        {opts.map((o) => {
          const on = current.includes(o.value);
          return (
            <button
              key={o.value}
              type="button"
              disabled={disabled}
              aria-pressed={on}
              className={cn(
                'rounded-full border px-2.5 py-0.5 text-[12px]',
                on ? 'border-[var(--accent)] bg-[var(--accent-subtle)]' : 'border-[var(--border)]',
              )}
              onClick={() =>
                set(on ? current.filter((x) => x !== o.value) : [...current, o.value], true)
              }
            >
              {o.label}
            </button>
          );
        })}
      </div>
    );
  }
  return (
    <Select
      value={typeof value === 'string' ? value : null}
      options={opts}
      disabled={disabled}
      placeholder={S(node, 'placeholder')}
      ariaLabel={S(node, 'a11yLabel') ?? kind}
      onChange={(v) => set(v, true)}
    />
  );
}

function diffLines(a: string[], b: string[]): { kind: ' ' | '+' | '-'; text: string }[] {
  // longest common subsequence, enough for the short texts a view shows
  const n = a.length;
  const m = b.length;
  if (n * m > 4_000_000)
    return [
      ...a.map((text) => ({ kind: '-' as const, text })),
      ...b.map((text) => ({ kind: '+' as const, text })),
    ];
  const dp = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      dp[i]![j] = a[i] === b[j] ? dp[i + 1]![j + 1]! + 1 : Math.max(dp[i + 1]![j]!, dp[i]![j + 1]!);
  const out: { kind: ' ' | '+' | '-'; text: string }[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) {
      out.push({ kind: ' ', text: a[i]! });
      i++;
      j++;
    } else if (dp[i + 1]![j]! >= dp[i]![j + 1]!) out.push({ kind: '-', text: a[i++]! });
    else out.push({ kind: '+', text: b[j++]! });
  }
  while (i < n) out.push({ kind: '-', text: a[i++]! });
  while (j < m) out.push({ kind: '+', text: b[j++]! });
  return out;
}
