# 02 能力 API

插件通过虚拟模块 `astra` 使用宿主能力。每个命名空间对应一组宿主调用（01 §5），由权限控制是否可用。

```js
import { definePlugin, gateway, storage, http, ui, commands, events, log, t } from 'astra';
```

## 1. 通用约定

- **全部异步**：宿主调用返回 promise（少数纯本地的函数除外，如 `t()`、`ui.*` 构建函数）。
- **只传 JSON**：参数与结果都是可序列化的值；二进制用 `Uint8Array`（桥上以 base64 传输）。
- **错误**：拒绝的 promise 带 `AstraError { code, message, details? }`。通用 `code`：`permission_denied`、`not_supported`、
  `invalid_argument`、`not_found`、`rate_limited`、`timeout`、`quota_exceeded`、`unavailable`。
- **释放**：登记类调用（`on`、`register`）返回 `{ dispose() }`；放进 `ctx.subscriptions` 的会在停用时自动释放。
- **特性探测**：

  ```js
  astra.version              // 宿主版本 "0.15.0"
  astra.api                  // API 版本 "2.0"
  astra.has('gateway.hooks') // 扩展点或命名空间是否存在且已授权
  astra.has('gateway.hooks@2')   // 至少是该版本
  ui.supports('Heatmap')     // 当前会话的壳是否支持某组件
  ```

## 2. 扩展点一览

| 扩展点 id | 作用 | 上下文 | 阶段 |
|---|---|---|---|
| `configuration` | 设置模式 → 自动表单、校验、`astra.config` | — | 2.0 |
| `commands` | 具名命令（界面、菜单、CLI 可调用） | main | 2.0 |
| `ui.pages` / `ui.settings` / `ui.slots` / `ui.menus` | 界面（03） | main | 2.0 |
| `gateway.hooks` | 请求 / 上游 / 流事件 / 响应 / 完成 钩子 | gateway | 2.0 |
| `jobs` | 定时与后台任务 | main | 2.0 |
| `events` | 声明插件发出的事件（供其他插件订阅） | main | 2.0 |
| `providers` | 服务商：模型、登录、请求准备、用量（替代 v1 provider 插件） | main | 2.1 |
| `clients` | 客户端适配：把网关接入某个本地 AI 客户端的配置（替代 v1 agent 插件） | main | 2.1 |
| `ui.flows` | 分步流程（登录向导） | main | 2.1 |
| `mcp.tools` | 经 MCP 网关提供工具 | main | 之后 |
| `routing.judges` | 智能路由的判定器 | main | 之后 |
| `alerts.channels` | 告警通知渠道 / 格式 | main | 之后 |

「之后」的几项只是说明注册表能容纳它们，不在本设计里展开。

## 3. `gateway` — 网关钩子

清单：

```jsonc
"gateway.hooks": [{
  "id": "guard",
  "on": ["request", "event", "response", "upstream", "complete"],
  "view": "ir",                       // "ir" | "raw"，见下
  "match": { "protocols": ["anthropic", "chat"], "models": ["claude-*"], "clients": ["claude-code"], "stream": true },
  "events": ["content_block_delta"],  // 只对这些事件类型调用 onEvent（raw 视图）
  "reads": ["words"]                  // 放进 ctx.shared 的 storage 键（01 §3）
}]
```

`match` 由宿主在调用前判断：不匹配的请求根本不进沙箱，没有钩子匹配时不取引擎（保持「没有插件的网关零开销」）。

代码（`gateway` 入口）：

```js
import { gateway } from 'astra';

export default gateway.defineHooks({
  guard: {
    onRequest(req, ctx) {
      // ctx: { protocol, model, stream, path, client, token: { id, name }, options, shared, state, requestId }
      if (hasSecret(req)) return ctx.reject(400, 'has secret');   // 需要 gateway.request:reject
      req.system.push({ type: 'text', text: 'Be concise.' });     // 需要 gateway.request:rewrite
      return req;                                                 // undefined = 不改
    },
    onUpstream(req, ctx)  { ctx.setHeader('x-acme', '1'); },     // 每次上游尝试；ctx.upstream = { protocol, provider, model }
    onEvent(ev, ctx)      { return ev; },                         // null = 丢弃该事件
    onResponse(res, ctx)  { return res; },
    onComplete(info, ctx) { /* info: { status, usage, durationMs, error? }；只读，结果被忽略 */ },
  },
});
```

**两种视图**（决定 D4）：

