// Page patterns built from UI 1.0 components (design/plugin-v2 06 §1): plain functions that return UI nodes, so they
// need no shell support. No imports — the same file runs in Astra's sandbox (bundled by `astra-plugin build`), in Node
// tests, and in a plugin that copies it. Event props take action names of the view that renders the pattern.

const node = (type, props = {}, children) => {
  const p = {};
  let key;
  for (const [k, v] of Object.entries(props)) {
    if (v === undefined) continue;
    if (k === 'key') key = String(v);
    else p[k] = v;
  }
  const n = { type, props: p };
  if (key !== undefined) n.key = key;
  const kids = (Array.isArray(children) ? children : children === undefined || children === null ? [] : [children]).flat(Infinity).filter((c) => c != null && c !== false);
  if (kids.length > 0) n.children = kids.map((c) => (typeof c === 'string' || typeof c === 'number' ? { type: 'Text', props: { value: String(c) } } : c));
  return n;
};
const event = (action, args) => (action === undefined ? undefined : args === undefined ? action : { action, args });

/**
 * A settings page: groups of labelled rows, each holding a control. With `saveAction` the rows sit in a Form that
 * submits to it and shows the unsaved-changes bar.
 */
export function SettingsPage({ title, subtitle, groups = [], saveAction, dirtyBar = true, width = 'narrow' }) {
  const body = groups.map((g, gi) =>
    node('Group', { key: g.key ?? `group-${gi}`, title: g.title, description: g.description, footer: g.footer }, g.rows.map((r, ri) =>
      node('Row', { key: r.key ?? `row-${ri}`, label: r.label, detail: r.detail, hint: r.hint, icon: r.icon }, r.control ? [r.control] : []),
    )),
  );
  return node('Page', { title, subtitle, width }, saveAction ? [node('Form', { onSubmit: saveAction, dirtyBar }, body)] : body);
}

/**
 * A list with the selected item's detail beside it. On a compact container (`width: ctx.client.width`) it becomes
 * navigation: the list alone, or the detail alone with a back button to `backAction`. Wider, both panes in a Split
 * that stacks below `regular`.
 */
export function ListDetail({ title, items = [], selected, selectAction, backAction, detail, empty, width, backLabel = 'Back', ratio = 0.35 }) {
  const list = node('Group', { key: 'list', title }, items.length === 0
    ? [empty ?? node('EmptyState', { title: 'Nothing here yet' })]
    : items.map((it) =>
        node('Row', { key: String(it.key), label: it.label, detail: it.detail, icon: it.icon, selected: it.key === selected, onPress: event(selectAction, { key: it.key }) }),
      ));
  const pane = selected !== undefined && selected !== null && detail ? detail : node('EmptyState', { icon: 'mouse-pointer-click', title: 'Select an item' });
  if (width === 'compact') {
    if (selected === undefined || selected === null || !detail) return list;
    return node('Stack', { gap: '3' }, [
      backAction ? node('Button', { key: 'back', label: backLabel, icon: 'arrow-left', variant: 'ghost', onPress: backAction }) : null,
      node('Box', { key: 'detail' }, [pane]),
    ]);
  }
  return node('Split', { ratio, stackBelow: 'compact' }, [list, node('Box', { key: 'detail' }, [pane])]);
}

/**
 * Steps with a stepper on top, the current step's content, and back / next (finish on the last step).
 * `canNext: false` disables next; `busy` shows next as loading.
 */
