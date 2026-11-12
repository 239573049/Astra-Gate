// The components the web console had no equivalent for, built for plugin views (design/plugin-v2 04 §12): tree, date
// pickers, line / area / donut charts, rich text, key-value and list editors, tag input, slider, JSON and log views,
// gauge, canvas. Styling uses the console's CSS variables only, so they follow its theme like every other control.
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ChevronDown, ChevronLeft, ChevronRight, Plus, Search, X } from 'lucide-react';

import { cn } from '../lib/cn';
import {
  downsample,
  format,
  niceTicks,
  pad,
  presetRange,
  resolveDate,
  seriesColor,
  todayIso,
  toneColor,
} from './protocol';

// ---------------------------------------------------------------- rich text

export interface Span {
  text: string;
  bold?: boolean;
  italic?: boolean;
  strike?: boolean;
  code?: boolean;
  link?: string;
  action?: string;
}

export function RichTextView({
  spans,
  onLink,
  size,
}: {
  spans: Span[];
  onLink: (span: Span) => void;
  size?: string;
}) {
  return (
    <p
      className={cn(
        'whitespace-pre-wrap break-words leading-relaxed',
        size === 'caption' ? 'text-[12px] text-[var(--text-secondary)]' : 'text-[13px]',
      )}
    >
      {spans.map((s, i) => {
        let el: ReactNode = s.text;
        if (s.code)
          el = (
            <code className="rounded bg-[var(--surface-muted)] px-1 py-px font-mono text-[12px]">
              {el}
            </code>
          );
        if (s.bold) el = <strong>{el}</strong>;
        if (s.italic) el = <em>{el}</em>;
        if (s.strike) el = <s>{el}</s>;
        if (s.link)
          el = (
            <a
              href={s.link.startsWith('https://') ? s.link : undefined}
              className="text-[var(--accent)] underline-offset-2 hover:underline"
              onClick={(e) => {
                e.preventDefault();
                onLink(s);
              }}
            >
              {el}
            </a>
          );
        return <span key={i}>{el}</span>;
      })}
    </p>
  );
}

// ---------------------------------------------------------------- tree

export interface TreeNodeData {
  key: string;
  label: string;
  detail?: string;
  icon?: ReactNode;
  badge?: string;
  trailing?: ReactNode;
  children?: TreeNodeData[];
  lazy?: boolean;
  disabled?: boolean;
}

