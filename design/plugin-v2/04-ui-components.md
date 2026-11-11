# 04 UI 组件参考（UI 1.0 完整词汇）

本文列出 UI 1.0 的全部组件：属性、事件、子节点，以及它在两端映射到什么。协议与状态模型见 [03](03-ui-api.md)，
布局、导航、表单、数据等系统性规则见 [05](05-ui-systems.md)。

## 0. 约定

- **通用属性**（所有组件都有，下文不再重复）：
  `key`、`visible`、`disabled`（布尔或本地条件，03 §6）、`tooltip`、`testId`、`a11yLabel`、`fallback`（降级子树，03 §5.3）、
  布局属性 `width` / `grow` / `alignSelf` / `minWidth` / `maxWidth`（05 §1）。
- **事件属性**的值是动作引用 `{ action, args? }` 或直接写动作名。事件载荷固定：

  | 事件 | 载荷 |
  |---|---|
  | `onPress` | `{}` |
  | `onChange` | `{ value }` |
  | `onSubmit` | `{ values }`（表单内全部具名字段） |
  | `onSelect` | `{ value }` 或 `{ keys }`（多选） |
  | `onSort` / `onPage` / `onSearch` / `onFilter` | `{ sort }` / `{ page, pageSize }` / `{ query }` / `{ filters }` |
  | `onReorder` | `{ keys }`（新顺序） |
  | `onOpenChange` | `{ open }` |
  | `onDismiss` | `{}` |
  | `onVisible` | `{}`（首次进入视口，用于懒加载 / 无限滚动） |
  | `onDrop` | `{ files: [{ name, size, type, ref }] }` |

  所有事件另带 `local`（本地状态袋）和触发节点的 `key`。
- **「Web」「桌面」列**是映射目标：Web 指 `web/src/components/arc/<目录>` 或 `components/ui/*`；桌面指 TKCode.Arc 的控件或
  `src/Astra.Desktop/Components/Kit/*`。标 **新建** 的是该端现在没有、需要为插件 UI 补上的（见 §12 的缺口汇总）。

## 1. 页面与布局

| 组件 | 主要属性 | 子节点 | Web | 桌面 |
|---|---|---|---|---|
| `Page` | `title` `subtitle` `icon` `actions`（节点列表） `tabs` `back`（05 §3） `width: narrow｜default｜wide｜full` | 任意 | 宿主页面骨架 | 同左（页面骨架） |
| `Stack` | `direction: column｜row` `gap` `align` `justify` `wrap` `padding` | 任意 | flex | `StackPanel` / 自定义面板 |
| `Grid` | `columns`（数字或 `{ min: 档位 }` 自适应） `gap` `rowGap` | 任意；子节点可带 `span` | CSS grid | `TilePanel` / `Grid` |
| `Split` | `ratio` `resizable` `collapse: start｜end｜none` `stackBelow`（窄宽度时改为上下） | 恰好 2 个 | **新建** | `ResizablePanels` |
| `Scroll` | `direction` `maxHeight` | 1 个 | overflow 容器 | `ScrollArea` |
| `Box` | `surface: none｜card｜sunken｜accent` `padding` `border` `radius: none｜md｜lg` | 任意 | div + 令牌 | `Border` |
| `Divider` | `direction` `label` | — | 已有样式 | `Separator` |
| `Spacer` | `size` 或 `grow` | — | — | — |
| `Sticky` | `edge: top｜bottom` | 1 个；用于吸顶工具栏、底部「待保存」条 | **新建** | **新建** |

## 2. 容器与结构

