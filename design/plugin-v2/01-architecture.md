# 01 总体架构

## 1. 设计原则

1. **声明优先。** 插件「提供什么」写在清单里，宿主不运行代码就能列出、校验、展示。代码只负责「怎么做」。
2. **一个口子。** 沙箱与宿主之间只有一种交互：宿主调用表里的一次具名调用，参数和结果都是 JSON。
   权限、限额、统计、审计全部在这里做。
3. **扩展点是数据。** 每个扩展点是一条注册（id、清单模式、所需权限、宿主 API、版本）。加载器不认识任何具体扩展点。
4. **契约带版本，只增不改。** 同一主版本内只做加法；插件用特性探测适配不同的宿主。
5. **失败隔离。** 一个插件出错、超时、超限，只影响它自己；宿主回到「没有这个插件」的行为。

## 2. 插件包与清单

一个 v2 插件是一个目录（开发时）或一个 tarball（分发时）：

```
my-plugin/
  astra.plugin.json     # 清单，必需；它的存在且 manifestVersion = 2 就是「这是 v2 插件」的判定
  dist/main.js          # main 上下文入口（ESM，依赖已打包进来）
  dist/gateway.js       # gateway 上下文入口（可选，只有贡献网关钩子时需要）
  locales/zh.json en.json
  assets/icon.svg …
```

沙箱只能 `import` 插件根目录内的相对路径（沿用现有 `JsModuleLoader` 的规则，含符号链接检查），外加宿主提供的虚拟模块
`astra`。npm 依赖必须在构建时打包进入口文件。

### 清单

```jsonc
{
  "manifestVersion": 2,
  "id": "acme.word-guard",            // <publisher>.<name>，^[a-z0-9][a-z0-9-]{0,31}\.[a-z0-9][a-z0-9-]{0,63}$
  "version": "2.0.0",                 // semver
  "name": { "zh": "敏感词守卫", "en": "Word Guard" },
  "description": { "zh": "…", "en": "…" },
  "icon": "assets/icon.svg",
  "publisher": { "name": "Acme", "url": "https://acme.example" },
  "license": "MIT",
  "homepage": "https://…",

  "engines": { "astra": ">=0.15.0", "api": "^2.0" },

  "main": "dist/main.js",
  "gateway": "dist/gateway.js",

  "permissions": [
    "gateway.request:rewrite",
    "storage",
    "net:https://api.acme.example"
  ],

  "activation": ["onStartup"],        // 见 §4

  "contributes": {
    "configuration": { /* 设置模式，自动生成设置表单，见 02 §9 */ },
    "gateway.hooks": [{ "id": "guard", "on": ["request"], "view": "ir" }],
    "ui.pages":     [{ "id": "dashboard", "title": { "zh": "守卫", "en": "Guard" }, "icon": "shield", "nav": "tools" }],
    "ui.slots":     [{ "slot": "request.detail.sections", "view": "hits" }],
    "commands":     [{ "id": "clear", "title": { "zh": "清空命中记录", "en": "Clear hits" } }]
  },

  "replaces": [{ "v1": "@astragate-community/middleware-word-guard" }]   // 见 07
}
```

要点：

- `contributes` 的每个键是一个**扩展点 id**。宿主按注册表里该扩展点的模式校验它的值；不认识的键给出警告并忽略
  （新插件装到旧宿主上时，其余部分照常工作）。
- 所有面向用户的字符串可以是纯字符串，也可以是 `{ "zh": …, "en": … }`。
- 清单是唯一事实来源。`package.json` 可以存在（方便放到 npm 上），但宿主不读它。

## 3. 运行模型

v2 插件的代码只在 Jaunt 沙箱里运行（决定 D2）。没有 Bun、没有子进程。插件需要的网络、存储、定时都由宿主能力提供。

一个插件有两种执行上下文：

| | `main` | `gateway` |
|---|---|---|
| 引擎数量 | 每个插件 1 个，长寿命 | 引擎池，按并发伸缩 |
| 调度 | 串行（一次只处理一个事件，像 actor） | 每个请求独占一个引擎直到请求结束 |
| 承载 | `activate`、UI 视图、命令、后台任务、事件订阅、provider / client 逻辑 | 网关钩子 |
| 模块状态 | 在引擎存活期间保留，但**不保证**（引擎会被替换，见下） | 不保证，也不在引擎间共享 |
| 时限 | 每次事件处理默认 1 s CPU；异步等待不计入 | 沿用现值：request / response 250 ms，每个 event 50 ms |
| 可用 API | 全部已授权的能力 | 只有热路径安全的子集（见 02 §3） |

