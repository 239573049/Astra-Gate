# 03 UI API：模型与协议

## 1. 要解决的问题

插件要能有自己的界面，并且同一份插件代码在 Web 控制台和 Avalonia 桌面端上看起来、用起来尽量一样。

两端的现状决定了方案：

- 两端都是 `astra-server` 管理 API 的客户端，互不引用；桌面端是 Native AOT，没有 JS 引擎，也没有 WebView。
- 两端已经共用一套设计语言：Web 是 `web/src/components/arc/*`（约 40 个组件）加 `components/ui/controls.tsx`，
  桌面是 TKCode.Arc 加 `src/Astra.Desktop/Components/Kit/*`。同一个页面两端各写一遍，并有视觉回归工具比对
  （`scripts/visual/`、`tests/Astra.Desktop.UiTests`）。

所以 UI API 不是「给插件一块画布」，而是**把这套已有的共同词汇公开成一个带版本的协议**。

## 2. 模型：服务端驱动的声明式 UI

```
插件 (main 沙箱)                宿主                              壳 (Web / Avalonia)
────────────────               ────────────────                  ─────────────────────
render(state) ──► UI 树 ──►  规范化 · 校验 · 求差  ── 补丁(SSE) ──►  渲染器：节点 → Arc 组件
                                                                        │ 用户操作
handler(args) ◄── 分发 ◄────────────────────────── 事件(POST) ◄─────────┘
   │ 改状态 → 重新 render
   └─ 效果（toast、打开面板、跳转…）──────────── 效果(SSE) ──►  壳执行
```

- 插件产出的是**数据**（一棵 JSON 树），不是 HTML、不是控件对象。
- 宿主负责校验、和上一棵树求差、把补丁推给壳。
- 壳的渲染器是一张「节点类型 → 本端组件」的表。

选择这个模型（决定 D1）的理由：

| | 服务端驱动（建议） | 两端各自执行插件代码 |
|---|---|---|
| 一致性 | 同一棵树，差异只在渲染器 | 两个 JS 环境、两套 DOM / 控件 API，插件作者要适配 |
| 桌面 AOT | 不需要 JS 引擎 | 桌面要再嵌一个引擎，并把 Avalonia 暴露给 JS |
| 安全 | 壳不执行插件代码；树是数据，无注入面 | 浏览器里要沙箱 iframe，桌面要另做一套 |
| 能力访问 | UI 代码和业务代码在同一个上下文 | UI 在客户端，能力在服务端，中间要再设计一层 RPC |
| 代价 | 每次交互一次往返（本机通常是毫秒级） | 无往返 |

往返代价用 §6 的本地状态规则消掉最敏感的部分（输入框打字不往返）。远程管理下的延迟列在待验证项里。

## 3. 协议

协议是 UI API 真正的契约；§4 的 JS 写法只是它上面的一层糖。

### 3.1 节点

```jsonc
{
  "type": "Button",            // 组件类型，见 §5
  "key": "save",               // 可选；同一父节点下稳定标识，用于求差和保留本地状态
  "props": { "label": "保存", "variant": "primary", "disabled": false,
             "onPress": { "action": "save", "args": { "id": 3 } } },
  "children": [ /* 节点 */ ]
}
```

- `props` 的值只能是 JSON。**没有 style、没有 className、没有像素和颜色**，只有语义属性和设计令牌（§5.1）。
- 事件属性的值是**动作引用** `{ action, args? }`：一个名字加 JSON 参数。名字指向视图里登记的处理函数。
  用名字而不是函数 id 的原因：可序列化、重渲染后仍然稳定、可以在测试里直接断言。
- 文本属性可以是字符串（插件已按会话语言解析好）。

### 3.2 会话

壳每打开一个插件视图，就建立一个**视图会话**：

| 步骤 | 接口 | 说明 |
|---|---|---|
| 打开 | `POST /api/plugins/v2/ui/sessions` `{ plugin, view, params, client }` | `client` = `{ ui: "1.0", components: […], locale, theme, shell: "web"｜"desktop" }`；返回会话 id 和首棵树 |
| 订阅 | `GET  /api/plugins/v2/ui/sessions/{id}/stream`（SSE） | 推送 `patch`、`data`（数据通道，05 §5）、`effect`、`reset`、`error` |
| 事件 | `POST /api/plugins/v2/ui/sessions/{id}/events` `{ action, args, local, seq }` | `local` 是本地状态袋（§6） |
| 关闭 | `DELETE …/sessions/{id}`，或流断开后超时回收 | 调用视图的 `dispose` |

