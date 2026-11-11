# 07 v1 共存与 v2 升级提醒

## 1. 原则

- **v1 保留，冻结。** 已安装的 v1 插件（magpie 兼容：中间件、agent、provider、pi）继续按现在的方式运行，
  用户不需要做任何事。v1 不再增加能力，只修缺陷和安全问题。
- **v2 不兼容 v1。** v2 不读 `package.json` 的 `magpie.*`，不认 `x-magpie-*` 头，不提供 v1 的 `ctx` 形状。
  一个包要么是 v1 插件，要么是 v2 插件：有 `astra.plugin.json` 且 `manifestVersion: 2` 的是 v2，其余按 v1 规则识别。
- **两套并行，互不可见。** v1 插件不能调用 `astra.*`，v2 插件拿不到 v1 的宿主进程和凭据文件。

## 2. v1 在代码里的位置

| | v1 | v2 |
|---|---|---|
| 识别 | `PackageManifest.Read`（现状） | `astra.plugin.json` |
| 进程内运行 | `Middleware/`、`Agents/`（Jaunt） | `V2/`（Jaunt，`main` + `gateway` 上下文） |
| 进程外运行 | Bun + 内置的 magpie `host.js` / `pi.js` | 无 |
| 凭据 | `~/.astra/plugins/plugin-auth.json` | `plugin_secrets` 表（加密） |
| 列表 | `plugins` 表，`api_version = 1` | 同一张表，`api_version = 2` |
| 管理接口 | `/api/plugins*`（现状） | 列表接口同一个（多一个版本字段），v2 专有的在 `/api/plugins/v2/*` |

- `plugins` 表加 `api_version`（默认 1，新 migration）。已有行不需要改动。
- 插件页的一个列表同时显示两种，v1 行带「v1」标记。
- **网关链**：v1 中间件和 v2 钩子按用户在列表里排的同一个顺序执行，由一个组合对象依次调用两套引擎。
  对网关流水线来说仍然是一个可空的依赖。
- v1 的 Bun 运行时只在装有需要它的 v1 插件时才下载和启动（现状已是按需）。只装 v2 插件的用户不会碰到 Bun。
- AGENTS.md 里「magpie 插件必须不改一行运行」的约束继续有效，但范围写明是 v1。

**v1 的退场**不在本设计里决定。建议的做法是先上报数据（有多少安装还在用 v1、哪些 v1 插件没有 v2 版本），再定时间表。

## 3. 升级提醒

「插件有 v2 新版本则提醒用户更新」分三件事：怎么知道有、怎么提醒、怎么升级。

### 3.1 怎么知道有 v2 版本

来源只有市场注册表（客户端不自己去问 npm 或 GitHub，这是现有规则）。注册表条目增加：

```jsonc
{
  "package": "acme.word-guard",        // v2 条目用插件 id
  "apiVersion": 2,
  "permissions": ["gateway.request:rewrite", "storage"],
  "replaces": [
    { "v1": "@astragate-community/middleware-word-guard" },   // 它接替的 v1 包名（可多个）
    { "v1": "github:acme/word-guard" }                         // 或 v1 的其他 spec 形式
  ],
  "minAstraVersion": "0.15.0",
  …
}
```

匹配规则：对每个已安装的 v1 插件，取它的包名（`plugins.name`）和 spec，在注册表里找 `apiVersion: 2` 且 `replaces`
包含它的条目。找到、且本机 Astra 版本满足 `minAstraVersion`，就是「有 v2 版本」。

- 接替关系由 **v2 一方**声明并经市场审核，v1 包不需要改动——很多 v1 插件是社区的 magpie / OpenCode 包，改不了。
- 一个 v2 插件可以接替多个 v1 插件（例如把一个中间件和一个 agent 插件合并）。
- 不在市场里的 v1 插件（本地路径、私有 git）不会有提醒；用户可以手动安装 v2 版本，安装时宿主按 v2 清单里的
  `replaces` 做同样的匹配，询问是否接替。
- 检查时机：沿用市场注册表的缓存周期（现在是 6 小时），以及用户打开插件页或手动刷新时。

