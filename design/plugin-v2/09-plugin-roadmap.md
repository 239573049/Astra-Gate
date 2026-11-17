# 09 插件路线图

状态：2026-11-16 定稿；P1 已实施（平台改动在本仓库，五个插件在社区仓库 `packages/`，均未发布），P2 / P3 未开始。本文回答两件事：现有插件覆盖了哪些能力，接下来做哪些插件、平台为它们先补什么。
扩展点、权限、宿主调用的定义仍以 01–08 和代码为准；这里只记「做什么、为什么、先后」。

## 1. 现状

### 1.1 已有插件（社区仓库 `astragate-community/astra-gate-plugins`，2026-11-16 全部是 v2）

| 类型 | 数量 | 插件 |
|---|---|---|
| 订阅类服务商（`providers` + `ui.flows`） | 16 | Cursor、Kiro、Factory、Devin、Zed、Grok、Cline、Command Code、Qoder、Trae、WorkBuddy、ZCode、MiMo、MiniMax、Antigravity、Zen Free |
| 网关中间件（`gateway.hooks` + `ui.settings`） | 6 | Model Map、Param Override、System Prompt、Think Tags、Word Guard、Claude Code 身份 |
| 客户端（`clients`） | 1 | Aider |

### 1.2 扩展点的使用面

| 扩展点 | 用到它的插件 |
|---|---|
| `providers` / `ui.flows` | 16 |
| `gateway.hooks` / `ui.settings` | 6 |
| `configuration` | 4 |
| `jobs` | 2 |
| `ui.pages` / `clients` | 各 1 |
| `commands` / `ui.slots` / `ui.menus` / `events` / `data.*` | **0** |

生态是「接入」型（让 Astra 用上更多订阅），「增值」型（观测、治理、自动化、往宿主页面里加东西）几乎为空；
2.0 为后者准备的挂载位、菜单、事件、只读数据都没有被真实插件用过，也就没有被验证过。

### 1.3 平台缺口（读代码核实过）

| # | 缺口 | 证据 | 处理 |
|---|---|---|---|
| G1 | `provider.changed`、`account.changed`、`alert.fired` 在 `PluginV2Host.HostEvents` 里可订阅，但没有任何地方发布；只有 `request.completed`、`settings.changed`、`plugin.configChanged` 会到 | `grep Publish(` | P1 补上（§4.1） |
| G2 | `data.requests.stats` 没有 02 §10 写的 `groupBy`；`data.requests.query` 的行和 `request.completed` 没有时间、缓存 token、是否流式；`data.models.list` 没有价格 | `PluginV2HostServices.QueryAsync`、`PluginEventSink` | P1 补齐请求部分（§4.2）；价格等到有插件要用 |
| G3 | 钩子不能直接给出一个正常回复：`ctx.reject` 只接受 400–599 | 02 §3 | P3（`ctx.respond`） |
| G4 | 插件调不了 Astra 自己的网关：`http.fetch` 禁止 loopback 与内网 | 02 §5 | P3（`gateway.invoke`） |
| G5 | 「之后」的扩展点：`mcp.tools`、`routing.judges`、`alerts.channels`；插件间 API；`command.palette` 两端都没有显示处 | 01 §11、AGENTS.md 联动更新 27 | P2 / P3 |
| G6 | `ir` 视图改写后由编解码器重新编码，Anthropic `cache_control` 与 Responses `prompt_cache_breakpoint` 丢失 | `Ir.cs`（缓存断点不在 IR 里）、`GatewayHookViews.ApplyRequestView` | 不改：会改写请求体、又要保住提示缓存的插件用 `raw` 视图（§3.1） |

更正：provider 的 `usage()` **已有消费方**（`GET /api/plugin-providers/{id}/accounts/{account}/usage`，两端账号卡都显示），
08 §2 与 `astra.js` 里「宿主不调用」的注释是旧的，随 P1 改正。它没有进入后台额度同步（`SubscriptionQuotaSync` 只看
OAuth 订阅），所以插件账号没有额度告警、托盘显示和冷却复查——这是以后的一项，不在 P1。

## 2. 选插件的规则

1. **不重复核心。** 隐私守卫、令牌限额、智能路由、告警 webhook、请求详情里的计费拆解都在核心里；插件做核心不做、或因人而异的部分。
   据此，草案里的「单次请求成本拆解」被删掉（请求详情已经显示计费说明）。
