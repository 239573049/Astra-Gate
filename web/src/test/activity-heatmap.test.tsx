import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { ActivityHeatmap } from '../components/arc/activity-heatmap/activity-heatmap';
import { activityGrid } from '../lib/stats';

const today = new Date();
const pad = (n: number) => String(n).padStart(2, '0');
const TODAY = `${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}`;

function show(data: { day: string; requests: number }[]) {
  return render(
    <ActivityHeatmap
      label="Activity"
      data={data}
      weekdays={['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']}
      summary="2025-10-09 ~ 2026-10-08 · 1 / 365 days active · 4/day at most"
      legend={{ less: 'Less', more: 'More' }}
      formatDay={(day, requests) => `${day}: ${requests}`}
      formatMonth={(month) => `M${month}`}
      formatMonthStart={(month, year) => `M${month} ${year}`}
    />,
  );
}

describe('ActivityHeatmap', () => {
  afterEach(cleanup);

  it('draws one cell per day of the window and names itself for assistive technology', () => {
    const { container } = show([{ day: TODAY, requests: 4 }]);
    expect(screen.getByRole('img', { name: /Activity\. 2025-10-09/ })).toBeInTheDocument();
    // Whole weeks are covered, each column one week: the newest day is never alone in its column.
    // (Cell class names are hashed by the CSS module, so the grid is read by its data attributes.)
    expect(container.querySelectorAll('[data-grid] [data-level]')).toHaveLength(activityGrid(365).days.length);
    expect(container.querySelectorAll('[data-grid] > span')).toHaveLength(activityGrid(365).weeks.length);
  });

  it('keeps the weekday names in a column of their own beside the grid', () => {
    const { container } = show([]);
    const labels = [...container.querySelectorAll('[data-weekdays] > span')].map((label) => label.textContent);
    expect(labels).toEqual(['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']);
  });

  it('names the month every column opens, and the one the window starts inside of', () => {
    const { container } = show([]);
    const headers = [...container.querySelectorAll('[data-months] > span')].map((header) => header.textContent);
    const grid = activityGrid(365);
    // The first column is padded into the window's first month, so it carries that month and its year; the rest name the month whose 1st starts in them.
    expect(headers[0]).toMatch(/^M\d+ \d{4}$/);
    expect(headers.filter(Boolean)).toHaveLength(grid.months.length + 1);
    for (const month of grid.months) {
      expect(headers[month.index]).toBe(`M${month.label}`);
      expect(grid.weeks[month.index]!.some((at) => grid.days[at]!.slice(8) === '01')).toBe(true);
    }
  });

  it('sizes cells by the day against the busiest one, and leaves quiet days flat', () => {
    const yesterday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1);
    const { container } = show([
      { day: TODAY, requests: 4 },
      { day: `${yesterday.getFullYear()}-${pad(yesterday.getMonth() + 1)}-${pad(yesterday.getDate())}`, requests: 2 },
    ]);
    // Scoped to the grid's cells: the legend swatches carry the same levels.
    const level = (value: string) => container.querySelectorAll(`[data-grid] [data-level="${value}"]`);
    expect(level('4')).toHaveLength(1); // the busiest day
    expect(level('2')).toHaveLength(1); // half as busy
    expect(level('0').length).toBeGreaterThan(360); // every quiet day stays flat
  });

  it('answers a hovered cell with a bubble naming the day and its requests', () => {
    const { container } = show([{ day: TODAY, requests: 4 }]);
    const cell = container.querySelector<HTMLElement>(`[data-grid] [data-day="${TODAY}"]`)!;
    // Nothing while no cell is hovered; one bubble follows the cell in, and leaving the grid takes it away.
    expect(container.querySelector('[data-bubble]')).toBeNull();
    fireEvent.pointerOver(cell);
    const bubble = container.querySelector('[data-bubble]')!;
    expect(bubble.textContent).toBe(`${TODAY}: 4`);
    expect(bubble.getAttribute('style')).toContain('left');
    fireEvent.pointerLeave(container.querySelector('[data-grid]')!);
    expect(container.querySelector('[data-bubble]')).toBeNull();
  });

  it('shows the window summary and the legend ends', () => {
    show([]);
    expect(screen.getByText(/1 \/ 365 days active/)).toBeInTheDocument();
    expect(screen.getByText(/Less/)).toBeInTheDocument();
    expect(screen.getByText(/More/)).toBeInTheDocument();
  });
});
