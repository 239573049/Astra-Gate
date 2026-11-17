import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { PriceInput, inputTextToUsd, priceToInputText } from '../components/PriceInput';
import { resolveCurrency, setDisplayCurrency } from '../lib/currency';
import { formatUnitPrice, priceSummary } from '../lib/format';

afterEach(() => setDisplayCurrency(resolveCurrency('USD', undefined)));

describe('unit prices in the display currency', () => {
  it('reads a USD price in the display currency and stays USD by default', () => {
    expect(formatUnitPrice(3)).toBe('$3');
    setDisplayCurrency(resolveCurrency('CNY', { CNY: '7.2' }));
    expect(formatUnitPrice(3)).toBe('¥21.6');
    expect(formatUnitPrice('0.075')).toBe('¥0.54');
    expect(priceSummary({ base: { input: 3, output: 15 } })).toBe('¥21.6 / ¥108');
  });

  it('converts typed text to USD without binary noise', () => {
    expect(inputTextToUsd('21.6', 7.2)).toBe(3);
    expect(inputTextToUsd('0.54', 7.2)).toBe(0.075);
    expect(inputTextToUsd('', 7.2)).toBeNull();
    expect(priceToInputText(0.075, 7.2)).toBe('0.54');
    expect(priceToInputText(null, 7.2)).toBe('');
  });

  it('PriceInput takes the display currency and emits USD', () => {
    setDisplayCurrency(resolveCurrency('CNY', { CNY: '7.2' }));
    const onChange = vi.fn();
    render(<PriceInput label="input" value={3} onChange={onChange} />);
    const box = screen.getByLabelText('input (CNY)') as HTMLInputElement;
    expect(box.value).toBe('21.6');
    expect(screen.getByText(/≈ \$3/)).toBeTruthy();
    fireEvent.change(box, { target: { value: '36' } });
    expect(onChange).toHaveBeenCalledWith(5);
  });

  it('PriceInput in USD is the plain number box', () => {
    const onChange = vi.fn();
    render(<PriceInput label="input" value={3} onChange={onChange} />);
    fireEvent.change(screen.getByLabelText('input'), { target: { value: '4' } });
    expect(onChange).toHaveBeenCalledWith(4);
  });
});
