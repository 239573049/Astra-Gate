# 06 UI 工具链、测试与一致性

一套 UI API 能不能用好，一半取决于配套：类型、预览、调试、测试、以及保证两端一致的流程。

## 1. SDK（`@astragate/plugin-sdk`）

| 部分 | 内容 |
|---|---|
| 类型 | 由模式文件生成的 `astra` 模块声明：全部组件的属性与事件、全部宿主调用、清单类型。动作名和 `args` 有类型检查（`defineView` 的泛型从 `actions` 推导） |
| 构建函数 | `ui.Stack(props, children)` 等；运行时很小，只是造节点对象并做开发期校验 |
| JSX 运行时 | `jsxImportSource: '@astragate/plugin-sdk'`，编译产物调用同样的构建函数 |
| 模式组合件 | 用基础组件拼好的常用页面骨架，纯 JS，不需要壳支持：`SettingsPage`、`ListDetail`（列表 + 详情两栏，窄宽度下变为导航）、`Wizard`（多步）、`Dashboard`（指标卡栅格 + 图表）、`CrudTable`（表格 + 新建 / 编辑 Sheet + 删除确认） |
| 打包 | `astra-plugin build`：esbuild 打成单文件 ESM（沙箱只能相对导入），生成清单校验报告 |
| 校验 | `astra-plugin validate`：清单模式、权限与实际使用的 API 是否一致、缺失的 `a11yLabel`、缺失的翻译键、包体积 |
| 测试夹具 | 见 §4 |

## 2. 开发模式

`astra plugin dev <dir>`（只允许本机来源）把一个目录作为开发插件挂载：

- **热重载**：文件变化 → 重新加载模块 → 存活的会话用**原有状态**重新渲染。因为视图状态由宿主持有（03 §3.2），
  改 `render` 不丢界面状态；状态形状变了（`init` 的版本号变化）才重新初始化。
- **组件画廊**：宿主内置页面 `/plugins/dev/gallery`（桌面同样有），列出当前 UI 版本的每个组件、每种状态、可调属性和
  对应的代码。它由一致性场景集（§5）直接生成，所以画廊里看到的就是两端被比对过的东西。
- **检查器**（开发模式下视图右上角的入口）：
  - 当前 UI 树（点选界面元素定位到节点）、节点属性、来自哪个 `key`；
  - 状态快照与每次动作前后的差异；
  - 事件与补丁的时间线（每次往返的耗时：排队、处理函数、渲染、求差、传输）；
  - 宿主调用日志（调用名、耗时、是否被权限或限额拒绝）；
  - 模式校验的警告（未知属性、被降级的组件、超出限额的树）。
- **壳切换预览**：Web 的开发模式可以把视图容器切成 `compact` / `regular` / `wide`、明 / 暗、`tray` / `sheet` 表面，
  不用开桌面端就能看大部分布局问题。最终仍以两端实际渲染为准。
- 开发插件的日志实时显示在检查器和 `astra plugin dev` 的终端里。

## 3. 开发期校验

构建函数在开发模式下对每个节点做模式校验，在发布包里关闭（宿主侧的校验始终开启）：

| 检查 | 级别 |
|---|---|
| 未知组件、未知属性、属性类型不符 | 错误 |
| 事件指向未登记的动作 | 错误 |
| 列表子节点缺 `key` | 警告 |
| 缺 `a11yLabel` / `alt` | 错误（发布校验） |
| 当前挂载位不允许的根组件（如托盘里放 `Table`） | 错误 |
| 使用了壳不支持的组件且没有 `fallback` | 警告 |
| 状态或树超过限额的 80% | 警告 |

## 4. 测试

视图是「状态 → 树」的纯函数加「事件 → 状态」的处理函数，可以不启动服务器、不开界面地测：

```js
import { mount } from '@astragate/plugin-sdk/testing';
import { dashboard } from '../src/views.js';

test('清空后显示空状态', async () => {
  const v = await mount(dashboard, {
    storage: { hits: [{ id: 1, word: 'x' }] },
    client: { shell: 'desktop', locale: 'zh' },
    confirm: () => true,                         // 效果的桩
  });
  expect(v.find('Table').props.rows).toHaveLength(1);

  await v.press('clear');                        // 按 key 或动作名触发
  expect(v.find('EmptyState')).toBeTruthy();
  expect(v.effects).toContainEqual({ type: 'toast', tone: 'success', message: '已清空' });
  expect(await v.storage.get('hits')).toEqual([]);
});
```

- `mount` 提供内存实现的 `storage` / `secrets` / `http`（按请求打桩）/ `events`，以及 `find` / `press` / `change` / `submit`
  等按语义操作的方法。
- 树快照测试：`expect(v.tree).toMatchSnapshot()`。
- 网关钩子有对应的 `runHook(hooks.guard, { request, protocol, view })`。
- 夹具跑在 Node 上，行为与沙箱的差异（Jaunt 没有的全局对象）由 SDK 的 lint 规则在构建时拦截；
  CI 里另有 `astra plugin test`，在真实的服务端沙箱里跑同一组用例。

## 5. 两端一致性流程

1. **场景集** `tests/fixtures/plugin-ui/`：每个组件一个目录，每个文件是一棵 UI 树 + 元数据（主题、宽度档、语言）。
   另有组合场景（设置页、列表详情、仪表盘、表单校验状态、空 / 加载 / 错误状态）。
2. **渲染**：Web 用现有的截图脚本渲染场景，桌面用无头 UI 测试渲染同一批场景（仓库已有的做法：
   `scripts/visual/capture-web.mjs`、`tests/Astra.Desktop.UiTests`、`scripts/visual/compare.mjs`）。
3. **比对**：每个场景有像素差异阈值；超出即失败。新增组件或改渲染器必须过这一关。
4. **行为用例** `tests/fixtures/plugin-ui-behavior/`：与渲染无关的协议行为，两端单元测试各跑一遍——
   补丁应用（给定树 + 补丁 → 期望树）、本地条件求值、表单取值与脏状态、声明式校验、响应式属性解析、`format` 输出。
5. **协议兼容测试**：旧 UI 版本的场景在新壳上必须渲染不变；新场景在「只声明支持旧版本」的壳上必须走降级而不出错。

## 6. UI 版本

- UI 协议有自己的 semver（`ui: "1.0"`），与 API 版本、Astra 版本分开。
- 壳在会话里上报版本和组件列表；服务端、Web、桌面三者版本可能不同（桌面应用比它连接的服务端旧或新都可能发生），
  所以协商以**壳实际上报的组件列表**为准，不靠版本号推断。
- 次版本只做加法（新组件、新属性、新事件、新 `format`、新 `cell` 类型）。属性的取值集合也只增不减。
- 清单可以声明 `engines.ui: ">=1.2"`；不满足的壳上，宿主对该插件的视图显示「需要更新客户端」的说明，而不是渲染一半。

## 7. 文档

由模式文件生成并发布在文档站：每个组件一页（属性表、事件、两端截图、可复制的示例，截图直接取自一致性场景集）、
每个宿主调用一页、清单参考、挂载位与插槽列表、从零开始的教程、以及 v1 → v2 的迁移指南。
