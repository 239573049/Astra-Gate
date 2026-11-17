import { useRef, useState } from 'react';

import { getDisplayCurrency } from '../lib/currency';
import { formatPlainUsd } from '../lib/format';
import { Input, NumberInput } from './ui/controls';

/** A USD unit price as the text shown in the display currency (8 significant digits, like `formatUnitPrice`). */
export function priceToInputText(usd: number | null | undefined, rate: number): string {
  if (usd == null || Number.isNaN(usd)) return '';
  return String(Number((usd * rate).toPrecision(8)));
}

/** A typed amount in the display currency as USD (12 significant digits: enough for a price, none of the binary noise). */
export function inputTextToUsd(text: string, rate: number): number | null | undefined {
  const raw = text.trim();
  if (raw === '') return null;
  const n = Number(raw);
  if (Number.isNaN(n)) return undefined;
  return Number((n / rate).toPrecision(12));
}

/**
 * A price field (USD underneath, pricing JSON stays USD). In USD it is a plain number box; in another display currency the box takes
 * that currency and the USD value it saves is shown under it. A price the person did not touch is never rewritten.
 */
export function PriceInput({
  value,
  onChange,
  className,
  label,
  min,
}: {
  value: number | null | undefined;
  onChange: (usd: number | null) => void;
  className?: string;
  label?: string;
  min?: number;
}) {
  const currency = getDisplayCurrency();
  const [text, setText] = useState(() => priceToInputText(value, currency.rate));
  // what this box last emitted: a different incoming value (a reset, the JSON editor) replaces the text
  const emitted = useRef<number | null | undefined>(value);
  if (currency.code === 'USD') return <NumberInput className={className} label={label} value={value ?? null} onChange={onChange} min={min} />;
  let shown = text;
  if ((value ?? null) !== (emitted.current ?? null)) {
    shown = priceToInputText(value, currency.rate);
    emitted.current = value;
    setText(shown);
  }
  return (
    <div className={className}>
      <Input
        type="number"
        inputMode="decimal"
        step="any"
        min={min}
        aria-label={label ? `${label} (${currency.code})` : currency.code}
        placeholder={currency.code}
        style={{ fontVariantNumeric: 'tabular-nums', textAlign: 'right' }}
        value={shown}
        onChange={(e) => {
          const usd = inputTextToUsd(e.target.value, currency.rate);
          setText(e.target.value);
          if (usd === undefined) return;
          emitted.current = usd;
          onChange(usd);
        }}
      />
      {value != null && value > 0 && <div className="num mt-0.5 text-right text-[11px] text-[var(--text-muted)]">≈ {formatPlainUsd(value)}</div>}
    </div>
  );
}