| 组件 | 主要属性 / 事件 | 子节点 | Web | 桌面 |
|---|---|---|---|---|
| `Group` | `title` `description` `footer` `actions` `dense` | `Row` 或任意 | `ui/controls` `Group` | Kit `SettingsGroup` |
| `Row` | `label` `detail` `hint` `icon` `selected`；`onPress` | 尾部控件 | `ui/controls` `Row` | Kit `SettingsRow` |
| `Card` | `title` `subtitle` `icon` `actions` `tone` `footer`；`onPress` | 任意 | `card` | `Card` |
| `Section` | `title` `description` `actions` `collapsible` `defaultOpen` | 任意 | `SectionTitle` / `DetailSection` | Kit `Sections` |
| `Tabs` | `value` `items: [{ value, label, icon, badge }]` `variant: line｜pill`；`onChange` | 每个 item 一个 `Tab` 子节点（`lazy` 可选） | `tabs` | `Tabs` |
| `Accordion` | `mode: single｜multiple` `items` | `AccordionItem(title, …)` | `accordion` | `Accordion` |
| `Disclosure` | `title` `defaultOpen` | 任意 | `ui/controls` `Disclosure` | `ExpandableCard` / Kit |
| `Stepper` | `steps: [{ title, description, status }]` `current` `direction` | — | `stepper` | `Stepper` |
| `Timeline` | `items: [{ time, title, detail, tone, icon }]` | 或 `TimelineItem` 子节点 | **新建** | `Timeline` |
| `Breadcrumb` | `items: [{ label, action }]` | — | **新建** | `Breadcrumb` |
| `Toolbar` | `start` `end`（节点列表） | — | `filter-toolbar` | `FilterToolbar` |
| `Boundary` | `loading`（节点） `error`（节点） | 1 个；子树出错或加载中时显示替代内容（05 §6） | **新建** | **新建** |

## 3. 文本与展示

| 组件 | 主要属性 | Web | 桌面 |
|---|---|---|---|
| `Text` | `value` `variant: body｜caption｜label｜mono` `tone` `weight` `truncate`（行数） `selectable` `format`（05 §9） | 文本样式 | `TextBlock` |
| `Heading` | `value` `level: 1–4` | 同上 | 同上 |
| `Markdown` | `value`（固定子集，宿主解析成普通节点，见 §7） | 无需组件（宿主解析） | 无需组件（宿主解析） |
| `RichText` | `spans: [{ text, bold, italic, strike, code, link }]`（一段带行内样式的文本） | **新建** | **新建** |
| `Code` | `value` `language` `filename` `maxHeight` `copy` `wrap` | `code-block` | `CodeBlock` |
| `JsonView` | `value`（任意 JSON） `expandDepth` `copy` | **新建** | `JsonViewer` |
| `DiffView` | `before` `after` `language` `mode: unified｜split` | 宿主已有的 diff 展示 | 同左 |
| `Link` | `label` `href`（https）或 `action` | a | 链接按钮 |
| `Icon` | `name`（Lucide）或 `brand`（宿主品牌图标集）或 `src` `size` `tone` | `lucide-react` / `icons.tsx` | `Arc.Icons.Lucide` / `Kit/Brands` |
| `Image` | `src`（插件资源 / data；可给 `{ light, dark }`） `alt` `fit` `ratio` `radius` | img | `Image` |
| `Avatar` | `name` `src` `icon` `size` `status` | **新建** | `Avatar` |
| `Badge` | `label` `tone` `icon` `size` | `badge` | `Badge` |
| `StatusDot` | `tone` `label` `pulse` | `ui/controls` `Dot` | Kit `StatusDot` |
| `Kbd` | `keys` | 已有样式 | Kit `Kbd` |
| `KV` | `items: [{ label, value, mono, copy }]` `columns` | `ui/controls` `KV` | Kit |
| `Alert` | `tone` `title` `message` `actions` `dismissible`；`onDismiss` | `alert` | `Alert` |
| `EmptyState` | `icon` `title` `detail` `action` | `empty-state` | `EmptyState` |
| `Skeleton` | `shape: text｜block｜circle` `lines` | `skeleton` | `Skeleton` |
| `Spinner` | `size` `label` | `ui/controls` `Spinner` | Kit `States` |
| `Progress` | `value`（0–1；省略 = 不确定） `label` `tone` `variant: bar｜ring` | **新建** | `Progress` |
| `Meter` | `value` `max` `segments` `label`（配额 / 用量条） | 宿主已有的配额条 | `UsageMeter` |
| `Counter` | `value` `format`（数值变化时滚动） | `animated-counter` | `AnimatedCounter` |
| `QRCode` | `value` `size`（设备码登录、加入链接） | **新建** | **新建** |

## 4. 操作