为什么分开：网关钩子在每个请求、每个 SSE 事件上运行，需要并发和很紧的时限；UI 和后台任务需要一个有连续状态的地方。
把两者放在同一个引擎里，要么钩子被 UI 阻塞，要么状态在池里碎成多份（v1 的「模块全局变量每个引擎各一份」就是这个问题）。

**状态的规则**：需要保留的东西写进 `astra.storage`。模块级变量只是缓存。原因是 Jaunt 的两个性质
（`.agents/memory/jaunt-replaces-jint.md`）：

- 触到任何限额（超时、递归深度、内存）会让引擎永久失效，宿主必须换一个新的；
- `TrackedBytes` 只增不减，长寿命引擎到阈值后要退役重建。

所以宿主有权在任何两次事件之间替换引擎：新引擎重新执行模块、调用 `activate`。UI 的视图状态由宿主持有快照
（03 §3.2），所以替换引擎不影响打开中的界面。插件按这个前提写，就不会在超时后丢数据。

**空闲卸载**：同一个前提让 `main` 引擎可以在空闲一段时间后释放（没有打开的会话在等待、没有进行中的任务和定时器），
下一个事件到达时再加载。装了很多插件但只用其中几个时，常驻内存只有清单和注册信息。声明了 `onStartup` 且登记了
事件订阅的插件，宿主记住订阅表，事件到达时再唤醒，不需要引擎一直在。

**`gateway` 的共享数据**：钩子经常需要 `main` 维护的数据（规则表、词库、远程拉来的配置），但热路径上不能做异步读取。
清单在钩子上声明 `reads: ["rules", "words"]`，宿主在把引擎交给请求之前，把这些 `storage` 键和当前设置的值放进
`ctx.shared`（同步可读、只读）；键的值变化时宿主更新池里引擎的副本。钩子要往回写的东西（命中计数、事件）用
`ctx.emit` / `ctx.count`，异步投递，不等待。

**异步**：宿主调用返回 promise；宿主用 Jaunt 的异步宿主函数实现（待验证 1）。`main` 在等待宿主调用（比如一次 HTTP 请求）
时可以处理下一个事件，即事件处理是可重入的协程式调度，不是抢占式的。

## 4. 生命周期与激活

```
已安装 ──(启用)──► 已启用 ──(激活事件)──► 活动中 ──(禁用 / 卸载 / 升级 / 引擎替换)──► 已停用
```

- **激活事件**（清单 `activation`）：`onStartup`、`onView:<id>`、`onCommand:<id>`、`onEvent:<name>`、`onJob:<id>`。
  默认是懒激活：贡献了页面的插件在第一次打开页面时才加载代码。贡献网关钩子的插件，`gateway` 入口在第一个请求到达时加载。
- 入口模块：

  ```js
  import { definePlugin } from 'astra';

  export default definePlugin({
    async activate(ctx) {
      // ctx.plugin: { id, version }；ctx.reason: 'startup' | 'view' | 'command' | 'upgrade' | 'restart' …
      // ctx.subscriptions: 这里登记的东西在停用时由宿主统一释放
    },
    async deactivate() {},
  });
  ```

- 宿主对每个插件维护健康状态：连续激活失败或在短时间内反复触限的插件被自动停用，并在插件页和通知里说明原因
  （`plugin.unhealthy` 事件，可接入现有告警）。

## 5. 扩展点注册表与宿主调用表

这是可扩展性的核心。两张表都在宿主里，都是数据。

### 扩展点

```csharp
// 示意
interface IExtensionPoint
{
    string Id { get; }                 // "gateway.hooks"
    int Version { get; }               // 该扩展点自己的版本
    JsonSchema ManifestSchema { get; } // contributes[Id] 的形状
    IReadOnlyList<string> Permissions { get; }  // 贡献它需要的权限
    void Bind(PluginHandle plugin, JsonElement contribution);   // 把一条贡献接到宿主功能上
    void Unbind(PluginHandle plugin);
}
```