- 每个会话有自己的状态。Web 和桌面同时打开同一个页面是两个会话；它们要同步的数据放在 `astra.storage`，
  通过 `astra.events` 通知对方重新渲染。
- **视图状态由宿主持有。** 状态必须是 JSON。每次 `init` / 动作结束后，宿主取回状态的快照并保存在会话上；
  下一次调用时再交给引擎。也就是说引擎在两次事件之间不持有任何会话数据。由此得到：
  - 引擎被替换（触限、退役，01 §3）不丢界面状态：新引擎拿着快照直接 `render`，壳甚至不需要知道；
  - 空闲的插件可以卸载引擎，打开着的页面不受影响，下一次事件再加载；
  - 动作抛错时回到动作开始前的快照，不会留下改了一半的状态；
  - 开发模式的热重载保留状态（06 §2）。
  代价是每次事件一次状态的序列化，所以状态有大小上限（建议 256 KB）；大数据走数据通道（05 §5），不放进状态。
- **断线重连**：壳带上已应用的 `rev` 重新订阅；宿主保留最近若干个补丁，能续上就补发，续不上就推送 `reset`（全量树）。
  本地状态袋由壳保留，所以输入不丢。
- 渲染出错或超时：宿主推送 `error`，壳显示统一的错误占位和「重试」，不影响页面其余部分（05 §6）。
- **同一会话的事件串行处理**；不同会话的处理函数可以在等待宿主调用时交错执行，它们各有各的状态，互不影响。
  共享数据的并发修改用 `storage.update`（02 §4）。

### 3.3 补丁

宿主对新旧两棵树求差（按 `key`，否则按位置），推送操作列表：

```jsonc
{ "rev": 42, "ops": [
  { "op": "props",   "path": [0, 2],    "set": { "disabled": true }, "unset": ["tone"] },
  { "op": "insert",  "path": [1],       "index": 3, "node": { … } },
  { "op": "remove",  "path": [1],       "index": 0 },
  { "op": "move",    "path": [1],       "from": 4, "to": 1 },
  { "op": "replace", "path": [0],       "node": { … } }
] }
```

求差放在宿主（C#）而不是 SDK 里：沙箱保持简单，补丁格式和 SDK 版本无关，壳的渲染器只实现这五种操作。

### 3.4 效果

处理函数可以要求壳做一次性的事。效果随流下发，由**发起事件的那个会话**的壳执行：

`toast`、`confirm`（有返回值）、`openSheet` / `openDialog` / `close`、`navigate`（到宿主路由或插件页面）、
`openExternal`（https，壳提示后用系统浏览器打开）、`copy`、`download`（插件生成的文本 / 字节）、`pickFile`（读回内容，不给路径）、
`focus`（把焦点移到某个 key）。

有返回值的效果（`confirm`、`pickFile`）在插件侧是 promise，壳把结果作为一个内部事件发回。

## 4. 编写方式

基础是构建函数，不需要构建步骤（决定 D3）；SDK 另外提供 JSX 运行时，编译后调用同样的构建函数。

