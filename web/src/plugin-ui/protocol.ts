// The v2 plugin UI protocol on the shell side (design/plugin-v2 03, 05): the node and message shapes, applying a patch,
// the local-condition evaluator, responsive values, formatting and chart ticks. The Avalonia shell implements exactly
// the same rules (src/Astra.Desktop/PluginUi); tests/fixtures/plugin-ui-behavior holds the cases both run.

export interface UiNode {
  type: string;
  key?: string;
  props: Record<string, unknown>;
  children?: UiNode[];
}

export interface UiAction {
  action: string;
  args?: unknown;
}

export type UiOp =
  | { op: 'props'; path: number[]; set?: Record<string, unknown>; unset?: string[] }
  | { op: 'insert'; path: number[]; index: number; node: UiNode }
  | { op: 'remove'; path: number[]; index: number }
  | { op: 'move'; path: number[]; from: number; to: number }
  | { op: 'replace'; path: number[]; node: UiNode };

export type UiEffect = { type: string; [k: string]: unknown };

export type UiMessage =
  | { type: 'patch'; base: number; rev: number; ack?: number; ops: UiOp[] }
  | { type: 'reset'; rev: number; tree: UiNode; collections: Record<string, UiCollectionState> }
  | { type: 'effect'; effect: UiEffect }
  | { type: 'ask'; id: string; effect: UiEffect }
  | {
      type: 'data';
      collection: string;
      op: string;
      items?: unknown[];
      meta?: Record<string, unknown>;
      key?: string;
      patch?: Record<string, unknown>;
    }
  | { type: 'error'; message: string }
  | { type: 'closed'; reason: string };

export interface UiCollectionState {
  items: Record<string, unknown>[];
  meta: Record<string, unknown>;
  children: Record<string, unknown[]>;
}

/** The UI protocol version this shell renders. */
export const UI_VERSION = '1.0';

/** Applies patch ops in order; returns a new tree (nodes along changed paths are copied). */
export function applyPatch(tree: UiNode, ops: UiOp[]): UiNode {
  let root = tree;
  for (const op of ops) root = applyOne(root, op);
  return root;
}

function applyOne(root: UiNode, op: UiOp): UiNode {
  if (op.op === 'replace' && op.path.length === 0) return op.node;
  const path = op.op === 'replace' ? op.path.slice(0, -1) : op.path;
  return update(root, path, 0, (node) => {
    const kids = [...(node.children ?? [])];
    switch (op.op) {
      case 'props': {
        const props = { ...node.props, ...(op.set ?? {}) };
        for (const k of op.unset ?? []) delete props[k];
        return { ...node, props };
      }
      case 'insert':
        kids.splice(op.index, 0, op.node);
        return { ...node, children: kids };
      case 'remove':
        kids.splice(op.index, 1);
        return kids.length ? { ...node, children: kids } : { ...node, children: undefined };
      case 'move': {
        const [item] = kids.splice(op.from, 1);
        kids.splice(op.to, 0, item!);
        return { ...node, children: kids };
      }
      case 'replace':
        kids[op.path[op.path.length - 1]!] = op.node;
        return { ...node, children: kids };
    }
  });
}

function update(node: UiNode, path: number[], depth: number, fn: (n: UiNode) => UiNode): UiNode {
  if (depth === path.length) return fn(node);
  const kids = [...(node.children ?? [])];
  const i = path[depth]!;
  kids[i] = update(kids[i]!, path, depth + 1, fn);
  return { ...node, children: kids };
}

// ---------- local conditions (03 §6): eq ne and or not in empty, over ['local', name]

export type Expr = unknown;

export function evalWhen(expr: Expr, local: Record<string, unknown>): boolean {
  return Boolean(value(expr, local));
}

function value(expr: Expr, local: Record<string, unknown>): unknown {
  if (!Array.isArray(expr)) return expr;
  const [op, ...args] = expr as [string, ...Expr[]];
  switch (op) {
    case 'local':
      return local[String(args[0])];
    case 'eq':
      return same(value(args[0], local), value(args[1], local));
    case 'ne':
      return !same(value(args[0], local), value(args[1], local));
    case 'and':
      return args.every((a) => Boolean(value(a, local)));
    case 'or':
      return args.some((a) => Boolean(value(a, local)));
    case 'not':
      return !value(args[0], local);
    case 'in': {
      const list = value(args[1], local);
      return Array.isArray(list) && list.some((x) => same(x, value(args[0], local)));
    }
    case 'empty': {
      const v = value(args[0], local);
      return v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0);
    }
    case 'gt':
      return Number(value(args[0], local)) > Number(value(args[1], local));
    case 'lt':
      return Number(value(args[0], local)) < Number(value(args[1], local));
    default:
      return expr;
  }
}