2. **优先用闲置的能力。** 首批官方插件同时是挂载位、事件、`data.*`、`jobs` 的第一批真实用户。
3. **热路径不等待。** 钩子里没有 `http`、没有异步 `storage`；要外部数据的插件在 `main` 里后台拉取写进 `storage`，钩子从 `ctx.shared` 读（02 §3）。
4. **服务商插件按需求做。** 它们维护成本最高（上游一改就坏），复用订阅还可能有条款风险。

## 3. 插件清单

「阶段」一列：P1 现在就做；P2 / P3 要先有 §4 里对应的平台能力；「按需」不排期。

### 3.1 网关增值（`gateway.hooks`）

| 插件 | 做什么 | 视图 / 权限 | 阶段 |
|---|---|---|---|
| **Context Trim**（`context-trim`） | 请求估算超过模型上下文的一定比例时，从最旧的开始把 `tool_result` 的内容换成一行占位，直到回到预算内；不碰最近 N 轮、不碰系统提示 | `raw`（保住缓存断点）；`gateway.request:rewrite`、`data.models:read`（`main` 的任务把各模型的上下文长度写进 storage，钩子从 `ctx.shared` 读） | P1 |
| **Tool Policy**（`tool-policy`） | 按客户端、令牌、模型匹配规则，把声明的工具去掉或整个请求拒绝；命中计数 | `raw`；`gateway.request:rewrite`、`gateway.request:reject` | P1 |
| Prompt Cache | 给 Anthropic 上游自动打 `cache_control` 断点（核心已有 provider 级的 `AutoCacheControl`；插件只在要按客户端 / 模型细调时才有意义） | 要先确认 `onUpstream` 能否改请求体 | 待定 |
| 繁简转换 | OpenCC 词表改写回复文本 | `onText` + `holdBack`；`gateway.response:rewrite` | 按需 |
| Word Guard 规则包 | 预置规则（密钥格式、证件号），回复侧也遮蔽 | 扩展现有插件，不新建 | 按需 |

### 3.2 观测与运营（`events` / `data.*` / `jobs` / `ui.slots` / `notifications`）

| 插件 | 做什么 | 扩展点 / 权限 | 阶段 |
|---|---|---|---|
| **Usage Insights**（`usage-insights`） | 概览页一张卡：今天、本月花费，按近 7 天的速度预测月底，月预算进度；越过阈值（默认 80%、100%）各通知一次；按日 / 周把报表（花费、请求数、前几名模型）推到飞书、钉钉、企业微信、Slack、Discord 或通用 JSON webhook | `ui.slots: overview.cards`、`jobs`、`data.requests:read`、`notifications`、`net:{config.webhookUrl}` | P1 |
| **Status Watch**（`status-watch`） | 每 5 分钟读各家 Statuspage 的 `summary.json`（预置 Claude、OpenAI、GitHub、Moonshot、Mistral、Cloudflare，2026-11-16 核实可用；另可填一个自定义地址），概览页显示状态，状态变化时通知 | `jobs`、`ui.slots: overview.cards`、`notifications`、每个预置源一条 `net:`、`net:{config.customUrl}` | P1 |
| **OTel Export**（`otel-export`） | 把每个完成请求的元数据（不含正文）按 OTLP/HTTP JSON 批量发成 span（`gen_ai.*` 语义约定），可加自定义头（Langfuse、Honeycomb、Grafana Cloud 等） | `events: request.completed`、`jobs`（攒批与重试）、`data.requests:read`、`net:{config.endpoint}`、`secrets`（头里的密钥） | P1 |

OTel Export 的已知限制：`http.fetch` 不允许内网与本机地址，所以收集器必须是公网 https 地址；本机的 `localhost:4318` 收集器要等 G4 一类的放宽或核心内置导出。

### 3.3 服务商（`providers`，按需）

候选：Gemini Code Assist（可复用 Antigravity 的 cloudcode-pa 链路）、Windsurf（Devin 已在用 `server.codeium.com`）、
Amazon Q Developer（与 Kiro 共用 AWS OIDC）、通义灵码、百度 Comate、iFlow。动手前逐个确认接口与条款。
Ollama / LM Studio 这类本机服务做不成插件（`http` 禁 loopback），它们本来就是内置模板。

### 3.4 客户端（`clients`，按需）

