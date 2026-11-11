# 05 UI 系统规则

组件之外，一套完整的 UI 还需要这些横向的规则。每一节都是协议的一部分，两端按同一份定义实现。

## 1. 布局与尺寸

**盒模型只有三种容器**：`Stack`（一维）、`Grid`（二维）、`Split`（两栏）。没有绝对定位，没有层叠次序。

- **尺寸**：子节点的通用布局属性
  - `width`: `auto`（内容宽）｜`fill`（占满）｜档位 `xs sm md lg xl`（固定的令牌宽度，用于表单控件和侧栏）
  - `grow`: 在 `Stack` 主轴上分配剩余空间的份数
  - `minWidth` / `maxWidth`: 档位
  - `alignSelf`: 覆盖父容器的交叉轴对齐
- **高度**一般由内容决定。需要固定或限制高度的只有 `Scroll.maxHeight`、`Chart.height`、`Canvas.height`、`Code.maxHeight`、
  `LogView.height`，取值是档位或 `fill`（占满页面剩余高度）。
- **间距**只用间距档位（`gap`、`padding`）。相邻组件之间的默认间距由容器决定，不需要每处手写。
- **溢出**：文本用 `truncate`（行数）；容器内容超出时自动滚动的只有 `Scroll` 和带 `maxHeight` 的组件，其余容器随内容增高。

**响应式**用容器宽度而不是屏幕宽度（插件视图可能在整页、Sheet、插槽、托盘面板里）：

- 壳把视图容器的宽度归为 `compact`（< 480）、`regular`、`wide`（≥ 960）三档，通过 `ctx.client.width` 告诉插件，变化时触发重渲染。
- 多数情况不需要读它：`Grid({ columns: { min: 'md' } })` 按最小列宽自动折行；`Split.stackBelow` 在窄宽度下自动改为上下；
  `Stack({ wrap: true })` 自动换行；`Table` 在 `compact` 下把次要列（`priority: low`）收进展开行。
- 任何属性都可以写成按宽度取值：`columns: { compact: 1, regular: 2, wide: 4 }`。由壳在本地解析，改变窗口大小不往返。

**密度**：`dense`（通用于 `Group`、`Table`、`List`、`Form`）对应桌面设置页的紧凑度量；默认跟随所在宿主页面。

## 2. 排版与颜色令牌

| 类别 | 令牌 |
|---|---|
| 文本样式 | `heading1–4` `body` `label` `caption` `mono`（字号、行高、字重由壳定） |
| 文本颜色 | `text` `muted` `subtle` `accent` `success` `warning` `danger` `onAccent` |
| 表面 | `surface` `card` `sunken` `accentSoft` `successSoft` `warningSoft` `dangerSoft` |
| 边框 | `border` `borderStrong` |
| 数据调色板 | `color: 0–7`（图表与 `Canvas` 的系列色，明暗各一套） |

插件拿不到具体色值，也不能自定义主题色。唯一的例外是 `Canvas` 和 `Chart` 的数据系列可以用受限的十六进制色
（品牌色之类），并且必须同时给出暗色主题下的值。

## 3. 导航

**插件页面内的导航**是一个路由栈，由宿主管理，和宿主自己的路由打通：

```jsonc
"ui.pages": [{
  "id": "rules", "title": …, "icon": "list", "nav": "tools",
  "routes": [
    { "path": "",            "view": "ruleList" },
    { "path": ":ruleId",     "view": "ruleDetail" },
    { "path": ":ruleId/log", "view": "ruleLog" }
  ]
}]
```

- 地址：Web 是 `/plugins/<pluginId>/<pageId>/<path>?<query>`，桌面是同形的内部路由。浏览器前进 / 后退、刷新、
  复制链接都有效；桌面的返回键同样工作。
- `ctx.navigate('rules/42')`、`ctx.navigate({ page: 'rules', path: '42', query: { tab: 'log' } })`、`ctx.back()`。
  跳到宿主页面：`ctx.navigate({ host: 'requests', query: { model: 'x' } })`（宿主公开的路由名是契约的一部分）。
- `Page` 的 `back` 属性显示返回入口；面包屑用 `Breadcrumb`。
- **视图参数**（`params`）来自路径和查询串。视图里「应该能被链接到」的状态（当前标签页、筛选条件、页码）放在查询串：
  `ui.defineView({ query: ['tab', 'q', 'page'], … })`，这些键的状态变化会同步到地址，返回时自动恢复。
- 侧边栏分组 `nav` 取宿主已有的分组；插件多时宿主把它们收进「插件」子菜单，用户可以隐藏（和现有可隐藏导航项一致）。
- 外部深链：`astra://plugin/<pluginId>/<pageId>/<path>` 由桌面的协议处理转成上述路由（`astra` 协议已经注册，用于加入链接）。

## 4. 表单

