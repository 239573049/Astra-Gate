#!/usr/bin/env node
// The v2 plugin UI scene set (design/plugin-v2 06 §5): one directory per component under tests/fixtures/plugin-ui/, a file per
// state (default, each variant / tone / size, loading, invalid, disabled), plus composite scenes (_patterns/). The gallery
// (/plugins/dev/gallery on both shells) shows exactly these, and the visual comparison renders them on web and desktop.
//
//   node scripts/plugin-ui/gen-scenes.mjs           writes tests/fixtures/plugin-ui/
//   node scripts/plugin-ui/gen-scenes.mjs --check   fails when the files are stale or a component has no scene
import { readFileSync, writeFileSync, mkdirSync, rmSync, readdirSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = dirname(dirname(dirname(fileURLToPath(import.meta.url))));
const schema = JSON.parse(readFileSync(join(repo, 'src/Astra.Plugins/V2/Ui/components.json'), 'utf8'));
const out = join(repo, 'tests/fixtures/plugin-ui');
const check = process.argv.includes('--check');

const n = (type, props = {}, children) => ({ type, props, ...(children ? { children: Array.isArray(children) ? children : [children] } : {}) });
const text = (value) => n('Text', { value });
const opts = [
  { value: 'anthropic', label: 'Anthropic' },
  { value: 'openai', label: 'OpenAI' },
  { value: 'gemini', label: 'Gemini' },
];
const series = [
  { date: '2026-11-01', requests: 120, errors: 4 },
  { date: '2026-11-02', requests: 180, errors: 2 },
  { date: '2026-11-03', requests: 150, errors: 9 },
  { date: '2026-11-04', requests: 210, errors: 3 },
  { date: '2026-11-05', requests: 260, errors: 5 },
  { date: '2026-11-06', requests: 240, errors: 1 },
  { date: '2026-11-07', requests: 300, errors: 6 },
];
// money is nanodollars (05 §9), as everywhere in Astra
const rows = [
  { id: 'r1', model: 'claude-sonnet-4', status: 'ok', tokens: 18234, cost: 42000000 },
  { id: 'r2', model: 'gpt-5', status: 'error', tokens: 912, cost: 3000000 },
  { id: 'r3', model: 'gemini-2.5-pro', status: 'ok', tokens: 50211, cost: 118000000 },
];
const columns = [
  { key: 'model', title: 'Model', sortable: true },
  { key: 'status', title: 'Status', cell: { type: 'badge', toneBy: { ok: 'success', error: 'danger' } } },
  { key: 'tokens', title: 'Tokens', format: 'tokens', align: 'end', sortable: true },
  { key: 'cost', title: 'Cost', format: 'money', align: 'end' },
];
const tree = [
  { key: 'src', label: 'src', children: [{ key: 'src/main.js', label: 'main.js', detail: '2 KB' }, { key: 'src/gateway.js', label: 'gateway.js', badge: 'hook' }] },
  { key: 'locales', label: 'locales', children: [{ key: 'locales/en.json', label: 'en.json' }, { key: 'locales/zh.json', label: 'zh.json' }] },
  { key: 'astra.plugin.json', label: 'astra.plugin.json' },
];

// sample props per component: what the default scene shows. Inputs get name / value too (inputProps).
const SAMPLE = {
  Page: () => [{ title: 'Word guard', subtitle: 'acme.word-guard', icon: 'shield', actions: [n('Button', { label: 'Add word', variant: 'primary', onPress: 'add' })] }, [text('Page body')]],
  Stack: () => [{ gap: '3' }, [n('Badge', { label: 'one' }), n('Badge', { label: 'two', tone: 'accent' }), n('Badge', { label: 'three', tone: 'success' })]],
  Grid: () => [{ columns: 3, gap: '3' }, [1, 2, 3, 4, 5, 6].map((i) => n('Box', { surface: 'card', padding: '3' }, text(`Cell ${i}`)))],
  Split: () => [{ ratio: 0.35 }, [n('Box', { surface: 'sunken', padding: '3' }, text('List')), n('Box', { surface: 'card', padding: '3' }, text('Detail'))]],
  Scroll: () => [{ maxHeight: 'xs' }, n('Stack', { gap: '2' }, Array.from({ length: 12 }, (_, i) => text(`Line ${i + 1}`)))],
  Box: () => [{ surface: 'card', padding: '4', border: true, radius: 'md' }, text('Inside a card box')],
  Divider: () => [{ label: 'or' }],
  Spacer: () => [{ size: '4' }],
  Sticky: () => [{ edge: 'top' }, n('Toolbar', { start: [n('Button', { label: 'Save', variant: 'primary' })] })],
  Group: () => [{ title: 'Detection', description: 'What counts as a hit', footer: 'Applies to every request' }, [n('Row', { label: 'Case sensitive' }, n('Switch', { name: 'case', checked: false })), n('Row', { label: 'Action', detail: 'When a word is found' }, n('Segmented', { name: 'action', value: 'mask', options: [{ value: 'mask', label: 'Mask' }, { value: 'reject', label: 'Reject' }] }))]],
  Row: () => [{ label: 'Notifications', detail: 'A toast when a request is rejected', icon: 'bell' }, n('Switch', { name: 'notify', checked: true })],
  Card: () => [{ title: 'Today', subtitle: 'since 00:00', icon: 'activity', tone: 'accent' }, text('1,204 requests')],
  Section: () => [{ title: 'Advanced', description: 'Rarely needed', collapsible: true, defaultOpen: true }, text('Section body')],
  Tabs: () => [{ value: 'a', items: [{ value: 'a', label: 'Overview' }, { value: 'b', label: 'Hits', badge: '3' }, { value: 'c', label: 'Settings' }], onChange: 'tab' }, [n('Tab', { value: 'a' }, text('Overview tab')), n('Tab', { value: 'b' }, text('Hits tab')), n('Tab', { value: 'c' }, text('Settings tab'))]],
  Tab: () => [{ value: 'a' }, text('A tab on its own')],
  Accordion: () => [{ mode: 'single' }, [n('AccordionItem', { title: 'What is checked?', defaultOpen: true }, text('Every user message.')), n('AccordionItem', { title: 'Is anything stored?' }, text('Only counts.'))]],
  AccordionItem: () => [{ title: 'One item', defaultOpen: true }, text('Its body')],
  Disclosure: () => [{ title: 'Show details', defaultOpen: true }, text('The details')],
  Stepper: () => [{ steps: [{ title: 'Install' }, { title: 'Grant', description: '3 permissions' }, { title: 'Configure' }], current: 1 }],
  Timeline: () => [{ items: [{ title: 'Installed', time: '2026-11-10T09:00:00Z' }, { title: 'Upgraded to 2.1.0', time: '2026-11-11T08:00:00Z', tone: 'accent' }, { title: 'Hook failed', time: '2026-11-11T09:30:00Z', tone: 'danger' }] }],
  Breadcrumb: () => [{ items: [{ label: 'Plugins' }, { label: 'Word guard' }, { label: 'Rules' }] }],
  Toolbar: () => [{ start: [n('SearchField', { name: 'q', placeholder: 'Search' })], end: [n('Button', { label: 'Export', icon: 'download' }), n('Button', { label: 'Add', variant: 'primary', icon: 'plus' })] }],
  Boundary: () => [{ busy: false }, text('Content inside a boundary')],
  Text: () => [{ value: 'The quick brown fox jumps over the lazy dog.' }],
  Heading: () => [{ value: 'Section heading', level: 2 }],
  RichText: () => [{ spans: [{ text: 'Plain, ' }, { text: 'bold', bold: true }, { text: ', ' }, { text: 'code', code: true }, { text: ' and a ' }, { text: 'link', link: 'https://astra-gate.si' }] }],
  Markdown: () => [{ value: '## Markdown\n\nSome **bold**, *italic*, `code` and a [link](https://astra-gate.si).\n\n- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |' }],
  Code: () => [{ value: 'export default {\n  onRequest(req) {\n    return req;\n  },\n};', language: 'js', filename: 'gateway.js', copy: true }],
  JsonView: () => [{ value: { model: 'claude-sonnet-4', stream: true, messages: [{ role: 'user', content: 'hi' }] }, expandDepth: 2 }],
  DiffView: () => [{ before: 'max_tokens: 1024\ntemperature: 1', after: 'max_tokens: 4096\ntemperature: 1\ntop_p: 0.9', mode: 'unified' }],
  Link: () => [{ label: 'Open the docs', href: 'https://astra-gate.si/docs' }],
  Icon: () => [{ name: 'shield-check', size: 'md', tone: 'accent' }],
  Image: () => [{ src: 'https://astra-gate.si/logo.png', alt: 'Astra logo', ratio: 1, fit: 'contain', width: 'xs' }],
  Avatar: () => [{ name: 'Ada Lovelace', size: 'md', status: 'success' }],
  Badge: () => [{ label: 'beta', tone: 'accent' }],
  StatusDot: () => [{ tone: 'success', label: 'Running', pulse: true }],
  Kbd: () => [{ keys: ['mod', 'shift', 'k'] }],
  KV: () => [{ items: [{ label: 'Version', value: '2.1.0' }, { label: 'Publisher', value: 'acme' }, { label: 'Id', value: 'acme.word-guard', mono: true, copy: true }], columns: 1 }],
  Alert: () => [{ tone: 'warning', title: 'Words list is empty', message: 'Nothing will be checked until you add a word.' }],
  Banner: () => [{ tone: 'accent', message: 'A new version of this plugin is available.' }],
  EmptyState: () => [{ icon: 'inbox', title: 'No hits yet', detail: 'Requests that contain a word show up here.', action: n('Button', { label: 'Add a word', variant: 'primary' }) }],
  Skeleton: () => [{ shape: 'text', lines: 3 }],
  Spinner: () => [{ size: 'md', label: 'Loading' }],
  Progress: () => [{ value: 0.62, label: 'Syncing' }],
  Meter: () => [{ value: 70, max: 100, label: 'Quota', segments: [{ value: 50, tone: 'success', label: 'Used' }, { value: 20, tone: 'warning', label: 'Reserved' }] }],
  Counter: () => [{ value: 128430, format: 'compact' }],
  QRCode: () => [{ value: 'astra://join?gateway=https://gw.example&token=abc', size: 'md' }],
  Button: () => [{ label: 'Save', variant: 'primary', icon: 'check', onPress: 'save' }],
  IconButton: () => [{ icon: 'trash-2', variant: 'ghost', a11yLabel: 'Delete', onPress: 'delete' }],
  ButtonGroup: () => [{}, [n('Button', { label: 'Day' }), n('Button', { label: 'Week' }), n('Button', { label: 'Month' })]],
  SplitButton: () => [{ label: 'Export', variant: 'primary', items: [{ label: 'CSV', action: 'csv' }, { label: 'JSON', action: 'json' }], onPress: 'export' }],
  CopyButton: () => [{ value: 'sk-astra-…', label: 'Copy key' }],
  ConfirmButton: () => [{ label: 'Delete', confirmLabel: 'Really delete?', tone: 'danger', onPress: 'delete' }],
  Menu: () => [{ label: 'More', icon: 'ellipsis', items: [{ label: 'Rename', action: 'rename' }, { label: 'Duplicate', action: 'dup' }, { separator: true }, { label: 'Delete', action: 'del', tone: 'danger' }] }],
  ContextMenu: () => [{ items: [{ label: 'Copy', action: 'copy' }, { label: 'Open', action: 'open' }] }, n('Box', { surface: 'sunken', padding: '4' }, text('Right-click me'))],
  TextField: () => [{ name: 'endpoint', value: 'https://api.acme.example', placeholder: 'https://…', type: 'url', clearable: true }],
  PasswordField: () => [{ name: 'apiKey', placeholder: 'sk-…', hasValue: true }],
  TextArea: () => [{ name: 'notes', value: 'Line one\nLine two', rows: 4 }],
  NumberField: () => [{ name: 'limit', value: 25, min: 1, max: 100, step: 1, unit: 'req/min' }],
  MoneyField: () => [{ name: 'budget', value: 12.5, currency: 'USD' }],
  SearchField: () => [{ name: 'q', placeholder: 'Search hits', onSearch: 'search' }],
  CodeField: () => [{ name: 'rules', value: '{\n  "words": ["secret"]\n}', language: 'json', rows: 5 }],
  Switch: () => [{ name: 'enabled', checked: true, label: 'Enabled' }],
  Checkbox: () => [{ name: 'agree', checked: true, label: 'Mask instead of reject' }],
  RadioGroup: () => [{ name: 'provider', value: 'openai', options: opts }],
  RadioCards: () => [{ name: 'plan', value: 'anthropic', columns: 3, options: opts.map((o) => ({ ...o, description: `${o.label} models` })) }],
  Segmented: () => [{ name: 'range', value: 'week', options: [{ value: 'day', label: 'Day' }, { value: 'week', label: 'Week' }, { value: 'month', label: 'Month' }] }],
  Select: () => [{ name: 'provider', value: 'anthropic', options: opts }],
  Combobox: () => [{ name: 'model', value: 'gpt-5', options: [{ value: 'gpt-5', label: 'gpt-5' }, { value: 'claude-sonnet-4', label: 'claude-sonnet-4' }], allowCustom: true }],
  MultiSelect: () => [{ name: 'protocols', value: ['anthropic', 'gemini'], options: opts }],
  ChipGroup: () => [{ name: 'tags', value: ['openai'], options: opts, multiple: true }],
  TagInput: () => [{ name: 'words', value: ['secret', 'password'], suggestions: ['token', 'api key'] }],
  Slider: () => [{ name: 'temperature', value: 0.7, min: 0, max: 2, step: 0.1, marks: [0, 1, 2] }],
  DatePicker: () => [{ name: 'day', value: '2026-11-11' }],
  DateRangePicker: () => [{ name: 'range', value: { from: '2026-11-01', to: '2026-11-11' }, presets: ['today', 'last7', 'last30'] }],
  TimePicker: () => [{ name: 'at', value: '09:30', minuteStep: 15 }],
  KeyValueEditor: () => [{ name: 'headers', value: [{ key: 'x-team', value: 'platform' }, { key: 'x-env', value: 'prod' }], keyPlaceholder: 'Header', valuePlaceholder: 'Value' }],
  ListEditor: () => [{ name: 'origins', value: [{ origin: 'https://a.example', note: 'staging' }, { origin: 'https://b.example', note: '' }], item: [{ name: 'origin', placeholder: 'https://…' }, { name: 'note', label: 'Note' }], addLabel: 'Add origin', reorderable: true }],
  FileDrop: () => [{ name: 'file', accept: '.json', label: 'Drop a rules file', onDrop: 'drop' }],
  Form: () => [{ onSubmit: 'save', dirtyBar: true }, [n('Field', { label: 'Endpoint', hint: 'An https address', required: true }, n('TextField', { name: 'endpoint', value: 'https://api.acme.example' })), n('Button', { label: 'Save', variant: 'primary', submit: true })]],
  Field: () => [{ label: 'API key', hint: 'Stored encrypted', required: true }, n('PasswordField', { name: 'key' })],
  FieldGroup: () => [{ title: 'Limits', columns: 2 }, [n('Field', { label: 'Per minute' }, n('NumberField', { name: 'rpm', value: 60 })), n('Field', { label: 'Per day' }, n('NumberField', { name: 'rpd', value: 5000 }))]],
  Table: () => [{ columns, rows, rowKey: 'id', total: 3, page: 1, pageSize: 20, sort: { key: 'tokens', dir: 'desc' }, onSort: 'sort', onPage: 'page', onRowPress: 'open' }],
  List: () => [{ items: [{ key: 'a', title: 'secret', detail: '12 hits', icon: 'shield' }, { key: 'b', title: 'password', detail: '3 hits', icon: 'shield' }, { key: 'c', title: 'api key', detail: 'no hits', icon: 'shield' }], onItemPress: 'open' }],
  Tree: () => [{ nodes: tree, expanded: ['src'], selected: ['src/main.js'], selection: 'single', onSelect: 'select' }],
  Pagination: () => [{ page: 2, pageSize: 20, total: 135, onPage: 'page' }],
  LogView: () => [{ lines: [{ at: '2026-11-11T09:00:01Z', level: 'info', text: 'activated startup' }, { at: '2026-11-11T09:00:02Z', level: 'warn', text: 'words list is empty' }, { at: '2026-11-11T09:00:03Z', level: 'error', text: 'fetch failed: 503' }], height: 'xs' }],
  MetricCard: () => [{ label: 'Requests today', value: 1204, format: 'number', delta: 0.12, trend: series.map((s) => s.requests), icon: 'activity' }],
  Sparkline: () => [{ data: series.map((s) => s.requests), tone: 'accent', area: true }],
  Chart: () => [{ kind: 'line', data: series, x: { key: 'date', type: 'time' }, series: [{ key: 'requests', label: 'Requests' }, { key: 'errors', label: 'Errors', tone: 'danger', axis: 'right' }], yRight: { format: 'number' }, curve: 'smooth', height: 'md' }],
  Heatmap: () => [{ days: Array.from({ length: 35 }, (_, i) => ({ date: `2026-10-${String(1 + (i % 31)).padStart(2, '0')}`, value: (i * 37) % 11 })), tone: 'accent' }],
  Gauge: () => [{ value: 72, max: 100, label: 'Budget used', thresholds: [{ at: 60, tone: 'warning' }, { at: 90, tone: 'danger' }] }],
  Canvas: () => [{ height: 'sm', viewBox: [0, 0, 200, 100], draw: [{ rect: [10, 10, 80, 50], fill: 'accent' }, { circle: [150, 50, 30], fill: 'success' }, { line: [10, 90, 190, 90], stroke: 'border' }, { text: 'Canvas', at: [100, 80], anchor: 'middle' }] }],
  Sheet: () => [{ open: false, title: 'Edit rule', size: 'md' }, text('Sheet body')],
  Dialog: () => [{ open: false, title: 'Confirm' }, text('Dialog body')],
  Popover: () => [{ trigger: n('Button', { label: 'Details' }) }, text('Popover body')],
  ModelPicker: () => [{ name: 'model', value: 'claude-sonnet-4' }],
  ProviderPicker: () => [{ name: 'provider' }],
  AccountPicker: () => [{ name: 'account' }],
  ClientPicker: () => [{ name: 'client', value: 'claude-code' }],
  TokenPicker: () => [{ name: 'token' }],
  ProtocolPicker: () => [{ name: 'protocol', value: 'anthropic' }],
  ModelLabel: () => [{ id: 'claude-sonnet-4' }],
  ProviderLabel: () => [{ id: 'anthropic' }],
  ClientLabel: () => [{ id: 'claude-code' }],
  Money: () => [{ value: 12345600000 }], // nanodollars, like every money value
  RequestLink: () => [{ id: 'req_123', label: 'Open the request' }],
  TraceLink: () => [{ id: 'trace_123', label: 'Open the trace' }],
  UsageBar: () => [{ used: 7300, limit: 10000, label: 'Tokens this month', format: 'tokens' }],
};

// states beyond the default: every value of variant / tone / size, and a few flags
const STATE_PROPS = { variant: 'enum', tone: 'token:tone', size: 'token:size' };
const FLAG_STATES = { loading: { loading: true }, invalid: { invalid: true }, disabled: { disabled: true } };

function enumValues(type) {
  if (type.startsWith('enum:')) return type.slice(5).split('|');
  if (type === 'token:tone') return schema.tokens?.tone ?? ['neutral', 'accent', 'success', 'warning', 'danger'];
  if (type === 'token:size') return schema.tokens?.size ?? ['sm', 'md', 'lg'];
  return null;
}

function scenesOf(name, def) {
  const make = SAMPLE[name];
  if (!make) return null;
  const [props, children] = make();
  const list = [{ state: 'default', tree: n(name, props, children) }];
  const allProps = { ...(def.props ?? {}), ...(def.input ? schema.inputProps : {}) };
  for (const [prop, kind] of Object.entries(STATE_PROPS)) {
    const type = allProps[prop];
    if (!type || (kind === 'enum' ? !type.startsWith('enum:') : type !== kind)) continue;
    for (const v of enumValues(type) ?? []) if (v !== props[prop]) list.push({ state: `${prop}-${v}`, tree: n(name, { ...props, [prop]: v }, make()[1]) });
  }
  for (const [state, flag] of Object.entries(FLAG_STATES)) {
    const key = Object.keys(flag)[0];
    const boolean = allProps[key] === 'boolean' || (key === 'invalid' && 'name' in props && def.input);
    if (key === 'disabled' ? name in INTERACTIVE : boolean) list.push({ state, tree: n(name, { ...props, ...flag }, make()[1]) });
  }
  return list;
}

const INTERACTIVE = Object.fromEntries(['Button', 'IconButton', 'TextField', 'Switch', 'Checkbox', 'Select', 'Segmented', 'Slider', 'DatePicker'].map((k) => [k, true]));

// composite scenes: whole pages built from many components (06 §5 step 1)
const PATTERNS = {
  'settings-page': n('Page', { title: 'Settings' }, [
    n('Group', { title: 'General' }, [n('Row', { label: 'Enabled' }, n('Switch', { name: 'on', checked: true })), n('Row', { label: 'Mode', detail: 'What happens on a hit' }, n('Select', { name: 'mode', value: 'mask', options: [{ value: 'mask', label: 'Mask' }, { value: 'reject', label: 'Reject' }] }))]),
    n('Group', { title: 'Words' }, [n('TagInput', { name: 'words', value: ['secret', 'password'] })]),
  ]),
  'list-detail': n('Split', { ratio: 0.35, stackBelow: 'compact' }, [
    n('List', { items: [{ key: 'a', title: 'secret', detail: '12 hits' }, { key: 'b', title: 'password', detail: '3 hits' }], selected: ['a'], selectable: 'single' }),
    n('Stack', { gap: '3' }, [n('Heading', { value: 'secret', level: 3 }), n('KV', { items: [{ label: 'Hits', value: 12 }, { label: 'Last', value: '2026-11-11 09:30' }] })]),
  ]),
  dashboard: n('Stack', { gap: '4' }, [
    n('Grid', { columns: 3, gap: '3' }, [n('MetricCard', { label: 'Requests', value: 1204, delta: 0.12 }), n('MetricCard', { label: 'Hits', value: 37, tone: 'warning' }), n('MetricCard', { label: 'Cost', value: 4210000000, format: 'money' })]),
    n('Chart', { kind: 'area', data: series, x: { key: 'date', type: 'time' }, series: [{ key: 'requests', label: 'Requests' }], height: 'sm' }),
  ]),
  'form-validation': n('Form', { onSubmit: 'save' }, [
    n('Field', { label: 'Endpoint', error: 'Must be an https address', required: true }, n('TextField', { name: 'endpoint', value: 'http://insecure', invalid: true })),
    n('Field', { label: 'Limit', hint: '1 – 100' }, n('NumberField', { name: 'limit', value: 25 })),
    n('Button', { label: 'Save', variant: 'primary', submit: true }),
  ]),
  'empty-loading-error': n('Stack', { gap: '4' }, [
    n('EmptyState', { icon: 'inbox', title: 'Nothing yet' }),
    n('Skeleton', { shape: 'block', lines: 2 }),
    n('Alert', { tone: 'danger', title: 'Could not load', message: 'The plugin stopped: fetch failed (503).' }),
  ]),
};

const files = new Map();
const missing = [];
for (const [name, def] of Object.entries(schema.components)) {
  const scenes = scenesOf(name, def);
  if (!scenes) {
    missing.push(name);
    continue;
  }
  for (const s of scenes) files.set(`${name}/${s.state}.json`, { id: `${name}/${s.state}`, component: name, state: s.state, tree: s.tree });
}
for (const [id, tree] of Object.entries(PATTERNS)) files.set(`_patterns/${id}.json`, { id: `_patterns/${id}`, component: null, state: id, tree });
if (missing.length) {
  console.error(`no sample for: ${missing.join(', ')} — add them to SAMPLE in scripts/plugin-ui/gen-scenes.mjs`);
  process.exit(1);
}

const render = (v) => JSON.stringify(v, null, 2) + '\n';
// the same scenes in one file the server embeds (src/ can't reach into tests/ at build time): GET /api/plugins/v2/dev/scenes
const bundlePath = join(repo, 'src/Astra.Plugins/V2/Ui/scenes.json');
const bundle = JSON.stringify({ $comment: 'generated by scripts/plugin-ui/gen-scenes.mjs from tests/fixtures/plugin-ui — do not edit', scenes: [...files.values()] }) + '\n';
if (check) {
  const stale = [];
  for (const [rel, v] of files) {
    const p = join(out, rel);
    if (!existsSync(p) || readFileSync(p, 'utf8') !== render(v)) stale.push(rel);
  }
  const on = existsSync(out) ? readdirSync(out, { recursive: true }).filter((f) => String(f).endsWith('.json')).map(String) : [];
  for (const f of on) if (!files.has(f.split('\\').join('/'))) stale.push(`${f} (no longer generated)`);
  if (!existsSync(bundlePath) || readFileSync(bundlePath, 'utf8') !== bundle) stale.push('src/Astra.Plugins/V2/Ui/scenes.json');
  if (stale.length) {
    console.error(`tests/fixtures/plugin-ui is stale (${stale.length}): ${stale.slice(0, 8).join(', ')} — run node scripts/plugin-ui/gen-scenes.mjs`);
    process.exit(1);
  }
  console.log(`plugin-ui scenes are up to date (${files.size}).`);
} else {
  rmSync(out, { recursive: true, force: true });
  for (const [rel, v] of files) {
    mkdirSync(dirname(join(out, rel)), { recursive: true });
    writeFileSync(join(out, rel), render(v));
  }
  writeFileSync(bundlePath, bundle);
  console.log(`wrote ${files.size} scenes to tests/fixtures/plugin-ui/ and src/Astra.Plugins/V2/Ui/scenes.json`);
}