内置 28 个适配器之外：Goose、Continue、Amp，动手前核对各自的配置文件格式。VS Code 扩展类（Roo、Kilo）的配置不在普通文件里，
`clients` 描述符接不住。

### 3.5 需要新扩展点的（P2 / P3）

| 扩展点 | 插件 | 已有的接缝 |
|---|---|---|
| `alerts.channels`（P2） | Telegram、Bark、ntfy、Server 酱、PushPlus、经 Resend 发邮件 | `WebhookFormatter` 已有 5 种格式；插件接长尾 |
| `routing.judges`（P2） | 规则判定器：按关键字、长度、附件、工具数选档位，零延迟 | `IRoutingJudge` / `RoutingJudges.Register` |
| `mcp.tools`（P3） | 联网搜索（Tavily、博查、Brave）、网页转 Markdown、「Astra 自查」（今天花了多少、哪个模型最便宜，基于 `data.*`） | MCP 网关（联动更新 25） |

### 3.6 需要 `ctx.respond` 或 `gateway.invoke` 的（P3）

响应缓存（确定性请求完全相同时直接命中）、Mock 上游、模型对比 Playground 页（同一 prompt 并排跑几个模型）、
离线评测（一组 prompt × 多个模型，定时跑并打分）。

## 4. 平台改动

### 4.1 P1：补发宿主事件（G1）

| 事件 | 载荷 | 何时 |
|---|---|---|
| `provider.changed` | `{ providerId, change: "created" \| "updated" \| "deleted", name?, enabled? }` | `ProviderRepository` 的增、改、删（含额度快照写入；不含排序与模型行） |
| `account.changed` | `{ accountId, change, providerId?, status?, enabled?, cooldownUntil? }`（`deleted` 时只有前两项） | `ProviderAccountStore` 的增、删、状态 / 启用 / 名称 / 代理 / 冷却 / 当前账号 / 整行更新；**不含**令牌刷新（`UpdateTokensAsync`，太频繁且与凭据有关） |
| `alert.fired` | `{ ruleId, rule, event, state: "firing" \| "resolved", repeat, subjectType, subjectId, subject, message, at }` | `AlertEngine` 决定发通知时（与有没有 webhook 无关） |

- 仓库只发「哪一行、哪种变化」的信号；服务端的 `PluginDataEvents` 按行合并 500 ms 内的连续变化（`deleted` > `created` > `updated`），
  合并后再读一次行填载荷，没有订阅者时什么都不做。事件是「去重读」的信号，不保证每次写入一条。
- 不带密钥、令牌、邮箱；`provider.changed` / `account.changed` 仍要 `data.providers:read`。

### 4.2 P1：补齐 `data.*`（G2，按 02 §10 原设计）

- `data.requests.query`：新增 `id`（取一条）、`token` 过滤；每行新增 `at`（开始时间，ISO 8601）、`stream`、`cacheReadTokens`、
  `cacheWriteTokens`、`reasoningTokens`、`ttftMs`。
- `data.requests.stats`：新增 `groupBy`：`day` | `hour`（返回 `series: [{ bucket, requests, costNanoUsd, inputTokens, outputTokens }]`）
  或 `model` | `provider` | `client` | `token`（返回 `groups: [{ key, label, … }]`，按花费降序），以及 `utcOffsetMinutes`（按本地日界分桶）。
  没有请求的桶不出现。不带 `groupBy` 时与原来相同（另加 `cacheReadTokens` / `cacheWriteTokens`）。`model` 分组与控制台的图表相同：
  关联了目录模型的用目录模型 id，否则用请求的模型名（`RequestRepository.TimeseriesAsync`）。
- `data.requests.query` 的 `provider` / `token` 过滤取 id（`data.providers.list` / `data.tokens.list` 给出），行里同时有名字和 id。
- `request.completed` 的载荷与 `data.requests.query` 的行同形。
- SDK：`index.d.ts` 的类型、测试宿主应答 `data.*`（测试给定）、`runJob` / `deliverEvent` 两个测试助手。

### 4.3 P1 中加上的：`env.locale` / `env.timeZone` / `env.utcOffsetMinutes`

见 §6。读取时取当前值（getter），因为设置可以改、夏令时会变。

### 4.4 P2 / P3