export function TreeView({
  nodes,
  expanded: expandedProp,
  selected,
  selection,
  search,
  dense,
  onExpand,
  onSelect,
  onPress,
  loadingKeys,
  menu,
  emptyLabel,
  searchLabel,
}: {
  nodes: TreeNodeData[];
  expanded?: string[];
  selected?: string[];
  selection: 'none' | 'single' | 'multiple' | 'checkbox';
  search?: boolean;
  dense?: boolean;
  onExpand: (key: string, expanded: boolean) => void;
  onSelect: (keys: string[]) => void;
  onPress: (key: string) => void;
  loadingKeys: Set<string>;
  menu?: (node: TreeNodeData) => ReactNode;
  emptyLabel: string;
  searchLabel: string;
}) {
  const [open, setOpen] = useState<Set<string>>(() => new Set(expandedProp ?? []));
  const [query, setQuery] = useState('');
  const [focus, setFocus] = useState<string | null>(null);
  useEffect(() => {
    if (expandedProp) setOpen(new Set(expandedProp));
  }, [expandedProp]);
  const sel = new Set(selected ?? []);

  const filtered = useMemo(() => {
    if (!query.trim()) return nodes;
    const q = query.toLowerCase();
    const walk = (list: TreeNodeData[]): TreeNodeData[] =>
      list.flatMap((n) => {
        const kids = n.children ? walk(n.children) : [];
        return n.label.toLowerCase().includes(q) || kids.length
          ? [{ ...n, children: kids.length ? kids : n.children }]
          : [];
      });
    return walk(nodes);
  }, [nodes, query]);

  // the visible rows, flattened (keyboard navigation walks this list)
  const rows: { node: TreeNodeData; depth: number; parent: string | null }[] = [];
  const flatten = (list: TreeNodeData[], depth: number, parent: string | null) => {
    for (const n of list) {
      rows.push({ node: n, depth, parent });
      if ((open.has(n.key) || query) && n.children) flatten(n.children, depth + 1, n.key);
    }
  };
  flatten(filtered, 0, null);

  const toggle = (n: TreeNodeData) => {
    const isOpen = open.has(n.key);
    const next = new Set(open);
    if (isOpen) next.delete(n.key);
    else next.add(n.key);
    setOpen(next);
    if (n.children || n.lazy) onExpand(n.key, !isOpen);
  };

  const descendants = (n: TreeNodeData): string[] =>
    (n.children ?? []).flatMap((c) => [c.key, ...descendants(c)]);
  const check = (n: TreeNodeData) => {
    const next = new Set(sel);
    const all = [n.key, ...descendants(n)];
    if (sel.has(n.key)) all.forEach((k) => next.delete(k));
    else all.forEach((k) => next.add(k));
    onSelect([...next]);
  };
  const state = (n: TreeNodeData): 'on' | 'off' | 'some' => {
    if (sel.has(n.key)) return 'on';
    const d = descendants(n);
    return d.some((k) => sel.has(k)) ? 'some' : 'off';
  };

  const keyDown = (e: React.KeyboardEvent) => {
    const i = rows.findIndex((r) => r.node.key === focus);
    const row = rows[i];
    if (e.key === 'ArrowDown') setFocus(rows[Math.min(rows.length - 1, i + 1)]?.node.key ?? null);
    else if (e.key === 'ArrowUp') setFocus(rows[Math.max(0, i - 1)]?.node.key ?? null);
    else if (
      e.key === 'ArrowRight' &&
      row &&
      !open.has(row.node.key) &&
      (row.node.children || row.node.lazy)
    )
      toggle(row.node);
    else if (e.key === 'ArrowLeft' && row) {
      if (open.has(row.node.key)) toggle(row.node);
      else if (row.parent) setFocus(row.parent);
    } else if (e.key === ' ' && row && selection === 'checkbox') check(row.node);
    else if (e.key === 'Enter' && row) {
      if (selection === 'single') onSelect([row.node.key]);
      onPress(row.node.key);
    } else if (e.key.length === 1 && /\S/.test(e.key)) {
      const next = rows.find(
        (r, j) => j > i && r.node.label.toLowerCase().startsWith(e.key.toLowerCase()),
      );
      if (next) setFocus(next.node.key);
    } else return;
    e.preventDefault();
  };

  return (
    <div className="flex flex-col gap-2">
      {search && (
        <label className="flex items-center gap-2 rounded-[var(--radius-control)] border border-[var(--border)] px-2">
          <Search className="size-3.5 text-[var(--text-muted)]" />
          <input
            className="h-8 flex-1 bg-transparent text-[13px] outline-none"
            placeholder={searchLabel}
            aria-label={searchLabel}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </label>
      )}
      <div
        role="tree"
        tabIndex={0}
        onKeyDown={keyDown}
        className="outline-none"
        aria-multiselectable={selection === 'multiple' || selection === 'checkbox'}
      >
        {rows.length === 0 && (
          <div className="px-2 py-3 text-[12px] text-[var(--text-muted)]">{emptyLabel}</div>
        )}
        {rows.map(({ node, depth }) => {
          const expandable = Boolean(node.children?.length || node.lazy);
          const isOpen = open.has(node.key) || Boolean(query);
          const isSel = sel.has(node.key);
          return (
            <div
              key={node.key}
              role="treeitem"
              aria-expanded={expandable ? isOpen : undefined}
              aria-selected={isSel}
              className={cn(
                'group flex items-center gap-1.5 rounded-md pr-2',
                dense ? 'h-7' : 'h-8',
                isSel && selection !== 'checkbox'
                  ? 'bg-[var(--accent-subtle)]'
                  : 'hover:bg-[var(--surface-muted)]',
                focus === node.key && 'ring-1 ring-[var(--accent)]',
                node.disabled && 'opacity-50',
              )}
              style={{ paddingLeft: 4 + depth * 16 }}
              onClick={() => {
                setFocus(node.key);
                if (node.disabled) return;
                if (selection === 'single') onSelect([node.key]);
                else if (selection === 'multiple')
                  onSelect(isSel ? [...sel].filter((k) => k !== node.key) : [...sel, node.key]);
                onPress(node.key);
              }}
            >
              <button
                type="button"
                aria-label={isOpen ? 'collapse' : 'expand'}
                className={cn(
                  'grid size-5 place-items-center rounded text-[var(--text-muted)]',
                  !expandable && 'invisible',
                )}
                onClick={(e) => {
                  e.stopPropagation();
                  toggle(node);
                }}
              >
                {loadingKeys.has(node.key) ? (
                  <span className="size-3 animate-spin rounded-full border border-[var(--text-muted)] border-t-transparent" />
                ) : (
                  <ChevronRight
                    className={cn('size-3.5 transition-transform', isOpen && 'rotate-90')}
                  />
                )}
              </button>
              {selection === 'checkbox' && (
                <input
                  type="checkbox"
                  className="accent-[var(--accent)]"
                  checked={state(node) === 'on'}
                  ref={(el) => {
                    if (el) el.indeterminate = state(node) === 'some';
                  }}
                  onClick={(e) => e.stopPropagation()}
                  onChange={() => check(node)}
                />
              )}
              {node.icon}
              <span className="min-w-0 flex-1 truncate text-[13px]">{node.label}</span>
              {node.detail && (
                <span className="shrink-0 text-[11px] text-[var(--text-muted)]">{node.detail}</span>
              )}
              {node.badge && (
                <span className="shrink-0 rounded bg-[var(--surface-muted)] px-1.5 text-[11px] text-[var(--text-secondary)]">
                  {node.badge}
                </span>
              )}
              {node.trailing}
              {menu?.(node)}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- dates

function monthGrid(year: number, month: number, weekStart: number): (string | null)[] {
  const first = new Date(year, month, 1);
  const lead = (first.getDay() - weekStart + 7) % 7;
  const days = new Date(year, month + 1, 0).getDate();
  const cells: (string | null)[] = Array.from({ length: lead }, () => null);
  for (let d = 1; d <= days; d++) cells.push(`${year}-${pad(month + 1)}-${pad(d)}`);
  while (cells.length % 7) cells.push(null);
  return cells;
}

function Calendar({
  locale,
  weekStart,
  value,
  range,
  min,
  max,
  onPick,
}: {
  locale: string;
  weekStart: number;
  value?: string;
  range?: { from?: string; to?: string };
  min?: string;
  max?: string;
  onPick: (day: string) => void;
}) {
  const anchor = value ?? range?.to ?? range?.from ?? todayIso();
  const [cursor, setCursor] = useState(() => ({
    y: Number(anchor.slice(0, 4)),
    m: Number(anchor.slice(5, 7)) - 1,
  }));
  const title = new Date(cursor.y, cursor.m, 1).toLocaleDateString(locale, {
    year: 'numeric',
    month: 'long',
  });
  const dayNames = Array.from({ length: 7 }, (_, i) =>
    new Date(2024, 0, 7 + ((i + weekStart) % 7)).toLocaleDateString(locale, { weekday: 'narrow' }),
  );
  const move = (n: number) =>
    setCursor((c) => ({
      y: c.m + n < 0 ? c.y - 1 : c.m + n > 11 ? c.y + 1 : c.y,
      m: (c.m + n + 12) % 12,
    }));
  const today = todayIso();
  return (
    <div className="w-[248px] select-none">
      <div className="mb-2 flex items-center justify-between">
        <button
          type="button"
          className="grid size-7 place-items-center rounded hover:bg-[var(--surface-muted)]"
          onClick={() => move(-1)}
          aria-label="previous month"
        >
          <ChevronLeft className="size-4" />
        </button>
        <span className="text-[13px] font-medium">{title}</span>
        <button
          type="button"
          className="grid size-7 place-items-center rounded hover:bg-[var(--surface-muted)]"
          onClick={() => move(1)}
          aria-label="next month"
        >
          <ChevronRight className="size-4" />
        </button>
      </div>
      <div className="grid grid-cols-7 gap-0.5 text-center text-[11px] text-[var(--text-muted)]">
        {dayNames.map((d, i) => (
          <span key={i}>{d}</span>
        ))}
      </div>
      <div className="mt-1 grid grid-cols-7 gap-0.5">
        {monthGrid(cursor.y, cursor.m, weekStart).map((day, i) => {
          if (!day) return <span key={i} />;
          const disabled = (min && day < min) || (max && day > max);
          const picked = day === value || day === range?.from || day === range?.to;
          const inside = range?.from && range.to && day > range.from && day < range.to;
          return (
            <button
              key={i}
              type="button"
              disabled={Boolean(disabled)}
              onClick={() => onPick(day)}
              className={cn(
                'h-8 rounded text-[12px] tabular-nums',
                picked
                  ? 'bg-[var(--accent)] text-[var(--accent-foreground)]'
                  : inside
                    ? 'bg-[var(--accent-subtle)]'
                    : 'hover:bg-[var(--surface-muted)]',
                day === today && !picked && 'font-semibold text-[var(--accent)]',
                disabled && 'opacity-30',
              )}
            >
              {Number(day.slice(8))}
            </button>
          );
        })}
      </div>
    </div>
  );
}

function Popup({
  open,
  onClose,
  children,
}: {
  open: boolean;
  onClose: () => void;
  children: ReactNode;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const down = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onClose();
    };
    const key = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    document.addEventListener('mousedown', down);
    document.addEventListener('keydown', key);
    return () => {
      document.removeEventListener('mousedown', down);
      document.removeEventListener('keydown', key);
    };
  }, [open, onClose]);
  if (!open) return null;
  return (
    <div
      ref={ref}
      className="absolute z-50 mt-1 rounded-[var(--radius-panel)] border border-[var(--border)] bg-[var(--surface-solid)] p-3 shadow-lg"
    >
      {children}
    </div>
  );
}

const fieldClass =
  'flex h-8 w-full items-center gap-2 rounded-[var(--radius-control)] border border-[var(--border)] bg-[var(--surface)] px-2 text-left text-[13px] disabled:opacity-50';

/** A calendar date (YYYY-MM-DD), or with withTime an instant (ISO UTC, edited in local time). */
export function DatePickerView({
  value,
  onChange,
  locale,
  weekStart,
  min,
  max,
  withTime,
  minuteStep = 1,
  clearable,
  disabled,
  placeholder,
  label,
}: {
  value: string | null;
  onChange: (v: string | null) => void;
  locale: string;
  weekStart: number;
  min?: unknown;
  max?: unknown;
  withTime?: boolean;
  minuteStep?: number;
  clearable?: boolean;
  disabled?: boolean;
  placeholder?: string;
  label?: string;
}) {
  const [open, setOpen] = useState(false);
  const local = value ? (withTime ? new Date(value) : null) : null;
  const day = withTime
    ? local
      ? `${local.getFullYear()}-${pad(local.getMonth() + 1)}-${pad(local.getDate())}`
      : undefined
    : (value ?? undefined);
  const time = local ? `${pad(local.getHours())}:${pad(local.getMinutes())}` : '09:00';
  const text = value ? format(withTime ? 'datetime' : 'date', value, locale) : (placeholder ?? '');
  const emit = (d: string, t: string) => {
    if (!withTime) return onChange(d);
    const [h, m] = t.split(':').map(Number);
    const at = new Date(
      Number(d.slice(0, 4)),
      Number(d.slice(5, 7)) - 1,
      Number(d.slice(8, 10)),
      h,
      m,
    );
    onChange(at.toISOString());
  };
  return (
    <div className="relative">
      <button
        type="button"
        disabled={disabled}
        className={fieldClass}
        onClick={() => setOpen((o) => !o)}
        aria-label={label}
      >
        <span className={cn('flex-1 truncate', !value && 'text-[var(--text-muted)]')}>{text}</span>
        {clearable && value && (
          <X
            className="size-3.5 text-[var(--text-muted)]"
            onClick={(e) => {
              e.stopPropagation();
              onChange(null);
            }}
          />
        )}
        <ChevronDown className="size-3.5 text-[var(--text-muted)]" />
      </button>
      <Popup open={open} onClose={() => setOpen(false)}>
        <Calendar
          locale={locale}
          weekStart={weekStart}
          value={day}
          min={resolveDate(min)}
          max={resolveDate(max)}
          onPick={(d) => {
            emit(d, time);
            if (!withTime) setOpen(false);
          }}
        />
        {withTime && (
          <div className="mt-2 flex items-center justify-between gap-2 border-t border-[var(--border)] pt-2">
            <TimeInput value={time} step={minuteStep} onChange={(t) => day && emit(day, t)} />
            <button
              type="button"
              className="text-[12px] text-[var(--accent)]"
              onClick={() => setOpen(false)}
            >
              OK
            </button>
          </div>
        )}
      </Popup>
    </div>
  );
}

export function TimeInput({
  value,
  onChange,
  step = 1,
  disabled,
}: {
  value: string;
  onChange: (v: string) => void;
  step?: number;
  disabled?: boolean;
}) {
  return (
    <input
      type="time"
      disabled={disabled}
      step={Math.max(1, step) * 60}
      value={value}
      onChange={(e) => e.target.value && onChange(e.target.value)}
      className="h-8 rounded-[var(--radius-control)] border border-[var(--border)] bg-[var(--surface)] px-2 text-[13px]"
    />
  );
}

export function DateRangePickerView({
  value,
  onChange,
  locale,
  weekStart,
  min,
  max,
  presets,
  maxSpanDays,
  disabled,
  presetLabel,
  label,
}: {
  value: { from?: string; to?: string } | null;
  onChange: (v: { from: string; to: string } | null) => void;
  locale: string;
  weekStart: number;
  min?: unknown;
  max?: unknown;
  presets?: unknown[];
  maxSpanDays?: number;
  disabled?: boolean;
  presetLabel: (name: string) => string;
  label?: string;
}) {
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState<{ from?: string; to?: string }>(value ?? {});
  useEffect(() => setDraft(value ?? {}), [value]);
  const text =
    value?.from && value.to
      ? `${format('date', value.from, locale)} – ${format('date', value.to, locale)}`
      : '';
  const pick = (d: string) => {
    if (!draft.from || draft.to) return setDraft({ from: d });
    let from = draft.from;
    let to = d;
    if (to < from) [from, to] = [to, from];
    if (
      maxSpanDays &&
      (new Date(to).getTime() - new Date(from).getTime()) / 86_400_000 + 1 > maxSpanDays
    ) {
      const f = new Date(to);
      f.setDate(f.getDate() - maxSpanDays + 1);
      from = `${f.getFullYear()}-${pad(f.getMonth() + 1)}-${pad(f.getDate())}`;
    }
    setDraft({ from, to });
    onChange({ from, to });
    setOpen(false);
  };
  return (
    <div className="relative">
      <button
        type="button"
        disabled={disabled}
        className={fieldClass}
        onClick={() => setOpen((o) => !o)}
        aria-label={label}
      >
        <span className={cn('flex-1 truncate', !text && 'text-[var(--text-muted)]')}>
          {text || '—'}
        </span>
        <ChevronDown className="size-3.5 text-[var(--text-muted)]" />
      </button>
      <Popup open={open} onClose={() => setOpen(false)}>
        <div className="flex gap-3">
          {presets && presets.length > 0 && (
            <div className="flex w-28 flex-col gap-0.5 border-r border-[var(--border)] pr-2">
              {presets.map((p, i) => {
                const named = typeof p === 'string';
                const r = named
                  ? presetRange(p)
                  : (p as { from?: string; to?: string; label?: string });
                if (!r?.from || !r.to) return null;
                return (
                  <button
                    key={i}
                    type="button"
                    className="rounded px-2 py-1 text-left text-[12px] hover:bg-[var(--surface-muted)]"
                    onClick={() => {
                      onChange({ from: r.from!, to: r.to! });
                      setOpen(false);
                    }}
                  >
                    {named
                      ? presetLabel(p)
                      : String((p as { label?: string }).label ?? `${r.from} – ${r.to}`)}
                  </button>
                );
              })}
            </div>
          )}
          <Calendar
            locale={locale}
            weekStart={weekStart}
            range={draft}
            min={resolveDate(min)}
            max={resolveDate(max)}
            onPick={pick}
          />
        </div>
      </Popup>
    </div>
  );
}

// ---------------------------------------------------------------- charts

export interface ChartSpec {
  kind: 'line' | 'area' | 'bar' | 'stackedBar' | 'donut';
  data: Record<string, unknown>[];
  x?: { key?: string; type?: 'time' | 'category' | 'number'; format?: string };
  y?: { format?: string; min?: number | 'auto'; max?: number | 'auto'; ticks?: number };
  yRight?: { format?: string };
  series: {
    key: string;
    label?: string;
    tone?: string;
    color?: number | string;
    dashed?: boolean;
    axis?: 'left' | 'right';
  }[];
  curve?: 'linear' | 'smooth' | 'step';
  points?: 'auto' | 'always' | 'never';
  gaps?: 'break' | 'connect' | 'zero';
  marks?: { x?: unknown; y?: number; label?: string; tone?: string }[];
  legend?: 'bottom' | 'top' | 'none';
  height?: number;
  stacked?: boolean;
  zoom?: boolean;
}

export function ChartView({
  spec,
  locale,
  onSelect,
  onRange,
  emptyLabel,
}: {
  spec: ChartSpec;
  locale: string;
  onSelect?: (datum: Record<string, unknown>, series: string) => void;
  onRange?: (from: unknown, to: unknown) => void;
  emptyLabel: string;
}) {
  const [hidden, setHidden] = useState<Set<string>>(new Set());
  const [hover, setHover] = useState<number | null>(null);
  const [drag, setDrag] = useState<{ a: number; b: number } | null>(null);
  const box = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(600);
  useEffect(() => {
    if (!box.current) return;
    const ro = new ResizeObserver((e) => setWidth(Math.max(120, e[0]!.contentRect.width)));
    ro.observe(box.current);
    return () => ro.disconnect();
  }, []);
  const height = spec.height ?? 240;
  const series = spec.series.filter((s) => !hidden.has(s.key));
  const legend = spec.legend !== 'none' && spec.series.length > 1 && (
    <div className="flex flex-wrap gap-3 text-[12px]">
      {spec.series.map((s, i) => (
        <button
          key={s.key}
          type="button"
          className={cn('flex items-center gap-1.5', hidden.has(s.key) && 'opacity-40')}
          onClick={() =>
            setHidden((h) => {
              const n = new Set(h);
              if (n.has(s.key)) n.delete(s.key);
              else n.add(s.key);
              return n;
            })
          }
        >
          <span className="size-2.5 rounded-sm" style={{ background: seriesColor(s, i) }} />
          {s.label ?? s.key}
        </button>
      ))}
    </div>
  );

  if (!spec.data.length)
    return (
      <div
        className="grid place-items-center text-[12px] text-[var(--text-muted)]"
        style={{ height }}
      >
        {emptyLabel}
      </div>
    );

  if (spec.kind === 'donut') {
    const s0 = spec.series[0];
    const labelKey = spec.x?.key ?? 'label';
    const total = spec.data.reduce((sum, d) => sum + (Number(d[s0?.key ?? 'value']) || 0), 0) || 1;
    let angle = -Math.PI / 2;
    const r = Math.min(height, 200) / 2 - 8;
    const cx = r + 8;
    return (
      <div className="flex items-center gap-6" ref={box}>
        <svg
          width={cx * 2}
          height={cx * 2}
          role="img"
          aria-label={spec.data.map((d) => `${d[labelKey]}: ${d[s0?.key ?? 'value']}`).join(', ')}
        >
          {spec.data.map((d, i) => {
            const v = Number(d[s0?.key ?? 'value']) || 0;
            const a = (v / total) * Math.PI * 2;
            const x1 = cx + r * Math.cos(angle);
            const y1 = cx + r * Math.sin(angle);
            angle += a;
            const x2 = cx + r * Math.cos(angle);
            const y2 = cx + r * Math.sin(angle);
            return (
              <path
                key={i}
                d={`M ${x1} ${y1} A ${r} ${r} 0 ${a > Math.PI ? 1 : 0} 1 ${x2} ${y2}`}
                fill="none"
                stroke={seriesColor({ color: i }, i)}
                strokeWidth={r * 0.35}
                onClick={() => onSelect?.(d, s0?.key ?? 'value')}
              />
            );
          })}
          <text
            x={cx}
            y={cx}
            textAnchor="middle"
            dominantBaseline="middle"
            className="fill-[var(--foreground)] text-[14px] font-semibold"
          >
            {format(spec.y?.format ?? 'compact', total, locale)}
          </text>
        </svg>
        <div className="flex flex-col gap-1 text-[12px]">
          {spec.data.map((d, i) => (
            <span key={i} className="flex items-center gap-1.5">
              <span
                className="size-2.5 rounded-sm"
                style={{ background: seriesColor({ color: i }, i) }}
              />
              {String(d[labelKey])}{' '}
              <span className="text-[var(--text-muted)]">
                {format(spec.y?.format ?? 'number', d[s0?.key ?? 'value'], locale)}
              </span>
            </span>
          ))}
        </div>
      </div>
    );
  }

  const xKey = spec.x?.key ?? 'x';
  const xType = spec.x?.type ?? 'category';
  const pad_ = { l: 44, r: spec.series.some((s) => s.axis === 'right') ? 44 : 12, t: 8, b: 24 };
  const w = width - pad_.l - pad_.r;
  const h = height - pad_.t - pad_.b;
  const rows =
    spec.kind === 'line' || spec.kind === 'area'
      ? downsample(
          spec.data,
          (r) => Number(r[series[0]?.key ?? '']) || 0,
          Math.max(50, Math.floor(w)),
        )
      : spec.data;
  const xs = rows.map((r, i) => {
    if (xType === 'time') return new Date(String(r[xKey])).getTime();
    if (xType === 'number') return Number(r[xKey]);
    return i;
  });
  const xMin = Math.min(...xs);
  const xMax = Math.max(...xs);
  const bar = spec.kind === 'bar' || spec.kind === 'stackedBar';
  const stacked = spec.kind === 'stackedBar' || (spec.stacked && spec.kind === 'area');
  const sx = (i: number) =>
    pad_.l +
    (bar
      ? ((i + 0.5) / rows.length) * w
      : xMax === xMin
        ? w / 2
        : ((xs[i]! - xMin) / (xMax - xMin)) * w);
  const left = series.filter((s) => s.axis !== 'right');
  const right = series.filter((s) => s.axis === 'right');
  const extent = (list: typeof series) => {
    let lo = Infinity;
    let hi = -Infinity;
    rows.forEach((r) => {
      let sum = 0;
      for (const s of list) {
        const v = Number(r[s.key]);
        if (!Number.isFinite(v)) continue;
        sum += v;
        lo = Math.min(lo, stacked ? sum : v);
        hi = Math.max(hi, stacked ? sum : v);
      }
    });
    if (!Number.isFinite(lo)) return [0, 1];
    return [Math.min(0, lo), hi];
  };
  const [lMin0, lMax0] = extent(left);
  const ticks = niceTicks(
    typeof spec.y?.min === 'number' ? spec.y.min : lMin0!,
    typeof spec.y?.max === 'number' ? spec.y.max : lMax0!,
    spec.y?.ticks ?? 4,
  );
  const yLo = ticks[0]!;
  const yHi = ticks[ticks.length - 1]!;
  const sy = (v: number) => pad_.t + h - ((v - yLo) / (yHi - yLo || 1)) * h;
  const [rMin, rMax] = extent(right);
  const rTicks = right.length ? niceTicks(rMin!, rMax!, 4) : [];
  const syR = (v: number) =>
    pad_.t + h - ((v - rTicks[0]!) / (rTicks[rTicks.length - 1]! - rTicks[0]! || 1)) * h;

  const path = (s: (typeof series)[number], si: number) => {
    const scale = s.axis === 'right' ? syR : sy;
    let d = '';
    let pen = false;
    rows.forEach((r, i) => {
      let v = Number(r[s.key]);
      if (stacked) v = series.slice(0, si + 1).reduce((sum, x) => sum + (Number(r[x.key]) || 0), 0);
      if (!Number.isFinite(v) || r[s.key] === null) {
        if (spec.gaps === 'zero') v = 0;
        else {
          if (spec.gaps !== 'connect') pen = false;
          return;
        }
      }
      const x = sx(i);
      const y = scale(v);
      if (!pen) d += `M${x},${y}`;
      else if (spec.curve === 'step') d += `H${x}V${y}`;
      else d += `L${x},${y}`;
      pen = true;
    });
    return d;
  };

  const tooltip = hover !== null && rows[hover] && (
    <div
      className="pointer-events-none absolute z-10 rounded-md border border-[var(--border)] bg-[var(--surface-solid)] px-2 py-1 text-[12px] shadow"
      style={{ left: Math.min(sx(hover) + 8, width - 160), top: 4 }}
    >
      <div className="mb-0.5 text-[var(--text-muted)]">
        {format(
          spec.x?.format ?? (xType === 'time' ? 'datetime' : undefined),
          rows[hover]![xKey],
          locale,
        )}
      </div>
      {series.map((s) => (
        <div key={s.key} className="flex items-center gap-1.5">
          <span
            className="size-2 rounded-full"
            style={{ background: seriesColor(s, spec.series.indexOf(s)) }}
          />
          {s.label ?? s.key}:{' '}
          <strong>
            {format(
              s.axis === 'right' ? spec.yRight?.format : spec.y?.format,
              rows[hover]![s.key],
              locale,
            )}
          </strong>
        </div>
      ))}
    </div>
  );

  const index = (clientX: number) => {
    const rect = box.current!.getBoundingClientRect();
    const x = clientX - rect.left;
    let best = 0;
    rows.forEach((_, i) => {
      if (Math.abs(sx(i) - x) < Math.abs(sx(best) - x)) best = i;
    });
    return best;
  };

  const xLabels = Math.min(rows.length, Math.max(2, Math.floor(w / 90)));
  const summary = series
    .map((s) => {
      const vals = rows.map((r) => Number(r[s.key])).filter(Number.isFinite);
      return `${s.label ?? s.key}: ${format(spec.y?.format, Math.min(...vals), locale)}–${format(spec.y?.format, Math.max(...vals), locale)}`;
    })
    .join('; ');

  return (
    <div className="flex flex-col gap-2">
      {spec.legend === 'top' && legend}
      <div
        ref={box}
        className="relative"
        onMouseMove={(e) => {
          const i = index(e.clientX);
          setHover(i);
          if (drag) setDrag({ ...drag, b: i });
        }}
        onMouseLeave={() => {
          setHover(null);
          setDrag(null);
        }}
        onMouseDown={(e) => spec.zoom && setDrag({ a: index(e.clientX), b: index(e.clientX) })}
        onMouseUp={() => {
          if (drag && drag.a !== drag.b)
            onRange?.(rows[Math.min(drag.a, drag.b)]![xKey], rows[Math.max(drag.a, drag.b)]![xKey]);
          else if (hover !== null && series[0]) onSelect?.(rows[hover]!, series[0].key);
          setDrag(null);
        }}
      >
        <svg width={width} height={height} role="img" aria-label={summary}>
          {ticks.map((t) => (
            <g key={t}>
              <line
                x1={pad_.l}
                x2={width - pad_.r}
                y1={sy(t)}
                y2={sy(t)}
                stroke="var(--border)"
                strokeDasharray={t === 0 ? undefined : '2 3'}
              />
              <text
                x={pad_.l - 6}
                y={sy(t)}
                textAnchor="end"
                dominantBaseline="middle"
                className="fill-[var(--text-muted)] text-[10px]"
              >
                {format(spec.y?.format ?? 'compact', t, locale)}
              </text>
            </g>
          ))}
          {rTicks.map((t) => (
            <text
              key={'r' + t}
              x={width - pad_.r + 6}
              y={syR(t)}
              dominantBaseline="middle"
              className="fill-[var(--text-muted)] text-[10px]"
            >
              {format(spec.yRight?.format ?? 'compact', t, locale)}
            </text>
          ))}
          {Array.from({ length: xLabels }, (_, k) =>
            Math.round((k * (rows.length - 1)) / Math.max(1, xLabels - 1)),
          ).map((i) => (
            <text
              key={'x' + i}
              x={sx(i)}
              y={height - 6}
              textAnchor="middle"
              className="fill-[var(--text-muted)] text-[10px]"
            >
              {format(
                spec.x?.format ?? (xType === 'time' ? 'date' : undefined),
                rows[i]?.[xKey],
                locale,
              )}
            </text>
          ))}
          {bar
            ? rows.map((r, i) => {
                const bw = (w / rows.length) * 0.7;
                let base = 0;
                return series.map((s, si) => {
                  const v = Number(r[s.key]) || 0;
                  const y0 = stacked ? base : 0;
                  const y1 = stacked ? base + v : v;
                  base += stacked ? v : 0;
                  const x = sx(i) - bw / 2 + (stacked ? 0 : (si * bw) / series.length);
                  const width_ = stacked ? bw : bw / series.length;
                  return (
                    <rect
                      key={s.key + i}
                      x={x}
                      y={sy(Math.max(y0, y1))}
                      width={Math.max(1, width_ - 1)}
                      height={Math.max(0, Math.abs(sy(y0) - sy(y1)))}
                      rx={2}
                      fill={seriesColor(s, spec.series.indexOf(s))}
                      opacity={hover === null || hover === i ? 1 : 0.6}
                    />
                  );
                });
              })
            : series.map((s, si) => {
                const color = seriesColor(s, spec.series.indexOf(s));
                const d = path(s, si);
                const showPoints =
                  spec.points === 'always' || (spec.points !== 'never' && rows.length <= 24);
                return (
                  <g key={s.key}>
                    {spec.kind === 'area' && d && (
                      <path
                        d={`${d}L${sx(rows.length - 1)},${sy(Math.max(0, yLo))}L${sx(0)},${sy(Math.max(0, yLo))}Z`}
                        fill={color}
                        opacity={0.15}
                      />
                    )}
                    <path
                      d={d}
                      fill="none"
                      stroke={color}
                      strokeWidth={2}
                      strokeDasharray={s.dashed ? '5 4' : undefined}
                      strokeLinejoin="round"
                    />
                    {showPoints &&
                      rows.map((r, i) =>
                        Number.isFinite(Number(r[s.key])) ? (
                          <circle
                            key={i}
                            cx={sx(i)}
                            cy={(s.axis === 'right' ? syR : sy)(Number(r[s.key]))}
                            r={2.5}
                            fill={color}
                          />
                        ) : null,
                      )}
                  </g>
                );
              })}
          {spec.marks?.map((m, i) =>
            m.y !== undefined ? (
              <g key={'m' + i}>
                <line
                  x1={pad_.l}
                  x2={width - pad_.r}
                  y1={sy(m.y)}
                  y2={sy(m.y)}
                  stroke={toneColor(m.tone ?? 'warning')}
                  strokeDasharray="4 3"
                />
                {m.label && (
                  <text
                    x={width - pad_.r - 4}
                    y={sy(m.y) - 4}
                    textAnchor="end"
                    className="text-[10px]"
                    fill={toneColor(m.tone ?? 'warning')}
                  >
                    {m.label}
                  </text>
                )}
              </g>
            ) : (
              (() => {
                const at = rows.findIndex((r) => r[xKey] === m.x);
                if (at < 0) return null;
                return (
                  <g key={'m' + i}>
                    <line
                      x1={sx(at)}
                      x2={sx(at)}
                      y1={pad_.t}
                      y2={pad_.t + h}
                      stroke={toneColor(m.tone ?? 'accent')}
                      strokeDasharray="4 3"
                    />
                    {m.label && (
                      <text
                        x={sx(at) + 4}
                        y={pad_.t + 10}
                        className="text-[10px]"
                        fill={toneColor(m.tone ?? 'accent')}
                      >
                        {m.label}
                      </text>
                    )}
                  </g>
                );
              })()
            ),
          )}
          {hover !== null && !bar && (
            <line
              x1={sx(hover)}
              x2={sx(hover)}
              y1={pad_.t}
              y2={pad_.t + h}
              stroke="var(--border-strong)"
            />
          )}
          {drag && (
            <rect
              x={Math.min(sx(drag.a), sx(drag.b))}
              y={pad_.t}
              width={Math.abs(sx(drag.b) - sx(drag.a))}
              height={h}
              fill="var(--accent)"
              opacity={0.12}
            />
          )}
        </svg>
        {tooltip}
      </div>
      {spec.legend !== 'top' && legend}
    </div>
  );
}

export function SparklineView({
  data,
  tone,
  area,
}: {
  data: number[];
  tone?: string;
  area?: boolean;
}) {
  if (!data.length) return null;
  const w = 120;
  const h = 32;
  const lo = Math.min(...data);
  const hi = Math.max(...data);
  const pts = data.map(
    (v, i) =>
      [
        (i / Math.max(1, data.length - 1)) * w,
        h - 2 - ((v - lo) / (hi - lo || 1)) * (h - 4),
      ] as const,
  );
  const d = pts.map(([x, y], i) => `${i ? 'L' : 'M'}${x},${y}`).join('');
  const color = toneColor(tone ?? 'accent');
  return (
    <svg width={w} height={h} aria-hidden>
      {area && <path d={`${d}L${w},${h}L0,${h}Z`} fill={color} opacity={0.15} />}
      <path d={d} fill="none" stroke={color} strokeWidth={1.5} />
    </svg>
  );
}

export function GaugeView({
  value,
  max = 100,
  label,
  thresholds,
  locale,
}: {
  value: number;
  max?: number;
  label?: string;
  thresholds?: { at: number; tone: string }[];
  locale: string;
}) {
  const ratio = Math.max(0, Math.min(1, value / (max || 1)));
  const tone =
    [...(thresholds ?? [])]
      .sort((a, b) => a.at - b.at)
      .filter((t) => value >= t.at)
      .pop()?.tone ?? 'accent';
  const r = 44;
  const arc = (from: number, to: number) => {
    const a = Math.PI * (1 + from);
    const b = Math.PI * (1 + to);
    return `M ${50 + r * Math.cos(a)} ${54 + r * Math.sin(a)} A ${r} ${r} 0 0 1 ${50 + r * Math.cos(b)} ${54 + r * Math.sin(b)}`;
  };
  return (
    <svg viewBox="0 0 100 64" width={140} role="img" aria-label={`${label ?? ''} ${value}/${max}`}>
      <path
        d={arc(0, 1)}
        stroke="var(--surface-muted)"
        strokeWidth={8}
        fill="none"
        strokeLinecap="round"
      />
      <path
        d={arc(0, ratio)}
        stroke={toneColor(tone)}
        strokeWidth={8}
        fill="none"
        strokeLinecap="round"
      />
      <text
        x={50}
        y={50}
        textAnchor="middle"
        className="fill-[var(--foreground)] text-[14px] font-semibold"
      >
        {format('number', value, locale)}
      </text>
      {label && (
        <text x={50} y={62} textAnchor="middle" className="fill-[var(--text-muted)] text-[7px]">
          {label}
        </text>
      )}
    </svg>
  );
}

// ---------------------------------------------------------------- editors

export function KeyValueEditorView({
  value,
  onChange,
  keyPlaceholder,
  valuePlaceholder,
  secretValues,
  disabled,
  addLabel,
}: {
  value: { key: string; value: string }[];
  onChange: (v: { key: string; value: string }[]) => void;
  keyPlaceholder?: string;
  valuePlaceholder?: string;
  secretValues?: boolean;
  disabled?: boolean;
  addLabel: string;
}) {
  return (
    <div className="flex flex-col gap-1.5">
      {value.map((row, i) => (
        <div key={i} className="flex gap-1.5">
          <input
            className={cn(fieldClass, 'font-mono')}
            value={row.key}
            placeholder={keyPlaceholder}
            disabled={disabled}
            onChange={(e) =>
              onChange(value.map((r, j) => (j === i ? { ...r, key: e.target.value } : r)))
            }
          />
          <input
            className={fieldClass}
            type={secretValues ? 'password' : 'text'}
            value={row.value}
            placeholder={valuePlaceholder}
            disabled={disabled}
            onChange={(e) =>
              onChange(value.map((r, j) => (j === i ? { ...r, value: e.target.value } : r)))
            }
          />
          <button
            type="button"
            disabled={disabled}
            className="grid size-8 shrink-0 place-items-center rounded hover:bg-[var(--surface-muted)]"
            onClick={() => onChange(value.filter((_, j) => j !== i))}
            aria-label="remove"
          >
            <X className="size-3.5" />
          </button>
        </div>
      ))}
      <button
        type="button"
        disabled={disabled}
        className="flex items-center gap-1 self-start text-[12px] text-[var(--accent)]"
        onClick={() => onChange([...value, { key: '', value: '' }])}
      >
        <Plus className="size-3.5" />
        {addLabel}
      </button>
    </div>
  );
}

export function TagInputView({
  value,
  onChange,
  suggestions,
  max,
  pattern,
  placeholder,
  disabled,
}: {
  value: string[];
  onChange: (v: string[]) => void;
  suggestions?: string[];
  max?: number;
  pattern?: string;
  placeholder?: string;
  disabled?: boolean;
}) {
  const [text, setText] = useState('');
  const add = (t: string) => {
    const v = t.trim();
    if (!v || value.includes(v) || (max && value.length >= max)) return;
    if (pattern) {
      try {
        if (!new RegExp(pattern).test(v)) return;
      } catch {
        // an unusable pattern accepts anything
      }
    }
    onChange([...value, v]);
    setText('');
  };
  return (
    <div className="flex min-h-8 flex-wrap items-center gap-1 rounded-[var(--radius-control)] border border-[var(--border)] bg-[var(--surface)] px-1.5 py-1">
      {value.map((tag) => (
        <span
          key={tag}
          className="flex items-center gap-1 rounded bg-[var(--surface-muted)] px-1.5 text-[12px]"
        >
          {tag}
          {!disabled && (
            <button
              type="button"
              aria-label={`remove ${tag}`}
              onClick={() => onChange(value.filter((t) => t !== tag))}
            >
              <X className="size-3" />
            </button>
          )}
        </span>
      ))}
      <input
        className="h-6 min-w-24 flex-1 bg-transparent text-[13px] outline-none"
        value={text}
        disabled={disabled}
        list={suggestions?.length ? 'tag-suggestions' : undefined}
        placeholder={value.length ? undefined : placeholder}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ',') {
            e.preventDefault();
            add(text);
          } else if (e.key === 'Backspace' && !text && value.length) onChange(value.slice(0, -1));
        }}
        onBlur={() => add(text)}
      />
      {suggestions?.length ? (
        <datalist id="tag-suggestions">
          {suggestions.map((s) => (
            <option key={s} value={s} />
          ))}
        </datalist>
      ) : null}
    </div>
  );
}

// ---------------------------------------------------------------- data views

export function JsonTree({
  value,
  depth = 0,
  expandDepth = 2,
}: {
  value: unknown;
  depth?: number;
  expandDepth?: number;
}) {
  const [open, setOpen] = useState(depth < expandDepth);
  if (value === null || typeof value !== 'object') {
    const color =
      typeof value === 'string'
        ? 'var(--green)'
        : typeof value === 'number'
          ? 'var(--accent)'
          : 'var(--orange)';
    return <span style={{ color }}>{JSON.stringify(value)}</span>;
  }
  const entries = Array.isArray(value)
    ? value.map((v, i) => [String(i), v] as const)
    : Object.entries(value as Record<string, unknown>);
  const [o, c] = Array.isArray(value) ? ['[', ']'] : ['{', '}'];
  if (!open)
    return (
      <button type="button" className="text-[var(--text-muted)]" onClick={() => setOpen(true)}>
        {o}…{entries.length}
        {c}
      </button>
    );
  return (
    <span>
      <button type="button" className="text-[var(--text-muted)]" onClick={() => setOpen(false)}>
        {o}
      </button>
      <div className="pl-4">
        {entries.map(([k, v]) => (
          <div key={k}>
            {!Array.isArray(value) && (
              <span className="text-[var(--text-secondary)]">{JSON.stringify(k)}: </span>
            )}
            <JsonTree value={v} depth={depth + 1} expandDepth={expandDepth} />
          </div>
        ))}
      </div>
      <span className="text-[var(--text-muted)]">{c}</span>
    </span>
  );
}

export function LogViewer({
  lines,
  follow = true,
  wrap,
  height = 320,
}: {
  lines: { level?: string; text?: string; at?: string }[];
  follow?: boolean;
  wrap?: boolean;
  height?: number;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (follow && ref.current) ref.current.scrollTop = ref.current.scrollHeight;
  }, [lines, follow]);
  return (
    <div
      ref={ref}
      className="overflow-auto rounded-[var(--radius-control)] bg-[var(--surface-muted)] p-2 font-mono text-[12px]"
      style={{ maxHeight: height }}
    >
      {lines.map((l, i) => (
        <div
          key={i}
          className={cn(wrap ? 'whitespace-pre-wrap break-all' : 'whitespace-pre')}
          style={{
            color:
              l.level === 'error' ? 'var(--red)' : l.level === 'warn' ? 'var(--orange)' : undefined,
          }}
        >
          {l.at && <span className="text-[var(--text-muted)]">{l.at.slice(11, 19)} </span>}
          {l.text ?? JSON.stringify(l)}
        </div>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------- canvas (02 §11): a small SVG display list

export interface CanvasOp {
  rect?: [number, number, number, number];
  circle?: [number, number, number];
  line?: [number, number, number, number];
  path?: string;
  text?: string;
  at?: [number, number];
  fill?: string;
  stroke?: string;
  strokeWidth?: number;
  size?: string;
  anchor?: 'start' | 'middle' | 'end';
  label?: string;
  hit?: { action?: string; args?: unknown; tooltip?: string };
  group?: CanvasOp[];
}

function paint(color: unknown): string | undefined {
  if (typeof color !== 'string') return undefined;
  if (/^#[0-9a-fA-F]{6}$/.test(color)) return color;
  switch (color) {
    case 'surface':
      return 'var(--surface)';
    case 'text':
      return 'var(--foreground)';
    case 'muted':
      return 'var(--text-muted)';
    case 'border':
      return 'var(--border)';
    default:
      return /^[0-7]$/.test(color) ? seriesColor({ color: Number(color) }, 0) : toneColor(color);
  }
}

export function CanvasView({
  ops,
  viewBox,
  height = 240,
  onHit,
}: {
  ops: CanvasOp[];
  viewBox?: number[];
  height?: number;
  onHit: (hit: { action?: string; args?: unknown }) => void;
}) {
  const draw = (list: CanvasOp[]): ReactNode[] =>
    list.map((op, i) => {
      const common = {
        fill: paint(op.fill) ?? (op.text ? 'var(--foreground)' : 'none'),
        stroke: paint(op.stroke),
        strokeWidth: op.strokeWidth,
        onClick: op.hit ? () => onHit(op.hit!) : undefined,
        style: op.hit ? { cursor: 'pointer' } : undefined,
        'aria-label': op.label,
      };
      const title = op.hit?.tooltip ? <title>{op.hit.tooltip}</title> : null;
      if (op.group) return <g key={i}>{draw(op.group)}</g>;
      if (op.rect)
        return (
          <rect
            key={i}
            x={op.rect[0]}
            y={op.rect[1]}
            width={op.rect[2]}
            height={op.rect[3]}
            {...common}
          >
            {title}
          </rect>
        );
      if (op.circle)
        return (
          <circle key={i} cx={op.circle[0]} cy={op.circle[1]} r={op.circle[2]} {...common}>
            {title}
          </circle>
        );
      if (op.line)
        return (
          <line
            key={i}
            x1={op.line[0]}
            y1={op.line[1]}
            x2={op.line[2]}
            y2={op.line[3]}
            {...common}
            stroke={common.stroke ?? 'var(--border-strong)'}
          >
            {title}
          </line>
        );
      if (op.path)
        return (
          <path key={i} d={op.path} {...common}>
            {title}
          </path>
        );
      if (op.text !== undefined)
        return (
          <text
            key={i}
            x={op.at?.[0]}
            y={op.at?.[1]}
            textAnchor={op.anchor ?? 'start'}
            dominantBaseline="middle"
            fontSize={op.size === 'lg' ? 16 : op.size === 'sm' ? 10 : 12}
            {...common}
          >
            {op.text}
            {title}
          </text>
        );
      return null;
    });
  return (
    <svg
      width="100%"
      height={height}
      viewBox={viewBox?.join(' ')}
      preserveAspectRatio="xMidYMid meet"
      role="img"
    >
      {draw(ops)}
    </svg>
  );
}

// ---------------------------------------------------------------- QR code (byte mode, error correction M, versions 1–10)

export function QrCodeView({ value, size = 160 }: { value: string; size?: number }) {
  const matrix = useMemo(() => qr(value), [value]);
  if (!matrix) return <code className="text-[11px] break-all">{value}</code>;
  const n = matrix.length;
  const cell = size / (n + 8);
  let d = '';
  matrix.forEach((row, y) =>
    row.forEach((on, x) => {
      if (on) d += `M${(x + 4) * cell},${(y + 4) * cell}h${cell}v${cell}h-${cell}z`;
    }),
  );
  return (
    <svg width={size} height={size} role="img" aria-label={value} className="rounded bg-white">
      <path d={d} fill="#000" />
    </svg>
  );
}

// A compact QR encoder (ISO/IEC 18004), enough for links and codes: byte mode, level M, versions 1–10, mask chosen by penalty.
const EC_M: [number, number, number, number, number][] = [
  // [ec codewords per block, group1 blocks, group1 data cw, group2 blocks, group2 data cw]
  [10, 1, 16, 0, 0],
  [16, 1, 28, 0, 0],
  [26, 1, 44, 0, 0],
  [18, 2, 32, 0, 0],
  [24, 2, 43, 0, 0],
  [16, 4, 27, 0, 0],
  [18, 4, 31, 0, 0],
  [22, 2, 38, 2, 39],
  [22, 3, 36, 2, 37],
  [26, 4, 43, 1, 44],
];
const ALIGN: number[][] = [
  [],
  [6, 18],
  [6, 22],
  [6, 26],
  [6, 30],
  [6, 34],
  [6, 22, 38],
  [6, 24, 42],
  [6, 26, 46],
  [6, 28, 50],
];

function qr(text: string): boolean[][] | null {
  const data = Array.from(new TextEncoder().encode(text));
  let version = 0;
  for (let v = 1; v <= 10; v++) {
    const [, b1, c1, b2, c2] = EC_M[v - 1]!;
    const capacity = b1 * c1 + b2 * c2;
    const bits = 4 + (v < 10 ? 8 : 16) + data.length * 8;
    if (bits <= capacity * 8) {
      version = v;
      break;
    }
  }
  if (!version) return null;
  const [ecLen, b1, c1, b2, c2] = EC_M[version - 1]!;
  const total = b1 * c1 + b2 * c2;
  const bits: number[] = [];
  const put = (v: number, n: number) => {
    for (let i = n - 1; i >= 0; i--) bits.push((v >> i) & 1);
  };
  put(4, 4);
  put(data.length, version < 10 ? 8 : 16);
  data.forEach((b) => put(b, 8));
  put(0, Math.min(4, total * 8 - bits.length));
  while (bits.length % 8) bits.push(0);
  const cw: number[] = [];
  for (let i = 0; i < bits.length; i += 8) cw.push(parseInt(bits.slice(i, i + 8).join(''), 2));
  for (let p = 0; cw.length < total; p++) cw.push(p % 2 ? 0x11 : 0xec);
  // Reed–Solomon over GF(256)
  const exp = new Array<number>(512);
  const log = new Array<number>(256);
  let x = 1;
  for (let i = 0; i < 255; i++) {
    exp[i] = x;
    log[x] = i;
    x <<= 1;
    if (x & 0x100) x ^= 0x11d;
  }
  for (let i = 255; i < 512; i++) exp[i] = exp[i - 255]!;
  const mul = (a: number, b: number) => (a && b ? exp[log[a]! + log[b]!]! : 0);
  let gen = [1];
  for (let i = 0; i < ecLen; i++) {
    const next = new Array<number>(gen.length + 1).fill(0);
    gen.forEach((g, j) => {
      next[j] ^= g;
      next[j + 1] ^= mul(g, exp[i]!);
    });
    gen = next;
  }
  const blocks: number[][] = [];
  let at = 0;
  for (let i = 0; i < b1 + b2; i++) {
    const n = i < b1 ? c1 : c2;
    blocks.push(cw.slice(at, at + n));
    at += n;
  }
  const ecc = blocks.map((b) => {
    const r = [...b, ...new Array<number>(ecLen).fill(0)];
    for (let i = 0; i < b.length; i++) {
      const f = r[i]!;
      if (f) gen.forEach((g, j) => (r[i + j] ^= mul(g, f)));
    }
    return r.slice(b.length);
  });
  const final: number[] = [];
  for (let i = 0; i < Math.max(c1, c2); i++)
    blocks.forEach((b) => i < b.length && final.push(b[i]!));
  for (let i = 0; i < ecLen; i++) ecc.forEach((e) => final.push(e[i]!));

  const size = version * 4 + 17;
  const m: (boolean | null)[][] = Array.from({ length: size }, () =>
    new Array<boolean | null>(size).fill(null),
  );
  const fn: boolean[][] = Array.from({ length: size }, () => new Array<boolean>(size).fill(false));
  const set = (r: number, c: number, v: boolean) => {
    m[r]![c] = v;
    fn[r]![c] = true;
  };
  const finder = (r: number, c: number) => {
    for (let i = -1; i <= 7; i++)
      for (let j = -1; j <= 7; j++) {
        if (r + i < 0 || r + i >= size || c + j < 0 || c + j >= size) continue;
        const on =
          i >= 0 &&
          i <= 6 &&
          j >= 0 &&
          j <= 6 &&
          (i === 0 || i === 6 || j === 0 || j === 6 || (i >= 2 && i <= 4 && j >= 2 && j <= 4));
        set(r + i, c + j, on);
      }
  };
  finder(0, 0);
  finder(0, size - 7);
  finder(size - 7, 0);
  for (let i = 8; i < size - 8; i++) {
    set(6, i, i % 2 === 0);
    set(i, 6, i % 2 === 0);
  }
  const al = ALIGN[version - 1]!;
  for (const r of al)
    for (const c of al) {
      if (fn[r]![c]) continue;
      for (let i = -2; i <= 2; i++)
        for (let j = -2; j <= 2; j++) set(r + i, c + j, Math.max(Math.abs(i), Math.abs(j)) !== 1);
    }
  set(size - 8, 8, true);
  for (let i = 0; i < 9; i++) {
    if (!fn[8]![i]) set(8, i, false);
    if (!fn[i]![8]) set(i, 8, false);
  }
  for (let i = 0; i < 8; i++) {
    set(8, size - 1 - i, false);
    set(size - 1 - i, 8, false);
  }
  if (version >= 7) {
    let rem = version;
    for (let i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1f25);
    const v = (version << 12) | rem;
    for (let i = 0; i < 18; i++) {
      const bit = ((v >> i) & 1) === 1;
      set(size - 11 + (i % 3), Math.floor(i / 3), bit);
      set(Math.floor(i / 3), size - 11 + (i % 3), bit);
    }
  }
  // data, in the zigzag
  const dataBits: number[] = [];
  final.forEach((b) => {
    for (let i = 7; i >= 0; i--) dataBits.push((b >> i) & 1);
  });
  let k = 0;
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    for (let vert = 0; vert < size; vert++)
      for (let j = 0; j < 2; j++) {
        const c = right - j;
        const upward = ((right + 1) & 2) === 0;
        const r = upward ? size - 1 - vert : vert;
        if (fn[r]![c]) continue;
        m[r]![c] = k < dataBits.length ? dataBits[k++] === 1 : false;
      }
  }
  const masks = [
    (r: number, c: number) => (r + c) % 2 === 0,
    (r: number) => r % 2 === 0,
    (_: number, c: number) => c % 3 === 0,
    (r: number, c: number) => (r + c) % 3 === 0,
    (r: number, c: number) => (Math.floor(r / 2) + Math.floor(c / 3)) % 2 === 0,
    (r: number, c: number) => ((r * c) % 2) + ((r * c) % 3) === 0,
    (r: number, c: number) => (((r * c) % 2) + ((r * c) % 3)) % 2 === 0,
    (r: number, c: number) => (((r + c) % 2) + ((r * c) % 3)) % 2 === 0,
  ];
  let best: boolean[][] | null = null;
  let bestScore = Infinity;
  masks.forEach((mask, mi) => {
    const g = m.map((row, r) =>
      row.map((v, c) => (fn[r]![c] ? Boolean(v) : Boolean(v) !== mask(r, c))),
    );
    // format bits: level M (00) with the mask
    let f = (0 << 3) | mi;
    let rem = f;
    for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
    f = ((f << 10) | rem) ^ 0x5412;
    const bit = (i: number) => ((f >> i) & 1) === 1;
    for (let i = 0; i <= 5; i++) g[i]![8] = bit(i);
    g[7]![8] = bit(6);
    g[8]![8] = bit(7);
    g[8]![7] = bit(8);
    for (let i = 9; i < 15; i++) g[8]![14 - i] = bit(i);
    for (let i = 0; i < 8; i++) g[8]![size - 1 - i] = bit(i);
    for (let i = 8; i < 15; i++) g[size - 15 + i]![8] = bit(i);
    g[size - 8]![8] = true;
    // penalty: runs and 2x2 blocks (enough to avoid the worst masks)
    let score = 0;
    for (let r = 0; r < size; r++) {
      let run = 1;
      for (let c = 1; c < size; c++) {
        if (g[r]![c] === g[r]![c - 1]) run++;
        else {
          if (run >= 5) score += run - 2;
          run = 1;
        }
      }
      if (run >= 5) score += run - 2;
    }
    for (let r = 0; r < size - 1; r++)
      for (let c = 0; c < size - 1; c++)
        if (g[r]![c] === g[r]![c + 1] && g[r]![c] === g[r + 1]![c] && g[r]![c] === g[r + 1]![c + 1])
          score += 3;
    if (score < bestScore) {
      bestScore = score;
      best = g;
    }
  });
  return best;
}