新增一个扩展点 = 实现这个接口 + 在组合根（`AstraApp.Create`）注册一行。加载、校验、列出、权限提示、文档生成都自动覆盖它。
首批扩展点见 02 §2。

### 宿主调用

```csharp
// 示意
record HostCall(string Name,            // "storage.get"
                string? Permission,     // 需要的权限，null = 总是可用
                Contexts Contexts,      // Main | Gateway
                JsonTypeInfo Args, JsonTypeInfo Result,
                Func<PluginHandle, JsonElement, CancellationToken, ValueTask<JsonNode?>> Run);
```

- 沙箱里只有一个原生入口（沿用现有 `JsRuntime` 的桥：注册一个原生全局，桥脚本取走后删除）。虚拟模块 `astra` 是一段
  由调用表**生成**的 JS：`astra.storage.get(k)` 就是 `call('storage.get', [k])`。
- 调用未授权的 API 时，promise 以 `PermissionError` 拒绝；该命名空间在 `astra.has()` 里报告为不可用。
- 所有参数和结果都是源生成的 JSON 类型（AOT 要求；注册在 `PluginsJsonContext`）。
- **单一模式源**：`api/` 下的模式文件描述每个调用、每个扩展点、每个 UI 组件。由它生成：TS 类型声明（发布为
  `@astragate/plugin-sdk`）、C# 的参数 / 结果记录和分发表、文档页。手写的只有 `Run` 的实现。这样三处不会漂移。

## 6. 权限

权限是字符串，写在清单 `permissions` 里，形如 `<域>[.<对象>][:<动作或范围>]`：

| 例子 | 含义 |
|---|---|
| `storage` | 插件自己的键值存储 |
| `secrets` | 插件自己的加密存储 |
| `net:https://api.acme.example` | 经宿主向这个源发 HTTP 请求（只允许 https；可写多条；不支持通配整个互联网） |
| `gateway.request:read` / `:rewrite` / `:reject` | 读 / 改写 / 拒绝请求 |
| `gateway.response:read` / `:rewrite` | 读 / 改写响应与流事件 |
| `gateway.upstream:headers` | 给上游请求加 / 删头（凭据类和成帧类的头仍被拒绝，沿用 `MiddlewareHeader`） |
| `data.requests:read` | 读请求日志的元数据（不含正文） |
| `data.providers:read`、`data.models:read`、`data.tokens:read` | 读对应的只读视图（密钥永远只有掩码） |
| `ui` | 贡献界面 |
| `notifications` | 发通知 / toast |
| `jobs` | 后台定时任务 |

规则：

- **安装时确认**（决定 D6）：安装对话框按「这个插件可以……」的人话列出权限；CLI 同样列出并要求确认（`--yes` 跳过）。
- **升级时只确认新增的权限。** 授予的集合存在 `plugins.granted_permissions_json`。
- **运行时申请**：清单的 `optionalPermissions` 列出可能需要、但安装时不授予的权限，插件在需要时调
  `permissions.request(['net:https://my-server.example'])`，壳弹出宿主自己的确认框。典型用途是用户在设置里填了一个
  自己的服务器地址：`net:` 的源可以带一个设置项占位（`net:{config.endpoint}`），值变化时重新确认。
  这样不需要为了少数情况申请宽泛的网络权限。
- **审计**：敏感调用（`net`、`secrets`、请求正文的读取与改写、权限变更）记入插件活动日志（谁、何时、调用名、目标源；
  不记内容），插件页可查。
- 权限只决定调用表里哪些调用可用、哪些扩展点可绑定。没有「全部权限」。
- 高风险权限（改写请求、读请求正文、`net`）在插件页上有常驻标识。
- 永远拿不到的东西（不设权限）：管理员密码与会话、provider 密钥明文、其他插件的存储、`~/.astra` 文件、进程环境变量。
- 沿用现有边界：服务不在 loopback 上监听时，远程安装默认关闭；安装本地路径插件只允许本机来源。

## 7. 存储

| 数据 | 位置 |
|---|---|
| 已安装列表 | 现有 `plugins` 表加列（新 migration，不改旧文件）：`api_version`（1 / 2）、`plugin_id`、`manifest_json`、`granted_permissions_json` |
| 插件键值存储 | 新表 `plugin_kv(plugin_id, key, value_json, updated_at_utc)`，每插件有容量上限 |
| 插件密钥 | 新表 `plugin_secrets(plugin_id, key, value_protected)`，经 `ISecretProtector` |
| 插件文件 | `~/.astra/plugins/v2/<id>/<version>/`（解包后的只读内容）；这是 `AstraPaths` 的新路径，三处布局定义要同步（AGENTS.md 关联更新 5） |
| 设置值 | `plugins.options_json`（沿用），按清单 `configuration` 模式校验 |