| 组件 | 主要属性 / 事件 | Web | 桌面 |
|---|---|---|---|
| `Button` | `label` `icon` `variant: primary｜secondary｜ghost｜danger｜link` `size` `loading` `submit`（提交所在 `Form`）；`onPress` | `button` | `Button` / `ActionButton` |
| `IconButton` | `icon` `a11yLabel`（必填）其余同 `Button` | `button` | `Button` |
| `ButtonGroup` | — ；子节点为 `Button` | `ui/controls` `ButtonGroup` | `ButtonGroup` |
| `SplitButton` | `label` `items`（同 `Menu`）；`onPress` | **新建**（button + dropdown-menu） | `SplitButton` |
| `CopyButton` | `value` `label` | `copy-button` | `CopyButton` |
| `ConfirmButton` | `label` `confirmLabel` `tone` `mode: morph｜hold`（就地二次确认，不弹框）；`onPress` | `confirm-morph` | `ConfirmMorph` / `HoldToConfirm` |
| `Menu` | `trigger`（节点） `items: [{ label, icon, tone, action, disabled, shortcut } ｜ { separator } ｜ { label, items }]` | `dropdown-menu` | `DropdownMenu` |
| `ContextMenu` | `items`；包住 1 个子节点，右键 / 长按打开 | **新建** | `ContextMenu` |

## 5. 输入

所有输入组件共有：`name`（进本地状态袋）、`value`、`placeholder`、`required`、`readOnly`、`invalid`、`onChange`、
`debounce`、`commit: change｜blur`（03 §6）。

| 组件 | 额外属性 | Web | 桌面 |
|---|---|---|---|
| `TextField` | `type: text｜url｜email` `prefix` `suffix` `maxLength` `pattern` `clearable` | `input` | `Input` |
| `PasswordField` | `reveal` | `password-field` | `PasswordField` |
| `TextArea` | `rows` `autoGrow` `maxLength` `mono` | `textarea` | `Textarea` |
| `NumberField` | `min` `max` `step` `unit` `integer` | `number-field` | `NumberField` |
| `MoneyField` | 值是纳美元整数；`currency` 显示 | 宿主的定价输入 | `MoneyInput` |
| `SearchField` | `onSearch`（回车或防抖） | `search-field` | `SearchField` |
| `CodeField` | `language: json｜yaml｜toml｜text` `rows` `validate`（JSON 语法即时校验） | 等宽 textarea + 校验 | 同左 |
| `Switch` | `checked` `label` | `switch` | `Switch` |
| `Checkbox` | `checked` `label` `indeterminate` | `checkbox` | `Checkbox` |
| `RadioGroup` | `options` `direction` | **新建** | `RadioGroup` |
| `RadioCards` | `options: [{ value, title, description, icon }]` `columns` | `radio-cards` | `RadioCards` |
| `Segmented` | `options` | `segmented-control` | `SegmentedControl` |
| `Select` | `options: [{ value, label, icon, description, group, disabled }]` `clearable` | `select` | `Select` |
| `Combobox` | `options` `allowCustom`；`onSearch`（远程选项） | `combobox` | `Combobox` |
| `MultiSelect` | `options` `max` | `multi-select` | `MultiSelect` |
| `ChipGroup` | `options` `multiple` | `chip-group` | `ChipGroup` |
| `TagInput` | `value: string[]` `suggestions` `max` `validate`（pattern） | **新建** | `TagInput` |
| `Slider` | `min` `max` `step` `marks` `range` | **新建** | `Slider` |
| `DatePicker` | `min` `max` `withTime` | **新建** | `DatePicker` / `TimePicker` |
| `DateRangePicker` | `presets: [{ label, range }]` | **新建** | `DateRangePicker` |
| `KeyValueEditor` | `value: [{ key, value }]` `keyPlaceholder` `valuePlaceholder` `secretValues`（头、环境变量） | **新建**（宿主 MCP 页有同类编辑器可抽取） | **新建**（同左） |
| `ListEditor` | `value: T[]` `item`（一行的字段模板） `addLabel` `reorderable` `max` | **新建** | `ReorderableList` + 模板 |
| `FileDrop` | `accept` `multiple` `maxSize`；`onDrop`（内容经宿主暂存，插件拿到 `ref` 后用 `ui.files.read(ref)` 读，拿不到路径） | **新建** | `FileDropzone` |

`Form` 与 `Field`：

| 组件 | 属性 / 事件 | Web | 桌面 |
|---|---|---|---|
| `Form` | `onSubmit` `dirtyBar`（有未保存修改时显示底部条：保存 / 放弃） `layout: vertical｜horizontal` | form | Kit |
| `Field` | `label` `hint` `error` `required` `info`（ⓘ 提示） | `ui/controls` `Field` | Kit `FormField` |
| `FieldGroup` | `title` `columns` | — | — |

## 6. 数据