- P2：`alerts.channels`、`routing.judges` 两个扩展点；`command.palette` 的显示处。
- P3：`ctx.respond(status, body)`（钩子直接回复；要定流式时怎么写出、计费与请求日志怎么记）、`gateway.invoke`（插件以一个令牌调用本机网关，
  受该令牌的模型与额度约束）、`mcp.tools`、插件间 API。
- 以后：插件账号进后台额度同步（§1.3 的更正）。

## 5. 分期

| 阶段 | 平台 | 插件 |
|---|---|---|
| **P1** | §4.1 事件、§4.2 `data.*` 与 SDK | Usage Insights、Status Watch、OTel Export、Context Trim、Tool Policy |
| P2 | `alerts.channels`、`routing.judges`、命令面板 | 通知渠道包、规则判定器 |
| P3 | `ctx.respond`、`gateway.invoke`、`mcp.tools` | 缓存、Mock、Playground、评测、MCP 工具包 |
| 按需 | — | 服务商、客户端 |

## 6. P1 实施记录（2026-11-16）

平台（本仓库）：

- §4.1 的三个事件：`ProviderRepository.Changed` / `ProviderAccountStore.Changed`（`RowChanges`）→ `PluginDataEvents`（合并 500 ms、读行填载荷）；
  `AlertEngine.Fired` → `alert.fired`。测试：`PluginV2HostEventsTests`。
- §4.2 的 `data.*`：`PluginRequestRows`（`data.requests.query` 的行与 `request.completed` 的载荷同形）、`requests.stats` 的 `groupBy`。
- 新增 §4.3：`env.locale`、`env.timeZone`、`env.utcOffsetMinutes`（`IPluginV2Services.Locale` / `UsageZone`）。起因：沙箱的 `Date` 是 UTC、
  任务和事件里没有观看者，「今天」「本月」和通知语言都会错。`boot.js` 在任务、事件里用 `env.locale` 翻译。
- 修复：本机有 HTTP 代理（`HTTPS_PROXY=http://127.0.0.1:…`，国内常见）时，所有 v2 插件的 `http.fetch` 都失败（「127.0.0.1 resolves to a
  private address」）。https 请求经代理时建立的是到代理的 CONNECT，私有地址检查拦住了代理本身。现在插件的 HTTP 客户端走
  `SystemProxy.Dynamic`（与网关相同，遵守设置里的代理模式），到代理本身的那一跳放行（`PluginV2HostServices.IsProxyHop`；目标地址仍在
  `HostCalls` 里逐跳检查）。这同样影响已有 provider 插件的登录流程。
- SDK：类型（`RequestRow`、`RequestStats`、三个事件载荷、`env`）、测试宿主应答 `data.*`、`runJob` / `deliverEvents`、`locale` / `timeZone`
  选项；每次运行前清掉上一次 activate 注册的监听（之前会累积）；`validate` 知道订阅 `request.completed` 等要 `data.*` 权限。

插件（社区仓库）：Usage Insights、Status Watch、OTel Export、Context Trim、Tool Policy，各有 SDK 测试；另在真实服务端（Jaunt 沙箱）里装上、
授权、打开卡片、跑命令，Status Watch 经本机代理读到了真实状态页，Context Trim / Tool Policy 经真实网关管线验证过改写与拒绝。

与草案不同之处：

- 「单次请求成本拆解」删掉（请求详情已有计费说明）；月预算与报表合成一个插件（Usage Insights）。
- Context Trim 的估算必须交给正则：逐字符的 JS 循环在 2.4 MB 的请求上超过请求钩子的 250 ms 时限（`MiddlewareEngine.RequestLimit`）。
  另有快速路径：请求的 JSON 长度不超过预算时不估算（每个字符至多一个 token）。
- Usage Insights 的 webhook 地址是加密设置：平台 webhook 的源写死在 `net:` 里，只有通用 JSON 需要 `net:{config.customOrigin}`（占位符只读
  非加密设置）。

待办：

- 社区仓库所有 v2 插件（含这五个）都写 `engines.astra: ">=0.15.0"`，当前 Astra 是 0.14.3110，所以在已发布的版本上都装不上（「needs Astra
  >=0.15.0」）；这五个确实依赖本次平台改动，要等含它们的版本发布。版本号怎么定由发版决定。
- `OTel Export` 只按 OTLP/HTTP JSON 发送，没有对 Langfuse、Honeycomb 逐个实测。

