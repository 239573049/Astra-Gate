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
01 §3）、`ctx.emit` / `ctx.count`（异步投递，不等待）、`log`、`crypto` / `encoding` 以及 §11.2 的沙箱全局。没有 `http`、没有 `ui`、没有异步的 `storage`——
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
res.status; res.headers; res.setCookies; await res.json(); await res.text(); await res.bytes();
```

- 只能访问 `net:` 权限里列出的源；重定向到列表外的源算失败（`permission_denied`）。只有 https（开发模式下允许本机 http）。
- 请求由宿主的 `HttpClient` 发出，走 Astra 的代理设置；不带任何 Astra 的头或凭据。
- 响应体有大小上限；流式读取（`res.stream()`）放在 2.1，随 `providers` 一起做。
- 不允许访问 loopback、链路本地和内网地址（解析后检查），防止把插件当成访问本机服务的跳板；重定向的每一跳都重新检查。
- `redirect`：`'follow'`（默认，最多 5 跳，每一跳都要过 `net:` 和内网检查）、`'manual'`（不跟随：直接返回 3xx 的 `status`、
  `headers.location` 和 `setCookies`，`Location` 不检查也不请求，由插件自己决定下一步）、`'error'`（遇到重定向就失败，
  `redirect_refused`）。Mimo 一类的 SSO 登录用 `manual` 一跳一跳走，自己带上 `cookie`。
- Cookie：请求头 `cookie` 可以发（只发到插件持有 `net:` 的源，本来任意请求头就可以带）；响应的每一行 `Set-Cookie` 原样放在
  `res.setCookies`（字符串数组；`res.headers` 里没有 `set-cookie`，因为它不能用逗号拼起来）。宿主**没有** cookie jar，
  不替插件保存、也不随请求自动带：要用的值自己放进 `storage` / 下一次请求头。`follow` 时中间跳的 `Set-Cookie` 看不到，
  需要它们就用 `manual`。
- 凭据不跨源：`cookie`、`authorization`、`proxy-authorization` 只发给最初请求的源；重定向一旦离开那个源，这三个头就从这一跳起
  永久丢弃（即使之后又绕回原来的源）。其他头（插件自己写的）照常带。同源重定向保留它们。

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
| `crypto` / `encoding` | 摘要、HMAC、AES、RSA、ECDSA、PBKDF2、HKDF 和 base64 / hex 编码，见 §11.1；沙箱里没有 Web Crypto |
| `env` | `{ platform, astraVersion, plugin: { id, version }, dev }` |
| `timers` | `setTimeout` / `setInterval`（只在 `main`，受最小间隔限制） |
| `permissions` | `permissions.has(p)`、`permissions.request([…])`（运行时申请，01 §6） |
| `ui.files` | `ui.files.read(ref)`：读用户通过 `pickFile` / `FileDrop` 交来的文件内容（04 §5） |

### 11.1 `crypto` 与 `encoding`

纯计算，在宿主里用 `System.Security.Cryptography` 做（不用反射，AOT 安全）。**不需要任何权限**，网关钩子里也能调用。输入上限
8 MiB；密钥或尺寸不对抛 `AstraError('invalid_argument')`。数据参数可以是字符串（UTF-8）、`Uint8Array` / `ArrayBuffer`，或
`{ base64 }` / `{ hex }`；结果由 `encoding` 选：`'hex'` | `'base64'` | `'base64url'` | `'utf8'`（仅解密）| `'bytes'`（`Uint8Array`）。
默认：摘要 / HMAC / PBKDF2 / HKDF 为 hex，加密和签名为 base64，解密为 bytes。

```js
crypto.digest('md5' | 'sha1' | 'sha256' | 'sha384' | 'sha512', data, { encoding })   // sha256(data) 仍是 hex
crypto.hmac(key, data, { algorithm: 'sha256', encoding })                            // hmac(key, data, 'sha1') 仍可用
crypto.aes.encrypt({ mode: 'cbc' | 'ecb' | 'gcm', key, iv, data, aad?, padding?: 'pkcs7' | 'none' })  // gcm：密文后附 16 字节 tag
crypto.aes.decrypt({ mode, key, iv, data, aad?, tag?, encoding: 'utf8' })
crypto.rsa.encrypt({ key: pem, data, padding: 'pkcs1' | 'oaep', hash? })              // 公钥：PEM（SPKI / PKCS#1）或 base64 DER，512–8192 位
crypto.sign({ algorithm: 'rsa-sha256' | 'ecdsa-p256-sha256' | …, key: pem, data, format?: 'der' | 'raw' })  // 私钥：PKCS#8 / PKCS#1 / SEC1
crypto.verify({ algorithm, key: publicPem, data, signature: { base64 } })
crypto.pbkdf2({ password, salt, iterations, length, hash })    // iterations ≤ 1,000,000，length ≤ 1024
crypto.hkdf({ key, salt?, info?, length, hash })
encoding.base64 | base64url | hex   // .encode(bytes | string) → string，.decode(text) → Uint8Array
encoding.utf8.encode(text) / .decode(bytes)
```

- AES 密钥 16 / 24 / 32 字节；CBC 的 iv 16 字节，GCM 的 iv 12 字节；`ecb` 只为对接老接口而留。`key` / `iv` 给字符串时按 UTF-8 取字节
  （Qoder 一类把 16 个字符的字符串直接当密钥和 iv 用）。
- RSA 签名：`rsa-sha1|256|384|512`（PKCS#1 v1.5）、`rsa-pss-sha256`（盐长 = 摘要长度）；ECDSA：`ecdsa-p256-sha256`、
  `ecdsa-p384-sha384`，输出默认 DER，`format: 'raw'` 给 r‖s。
- **MD5 和 SHA-1 只为对接仍在使用它们的厂商接口**，不要用于新的安全设计。
- 没有 `Buffer`（它不是 Web 全局）：字节与文本互转用 `encoding`，bytes 用 `Uint8Array`。
- Node 测试环境（`@astragate/plugin-sdk`）用 `node:crypto` 实现同一组调用、同样的默认值和错误码，测试里与真实沙箱算出同样的字节
  （同一批已知答案向量在 xunit 与 vitest 里各跑一遍）。

### 11.2 沙箱全局

Jaunt 引擎本身带 ECMAScript 内建（`Map`/`Set`/`Proxy`/`Reflect`/`BigInt`/`Intl`/`Atomics`、`Uint8Array.prototype.toHex` 等、
`escape`/`encodeURIComponent`）和 `console`、`setTimeout`。探测（真实引擎）显示它**没有** `TextEncoder` / `TextDecoder`、`atob` /
`btoa`、`URL` / `URLSearchParams`、`structuredClone`、`queueMicrotask`、`AbortController`、`Headers`、`fetch`、`Buffer`、`crypto`。
`V2/Runtime/globals.js` 在 boot 之前补上下面这些（引擎已有的全局不会被覆盖）；UTF-8 和 base64 这两条热路径走宿主调用
（`encoding.*`，同步、无权限、钩子里也能用），不用 JS 循环：

| 全局 | 说明 |
|---|---|
| `TextEncoder` | `encode`、`encodeInto`；只有 UTF-8 |
| `TextDecoder` | `new TextDecoder('utf-8' \| 'utf8', { fatal, ignoreBOM })`、`decode(bytes, { stream })`；`fatal` 时坏序列抛 `TypeError`，否则换成 U+FFFD；其他编码 `RangeError` |
| `atob` / `btoa` | 按规范，错误抛 `DOMException`（`InvalidCharacterError`）；`DOMException` 也一并提供 |
| `URL` / `URLSearchParams` | WHATWG 基本行为：解析、相对解析、特殊协议默认端口、IPv4 / IPv6 / 国际化域名（punycode）、各百分号编码集、`searchParams` 与 `search` 双向联动、`canParse` / `parse`、各 setter；没有 `blob:` 对象 URL 的创建 |
| `structuredClone` | 基本类型、`Date`、`RegExp`、`Map`、`Set`、数组、对象、`ArrayBuffer` / 类型化数组、`Error`、循环引用；函数和 `Symbol` 抛 `DOMException('DataCloneError')`；不支持 transfer |
| `queueMicrotask` | 用 `Promise` 实现 |

仍然没有：`Buffer`、`fetch`（用 `http.fetch`）、`AbortController`、`Headers`、`Web Crypto`（用 `crypto`）。

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