function same(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

/** visible / disabled: a boolean, or { when: expr } evaluated against the local state bag. */
export function flag(v: unknown, local: Record<string, unknown>, fallback: boolean): boolean {
  if (v === undefined || v === null) return fallback;
  if (typeof v === 'boolean') return v;
  if (typeof v === 'object' && v !== null && 'when' in v)
    return evalWhen((v as { when: Expr }).when, local);
  return fallback;
}

// ---------- responsive values (05 §1): { compact, regular, wide } by the view container's width class

export type WidthClass = 'compact' | 'regular' | 'wide';

export function widthClass(px: number): WidthClass {
  return px < 480 ? 'compact' : px >= 960 ? 'wide' : 'regular';
}

export function responsive<T>(
  v: T | Partial<Record<WidthClass, T>> | undefined,
  width: WidthClass,
): T | undefined {
  if (
    v !== null &&
    typeof v === 'object' &&
    !Array.isArray(v) &&
    ('compact' in v || 'regular' in v || 'wide' in v)
  ) {
    const r = v as Partial<Record<WidthClass, T>>;
    return (
      r[width] ??
      (width === 'wide' ? r.regular : width === 'compact' ? r.regular : undefined) ??
      r.compact ??
      r.regular ??
      r.wide
    );
  }
  return v as T | undefined;
}

// ---------- tokens → CSS

export const GAP: Record<string, number> = {
  '0': 0,
  '1': 4,
  '2': 8,
  '3': 12,
  '4': 16,
  '6': 24,
  '8': 32,
};
export const WIDTH: Record<string, string> = {
  auto: 'auto',
  fill: '100%',
  xs: '120px',
  sm: '200px',
  md: '320px',
  lg: '480px',
  xl: '720px',
};

export function toneColor(tone: unknown): string {
  switch (tone) {
    case 'accent':
      return 'var(--accent)';
    case 'success':
      return 'var(--green)';
    case 'warning':
      return 'var(--orange)';
    case 'danger':
      return 'var(--red)';
    default:
      return 'var(--text-secondary)';
  }
}

/** The 8-colour data palette (05 §2); one set for light and dark, chosen for contrast on both. */
export const PALETTE = [
  '#0a84ff',
  '#30d158',
  '#ff9f0a',
  '#bf5af2',
  '#ff375f',
  '#64d2ff',
  '#ffd60a',
  '#ac8e68',
];

export function seriesColor(s: { tone?: unknown; color?: unknown }, index: number): string {
  if (typeof s.color === 'number') return PALETTE[s.color % PALETTE.length]!;
  if (typeof s.color === 'string' && /^#[0-9a-fA-F]{6}$/.test(s.color)) return s.color;
  if (s.tone) return toneColor(s.tone);
  return PALETTE[index % PALETTE.length]!;
}

// ---------- formatting (05 §9)

export function format(kind: unknown, v: unknown, locale: string): string {
  if (v === null || v === undefined) return '';
  const n = typeof v === 'number' ? v : Number(v);
  switch (kind) {
    case 'number':
      return Number.isFinite(n) ? n.toLocaleString(locale) : String(v);
    case 'compact':
      return Number.isFinite(n)
        ? new Intl.NumberFormat(locale, { notation: 'compact', maximumFractionDigits: 1 }).format(n)
        : String(v);
    case 'percent':
      return Number.isFinite(n)
        ? new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 1 }).format(n)
        : String(v);
    case 'money': {
      const usd = n / 1e9;
      const digits = Math.abs(usd) >= 1 ? 2 : Math.abs(usd) >= 0.01 ? 4 : 6;
      return Number.isFinite(usd) ? '$' + usd.toFixed(digits) : String(v);
    }
    case 'tokens':
      return Number.isFinite(n)
        ? new Intl.NumberFormat(locale, { notation: 'compact', maximumFractionDigits: 1 }).format(n)
        : String(v);
    case 'bytes': {
      if (!Number.isFinite(n)) return String(v);
      const units = ['B', 'KB', 'MB', 'GB', 'TB'];
      let x = n;
      let u = 0;
      while (Math.abs(x) >= 1024 && u < units.length - 1) {
        x /= 1024;
        u++;
      }
      return `${u === 0 ? x : x.toFixed(1)} ${units[u]}`;
    }
    case 'duration':
      if (!Number.isFinite(n)) return String(v);
      return n < 1000
        ? `${Math.round(n)} ms`
        : n < 60_000
          ? `${(n / 1000).toFixed(1)} s`
          : `${Math.floor(n / 60_000)} min ${Math.round((n % 60_000) / 1000)} s`;
    case 'date':
      return dateOf(v)?.toLocaleDateString(locale) ?? String(v);
    case 'time':
      return (
        dateOf(v)?.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' }) ?? String(v)
      );
    case 'datetime':
      return (
        dateOf(v)?.toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'short' }) ?? String(v)
      );
    case 'relative': {
      const d = dateOf(v);
      if (!d) return String(v);
      const s = Math.round((d.getTime() - Date.now()) / 1000);
      const rtf = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
      const abs = Math.abs(s);
      if (abs < 60) return rtf.format(s, 'second');
      if (abs < 3600) return rtf.format(Math.round(s / 60), 'minute');
      if (abs < 86400) return rtf.format(Math.round(s / 3600), 'hour');
      return rtf.format(Math.round(s / 86400), 'day');
    }
    default:
      return typeof v === 'string'
        ? v
        : typeof v === 'number' || typeof v === 'boolean'
          ? String(v)
          : JSON.stringify(v);
  }
}