| 组件 | 主要属性 / 事件 | Web | 桌面 |
|---|---|---|---|
| `Table` | 见下 | `sortable-data-table` + `pagination` | `Table` |
| `List` | `items` 或 `source`；`item`（行模板：`title` `detail` `icon` `trailing` `actions`） `selectable` `reorderable`；`onSelect` `onReorder` `onVisible` | 列表 + `@dnd-kit` | `ReorderableList` |
| `Tree` | `nodes: [{ key, label, icon, badge, children, lazy }]` `expanded` `selected`；`onSelect` `onExpand`（懒加载子节点） | **新建** | `TreeView` |
| `Pagination` | `page` `pageSize` `total`；`onPage` | `pagination` | `Pagination` |
| `LogView` | `source`（追加型数据通道，05 §5） `follow` `wrap` `filter` `levels` | **新建** | **新建**（宿主日志面板可抽取） |
| `MetricCard` | `label` `value` `format` `delta` `trend`（sparkline 数据） `tone` `icon` | `metric-card` | `MetricCard` |
| `Sparkline` | `data` `tone` `area` | `sparkline` | `Sparkline` |
| `Chart` | 统一图表规格，见下 | `bar-chart` + **新建**（折线 / 面积 / 环形） | `BarChart` `LineChart` `DonutChart` |
| `Heatmap` | `days: [{ date, value }]` `tone` | `activity-heatmap` | `ActivityHeatmap` |
| `Gauge` | `value` `max` `label` `thresholds` | **新建** | `Gauge` |

### `Table`

```js
Table({
  columns: [
    { key: 'time',  title: '时间', format: 'datetime', sortable: true, width: 160 },
    { key: 'model', title: '模型', mono: true, truncate: true },
    { key: 'state', title: '状态', cell: { type: 'badge', toneBy: { ok: 'success', failed: 'danger' } } },
    { key: 'cost',  title: '费用', format: 'money', align: 'end', sortable: true },
  ],
  rows, rowKey: 'id',                       // 或 source: 数据通道
  total, page, pageSize, sort,              // 给了 total 即服务端分页
  selection: 'none' | 'single' | 'multiple', selected,
  rowActions: [{ label, icon, tone, action }],
  bulkActions: [{ label, action }],         // 多选时出现
  expand: (row) => 节点,                     // 展开行；SDK 把函数预先求值为每行的子树，或标记为按需
  empty: 节点, loading: bool, dense: bool, stickyHeader: bool,
  onSort, onPage, onSelect, onRowPress,
})
```

单元格不是任意节点，而是有限的几种 `cell` 类型：`text`（默认）、`badge`、`status`、`icon`、`link`、`progress`、`copy`、
`avatar`、`tags`、`actions`。原因是表格是最容易在两端走样、也最吃性能的组件；限制单元格类型换来虚拟滚动和一致的行高。
需要完全自由的行内容时用 `List`。

### `Chart`

```js
Chart({
  kind: 'line' | 'area' | 'bar' | 'stackedBar' | 'donut',
  data: [{ t: '2026-11-01', in: 1200, out: 800 }, …],
  x: { key: 't', format: 'date' },
  series: [{ key: 'in', label: '输入', tone: 'accent' }, { key: 'out', label: '输出', color: 2 }],
  y: { format: 'number', min: 0 }, legend: 'bottom', height: 240, stacked: false,
  onSelect,                                 // 点击数据点 / 扇区 → { datum, series }
})
```

系列颜色用 `tone` 或调色板序号（`color: 0–7`），调色板由壳提供，明暗主题各一套。更特殊的图用 `Canvas`（03 §11）。

## 7. 必须项的详细规格：树、日期、折线图、Markdown

树、日期、折线图、Markdown 是 UI 1.0 的**必须项**（用户决定，2026-11-11）。它们恰好都是 Web 侧现在没有的组件，
所以在 Web 的补齐顺序里排最前。

### `Tree`

```js
Tree({
  nodes: [
    { key: 'p1', label: 'OpenAI', icon: { brand: 'openai' }, badge: '12', children: [
      { key: 'p1/gpt-5', label: 'gpt-5', detail: '400k', trailing: Badge({ label: '默认' }) },
    ]},
    { key: 'p2', label: 'Anthropic', lazy: true },          // 展开时才取子节点
  ],
  expanded: s.expanded, selected: s.selected,               // 受控；省略则由壳本地持有
  selection: 'none' | 'single' | 'multiple' | 'checkbox',   // checkbox：父子联动的三态勾选
  nodeActions: [{ label: '重命名', action: 'rename' }],      // 行尾菜单 + 右键菜单
  reorderable: false, search: true, indent: 'md', dense: true,
  onSelect, onExpand, onReorder, onNodePress,
})
```

