import { Fragment, type ReactNode } from 'react';

import { useSettings } from '../api/hooks';
import { resolveCurrency, setDisplayCurrency } from '../lib/currency';

/**
 * Keeps lib/currency.ts (what every money format reads) in step with the settings, and re-mounts the pages below when the display
 * currency or its rate changes so each amount is drawn again. Money formatting is plain functions, not hooks; this is the one
 * place that tells them which currency to use.
 */
export function DisplayCurrencyProvider({ children }: { children: ReactNode }) {
  const settings = useSettings().data;
  const next = resolveCurrency(settings?.displayCurrency, settings?.currencyRates);
  // Set while rendering, before any child formats an amount.
  setDisplayCurrency(next);
  return <Fragment key={`${next.code}:${next.rate}`}>{children}</Fragment>;
}
