import { describe, expect, it } from 'vitest';
import { ui } from 'astra';
import { mount, validateTree } from '../src/testing.mjs';

describe('dev-time tree checks', () => {
  it('passes a correct tree', () => {
    const r = validateTree(ui.Stack({ gap: '2', width: { compact: 'fill', wide: 'md' } }, [ui.Text({ value: 'a' }), ui.IconButton({ icon: 'x', a11yLabel: 'Close', onPress: 'close' })]), { actions: ['close'] });
    expect(r.errors).toEqual([]);
    expect(r.warnings).toEqual([]);
  });

  it('names unknown components and props, wrong types, missing labels and unknown actions', () => {
    const tree = ui.Stack({ gap: 'huge' as never }, [
      ui.node('Blink', {}),
      ui.Text({ value: 'x', colour: 'red' } as never),
      ui.Button({ label: 'go', onPress: 'nowhere', loading: 'yes' as never }),
      ui.IconButton({ icon: 'x' } as never),
      ui.Image({ src: 'https://a.example/x.png' } as never),
      ui.Field({}, [ui.TextField({ name: 'a' }), ui.TextField({ name: 'b' })]),
    ]);
    const { errors } = validateTree(tree, { actions: ['go'] });
    const has = (s: string) => expect(errors.some((e) => e.includes(s))).toBe(true);
    has('not a gap token');
    has('unknown component "Blink"');
    has('unknown prop "colour"');
    has('the view has no action "nowhere"');
    has('loading: expected true or false');
    has('IconButton: a11yLabel is required');
    has('Image: alt is required');
    has('takes one child, has 2');
  });

  it('warns about unkeyed lists, a used fallback and a tree near the limit; refuses a wrong tray root', () => {
    const rows = Array.from({ length: 4 }, (_, i) => ui.Row({ label: String(i) }));
    expect(validateTree(ui.Group({}, rows)).warnings[0]).toContain('4 Row children without a key');
    const future = ui.node('Hologram', { fallback: ui.Text({ value: 'old shell' }) });
    expect(validateTree(future).warnings[0]).toContain('shells show its fallback');
    const big = ui.Stack({}, Array.from({ length: 4100 }, (_, i) => ui.Text({ key: String(i), value: '' })));
    expect(validateTree(big).warnings.some((w) => w.includes('over 80%'))).toBe(true);
    expect(validateTree(ui.Table({ columns: [] }), { slot: 'tray.panel' }).errors[0]).toContain("can't be the root of tray.panel");
  });

  it('mount fails on errors, or collects them with strict: false', async () => {
    const bad = ui.defineView({ render: () => ui.Button({ label: 'x', onPress: 'missing' }) });
    await expect(mount(bad)).rejects.toThrow(/no action "missing"/);
    const view = await mount(bad, { strict: false });
    expect(view.warnings.some((w) => w.includes('no action "missing"'))).toBe(true);
  });
});