- `raw`：客户端所用协议的原始 JSON（v1 的语义）。插件要自己处理四种协议。
- `ir`：协议无关的**规范视图**——消息、系统提示、工具、采样参数、流增量的统一形状，从网关的 IR
  （`src/Astra.Gateway/Protocol/Ir.cs`）投影出来，改写后写回。插件写一次，四种协议都生效。
  规范视图是独立定版本的公开契约（`gateway.hooks` 扩展点的版本），不是内部 IR 的原样暴露；IR 里有损或协议特有的部分
  以 `extra` 透传，插件不应改动。

语义（沿用 v1 已验证的规则）：

- 返回 `undefined` 保持原样；返回对象替换；`onEvent` 返回 `null` 丢弃事件；其他返回值算失败。
- 钩子抛错或超时：输入原样放行，计入失败统计。
- `ctx.reject` 只有第一次生效，之后链停止；状态码必须在 400–599。
- `ctx.state` 是这一次请求内、同一插件各钩子共享的对象。
- 执行位置不变：`request` 在隐私守卫之前；`upstream` 在每次尝试、已翻译且已过守卫的请求体上；插件注入的内容仍会经过守卫。
- 多个插件按用户排的顺序执行；v1 中间件和 v2 钩子在同一条链里按列表顺序排（07 §2）。

`gateway` 上下文可用的 API 只有：`ctx` 上的方法、`ctx.shared`（清单 `reads` 声明的 `storage` 键和设置的同步只读快照，
01 §3）、`ctx.emit` / `ctx.count`（异步投递，不等待）、`log`、`crypto`。没有 `http`、没有 `ui`、没有异步的 `storage`——
热路径上不允许不可控的等待，钩子因此可以是同步函数。需要外部数据的钩子在 `main` 里后台拉取、写进 `storage`，钩子从
`ctx.shared` 读。

流式文本的便利钩子：`onText(delta, ctx)` 只在文本增量上调用（`ir` 视图），返回改写后的文本或 `undefined`；
宿主负责跨事件的切分缓冲（`ctx.holdBack(n)` 声明需要保留的尾部字符数，用于跨增量匹配）。大多数「改写 / 过滤输出」
类插件只需要它，不必处理各协议的事件结构。

## 4. `storage` / `secrets` / `config`

```js
await storage.get(key)               // → 值 | undefined
await storage.set(key, value)        // 值是 JSON
await storage.delete(key)
await storage.list({ prefix, limit, cursor })   // → { items: [{ key, value }], cursor? }
await storage.update(key, fn)        // 读-改-写，宿主保证对同一个 key 串行

await secrets.get(name) / secrets.set(name, value) / secrets.delete(name)   // 经 ISecretProtector，永不出现在日志和 API 里

config.get()                         // 当前设置值（按 configuration 模式，带默认值）
config.onChange(fn)
```

存储按插件 id 隔离。卸载插件时默认清除，升级保留。

## 5. `http`

```js
const res = await http.fetch('https://api.acme.example/v1/x', {
  method: 'POST', headers: { … }, body: JSON.stringify(payload), timeoutMs: 10000,
});
res.status; res.headers; await res.json(); await res.text(); await res.bytes();
```

- 只能访问 `net:` 权限里列出的源；重定向到列表外的源算失败。只有 https（开发模式下允许本机 http）。
- 请求由宿主的 `HttpClient` 发出，走 Astra 的代理设置；不带任何 Astra 的头或凭据。
- 响应体有大小上限；流式读取（`res.stream()`）放在 2.1，随 `providers` 一起做。
- 不允许访问 loopback、链路本地和内网地址（解析后检查），防止把插件当成访问本机服务的跳板。

## 6. `commands`

```js
commands.register('clear', async (args, ctx) => { … return result; });
await commands.execute('acme.word-guard.clear', args);   // 也可以调宿主公开的命令
```

命令是插件功能的统一入口：`ui.menus` 的菜单项、UI 动作、CLI（`astra plugin run <plugin> <command> [--json args]`）、
以后的快捷键都指向命令。命令在清单里声明标题后才会出现在界面上。

## 7. `events`

```js
events.on('request.completed', (e) => { … });    // 宿主事件，需要对应的 data 权限
events.on('acme.other-plugin:something', fn);     // 其他插件声明过的事件
events.emit('hit', payload);                      // 自己的事件，全名是 <pluginId>:hit
```

首批宿主事件：`request.completed`（元数据，不含正文）、`provider.changed`、`account.changed`、`settings.changed`、
`plugin.configChanged`、`alert.fired`。事件是尽力投递、不保证顺序跨类型，处理函数受 `main` 的时限约束。

## 8. `jobs`

```jsonc
"jobs": [{ "id": "sync", "every": "6h" }, { "id": "daily", "cron": "0 9 * * *" }]
```