function dateOf(v: unknown): Date | null {
  if (typeof v !== 'string' && typeof v !== 'number') return null;
  // a bare YYYY-MM-DD is a calendar date: show it as that day, not as UTC midnight shifted
  const d =
    typeof v === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(v)
      ? new Date(v + 'T00:00:00')
      : new Date(v);
  return Number.isNaN(d.getTime()) ? null : d;
}

// ---------- chart ticks: the "nice numbers" both shells use for value axes (04 §7)

export function niceTicks(min: number, max: number, count = 4): number[] {
  if (!Number.isFinite(min) || !Number.isFinite(max)) return [0];
  if (min === max) {
    if (min === 0) return [0, 1];
    max = min > 0 ? min * 2 : 0;
    min = min > 0 ? 0 : min * 2;
  }
  const span = niceNum(max - min, false);
  const step = niceNum(span / Math.max(1, count), true);
  const lo = Math.floor(min / step) * step;
  const hi = Math.ceil(max / step) * step;
  const ticks: number[] = [];
  for (let v = lo; v <= hi + step / 2; v += step) ticks.push(Number(v.toPrecision(12)));
  return ticks;
}

function niceNum(range: number, round: boolean): number {
  const exp = Math.floor(Math.log10(range));
  const f = range / Math.pow(10, exp);
  const nf = round
    ? f < 1.5
      ? 1
      : f < 3
        ? 2
        : f < 7
          ? 5
          : 10
    : f <= 1
      ? 1
      : f <= 2
        ? 2
        : f <= 5
          ? 5
          : 10;
  return nf * Math.pow(10, exp);
}

/** Largest-triangle-like downsampling that keeps each bucket's min and max, for lines wider than their pixels. */
export function downsample<T>(rows: T[], value: (r: T) => number, max: number): T[] {
  if (rows.length <= max) return rows;
  const bucket = rows.length / (max / 2);
  const out: T[] = [];
  for (let b = 0; b < rows.length; b += bucket) {
    const slice = rows.slice(Math.floor(b), Math.floor(b + bucket));
    if (!slice.length) continue;
    let lo: T = slice[0] as T;
    let hi: T = slice[0] as T;
    for (const r of slice) {
      if (value(r) < value(lo)) lo = r;
      if (value(r) > value(hi)) hi = r;
    }
    if (rows.indexOf(lo) <= rows.indexOf(hi)) out.push(lo, hi);
    else out.push(hi, lo);
  }
  return out;
}

// ---------- dates (04 §7): calendar dates are YYYY-MM-DD, instants are ISO UTC

export function todayIso(): string {
  const d = new Date();
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export function pad(n: number): string {
  return String(n).padStart(2, '0');
}

/** "today", "-30d", "+1y" or a YYYY-MM-DD → YYYY-MM-DD in local time. */
export function resolveDate(v: unknown): string | undefined {
  if (typeof v !== 'string' || !v) return undefined;
  if (/^\d{4}-\d{2}-\d{2}/.test(v)) return v.slice(0, 10);
  const d = new Date();
  if (v === 'today') return todayIso();
  const m = /^([+-]\d+)([dwmy])$/.exec(v);
  if (!m) return undefined;
  const n = Number(m[1]);
  if (m[2] === 'd') d.setDate(d.getDate() + n);
  if (m[2] === 'w') d.setDate(d.getDate() + 7 * n);
  if (m[2] === 'm') d.setMonth(d.getMonth() + n);
  if (m[2] === 'y') d.setFullYear(d.getFullYear() + n);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export function presetRange(name: string): { from: string; to: string } | null {
  const t = new Date();
  const iso = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  const back = (days: number) => {
    const d = new Date(t);
    d.setDate(d.getDate() - days);
    return iso(d);
  };
  switch (name) {
    case 'today':
      return { from: iso(t), to: iso(t) };
    case 'yesterday':
      return { from: back(1), to: back(1) };
    case '7d':
      return { from: back(6), to: iso(t) };
    case '30d':
      return { from: back(29), to: iso(t) };
    case '90d':
      return { from: back(89), to: iso(t) };
    case 'thisMonth':
      return { from: iso(new Date(t.getFullYear(), t.getMonth(), 1)), to: iso(t) };
    case 'lastMonth':
      return {
        from: iso(new Date(t.getFullYear(), t.getMonth() - 1, 1)),
        to: iso(new Date(t.getFullYear(), t.getMonth(), 0)),
      };
    default:
      return null;
  }
}
