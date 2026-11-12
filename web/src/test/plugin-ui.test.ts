import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

import { COMPONENTS } from '../plugin-ui/components';
import { matchRoute } from '../plugin-ui/PluginPage';
import { applyPatch, evalWhen, flag, format, niceTicks, presetRange, resolveDate, responsive, type UiNode, type UiOp } from '../plugin-ui/protocol';

// the cases both shells run (tests/fixtures/plugin-ui-behavior; the Avalonia shell has the same tests)
const fixture = <T,>(name: string): T =>
  JSON.parse(readFileSync(resolve(process.cwd(), '../tests/fixtures/plugin-ui-behavior', name), 'utf8')) as T;
const schema = JSON.parse(readFileSync(resolve(process.cwd(), '../src/Astra.Plugins/V2/Ui/components.json'), 'utf8')) as {
  components: Record<string, unknown>;
};

describe('plugin UI protocol', () => {
  it('applies patches as the host computes them', () => {
    for (const c of fixture<{ cases: { name: string; before: UiNode; ops: UiOp[]; after: UiNode }[] }>('patch.json').cases)
      expect(applyPatch(c.before, c.ops), c.name).toEqual(c.after);
  });

  it('evaluates local conditions', () => {
    for (const c of fixture<{ cases: { expr: unknown; local: Record<string, unknown>; result: boolean }[] }>('conditions.json').cases)
      expect(evalWhen(c.expr, c.local), JSON.stringify(c.expr)).toBe(c.result);
    expect(flag({ when: ['eq', ['local', 'x'], 1] }, { x: 1 }, false)).toBe(true);
    expect(flag(undefined, {}, true)).toBe(true);
  });

  it('picks the same axis ticks as the desktop', () => {
    for (const c of fixture<{ cases: { min: number; max: number; count: number; ticks: number[] }[] }>('ticks.json').cases)
      expect(niceTicks(c.min, c.max, c.count), `${c.min}..${c.max}`).toEqual(c.ticks);
  });

  it('resolves responsive values by the view width', () => {
    expect(responsive({ compact: 1, regular: 2, wide: 4 }, 'wide')).toBe(4);
    expect(responsive({ compact: 1, regular: 2 }, 'wide')).toBe(2);
    expect(responsive(3, 'compact')).toBe(3);
  });

  it('formats money from nanodollars and calendar dates without shifting them', () => {
    expect(format('money', 12_345_000, 'en')).toBe('$0.0123');
    expect(format('money', 2_500_000_000, 'en')).toBe('$2.50');
    expect(format('bytes', 1536, 'en')).toBe('1.5 KB');
    expect(format('date', '2026-11-11', 'en-US')).toBe(new Date(2026, 10, 11).toLocaleDateString('en-US'));
  });

  it('resolves relative dates and presets', () => {
    expect(resolveDate('2026-01-02')).toBe('2026-01-02');
    expect(resolveDate('today')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    const week = presetRange('7d')!;
    expect((new Date(week.to).getTime() - new Date(week.from).getTime()) / 86_400_000).toBe(6);
  });

  it('matches page routes and binds their params', () => {
    expect(matchRoute('', '')).toEqual({});
    expect(matchRoute(':id', '42')).toEqual({ id: '42' });
    expect(matchRoute(':id/log', '42/log')).toEqual({ id: '42' });
    expect(matchRoute(':id', '42/log')).toBeNull();
  });

  it('renders every component of the UI 1.0 schema', () => {
    expect([...COMPONENTS].sort()).toEqual(Object.keys(schema.components).sort());
  });
});