- **懒加载**：`lazy: true` 的节点第一次展开时发 `onExpand { key, expanded: true }`，壳在该节点下显示加载行；
  处理函数把子节点补进状态（或用数据通道 `tree.setChildren(key, nodes)`，大树推荐后者）。
- **展开 / 折叠是纯本地操作**（已加载的节点不往返）。`expanded` 受控时仍发 `onExpand` 让插件记录。
- **搜索**：`search: true` 在已加载的节点里本地过滤并高亮；要搜未加载的部分，用 `onSearch` 由插件返回结果树。
- **节点内容有限**：`label` `detail` `icon` `badge` `trailing`（一个小节点：`Badge` / `StatusDot` / `Switch` / `IconButton`）。
  和 `Table` 的单元格同理，换来虚拟滚动和两端一致的行高。
- 键盘：上下移动、左右折叠展开、空格勾选、回车触发 `onNodePress`、输入字母跳转（壳实现）。
- 拖拽排序（`reorderable`）：同级内排序和跨父节点移动，`onReorder { key, parent, index }`。
- 需要多列的树用 `Table` 的 `tree: { childrenKey }` 模式（桌面 `TreeTable`；Web 在数据表格上扩展）。
- 映射：Web 新建 `arc/tree`；桌面 TKCode.Arc `TreeView`。

### `DatePicker` / `DateRangePicker` / `TimePicker`

```js
DatePicker({ name: 'day', value: '2026-11-11', min: '2026-01-01', max: 'today', clearable: true })
DatePicker({ name: 'at', value: '2026-11-11T09:30:00Z', withTime: true, minuteStep: 5 })
DateRangePicker({ name: 'range', value: { from: '2026-11-01', to: '2026-11-11' },
                  presets: ['today', '7d', '30d', 'thisMonth', { label: '本季度', from: '2026-10-01', to: '2026-12-31' }],
                  maxSpanDays: 90 })
TimePicker({ name: 't', value: '09:30', minuteStep: 15 })
```

值的规则（两端最容易不一致的地方，所以定死）：

| 组件 | 值的形状 | 含义 |
|---|---|---|
| `DatePicker` | `YYYY-MM-DD` | 日历日期，不带时区 |
| `DatePicker` + `withTime` | ISO 8601 UTC（`…Z`） | 一个时刻；壳按用户本地时区显示和编辑 |
| `DateRangePicker` | `{ from, to }`，两端都是 `YYYY-MM-DD`，**含首尾** | 日历日期范围 |
| `TimePicker` | `HH:mm` | 一天内的时间，不带时区 |

- 把日期范围换成查询用的时刻区间是常见需求，由 SDK 提供：`ui.dates.toUtcRange({ from, to }, ctx.client.timeZone)`
  → `{ fromUtc, toUtc }`（左闭右开）。`ctx.client` 因此增加 `timeZone`（IANA 名）和 `weekStart`。
- `min` / `max` 可以写 `today`、`-30d`、`+1y` 这样的相对值，由壳按本地日期解析。
- 预设（`presets`）的内置名字由壳翻译并按本地日期计算；自定义预设给出标签和具体日期。
- 周起始日、月份和星期的名称、日期的显示格式跟随会话语言；输入框允许直接键入，无法解析时标为无效。
- 映射：Web 新建 `arc/date-picker`（日历面板 + 输入框，范围选择共用日历）；桌面 TKCode.Arc
  `DatePicker` / `DateRangePicker` / `TimePicker` / `Calendar`。

### 折线图（`Chart` 的 `line` / `area`）