```js
import { ui, storage, t } from 'astra';
const { Page, Stack, Group, Row, Switch, Button, Table, Badge, EmptyState } = ui;

export const dashboard = ui.defineView({
  // 初始状态；params 是打开视图时的参数（挂载位给的上下文，见 §8）
  async init({ params }) {
    return { hits: await storage.get('hits') ?? [], enabled: true, busy: false };
  },

  // 纯函数：状态 → 树。不做 I/O。
  render(s) {
    return Page({ title: t('title') }, [
      Group({ title: t('settings') }, [
        Row({ label: t('enabled'), detail: t('enabled.detail') },
          Switch({ checked: s.enabled, onChange: 'toggle' })),
      ]),
      s.hits.length === 0
        ? EmptyState({ icon: 'shield-check', title: t('empty') })
        : Table({
            columns: [
              { key: 'time',  title: t('time'),  format: 'datetime' },
              { key: 'word',  title: t('word') },
              { key: 'model', title: t('model'), mono: true },
            ],
            rows: s.hits, rowKey: 'id',
            rowActions: [{ label: t('remove'), tone: 'danger', action: 'remove' }],
          }),
      Button({ label: t('clear'), variant: 'secondary', loading: s.busy,
               onPress: { action: 'clear' } }),
    ]);
  },

  // 处理函数：收到事件 → 改状态。返回后宿主重新 render 并下发补丁。
  actions: {
    toggle(s, { value }) { s.enabled = value; },
    async clear(s, _, ctx) {
      if (!await ctx.confirm({ title: t('clear.confirm'), tone: 'danger' })) return;
      s.busy = true; ctx.flush();            // 先把「忙」状态推给壳
      await storage.set('hits', []);
      s.hits = []; s.busy = false;
      ctx.toast({ message: t('cleared'), tone: 'success' });
    },
    remove(s, { row }) { s.hits = s.hits.filter(h => h.id !== row.id); },
  },

  // 可选：订阅外部变化。返回的取消函数在会话关闭时调用。
  subscribe(ctx) {
    return astra.events.on('acme.word-guard:hit', () => ctx.refresh());
  },
});
```

- **状态模型**是「单向数据流」：`init → state → render → 树`，事件进 `actions` 改状态，再 render。没有 hooks，没有
  组件实例；这在沙箱里实现简单，也最容易测试（给状态断言树，给事件断言状态）。
- 状态必须是 JSON（§3.2）。不要把函数、类实例、`Map` 放进状态；模块级变量只能当缓存用。
- **复用**靠普通函数：`function HitRow(hit) { return Row(…) }`。跨插件复用就是打包进来的 JS 库，宿主不需要知道。
- JSX 写法等价：`<Row label={t('enabled')}><Switch checked={s.enabled} onChange="toggle" /></Row>`。

## 5. 组件词汇

选取规则：**两端的 Arc 都已经有、或者能用已有组件直接拼出来**的才进首版词汇。下表「Web」「桌面」列是映射目标。

### 5.1 设计令牌（属性取值）

属性只接受令牌，这是一致性的来源：

| 令牌 | 取值 |
|---|---|
| `tone` | `neutral` `accent` `success` `warning` `danger`（映射到两端已有的 `Tone`：neutral / accent / green / orange / red） |
| `size` | `sm` `md` `lg` |
| `gap` / `padding` | `0` `1` `2` `3` `4` `6` `8`（间距阶梯的档位，不是像素） |
| `align` / `justify` | `start` `center` `end` `stretch` / `between` |
| `variant` | 各组件自己的枚举，如 Button 的 `primary` `secondary` `ghost` `danger` |
| `icon` | Lucide 图标名（两端都是 Lucide），或插件资源路径 |
| `width` | `auto` `fill`，或栅格份数 |

主题（明 / 暗）、字体、圆角、动效全部由壳决定，插件不参与。

### 5.2 首版组件（UI 1.0）

UI 1.0 约 100 个组件，按类别：

| 类别 | 组件 |
|---|---|
| 页面与布局 | `Page` `Stack` `Grid` `Split` `Scroll` `Box` `Divider` `Spacer` `Sticky` |
| 容器与结构 | `Group` `Row` `Card` `Section` `Tabs` `Accordion` `Disclosure` `Stepper` `Timeline` `Breadcrumb` `Toolbar` `Boundary` |
| 文本与展示 | `Text` `Heading` `RichText` `Markdown` `Code` `JsonView` `DiffView` `Link` `Icon` `Image` `Avatar` `Badge` `StatusDot` `Kbd` `KV` `Alert` `EmptyState` `Skeleton` `Spinner` `Progress` `Meter` `Counter` `QRCode` |
| 操作 | `Button` `IconButton` `ButtonGroup` `SplitButton` `CopyButton` `ConfirmButton` `Menu` `ContextMenu` |
| 输入 | `TextField` `PasswordField` `TextArea` `NumberField` `MoneyField` `SearchField` `CodeField` `Switch` `Checkbox` `RadioGroup` `RadioCards` `Segmented` `Select` `Combobox` `MultiSelect` `ChipGroup` `TagInput` `Slider` `DatePicker` `DateRangePicker` `TimePicker` `KeyValueEditor` `ListEditor` `FileDrop`，以及 `Form` `Field` `FieldGroup` |
| 数据 | `Table` `List` `Tree` `Pagination` `LogView` `MetricCard` `Sparkline` `Chart`（折线 / 面积 / 柱状 / 堆叠 / 环形） `Heatmap` `Gauge` |
| 浮层与反馈 | `Sheet` `Dialog` `Popover` `Tooltip` `Banner`，效果 `confirm` `prompt` `toast` |
| 领域组件 | `ModelPicker` `ProviderPicker` `AccountPicker` `ClientPicker` `TokenPicker` `ProtocolPicker`，`ModelLabel` 等标签，`Money` `RequestLink` `TraceLink` `UsageBar` |
| 自定义绘制 | `Canvas`（§11） |

