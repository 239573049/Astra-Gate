import { describe, expect, it } from 'vitest';
import { storage, ui } from 'astra';
import { CrudTable, Dashboard, ListDetail, SettingsPage, Wizard } from '../src/patterns.mjs';
import { mount } from '../src/testing.mjs';

describe('patterns', () => {
  it('SettingsPage: groups of rows in a form that saves', async () => {
    const view = ui.defineView({
      init: () => ({}),
      render: () =>
        SettingsPage({
          title: 'Guard',
          saveAction: 'save',
          groups: [{ title: 'Words', rows: [{ label: 'Block', control: ui.Switch({ name: 'block' }) }, { label: 'List', hint: 'one per line', control: ui.TagInput({ name: 'words' }) }] }],
        }),
      actions: { save: async (_s: unknown, _a: unknown, ctx: { local: Record<string, unknown> }) => void (await storage.set('words', ctx.local.words as never)) },
    });
    const v = await mount(view);
    expect(v.find('Form')?.props.onSubmit).toBe('save');
    expect(v.findAll('Row')).toHaveLength(2);
    await v.change('words', ['a']);
    await v.press('save');
    expect(await v.storage.get('words')).toEqual(['a']);
  });

  it('ListDetail: a split when wide, navigation when compact', async () => {
    const items = [{ key: 'a', label: 'A' }, { key: 'b', label: 'B' }];
    const view = ui.defineView({
      init: () => ({ selected: null as string | null }),
      render: (s: { selected: string | null }, ctx: { client: { width: string } }) =>
        ListDetail({ items, selected: s.selected, selectAction: 'pick', backAction: 'back', width: ctx.client.width, detail: s.selected ? ui.Text({ value: 'detail ' + s.selected }) : null }),
      actions: { pick: (s: { selected: string | null }, a: { key: string }) => void (s.selected = a.key), back: (s: { selected: string | null }) => void (s.selected = null) },
    });
    const wide = await mount(view, { client: { width: 'wide' } });
    expect(wide.find('Split')?.props.stackBelow).toBe('compact');
    await wide.press('pick', { key: 'b' });
    expect(wide.find('Text')?.props.value).toBe('detail b');

    const narrow = await mount(view, { client: { width: 'compact' } });
    expect(narrow.find('Split')).toBeNull();
    expect(narrow.findAll('Row')).toHaveLength(2);
    await narrow.press('pick', { key: 'a' });
    expect(narrow.find('Row')).toBeNull();
    expect(narrow.find('back')?.props.onPress).toBe('back');
  });

  it('Wizard: back from the second step, finish on the last', async () => {
    const steps = [{ title: 'Key', content: ui.TextField({ name: 'key' }) }, { title: 'Check', content: ui.Text({ value: 'ok?' }) }];
    type S = { step: number; done: boolean };
    const view = ui.defineView<S>({
      init: () => ({ step: 0, done: false }),
      render: (s) => Wizard({ steps, current: s.step, backAction: 'back', nextAction: 'next', finishAction: 'finish' }),
      actions: { back: (s) => void s.step--, next: (s) => void s.step++, finish: (s) => void (s.done = true) },
    });
    const v = await mount(view);
    expect(v.find('back')).toBeNull();
    await v.press('next');
    expect(v.find('Stepper')?.props.current).toBe(1);
    expect(v.find('finish')?.props.onPress).toBe('finish');
    await v.press('finish');
    expect(v.state.done).toBe(true);
  });

  it('Dashboard: metric cards and charts', async () => {
    const view = ui.defineView({
      render: () =>
        Dashboard({
          title: 'Usage',
          metrics: [{ label: 'Requests', value: 1200, format: 'compact' }, { label: 'Cost', value: 3.2, format: 'money', delta: -0.1 }],
          charts: [{ title: 'Per day', kind: 'line', data: [{ t: '2026-11-01', n: 3 }], x: { key: 't', format: 'date' }, series: [{ key: 'n', label: 'Requests' }] }],
        }),
    });
    const v = await mount(view);
    expect(v.findAll('MetricCard')).toHaveLength(2);
    expect(v.find('Chart')?.props.kind).toBe('line');
  });

  it('CrudTable: create and edit in a sheet, delete behind a confirm', async () => {
    type S = { rows: { id: string; name: string }[]; sheet: { open: boolean; mode?: 'create' | 'edit'; key?: string } };
    const view = ui.defineView<S>({
      init: () => ({ rows: [{ id: '1', name: 'one' }], sheet: { open: false } }),
      render: (s) =>
        CrudTable({
          title: 'Rules', columns: [{ key: 'name', title: 'Name' }], rows: s.rows, rowKey: 'id',
          createAction: 'create', editAction: 'edit', saveAction: 'save', closeAction: 'close', deleteAction: 'remove',
          sheet: { ...s.sheet, content: ui.TextField({ name: 'name' }) },
        }),
      actions: {
        create: (s) => void (s.sheet = { open: true, mode: 'create' }),
        edit: (s, a: { key: string }) => void (s.sheet = { open: true, mode: 'edit', key: a.key }),
        save: (s, _a, ctx) => {
          s.rows.push({ id: String(s.rows.length + 1), name: String(ctx.local.name) });
          s.sheet = { open: false };
        },
        close: (s) => void (s.sheet = { open: false }),
        remove: (s, a: { key: string }) => {
          s.rows = s.rows.filter((r) => r.id !== a.key);
          s.sheet = { open: false };
        },
      },
    });
    const v = await mount(view);
    expect(v.find('Sheet')?.props.open).toBe(false);
    await v.press('create');
    expect(v.find('delete')).toBeNull();
    await v.change('name', 'two');
    await v.press('save');
    expect(v.find('Table')?.props.rows).toHaveLength(2);
    await v.press('edit', { key: '1' });
    expect(v.find('delete')?.props.onPress).toEqual({ action: 'remove', args: { key: '1' } });
    await v.press('remove', { key: '1' });
    expect(v.state.rows.map((r) => r.id)).toEqual(['2']);
  });
});
