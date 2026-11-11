// The overview's daily bill: what GET /api/stats/daily-bill returns, shaped into the text both bill styles print.
// The Avalonia client ports this file (src/Astra.Desktop.Core/Overview/DailyBillShaping.cs) and its tests pin the same
// outputs (web/src/test/bill.test.ts ↔ tests/Astra.Desktop.Tests/Overview/DailyBillShapingTests.cs): change them together.

import type { DailyBill } from '../api/types';
import type { Locale } from '../i18n';
import { formatPercent, formatTokens, formatUsd, weekdayName } from './format';

/** The two looks a bill can be printed in: a thermal receipt and a comic page. */
export type BillStyle = 'receipt' | 'manga';
export const BILL_STYLES: readonly BillStyle[] = ['receipt', 'manga'];
/** localStorage key of the last style picked (the desktop keeps it in desktop-prefs.json as "billStyle"). */
export const BILL_STYLE_KEY = 'astra.bill.style';

export interface BillModelLine {
  name: string;
  cost: string;
  requests: string;
  input: string;
  output: string;
}

export interface BillProviderBlock {
  id: string;
  name: string;
  /** The receipt prints provider names in capitals. */
  upper: string;
  cost: string;
  requests: string;
  /** Share of the day's cost (of its requests when nothing cost anything), 0..1. */
  share: number;
  models: BillModelLine[];
}

export interface BillView {
  /** "AG-20261111". */
  number: string;
  /** "2026-11-11". */
  date: string;
  /** "周三" / "Wed". */
  weekday: string;
  /** "11.11". */
  dateShort: string;
  /** "00:00 – 18:42"; a finished day ends at "24:00". */
  window: string;
  /** The window's end, "18:42". */
  until: string;
  /** "2026-11-11 18:42". */
  generatedAt: string;
  total: string;
  requests: string;
  /** "99.3%", or "—" without requests. */
  successRate: string;
  /** Grouped in full: "8,728,100". */
  inputTokens: string;
  outputTokens: string;
  /** Input + output, compact: "9.4M". */
  tokensCompact: string;
  providers: BillProviderBlock[];
  /** The provider with the largest share and that share as a whole percent ("91%"); null on an empty day. */
  top: { name: string; share: string } | null;
}

const pad = (n: number) => String(n).padStart(2, '0');

/** Wall-clock parts of a UTC instant in the bill's own offset (not the viewer's zone). */
function wall(iso: string, offsetMinutes: number) {
  const d = new Date(Date.parse(iso) + offsetMinutes * 60_000);
  return { y: d.getUTCFullYear(), mo: d.getUTCMonth() + 1, d: d.getUTCDate(), h: d.getUTCHours(), mi: d.getUTCMinutes() };
}

/** Shapes a bill for printing; amounts and counts use the overview's formatters. */
export function shapeBill(bill: DailyBill, locale: Locale): BillView {
  const tz = bill.tzOffsetMinutes;
  const [year, month, day] = bill.date.split('-').map(Number);
  // Monday-first row of the day, as the heatmap's weekday names count.
  const row = (new Date(Date.UTC(year, month - 1, day)).getUTCDay() + 6) % 7;
  const from = wall(bill.fromUtc, tz);
  const to = wall(bill.toUtc, tz);
  const dayEnded = Date.parse(bill.toUtc) - Date.parse(bill.fromUtc) >= 86_400_000;
  const until = dayEnded ? '24:00' : `${pad(to.h)}:${pad(to.mi)}`;
  const gen = wall(bill.generatedAtUtc, tz);

  const byCost = bill.costUsd > 0;
  const providers = bill.providers.map((p) => ({
    id: p.providerId,
    name: p.name,
    upper: p.name.toUpperCase(),
    cost: formatUsd(p.costUsd),
    requests: formatTokens(p.requests),
    share: byCost ? p.costUsd / bill.costUsd : bill.requests > 0 ? p.requests / bill.requests : 0,
    models: p.models.map((m) => ({
      name: m.model,
      cost: formatUsd(m.costUsd),
      requests: formatTokens(m.requests),
      input: formatTokens(m.inputTokens, true),
      output: formatTokens(m.outputTokens, true),
    })),
  }));
  const lead = providers.reduce<BillProviderBlock | null>((best, p) => (best && best.share >= p.share ? best : p), null);

  return {
    number: `AG-${bill.date.replaceAll('-', '')}`,
    date: bill.date,
    weekday: weekdayName(row, locale),
    dateShort: `${pad(month)}.${pad(day)}`,
    window: `${pad(from.h)}:${pad(from.mi)} – ${until}`,
    until,
    generatedAt: `${gen.y}-${pad(gen.mo)}-${pad(gen.d)} ${pad(gen.h)}:${pad(gen.mi)}`,
    total: formatUsd(bill.costUsd),
    requests: formatTokens(bill.requests),
    successRate: bill.requests > 0 ? formatPercent(bill.successRequests / bill.requests) : '—',
    inputTokens: formatTokens(bill.inputTokens),
    outputTokens: formatTokens(bill.outputTokens),
    tokensCompact: formatTokens(bill.inputTokens + bill.outputTokens, true),
    providers,
    top: lead ? { name: lead.name, share: `${Math.round(lead.share * 100)}%` } : null,
  };
}

/** The file name a saved bill gets: "astra-bill-2026-11-11-receipt.png". */
export const billFileName = (date: string, style: BillStyle) => `astra-bill-${date}-${style}.png`;