```js
Chart({
  kind: 'line',                                   // 'area' 同一套属性，多一个 stacked
  data: s.points,                                 // 或 source: 数据通道（实时追加）
  x: { key: 't', type: 'time' | 'category' | 'number', format: 'datetime' },
  series: [
    { key: 'p50', label: 'P50', tone: 'accent' },
    { key: 'p95', label: 'P95', color: 2, dashed: true },
    { key: 'err', label: '错误率', color: 4, axis: 'right' },
  ],
  y:      { format: 'duration', min: 0, max: 'auto', ticks: 4 },
  yRight: { format: 'percent' },                  // 可选的第二个纵轴
  curve: 'linear' | 'smooth' | 'step',
  points: 'auto' | 'always' | 'never',            // 数据点标记；auto = 点少时显示
  gaps: 'break' | 'connect' | 'zero',             // 缺失值（null）的画法
  marks: [{ y: 1000, label: '预算', tone: 'warning' }, { x: '2026-11-10T00:00:00Z', label: '发布' }],
  legend: 'bottom' | 'top' | 'none', height: 'md',
  zoom: false,                                    // true = 可框选横轴范围 → onRange
  empty: 节点, loading: false,
  onSelect, onRange,
})
```

- **悬停提示是纯本地的**：壳显示十字线和该横坐标下各系列的值（按各自的 `format`），不往返。
- **图例点击**在本地显示 / 隐藏系列。
- **数据量**：壳在像素宽度不足时做降采样（保留极值）；单个图的数据点有上限（建议每系列 5000），超出由插件先聚合。
- **实时**：绑定只追加的数据通道，配 `window: 500`（只保留最近 N 个点）即为滚动的实时曲线。
- **颜色**来自调色板（05 §2），明暗主题各一套；系列顺序即图例顺序。
- **无障碍**：图表带一个由壳生成的文字摘要（系列名、范围、极值），并可切换为数据表格视图。
- 映射：Web 新建 `arc/line-chart`，和现有 `bar-chart`、`sparkline` 一样手写 SVG，不引入图表库（保持包体积，也便于和桌面对齐
  坐标轴刻度算法）；桌面 TKCode.Arc `LineChart`。**刻度取值算法**（「好看的」刻度、时间轴的步长）写进行为用例，两端共用，
  否则同一份数据两端的坐标轴会不一样。

### `Markdown`

```js
Markdown({ value: text, size: 'md', compact: false, onLink: 'openDoc' })
```

支持的语法是固定的子集，两端完全一致：

| 支持 | 不支持 |
|---|---|
| 标题（1–4 级）、段落、换行 | 原始 HTML（按纯文本显示） |
| 粗体、斜体、删除线、行内代码 | 远程图片（只允许插件资源路径和 `data:`） |
| 有序 / 无序 / 任务列表（只读勾选框），可嵌套 | 脚注、定义列表、数学公式 |
| 链接（https、`astra://`、插件内路由） | `javascript:` 等其他协议（去掉链接，保留文字） |
| 引用块、分隔线 | 嵌入内容（iframe、视频） |
| 围栏代码块（带语言，渲染为 `Code`，可复制） | |
| 表格（GFM，含对齐） | |
| 提示块（`> [!NOTE]` / `TIP` / `WARNING` / `CAUTION`，渲染为 `Alert`） | |

**实现方式：在宿主解析，两端只渲染。** `Markdown` 节点在宿主规范化阶段被解析成普通的 UI 节点
（`Heading`、`Text`、`List`、`Code`、`Table`、`Alert`、`Link`……），再求差下发。这样：

- 语法子集只有一份实现（宿主的 C# 解析器），不存在「Web 的解析库和桌面的解析库对同一段文本理解不同」的问题；
- 壳不需要 Markdown 解析库，Web 不新增依赖；
- 安全规则（不渲染 HTML、链接协议白名单）在一处执行。

为此协议增加一个基础节点 **`RichText`**：一段带行内样式的文本，
`{ type: 'RichText', props: { spans: [{ text, bold?, italic?, strike?, code?, link? }] } }`。它也可以被插件直接使用
（一句话里部分加粗、带链接）。Web 渲染为行内元素，桌面渲染为带 `Inline` 的文本块。

- 流式内容（比如展示模型输出）：`value` 增长时宿主重新解析，求差后只下发变化的尾部块；未闭合的代码块按代码块显示。
- 长文档：解析结果计入树的节点上限；超长内容用 `maxBlocks` 截断并给出「展开全部」。
- 链接：`https` 经壳确认后外开；插件内路由直接导航；给了 `onLink` 时改为发事件。
- 待验证：解析器是自己写这个子集，还是用现成的库——取决于它在 Native AOT 下是否无警告（仓库对 IL 警告零容忍）。
- 映射：Web 新建 `RichText` 渲染（其余复用已有组件）；桌面同样只需 `RichText`，不依赖 TKCode.Arc 的 `Markdown` 控件。

## 8. 浮层与反馈

