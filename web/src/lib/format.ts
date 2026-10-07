// Display formatting. Money arrives as integer nano-USD (1 USD = 1e9 nanos).

export const NANOS_PER_USD = 1_000_000_000;

export function usdFromNanos(nanos: number): number {
  return nanos / NANOS_PER_USD;
}

/** "$12.34", "$0.0213", "$0.009" — 3 significant digits below $1 so per-request costs stay visible. */
export function formatUsd(usd: number | null | undefined, opts: { compact?: boolean } = {}): string {
  if (usd === null || usd === undefined || Number.isNaN(usd)) return '—';
  const abs = Math.abs(usd);
  if (abs === 0) return '$0';
  if (opts.compact && abs >= 1000) return '$' + `${new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(usd)}`;
  if (abs >= 1) return '$' + `${new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(usd)}`;
  return '$' + `${new Intl.NumberFormat('en-US', { maximumSignificantDigits: 3, maximumFractionDigits: 10 }).format(usd)}`;
}

/** Short axis label: "$0.01", "$2.5", "$1.2K". */
export function formatUsdAxis(usd: number): string {
  if (usd === 0) return '$0';
  if (Math.abs(usd) >= 1000) return formatUsd(usd, { compact: true });
  return '$' + `${new Intl.NumberFormat('en-US', { maximumSignificantDigits: 2, maximumFractionDigits: 10 }).format(usd)}`;
}

export function formatNanos(nanos: number | null | undefined): string {
  return nanos === null || nanos === undefined ? '—' : formatUsd(usdFromNanos(nanos));
}

/** Per-1M-token unit price: "$3", "$0.075", "$1.25". */
export function formatUnitPrice(price: number | string | null | undefined): string {
  if (price === null || price === undefined || price === '') return '—';
  const n = typeof price === 'string' ? Number(price) : price;
  if (Number.isNaN(n)) return String(price);
  return `$${Number(n.toPrecision(8))}`;
}

/** 1234 → "1,234"; 8_100_000 → "8.1M" when compact. */
export function formatTokens(n: number | null | undefined, compact = false): string {
  if (n === null || n === undefined) return '—';
  if (compact && Math.abs(n) >= 10_000) {
    return new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(n);
  }
  return new Intl.NumberFormat('en-US').format(n);
}

export function formatMs(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return '—';
  if (ms >= 10_000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.round(ms)} ms`;
}

export function formatTps(tps: number | null | undefined): string {
  if (tps === null || tps === undefined || !Number.isFinite(tps) || tps < 0) return '—';
  return `${tps.toFixed(1)} tok/s`;
}

export function formatPercent(ratio: number | null | undefined): string {
  if (ratio === null || ratio === undefined || Number.isNaN(ratio)) return '—';
  return `${(ratio * 100).toFixed(ratio >= 0.999 || ratio === 0 ? 0 : 1)}%`;
}

export function formatContext(tokens: number | null | undefined): string {
  if (!tokens) return '—';
  if (tokens >= 1_000_000) return `${Number((tokens / 1_000_000).toFixed(2))}M`;
  if (tokens >= 1000) return `${Math.round(tokens / 1000)}K`;
  return String(tokens);
}

export function formatTime(iso: string, locale: string): string {
  const d = new Date(iso);
  const today = new Date();
  const sameDay = d.toDateString() === today.toDateString();
  return new Intl.DateTimeFormat(locale === 'zh' ? 'zh-CN' : 'en-US', {
    ...(sameDay ? { second: '2-digit' } : { month: '2-digit', day: '2-digit' }),
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(d);
}

export function formatDateTime(iso: string | null | undefined, locale: string): string {
  if (!iso) return '—';
  return new Intl.DateTimeFormat(locale === 'zh' ? 'zh-CN' : 'en-US', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(new Date(iso));
}

/** Short summary of a pricing schedule's base: "$3 / $15". */
export function priceSummary(p: { base?: Record<string, unknown> } | null | undefined): string {
  if (!p?.base) return '—';
  const input = p.base.input as number | null | undefined;
  const output = p.base.output as number | null | undefined;
  if (input == null && output == null) return '—';
  return `${formatUnitPrice(input)} / ${formatUnitPrice(output)}`;
}

export function pretty(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'string') {
    try {
      return JSON.stringify(JSON.parse(value), null, 2);
    } catch {
      return value;
    }
  }
  return JSON.stringify(value, null, 2);
}