```js
jobs.handle('sync', async (ctx) => { … });   // ctx: { scheduledAt, attempt }
await jobs.runNow('sync');
```

宿主负责调度、错过补跑一次、失败退避；任务有总时长上限。服务没运行时不触发。

## 9. `configuration` — 设置

清单里的设置模式（JSON Schema 的一个子集，加展示提示）：

```jsonc
"configuration": {
  "properties": {
    "words":   { "type": "array", "items": { "type": "string" }, "title": { "zh": "敏感词", "en": "Words" }, "default": [] },
    "action":  { "type": "string", "enum": ["reject", "mask"], "enumTitles": { … }, "default": "reject", "widget": "segmented" },
    "apiKey":  { "type": "string", "secret": true, "title": "API Key" },
    "timeout": { "type": "integer", "minimum": 100, "maximum": 5000, "default": 1000, "unit": "ms" }
  },
  "groups": [{ "title": { "zh": "规则", "en": "Rules" }, "properties": ["words", "action"] }]
}
```

- 宿主据此在两端生成设置表单（用的就是 03 的组件），校验后写入 `plugins.options_json`；`secret: true` 的字段进 `secrets`。
- 多数插件因此不需要写任何 UI 代码。需要更复杂的设置界面时贡献 `ui.settings` 视图。

## 10. `data` — 只读视图

按权限提供宿主数据的只读查询，形状是为插件单独定义的（不是管理 API 的 DTO，后者可以自由演进）：

| 调用 | 权限 | 内容 |
|---|---|---|
| `data.requests.query({ from, to, filter, limit, cursor })` | `data.requests:read` | 请求元数据：时间、模型、状态、用量、耗时、客户端。不含正文、不含 IP |
| `data.requests.stats({ from, to, groupBy })` | 同上 | 聚合 |
| `data.providers.list()` | `data.providers:read` | id、名称、协议、状态；没有密钥 |
| `data.models.list()` | `data.models:read` | 模型与价格 |
| `data.tokens.list()` | `data.tokens:read` | id、名称、状态、掩码 |

金额一律是纳美元整数。

## 11. 杂项

| 命名空间 | 内容 |
|---|---|
| `log` | `log.debug/info/warn/error(message, fields?)`；限流；在插件页的日志里可见 |
| `t` / `i18n` | `t(key, params)`；`i18n.locale` |
| `notifications` | `notifications.send({ title, body, tone })`：没有打开界面时也能到达（控制台通知、桌面系统通知） |
| `crypto` | `sha256`、`hmac`、`randomUUID`、`randomBytes`（沙箱里没有 Web Crypto） |
| `env` | `{ platform, astraVersion, plugin: { id, version }, dev }` |
| `timers` | `setTimeout` / `setInterval`（只在 `main`，受最小间隔限制） |
| `permissions` | `permissions.has(p)`、`permissions.request([…])`（运行时申请，01 §6） |
| `ui.files` | `ui.files.read(ref)`：读用户通过 `pickFile` / `FileDrop` 交来的文件内容（04 §5） |

## 12. `providers` 与 `clients`（2.1，方向）

只写方向，细节在 2.1 单独成文：

- **`providers`**：清单声明 provider（id、名称、图标、协议、登录方式）。代码提供 `models()`、`prepare(request, account)`
  （返回上游地址、头和可选的请求体改写；凭据由宿主从 `secrets` 取出并注入，不经过客户端）、`refresh(account)`、
  `usage(account)`。登录用 `ui.flows` 视图（API key 表单、设备码、粘贴授权码）。请求仍由宿主发出，所以故障转移、代理、
  账号调度、计费全部复用现有流水线——这替代了 v1 的 Bun 宿主进程和 `plugin://` 通道。
- **`clients`**：清单声明客户端描述（id、配置文件、格式、可执行文件名）。代码提供 `connect(input)` 返回要写的键值。
  写入仍然全部经过 `ClientConfigApplier`，备份、漂移检测、离线 `restore-all` 的不变量不变。

## 13. 版本与演进规则

- `engines.api` 写 semver 范围。宿主的 API 版本主版本不同则拒绝加载，并说明需要的 Astra 版本。
- 同一主版本内：只新增调用、扩展点、组件、可选字段；不改已有字段的含义；不删除。
- 每个扩展点和 UI 协议各有自己的版本号，用 `astra.has('x@n')` / `ui.supports()` 探测。
- 废弃：标记后至少保留一个次版本周期，加载时在插件页给开发者警告。
- 试验性 API 放在 `astra.experimental.*`，清单里要声明 `"experimental": ["…"]` 才可用，不受兼容承诺约束，
  市场上架的插件不得使用。
