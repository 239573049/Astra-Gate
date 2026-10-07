import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it } from 'vitest';

import type { PricingSchedule } from '../api/types';
import { emptySchedule, PricingEditor } from '../components/PricingEditor';
import { I18nProvider } from '../i18n';

function Editor({ empty = false }: { empty?: boolean }) {
  const [value, setValue] = useState<PricingSchedule | null>(empty ? null : emptySchedule());
  return <I18nProvider><PricingEditor value={value} onChange={setValue} /><output data-testid="value">{JSON.stringify(value)}</output></I18nProvider>;
}

const value = () => JSON.parse(screen.getByTestId('value').textContent!) as PricingSchedule | null;

function show(empty = false) {
  localStorage.setItem('astra.locale', 'en');
  render(<Editor empty={empty} />);
}

afterEach(() => { cleanup(); localStorage.clear(); });

describe('pricing additions use dialogs', () => {
  it('adds a context tier only after confirmation', async () => {
    show();
    fireEvent.click(screen.getAllByRole('button', { name: 'Add tier' })[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Add tier' });
    expect(value()!.context_tiers).toBeUndefined();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add' }));
    await waitFor(() => expect(value()!.context_tiers).toHaveLength(1));
    expect(value()!.context_tiers![0]).toMatchObject({ name: '>200K', threshold_input_tokens: 200_000, mode: 'whole' });
  });

  it('canceling a time-window dialog leaves the schedule untouched', async () => {
    show();
    fireEvent.click(screen.getByRole('button', { name: 'Add window' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add window' });
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Name' }), { target: { value: 'never-applied' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(value()!.time_windows).toBeUndefined();
  });

  it('adds a service tier through a dialog', async () => {
    show();
    const buttons = screen.getAllByRole('button', { name: 'Add tier' });
    fireEvent.click(buttons[1]!);
    const dialog = await screen.findByRole('dialog', { name: 'Add tier' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add' }));
    await waitFor(() => expect(value()!.service_tiers?.flex).toEqual({ multiplier: 0.5 }));
  });

  it('creating a previously unset price requires confirmation too', async () => {
    show(true);
    fireEvent.click(screen.getByRole('button', { name: 'Add pricing' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add pricing' });
    expect(value()).toBeNull();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add' }));
    await waitFor(() => expect(value()).toEqual(emptySchedule()));
  });
});
