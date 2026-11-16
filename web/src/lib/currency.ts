// Display currency with manual exchange rates (mirror of Astra.Core/Currency/DisplayCurrency.cs). Money is always stored and sent as
// USD (nano-USD); the console only changes how it reads. Rates are typed by the user (units of currency per 1 USD) — Astra never
// fetches them. Money formatting reads this module's current value (`formatUsd` in format.ts), which `DisplayCurrencyProvider`
// keeps in step with the settings.

export interface CurrencyInfo {
  code: string;
  symbol: string;
  /** Decimals of an amount of 1 or more. */
  decimals: number;
}

export const CURRENCIES: readonly CurrencyInfo[] = [
  { code: 'USD', symbol: '$', decimals: 2 },
  { code: 'CNY', symbol: '¥', decimals: 2 },
  { code: 'EUR', symbol: '€', decimals: 2 },
  { code: 'GBP', symbol: '£', decimals: 2 },
  { code: 'JPY', symbol: 'JP¥', decimals: 0 },
  { code: 'HKD', symbol: 'HK$', decimals: 2 },
  { code: 'TWD', symbol: 'NT$', decimals: 0 },
  { code: 'KRW', symbol: '₩', decimals: 0 },
  { code: 'SGD', symbol: 'S$', decimals: 2 },
  { code: 'AUD', symbol: 'A$', decimals: 2 },
  { code: 'CAD', symbol: 'C$', decimals: 2 },
  { code: 'CHF', symbol: 'CHF ', decimals: 2 },
  { code: 'RUB', symbol: '₽', decimals: 2 },
  { code: 'INR', symbol: '₹', decimals: 2 },
  { code: 'BRL', symbol: 'R$', decimals: 2 },
];

export interface DisplayCurrency extends CurrencyInfo {
  /** Units of this currency per 1 USD. */
  rate: number;
}

const USD: DisplayCurrency = { code: 'USD', symbol: '$', decimals: 2, rate: 1 };
let current: DisplayCurrency = USD;

/** A rate text is a plain positive decimal. */
export function parseRate(text: string | null | undefined): number | null {
  const s = (text ?? '').trim();
  if (!/^\d+(\.\d+)?$/.test(s)) return null;
  const n = Number(s);
  return n > 0 && n <= 1e9 ? n : null;
}

/** What the settings say, resolved: USD when the choice is USD, unknown or has no valid rate. */
export function resolveCurrency(code: string | undefined, rates: Record<string, string> | undefined): DisplayCurrency {
  const info = CURRENCIES.find((c) => c.code === code?.toUpperCase());
  if (!info || info.code === 'USD') return USD;
  const rate = parseRate(rates?.[info.code]);
  return rate ? { ...info, rate } : USD;
}

export function setDisplayCurrency(next: DisplayCurrency) {
  current = next;
}

export const getDisplayCurrency = (): DisplayCurrency => current;

/** USD → display currency. */
export const fromUsd = (usd: number): number => usd * current.rate;

/** Display currency → USD (what a typed amount means). */
export const toUsd = (amount: number): number => amount / current.rate;

/** Same as `toUsd` for the given currency, whatever is current. */
export const toUsdWith = (c: DisplayCurrency, amount: number): number => amount / c.rate;

/** "$12.34" → in the display currency: its decimals from 1 up, three significant digits below 1; zero is "<symbol>0". */
export function formatMoney(usd: number, opts: { compact?: boolean } = {}): string {
  const c = current;
  const value = usd * c.rate;
  const abs = Math.abs(value);
  if (abs === 0) return `${c.symbol}0`;
  if (opts.compact && abs >= 1000)
    return c.symbol + new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(value);
  if (abs >= 1) return c.symbol + new Intl.NumberFormat('en-US', { minimumFractionDigits: c.decimals, maximumFractionDigits: c.decimals }).format(value);
  return c.symbol + new Intl.NumberFormat('en-US', { maximumSignificantDigits: 3, maximumFractionDigits: 10 }).format(value);
}

/** Short axis label: "$0.01", "$2.5", "$1.2K" in the display currency. */
export function formatMoneyAxis(usd: number): string {
  const c = current;
  const value = usd * c.rate;
  if (value === 0) return `${c.symbol}0`;
  if (Math.abs(value) >= 1000) return formatMoney(usd, { compact: true });
  return c.symbol + new Intl.NumberFormat('en-US', { maximumSignificantDigits: 2, maximumFractionDigits: 10 }).format(value);
}

/** A USD amount in the display currency as an editable number (rounded to the currency's decimals, or 4 significant digits below 1). */
export function usdToInputValue(usd: number): number {
  const v = usd * current.rate;
  if (Math.abs(v) >= 1) return Number(v.toFixed(current.decimals));
  return Number(v.toPrecision(4));
}