树、日期、折线图、Markdown 是必须项，有单独的详细规格（04 §7）。

各部分的详细内容：

- 每个组件的属性、事件、两端映射，以及两端各自要补的组件：[04 组件参考](04-ui-components.md)
- 布局与响应式、令牌、导航、表单、数据通道、加载与错误、浮层、交互、格式化、无障碍、壳差异：[05 系统规则](05-ui-systems.md)
- SDK、开发模式、测试、两端一致性流程、UI 版本：[06 工具链](06-ui-tooling.md)

每个组件的属性、事件、子节点规则都写在模式文件里（01 §5 的单一模式源），生成 SDK 的 TS 类型、
Web 渲染器的属性类型、C# 节点类型。

### 5.3 扩展组件词汇

- 新增组件或属性 = 模式文件加一条 + 两端渲染器各加一个映射 + 一组一致性场景（§10）。UI 版本的次版本号加一。
- **协商**：壳在打开会话时上报 `ui` 版本和支持的组件列表；宿主把它交给插件（`ctx.client`、`ui.supports('Heatmap')`）。
- **降级**：任何节点可以带 `fallback` 子树。壳遇到不认识的类型时渲染 `fallback`；没有则渲染一个统一的
  「此版本不支持该组件」占位。不认识的属性被忽略。这样新插件在旧壳上不会崩。

## 6. 本地状态与表单

为了不让每次按键都往返，输入类组件的值由**壳本地**持有：

- 输入组件带 `name`。壳为每个会话维护一个**本地状态袋** `{ name: value }`。
- 每次事件都把状态袋带给处理函数（`ctx.local`，以及 `Form` 提交事件的 `values`）。
- `value` 属性是受控的初值 / 复位值：插件下发的 `value` 变化时覆盖本地值，否则以本地值为准。
- `onChange` 是可选的；带上时默认防抖（`debounce` 毫秒，默认 300），`commit: 'blur'` 表示失焦或回车才发。

简单的界面联动不必往返，用**本地条件**：

```js
Field({ label: t('url'), visible: { when: ['eq', ['local', 'mode'], 'custom'] } },
  TextField({ name: 'url' }))
```

条件是一个很小的 JSON 表达式（`eq` `ne` `and` `or` `not` `in` `empty` + `local` 取值），只能读本地状态袋，
两端各实现一个求值器并共用一份用例。它只用于 `visible` 和 `disabled`。更复杂的逻辑回到处理函数里。

校验：`Field` 的 `error` 由插件在处理函数里设置；`required`、`pattern`、`min` / `max` 这些声明式约束由壳即时校验（05 §4）。

### 6.1 本地值与下发值的对账

往返有延迟，所以要定清楚「用户刚输入的」和「服务端刚下发的」谁赢：

- 每个事件带递增的 `seq`；每个补丁带 `ack`（宿主处理到的最后一个 `seq`）。
- 壳记录每个输入最后一次本地修改时的 `seq`。补丁里对该输入 `value` 的修改，只有在 `ack` 不早于它时才应用；
  否则本地值更新，忽略这次覆盖。这样用户连续打字时不会被一个迟到的补丁改回去。