v2 不再有明文凭据文件：v1 的 `plugin-auth.json` 只属于 v1。

## 8. 限额与隔离

| 项 | 默认 |
|---|---|
| 引擎限额 | 沿用 `JsRuntime`：递归深度 512、内存预算、每次调用的时限；到退役阈值换引擎 |
| `main` 单次事件 CPU | 1 s（渲染 50 ms，见 03） |
| `gateway` 钩子 | request / response 250 ms，event 50 ms，加载 1 s（v1 现值） |
| 宿主调用速率 | 每命名空间令牌桶（例如 `net` 每分钟 N 次、并发 M） |
| 存储 | 每插件键值总量上限、单值上限 |
| 日志 | 每分钟行数上限（沿用 v1 的限流） |
| UI | 每棵树节点数上限、补丁合并间隔（见 03 §9） |

钩子出错或超时：输入原样放行（沿用 v1 语义）。统计（调用数、耗时、失败、最后错误）按插件、按扩展点记录，插件页展示。

## 9. 分发、安装、开发

- **市场**：沿用文档站提供的注册表和「地址与哈希只来自注册表」的规则（AGENTS.md 关联更新 10）。条目增加 `apiVersion`、
  `id`、`permissions`、`replaces`（字段变更是该关联更新的范围，四处一起改）。
- **安装**：下载 tarball → 校验 sha256 → 安全解包（沿用 `NpmTarballFetcher` 的检查：拒绝绝对路径、`..`、符号链接）→
  读清单 → 校验 `engines` → 权限确认 → 写入列表。不执行任何安装脚本，不需要 Bun。
- **本地目录**：`astra plugin dev <dir>` 以开发模式挂载一个目录，文件变化时重载；只允许本机来源。
- **SDK**（`@astragate/plugin-sdk`，npm）：类型声明、UI 构建函数与可选 JSX 运行时、打包脚本（单文件 ESM）、
  `astra plugin validate` / `pack`、一个不需要服务器的测试夹具（给钩子喂请求、给视图发事件并断言 UI 树）。

## 10. 与现有代码的关系

- 新代码放在 `src/Astra.Plugins/V2/`（同一程序集，复用 `Js/JsRuntime`、`JsModuleLoader`、市场、安装任务）。依赖方向不变。
- v1 的 `Middleware/`、`Agents/`、`Host/`、`Runtime/` 冻结，只修缺陷（见 07）。
- 网关接入点不变：`GatewayPipeline` 仍通过可空的中间件依赖调用；v1 链和 v2 钩子由一个组合对象按顺序执行，
  relay 主机继续传 null（AGENTS.md 关联更新 21）。
- UI 树、补丁、事件的 DTO 放在 `Astra.Api.Contracts`（桌面端和服务端共用，注册在 `ContractsJsonContext`）。
- 管理接口新增 `/api/plugins/v2/*`，照常经过 `SecurityMiddleware`（变更类调用要 `X-Astra-Admin`，非本机要会话）。

## 11. 分期（建议）

| 阶段 | 内容 |
|---|---|
| P0 | 技术验证（README「待验证」四项）+ 模式文件与代码生成的骨架 |
| P1 | 运行时核心：清单、注册表、调用表、权限、`main` 上下文、`storage` / `secrets` / `log` / `i18n` / `commands`、`configuration` 设置表单 |
| P2 | UI API：协议、两端渲染器、`ui.pages` / `ui.settings` / 首批 `ui.slots`、一致性测试 |
| P3 | 网关钩子 v2（`gateway` 上下文、`raw` / `ir` 视图）、与 v1 链的组合 |
| P4 | 市场条目、v2 升级提醒与升级流程（07）、CLI、SDK、文档 |
| 2.1 | `providers`、`clients` 扩展点（替代 v1 的 provider / agent 插件），`net` 流式、登录流程视图 |
| 之后 | `mcp.tools`、`routing.judges`、`alerts.channels`、插件间 API |