```js
Form({ onSubmit: 'save', dirtyBar: true }, [
  Field({ label: t('name'), required: true },
    TextField({ name: 'name', value: s.rule.name, maxLength: 64 })),
  Field({ label: t('mode') },
    Segmented({ name: 'mode', value: s.rule.mode, options: MODES })),
  Field({ label: t('url'), visible: { when: ['eq', ['local', 'mode'], 'webhook'] }, error: s.errors.url },
    TextField({ name: 'url', type: 'url' })),
  Field({ label: t('headers') },
    KeyValueEditor({ name: 'headers', value: s.rule.headers, secretValues: true })),
])
```

- **取值**：表单内所有具名输入的当前值在提交时作为 `values` 一次交给处理函数（03 §6 的本地状态袋）。
- **脏状态**：壳比较本地值与插件下发的 `value`，有差异即为脏。`dirtyBar` 显示底部的「保存 / 放弃」条（桌面客户端页的
  「待保存修改」条是同一个样式）；脏状态下离开页面，壳先询问。
- **校验分两层**：
  1. 声明式（壳即时执行，不往返）：`required`、`pattern`、`min` / `max`、`maxLength`、`type`。
  2. 业务校验（处理函数里）：`ctx.fail({ url: t('url.unreachable') })` 把错误设到对应 `Field`，并保持表单为脏。
  提交时先跑第一层，全部通过才发 `onSubmit`。
- **提交中**：`onSubmit` 进行时壳自动禁用提交按钮并显示进行中，结束后恢复（03 §6.3 的自动忙状态）。
- **重复结构**：`ListEditor`（数组的每一项是一组字段）、`KeyValueEditor`、`TagInput`。字段名用路径：`rules.0.word`。
- **密文字段**：`PasswordField` 的值不回显；已有值显示为占位，留空表示不修改（和宿主 provider 密钥的交互一致）。
- **自动表单**：清单 `configuration`（02 §9）生成的设置表单就是用上面这些组件拼的，`widget` 提示选择具体控件。

## 5. 数据：集合与数据通道

把大列表整个放进状态、每次变化重新渲染整棵树，在行数多或更新频繁时既慢又浪费。为此提供**数据通道**：
数据走单独的增量通道，树里只放引用。

```js
const hits = ui.collection('hits', { key: 'id' });     // 视图内的具名集合

render(s) { return Table({ source: hits, columns: […], onPage: 'page' }) }

actions: {
  async page(s, { page, pageSize }) {
    const r = await query(page, pageSize);
    hits.replace(r.items, { total: r.total, page });   // 换一页
  },
},
subscribe(ctx) {
  return events.on('acme.guard:hit', (h) => hits.prepend(h));   // 新数据到达：只推一条
}
```

- 操作：`replace(items, meta)`、`append` / `prepend(items)`、`update(key, patch)`、`remove(key)`、`clear()`。
  每个操作是 SSE 上的一条 `data` 消息，不触发树的重渲染和求差。
- 可以绑定集合的组件：`Table`、`List`、`Tree`、`LogView`、`Chart`、`Select` / `Combobox` 的远程选项。
- `LogView` 的通道是只追加的，带容量上限（壳保留最近 N 行）。
- 集合的内容不在视图状态里，不计入状态快照的大小（03 §3.2）；引擎替换后由 `init` 重新填充。
- 实时刷新：`ui.defineView({ refreshEvery: '10s' })`（只在视图可见时触发 `refresh`），或 `subscribe` 订阅事件。
  视图不可见（切到别的标签页、窗口隐藏）时壳通知宿主暂停推送。

## 6. 加载、错误与空状态

- **首屏**：`init` 未完成前壳显示骨架屏（`defineView({ skeleton: 节点 })` 可自定义）。超过时限显示错误占位。
- **局部加载**：`Boundary({ loading: Skeleton(…), error: … }, 子树)`；配合 `Tab` 的 `lazy` 和 `onVisible` 做按需加载。
- **动作进行中**：自动忙状态（03 §6.3）；长任务用 `Progress` + `ctx.flush()` 推进度。
- **错误**：
  - 处理函数抛错 → 壳显示错误 toast（带「详情」），状态回到动作开始前的快照。
  - `render` 抛错或超时 → 该视图显示统一的错误面板（原因、重试、查看插件日志），页面其余部分和其他插槽不受影响。
  - 插件被停用 / 卸载 / 不健康 → 打开中的视图显示对应说明，而不是空白。
- **空状态**：`EmptyState`；`Table` / `List` 的 `empty` 属性。

## 7. 浮层

- `Sheet`（侧边面板，Astra 的主要编辑容器）、`Dialog`（居中）、`Popover`（锚定）。每个浮层是一个独立视图会话的子会话，
  随父会话关闭。
- 浮层最多嵌套两层（Sheet 里可以开 Dialog / confirm，不能再开 Sheet）。
- 焦点进入浮层并被限制在内；Esc 关闭（脏表单先询问）；关闭后焦点回到触发它的控件。这些由壳实现，插件不参与。
- 浮层向打开者返回结果：`ctx.close(result)`。

## 8. 交互