- 非文本的即时控件（`Switch`、`Checkbox`、`Segmented`、`Select`）**乐观更新**：点击后立刻显示新值并发事件；
  处理函数没有接受这个值（状态里仍是旧值）时，下一次补丁把它改回来。

### 6.2 往返的三种档位

| 档位 | 例子 | 是否往返 |
|---|---|---|
| 纯本地 | 打字、展开折叠、切换 `Tabs`（非 `lazy`）、本地条件、响应式布局、tooltip、表格列宽 | 不往返 |
| 乐观 | 开关、选择、排序方向指示 | 先显示，后确认 |
| 往返 | 按钮动作、表单提交、翻页、搜索 | 等结果 |

### 6.3 自动忙状态

动作发出后超过 150 ms 未返回补丁，壳自动让触发它的控件进入忙状态（按钮转圈并禁用、表格显示加载条），返回后恢复。
插件不需要为每个按钮维护 `loading`；需要更细的进度时才用 `ctx.flush()` 主动推中间状态。

## 7. 国际化与主题

- 会话带 `locale`。`t(key, params)` 在沙箱里用插件的 `locales/<lang>.json` 解析，缺失的键回退到 `en`，再回退到键名。
- 壳切换语言或主题时通知宿主，宿主对该会话重新 render。
- 日期、数字、金额用格式属性（`format: 'datetime' | 'number' | 'money' | 'bytes' | 'duration'`），由壳按本地习惯格式化，
  保证和宿主页面一致（金额是纳美元整数，沿用 `Money`）。

## 8. 界面挂载位

视图通过扩展点挂到宿主界面上。每个挂载位定义：id、传给视图的 `params`、允许的根组件。

| 扩展点 | 位置 | `params` |
|---|---|---|
| `ui.pages` | 侧边栏导航里的一个页面（指定分组、图标、标题） | 路由参数 |
| `ui.settings` | 插件详情里的设置面板；不提供时用 `configuration` 自动生成表单 | — |
| `ui.slots` | 宿主页面里的具名插槽，见下表 | 各插槽不同 |
| `ui.menus` | 宿主的菜单 / 工具栏里加一个命令项 | 当前行的上下文 |
| `ui.flows` | 分步流程（登录、向导），在 Sheet 里呈现（2.1，随 providers） | 流程上下文 |

首批插槽（每个在两端都是一行挂载代码）：

| 插槽 | 位置 | `params` |
|---|---|---|
| `overview.cards` | 概览页的卡片区 | 时间范围 |
| `provider.detail.tabs` | 服务商详情页的标签页 | `providerId` |
| `provider.account.sections` | 订阅账号详情 | `providerId`, `accountId` |
| `request.detail.sections` | 请求详情 | `requestId` |
| `client.detail.sections` | 客户端面板 | `clientKind` |
| `token.detail.sections` | 令牌详情 | `tokenId` |
| `settings.sections` | 设置页 | — |
| `tray.panel` | 桌面托盘面板（Web 的托盘页同样支持） | — |

`ui.menus` 的首批位置：`request.row`、`provider.row`、`provider.account.row`、`token.row`、`client.row`、`command.palette`。

**新增插槽**是模式文件加一条、两端页面各放一个 `PluginSlot`（Web 组件 / Avalonia 控件）。插槽没有贡献时不占空间。

## 9. 性能与限额

| 项 | 规则 |
|---|---|
| render 时限 | 50 ms；超时按渲染出错处理，并让引擎退役（Jaunt 的限额语义） |
| 树的大小 | 节点数和序列化字节数有上限（建议 5000 节点 / 1 MB），超出报错而不是截断 |
| 补丁合并 | 同一会话的多次 render 合并，最多每 16 ms 推一次 |
| 大数据 | `Table` / `List` 支持 `total` + `onPage` / `onSort` / `onSearch`：壳只拿当前页，翻页是一次事件 |
| 会话数 | 每插件、每客户端有上限；流断开后宽限期回收 |
| 事件顺序 | 事件带 `seq`，同一会话串行处理；壳丢弃早于已应用 `rev` 的补丁 |

## 10. 两端一致性怎么保证