export function Wizard({ steps = [], current = 0, backAction, nextAction, finishAction, canNext = true, busy = false, labels = {} }) {
  const last = current >= steps.length - 1;
  const step = steps[current];
  return node('Stack', { gap: '4' }, [
    node('Stepper', { key: 'steps', steps: steps.map((s, i) => ({ title: s.title, description: s.description, status: i < current ? 'done' : i === current ? 'current' : 'pending' })), current }),
    node('Box', { key: `step-${current}` }, step?.content ? [step.content] : []),
    node('Toolbar', {
      key: 'nav',
      start: current > 0 && backAction ? [node('Button', { key: 'back', label: labels.back ?? 'Back', variant: 'secondary', onPress: backAction })] : [],
      end: [
        node('Button', {
          key: last ? 'finish' : 'next',
          label: last ? labels.finish ?? 'Finish' : labels.next ?? 'Next',
          variant: 'primary',
          loading: busy,
          disabled: !canNext,
          onPress: last ? finishAction ?? nextAction : nextAction,
        }),
      ],
    }),
  ]);
}

/** Metric cards in a grid, then one Section per chart. */
export function Dashboard({ title, subtitle, metrics = [], charts = [], columns = { min: 'sm' }, actions }) {
  return node('Page', { title, subtitle, width: 'wide', actions }, [
    metrics.length > 0
      ? node('Grid', { key: 'metrics', columns, gap: '3' }, metrics.map((m, i) =>
          node('MetricCard', { key: m.key ?? `metric-${i}`, label: m.label, value: m.value, format: m.format, delta: m.delta, trend: m.trend, tone: m.tone, icon: m.icon })))
      : null,
    ...charts.map((c, i) =>
      node('Section', { key: c.key ?? `chart-${i}`, title: c.title, description: c.description }, [
        node('Chart', {
          kind: c.kind ?? 'line', data: c.data, source: c.source, x: c.x, y: c.y, series: c.series, legend: c.legend,
          height: c.height ?? 240, stacked: c.stacked, empty: c.empty, loading: c.loading, onSelect: c.onSelect,
        }),
      ])),
  ]);
}

/**
 * A table with create / edit in a Sheet and delete behind a confirm. The view keeps `sheet` in its state:
 * `{ open, mode: 'create' | 'edit', key, title, content }`; `content` is the form's nodes.
 * Rows open with `editAction` (args: what the shell sends for the row); the sheet saves with `saveAction`,
 * closes with `closeAction`, and an edited row is deleted with `deleteAction` ({ key }).
 */
export function CrudTable({
  title, description, columns = [], rows = [], rowKey = 'id', source, empty, loading,
  createAction, editAction, saveAction, closeAction, deleteAction, sheet, labels = {},
}) {
  const editing = sheet?.open && sheet.mode === 'edit';
  return node('Section', {
    title, description,
    actions: createAction ? [node('Button', { key: 'create', label: labels.create ?? 'New', icon: 'plus', variant: 'primary', onPress: createAction })] : undefined,
  }, [
    node('Table', {
      key: 'table', columns, rows: source ? undefined : rows, source, rowKey, loading,
      empty: empty ?? node('EmptyState', { title: labels.empty ?? 'Nothing here yet', action: createAction ? node('Button', { label: labels.create ?? 'New', onPress: createAction }) : undefined }),
      onRowPress: editAction,
    }),
    node('Sheet', {
      key: 'sheet',
      open: Boolean(sheet?.open),
      title: sheet?.title ?? (editing ? labels.edit ?? 'Edit' : labels.create ?? 'New'),
      onOpenChange: closeAction,
      footer: [
        editing && deleteAction
          ? node('ConfirmButton', { key: 'delete', label: labels.delete ?? 'Delete', confirmLabel: labels.confirmDelete ?? 'Delete for good', tone: 'danger', onPress: event(deleteAction, { key: sheet.key }) })
          : null,
        closeAction ? node('Button', { key: 'cancel', label: labels.cancel ?? 'Cancel', variant: 'secondary', onPress: closeAction }) : null,
        saveAction ? node('Button', { key: 'save', label: labels.save ?? 'Save', variant: 'primary', onPress: saveAction }) : null,
      ].filter(Boolean),
    }, sheet?.open && sheet.content ? [sheet.content].flat() : []),
  ]);
}