| 能力 | 方式 |
|---|---|
| 快捷键 | 清单 `commands[].keybinding`（如 `mod+shift+g`，`mod` = ⌘ / Ctrl），或节点属性 `shortcut`（视图有焦点时生效）。与宿主快捷键冲突时宿主优先，并在插件页提示 |
| 命令面板 | 贡献到 `ui.menus` 的 `command.palette` |
| 拖拽排序 | `List` / `ListEditor` / `Table` 的 `reorderable` → `onReorder` |
| 文件 | 选取：`ctx.pickFile({ accept, multiple })`；拖入：`FileDrop`；导出：`ctx.download({ name, text｜bytes, type })`。插件拿到的是内容引用，不是路径 |
| 剪贴板 | 写：`ctx.copy(text)`、`CopyButton`。读剪贴板不提供 |
| 焦点 | `ctx.focus(key)`；`autoFocus` 属性 |
| 滚动 | `ctx.scrollTo(key)`；`onVisible` 做无限滚动 |
| 选择 | `Table` / `List` / `Tree` 的 `selection` + `bulkActions` |
| 右键菜单 | `ContextMenu`；`Table` / `List` 的 `rowActions` 同时出现在行尾菜单和右键菜单里 |
| 通知 | `notifications.send`（02 §11）：桌面走系统通知，Web 走控制台通知 |

**没有**的事件：鼠标移动、悬停、按键流、滚动位置、双击。它们要么无法在一次往返的延迟下有意义地处理，要么两端语义不同。
悬停反馈用 `tooltip` / `Popover`（壳本地处理）。

## 9. 格式化与国际化

`format` 属性由壳按会话语言和本地习惯格式化，保证和宿主页面的写法一致：

| `format` | 输入 | 例 |
|---|---|---|
| `number` / `compact` / `percent` | 数字 | 12,345 / 1.2万（12.3K） / 42% |
| `money` | 纳美元整数 | $0.0123 |
| `tokens` | 数字 | 1.2M |
| `bytes` / `duration` | 字节 / 毫秒 | 1.4 MB / 320 ms |
| `date` / `time` / `datetime` | ISO 8601 UTC | 本地时区 |
| `relative` | ISO 8601 UTC | 3 分钟前（自动更新，不往返） |

文案：`t(key, params)`，支持复数与选择（`{count, plural, one {…} other {…}}`）。插件至少提供 `en`；提供 `zh` 才在中文界面
显示中文。壳自带的文案（确认、取消、重试、分页、空状态默认语）不需要插件翻译。

## 10. 无障碍

- 语义组件自带角色、键盘操作和焦点样式；插件不需要也不能设置 ARIA 属性。
- `IconButton`、纯图标的 `Badge`、`Image` 必须给 `a11yLabel` / `alt`——模式校验强制，缺失时开发模式报错、发布包校验失败。
- 颜色不是唯一的信息载体：`tone` 类组件同时带图标或文字（壳实现）。
- 动效遵循系统的「减少动态效果」设置（壳实现）。
- `Canvas` 的图元可以带 `label`，壳把带标签的图元暴露为可聚焦的列表。

## 11. 壳之间的差异与上下文

`ctx.client` 告诉插件当前会话的环境：

```js
{ shell: 'web' | 'desktop', ui: '1.0', locale: 'zh', theme: 'dark',
  width: 'regular', surface: 'page' | 'sheet' | 'slot' | 'tray' | 'dialog',
  remote: false,            // 是否经隧道远程访问
  platform: 'macos' | 'windows' | 'linux' | 'browser' }
```

原则是**不鼓励按壳分支**。已知且由壳消化的差异：

| 差异 | 处理 |
|---|---|
| 托盘面板宽度固定、高度有限 | `surface: 'tray'`；插槽规定只允许 `Stack` / `Row` / `MetricCard` 等紧凑组件，模式校验拒绝 `Table` |
| 原生菜单外观、滚动条、字体渲染 | 接受，不比对 |
| 文件选取对话框 | 桌面用系统对话框，Web 用浏览器的；返回同样的内容引用 |
| `openExternal` | 桌面用系统浏览器，Web 开新标签页 |
| 远程访问（`remote: true`） | 往返更慢：壳把自动忙状态的显示阈值调低；本机专属的效果（`pickFile` 读本机路径之类）不存在这个问题，因为文件总是从用户所在的客户端读 |
| 系统通知 | 桌面有，Web 退化为控制台通知 |

## 12. 视图状态的持久化

| 范围 | 机制 |
|---|---|
| 一次会话内 | 视图状态（宿主持有快照，03 §3.2） |
| 可链接的状态 | 查询串（§3） |
| 同一用户下次打开还在 | `ui.defineView({ persist: ['sort', 'columns', 'collapsed'] })`：这些键由壳按「插件 + 视图」存在本地（Web 的 localStorage、桌面的设置文件），不进服务端 |
| 跨设备、跨客户端 | 插件自己写 `astra.storage` |