1. **一份模式**生成两端的节点类型，属性不会一边有一边没有。
2. **一份场景集**在两端各渲染一遍，用仓库现有的视觉回归流程比对；新增组件必须带场景。
3. **一份行为用例**（补丁应用、本地条件、表单取值、格式化、图表刻度）两端的单元测试都跑。
4. **渲染器只做映射。** 间距、字号、颜色都来自两端已有的 Arc 令牌；渲染器里不写插件专用的样式。
5. 已知且接受的差异写进文档（字体渲染、滚动条、原生菜单外观），不追求像素相同。

具体流程见 06 §5。

Avalonia 渲染器的一个硬性要求：按 `key` 复用控件、应用补丁，而不是整棵重建——整棵重建会重复触发仓库里已经踩过的
「控件已有视觉父级」崩溃（`.agents/memory/desktop-rebuild-reparent-crash.md`）。

## 11. 自定义绘制：`Canvas`，不提供 WebView

有些东西组件拼不出来（自定义图表、拓扑图）。为此提供 `Canvas`（决定 D8）：

```js
Canvas({ width: 'fill', height: 240, viewBox: [0, 0, 600, 240], draw: [
  { rect: [0, 0, 600, 240], fill: 'surface' },
  { path: 'M0 200 L100 120 L200 160', stroke: 'accent', strokeWidth: 2 },
  { text: '42%', at: [300, 120], size: 'lg', fill: 'text', anchor: 'middle' },
  { circle: [100, 120, 4], fill: 'accent', hit: { action: 'pick', args: { i: 1 }, tooltip: '周一 42%' } },
] })
```

- 显示列表是 SVG 的一个小子集（`rect` `circle` `line` `path` `text` `group`），颜色用令牌（也允许受限的十六进制色用于数据系列）。
- Web 渲染成内联 SVG，Avalonia 渲染成 `DrawingContext` 调用——两端是同一份几何数据。
- 图元可以带命中区域（点击动作、提示文字）。

**不提供 WebView / iframe / 原始 HTML**：Web 端可以做，但桌面端没有内置 WebView（引入它会带来体积、AOT 和各平台差异），
两端从此不一致；它也会把注入、钓鱼（仿冒宿主对话框）和任意联网重新带回来。这是有意排除的，不是遗漏。

## 12. 安全

- UI 树是数据：壳不执行其中任何内容。`Markdown` 不渲染 HTML；`Link` / `openExternal` 只接受 https，并由壳提示。
- `Image` 只接受插件自带资源（经 `/api/plugins/v2/{id}/assets/…` 提供）和 `data:image`；远程图片需要 `net` 权限里的源。
- 插件界面有明确的归属标识（页面标题旁的插件名与图标；插槽区域的「由 X 提供」），并且**不能**使用宿主的全屏对话框样式索要
  管理员密码之类的东西——`PasswordField` 的值只进插件自己的处理函数，文档与审核规则里禁止索取 Astra 凭据。
- 所有会话接口是管理 API：非本机来源需要会话，变更类调用需要 `X-Astra-Admin`。
- 事件的 `action` 必须是该视图登记过的名字，`args` 按大小限制；壳发来的任何内容在宿主侧都当作不可信输入校验。

## 13. 一个完整的往返（示例）

用户在桌面端打开「守卫」页面，点「清空」：

1. 壳 `POST /sessions`（`shell: desktop`，`locale: zh`）→ 宿主激活插件（`onView:dashboard`）、调 `init`、调 `render`
   → 返回首棵树 → Avalonia 渲染器建控件。
2. 用户点按钮 → 壳 `POST /events { action: "clear", local: {…}, seq: 7 }`。
3. 宿主在 `main` 上下文调 `actions.clear`。它调 `ctx.confirm` → 宿主下发 `effect: confirm` → 壳弹出自己的确认框 →
   用户确认 → 壳回传结果 → promise 继续。
4. `s.busy = true; ctx.flush()` → render → 求差 → `patch: [{ op: props, path: […], set: { loading: true } }]` → 按钮转圈。
5. `storage.set` 完成，状态更新，处理函数返回 → render → 补丁（表格换成空状态、按钮恢复）+ `effect: toast`。

Web 端的同一个操作走完全相同的五步，只有第 1 步的渲染器和第 3 步的确认框是 Web 自己的组件。