| 组件 / 效果 | 形式 | Web | 桌面 |
|---|---|---|---|
| `Sheet` | 效果 `ctx.openSheet(view, params, { title, size })` 或节点 `Sheet({ open, title, footer })` | `ui/overlays` `Sheet` | Kit `Sheet` |
| `Dialog` | 效果 `ctx.openDialog(view, …)` 或节点 | `dialog` | `Dialog` |
| `Popover` | 节点：`trigger` + 内容 | **新建** | `Popover` |
| `Tooltip` | 通用属性 `tooltip`，或节点（富内容） | `tooltip` | `Tooltip` |
| `confirm` | 效果，返回布尔：`{ title, message, confirmLabel, tone }` | `useFeedback` | Kit `ConfirmDialog` |
| `prompt` | 效果，返回字符串或 null：`{ title, label, value, secret }` | **新建**（dialog + input） | **新建** |
| `toast` | 效果：`{ message, tone, action? }` | `toast` | Kit `ToastPresenter` |
| `Banner` | 节点：页面顶部的常驻提示（`tone` `message` `actions`） | `alert` 变体 | `AnnouncementBar` / `Alert` |

浮层里的内容是另一个视图（有自己的状态和动作），关闭时可向打开者返回一个结果：
`const result = await ctx.openSheet('editRule', { id })`。

## 9. 领域组件（与 Astra 数据绑定）

这些组件由宿主实现，直接复用宿主页面里已有的选择器和展示，插件不需要自己拉数据、也不需要对应的 `data` 权限就能让用户
**选择**（插件只拿到用户选中的 id）。

| 组件 | 作用 |
|---|---|
| `ModelPicker` | 选模型（按 provider 分组、搜索）；`multiple` `providerId` |
| `ProviderPicker` / `AccountPicker` | 选服务商 / 订阅账号 |
| `ClientPicker` | 选本地 AI 客户端 |
| `TokenPicker` | 选令牌（只显示名称和掩码） |
| `ProtocolPicker` | 选协议（chat / responses / anthropic / gemini） |
| `ModelLabel` / `ProviderLabel` / `ClientLabel` | 按 id 显示名称 + 品牌图标 |
| `Money` | 纳美元整数 → 本地化金额 |
| `RequestLink` / `TraceLink` | 跳到宿主的请求详情 / 链路页 |
| `UsageBar` | 配额 / 用量条（复用订阅配额的展示） |

领域组件是一个开放的类别：宿主页面里稳定下来的展示片段都可以按 §11 的流程加进来。

## 10. 自定义绘制

`Canvas`：见 03 §11。Web 渲染为内联 SVG，桌面渲染为自绘控件。两端都是 **新建**。

## 11. 新增一个组件的流程

1. 模式文件加组件定义（属性、事件、子节点规则、所属 UI 版本）。
2. 两端渲染器各加一个映射；该端没有对应组件时先在该端的组件库里补。
3. 加一致性场景（`tests/fixtures/plugin-ui/<组件>/*.json`，覆盖主要状态）和行为用例。
4. 画廊页（06 §2）自动出现该组件。
5. UI 次版本号加一；SDK 发布新类型。

## 12. 缺口汇总

UI 1.0 的完整词汇需要在两端补的组件：

| 端 | 需要新建 |
|---|---|
| Web（`web/src/components/arc/`） | **优先（必须项）：`Tree`、`DatePicker`、`DateRangePicker`、`Chart` 的折线 / 面积、`RichText`**；其余：`Split`、`Sticky`、`Boundary`、`Timeline`、`Breadcrumb`、`JsonView`、`Avatar`、`Progress`、`QRCode`、`SplitButton`、`ContextMenu`、`RadioGroup`、`TagInput`、`Slider`、`TimePicker`、`KeyValueEditor`、`ListEditor`、`FileDrop`、`LogView`、`Chart` 的环形、`Gauge`、`Popover`、`prompt`、`Canvas` |
| 桌面（Kit） | `RichText`、`Sticky`、`Boundary`、`QRCode`、`KeyValueEditor`、`LogView`、`prompt`、`Canvas` |

Web 侧缺得多，是因为桌面的 TKCode.Arc 1.0 组件比 Web 的 `arc` 目录全（上表「Web」列按 `web/src/components/arc/` 的现状判断；
有些在页面里以一次性实现存在，需要抽成组件）。这部分工作量决定 UI 1.0 的交付节奏，见 README 的决定 D10。