注册表字段的变更属于 AGENTS.md 关联更新 10 的范围：文档站的类型与路由、`PluginMarket.Parse`、`web/src/api/types.ts`、
`cli/src/commands/plugin.ts`、文档页要一起改；两份内置注册表副本也要同步。旧版本的 Astra 会忽略不认识的字段，
但它会把 `apiVersion: 2` 的条目当成普通插件展示——所以 v2 条目需要一个旧客户端会跳过的标记方式
（例如放在注册表的新数组 `pluginsV2` 里，而不是混进 `plugins`）。**这一点要在动注册表之前定下来。**

### 3.2 怎么提醒

| 位置 | 形式 |
|---|---|
| 插件页（Web 与桌面） | v1 行上的「有 v2 版本」徽标；页面顶部一条横幅：「N 个插件有 v2 版本」+「查看」 |
| 插件详情 | 对比卡片：v2 的版本、变更说明、要申请的权限、「升级到 v2」按钮 |
| 侧边栏 | 插件导航项上的小圆点 |
| 通知 | 首次发现时一次 toast / 通知；同一个 v2 版本只提醒一次，用户可以「不再提醒此插件」 |
| CLI | `astra plugin list` 标出 `v2 available`；`astra plugin upgrade <name>` |
| 管理接口 | 列表 DTO 增加 `successor: { id, version, permissions, notes }`（DTO 在 `Astra.Api.Contracts`） |

提醒是**建议**，不是强制：v1 插件继续工作，用户可以一直不升。新的 UI 字符串中英文都要加，并重新生成桌面端的字符串。

普通的 v1 版本更新（同一个 v1 包出了新版本）维持现在的更新流程，和 v2 升级提醒是两件事，界面上分开显示。

### 3.3 怎么升级

升级需要用户确认，不自动进行（决定 D7）：v2 有权限要确认，而且换的是另一套运行方式。

```
用户点「升级到 v2」
  1. 展示：v2 申请的权限、会迁移什么、v1 的哪些东西带不过去
  2. 下载并校验 v2 包，读清单，检查 engines
  3. 以「停用」状态安装 v2；把 v1 的启用状态、排序位置记给它
  4. 迁移（见下）
  5. 启用 v2 并激活；成功 → 停用并移除 v1
     失败 → 移除 v2，v1 原样保留，告诉用户原因
```

第 5 步的顺序保证任何时刻至少有一个可用：v2 没有成功激活之前，v1 不动。

**迁移什么**

| v1 的东西 | 处理 |
|---|---|
| 启用状态、在链里的顺序 | 宿主直接带过去 |
| 选项（`options_json`） | 交给 v2 插件自己转换：激活时 `ctx.reason === 'upgrade'`，`ctx.upgrade = { from: { apiVersion: 1, package, version, options } }`；插件把它映射到自己的 `configuration`。宿主不猜字段对应关系 |
| provider 插件的登录凭据（`plugin-auth.json` 里它自己的条目） | 2.1 随 `providers` 扩展点定：v2 清单声明 `legacy.auth:import` 权限后，升级时宿主把**该 v1 插件自己的**条目一次性交给 `ctx.upgrade.from.auth`，由 v2 插件写入 `secrets`。用户在第 1 步能看到这一项 |
| provider 行、账号、绑定（`plugin-<id>`） | 2.1 定：目标是保留 provider 行和账号 id，只换背后的实现，这样令牌池、客户端绑定、历史请求都不受影响 |
| agent 插件写进客户端配置的键 | 2.1 定：经 `ClientConfigApplier` 的状态记录交接，不重新写用户的配置文件 |
| 模块全局状态、统计计数 | 不迁移 |

2.0 首版只有中间件类的 v1 插件有 v2 的对应物（`gateway.hooks`），所以首版的升级流程只需要前两行。
provider / agent 类 v1 插件在 2.1 之前不会收到升级提醒（市场上不会有接替它们的 v2 条目）。

**回退**：升级后一段时间内（建议保留 v1 的 tarball 与选项快照 30 天），插件详情里提供「回到 v1」。

## 4. 对用户的说明（文档与界面文案的要点）

- v1 插件继续可用，不需要立刻做什么。
- v2 版本会要求确认权限；它运行在沙箱里，不再以你的用户权限运行外部进程。
- 升级失败不会丢掉现有的插件。
- 升级后的设置由插件自己迁移；带不过去的项目会在升级前列出。
