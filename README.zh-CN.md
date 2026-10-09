# Astra

[English](README.md) | 简体中文

[![LINUX DO · 新的理想型社区](https://cdn3.ldstatic.com/original/4X/d/6/5/d65def8cc0c413f318bee2bcd1c774bc4ad109a8.png)](https://linux.do/)

**Astra** 是一个运行在你自己电脑上的 **本地 AI 网关**（local AI gateway）。它在你的 AI
客户端（Codex、Claude Code、Gemini CLI 等）与 AI 服务商（Anthropic、OpenAI、DeepSeek、
OpenRouter 等）之间放了一个本机中转层：

```
Codex ────────┤                                       ├─ Anthropic
Claude Code ──┤   Astra 网关 (127.0.0.1:17321)        ├─ OpenAI
Gemini CLI ───┤   协议转换 · 计费 · 隐私护栏           ├─ DeepSeek
OpenCode ─────┘                                       └─ OpenRouter / 自定义…
```

## 功能特性

- **一键接管客户端** — 不用手动改 `~/.codex/config.toml`、`~/.claude/settings.json`
  等配置文件。在控制台（或命令行）点一下「启用」，Astra 代写配置并自动备份原文件；
  「禁用」随时完整还原。
- **协议自动转换** — 客户端可以说 OpenAI Chat、OpenAI Responses、Anthropic Messages 或
  Gemini 任意一种协议。Astra 把每个请求解码为统一的中间表示，再按上游协议重新编码，
  客户端与服务商不必同构。
- **用量与费用看得见** — 每次请求的输入 / 输出 / 缓存 / 推理 token 与费用逐项存入本机
  SQLite，控制台提供汇总看板与单次请求的计费明细。
- **隐私护栏** — 请求发往服务商之前，在本机检测 API 密钥、邮箱、内网地址等敏感信息。
  可警告、拦截或脱敏（发出时替换为占位符、返回时还原），原值永不出本机。
- **多账号与订阅** — 同一服务商可添加多个提供商条目与订阅账号，客户端一键切换，
  无需重新配置。
- **本机优先** — API Key 用 DataProtection 加密存储在 `~/.astra/keys`；请求体与费用明细
  保存在本机 SQLite（`~/.astra/astra.db`）。不依赖任何云端账号。

## 安装

需要 Node.js ≥ 18（npm 会安装对应平台的服务端二进制，无需 .NET）。

```bash
npm install -g @aidotnet/astra-gate   # `astragate` 是同一个包的另一个名称
```

更喜欢图形界面？一条命令安装桌面端（Electron：内置控制台、托盘图标、服务生命周期管理）：

```bash
npx @aidotnet/astra-gate install --desktop
```

独立安装包 —— macOS `.dmg`、Windows `.exe`、Linux `.AppImage`，内置服务端、无需
Node.js —— 见[官网下载页](https://astra-gate.si/download)。

支持 macOS、Windows、Linux，各提供 x64 与 arm64 两个架构。

## 快速开始

```bash
astra start        # 默认后台运行，监听 127.0.0.1:17321
astra status       # 确认服务状态
astra open         # 在浏览器中打开管理控制台
```

如果 17321 被占用，Astra 会自动换一个可用端口，并把**实际端口**写进
`~/.astra/runtime.json` —— 控制台与 CLI 始终读取真实端口，脚本里不要写死 17321。

在控制台添加一个提供商（**提供商** → **添加提供商** → 选模板 → 填 API Key →
**测试连接**），然后把任意支持下列协议的客户端指向网关：

```bash
# OpenAI 兼容
curl http://127.0.0.1:17321/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-chat","messages":[{"role":"user","content":"你好，Astra"}]}'

# Anthropic 兼容
curl http://127.0.0.1:17321/anthropic/v1/messages \
  -H "Content-Type: application/json" \
  -d '{"model":"claude-sonnet-4-5","max_tokens":1024,"messages":[{"role":"user","content":"你好，Astra"}]}'

# Gemini 兼容
curl "http://127.0.0.1:17321/gemini/v1beta/models/gemini-2.5-pro:generateContent" \
  -H "Content-Type: application/json" \
  -d '{"contents":[{"parts":[{"text":"你好，Astra"}]}]}'
```

模型名只要存在于模型目录（或在某个提供商下添加过），Astra 就会路由到对应的上游，
协议不一致时自动转换。完整流程见
[docs/guide/first-request.mdx](docs/content/docs/guide/first-request.mdx)。

最后，与其手动给客户端配 base URL，不如让 Astra 代管 —— **客户端** 页点「启用」，
或：

```bash
astra client enable codex --provider deepseek --model deepseek-chat
```

## 支持的客户端

`codex` · `claude-code` · `gemini-cli` · `opencode` · `claude-desktop` · `grok-build` ·
`pi` · `hermes-agent` · `minimax-code` · `copilot-cli` · `vscode-copilot` · `crush` · `qwen-code` ·
`droid` · `kimi-code` · `zed` · `vscode-insiders` · `vscodium` · `omp` · `mimo-code` ·
`deepseek-harness` · `workbuddy`

Zed 不会从设置文件读取 API Key，启用后需在 Zed 的提供商设置里粘贴一次 Astra 令牌。

接管会在首次写入前备份原文件，永久保留首写备份、另保留最近 20 份滚动备份，并且
还原时会跳过你后来自己改过的值。`astra restore-all`（或卸载）可以在服务端不运行的
情况下离线还原全部受管配置。

## 命令行

不带子命令的 `astra` 等价于 `astra start`。

| 命令 | 用途 |
| --- | --- |
| `astra start` | 启动服务（默认后台；`--port`、`--host`、`--foreground`、`--open`） |
| `astra stop` / `restart` / `status` | 管理运行中的服务 |
| `astra logs [-f]` | 查看最近的服务日志 |
| `astra open` | 打开管理控制台 |
| `astra autostart enable\|disable\|status` | 登录时自启（LaunchAgent / systemd 用户服务 / schtasks） |
| `astra provider list\|add\|remove` | 管理上游提供商 |
| `astra client list\|status\|enable\|disable` | 管理客户端接管 |
| `astra config set-password` | 设置管理密码（绑定非回环地址前必须设置） |
| `astra update [--check]` | 更新 Astra（及桌面端），随后重启服务 |
| `astra restore-all [--purge]` | 离线还原全部受管客户端配置 |
| `astra uninstall [--desktop] [--purge]` | 卸载 Astra（还原客户端配置并停止服务） |

## 安全模型

- 服务端默认只监听 `127.0.0.1`。在设置管理密码之前（`astra config set-password`），
  拒绝绑定非回环地址。
- 所有变更类管理 API 都要求 `X-Astra-Admin: 1` 请求头；Host 头按回环白名单校验，
  防 DNS 重绑定。
- 提供商 API Key 用 DataProtection 加密；API 与控制台只显示掩码。密钥与请求体
  永远不会写入日志。

## 数据存放在哪里

所有数据都在 `~/.astra/` 下（可用环境变量 `ASTRA_HOME` 覆盖）：

| 路径 | 内容 |
| --- | --- |
| `config.json` | 启动选项（服务启动前读取） |
| `runtime.json` | 实际生效的 pid / 端口 / API 版本 |
| `astra.db` | SQLite（WAL）：提供商、模型、请求日志、用量与费用 |
| `keys/` | DataProtection 加密的提供商 API Key |
| `update-state.json` | 「下载 → 校验 → 切换 → 重启」更新流程的状态 |

## 桌面端

桌面端（`npx @aidotnet/astra-gate install --desktop`，或独立安装包）用 Electron 壳封装
同一个控制台：托盘 / 菜单栏图标显示服务状态，支持启动 / 停止 / 重启、按客户端切换
提供商、检查更新，以及登录时自启并只驻留托盘。

服务端更新采用分阶段流程 —— 下载 → sha256 校验 → 切换 → 重启，失败自动回滚 ——
绝不原地替换正在运行的二进制。

## 开发

环境要求：[.NET SDK](https://dotnet.microsoft.com/) 10.x（由 `global.json` 锁定）、
Node.js ≥ 18 与 [pnpm](https://pnpm.io) 9.15.0（`corepack enable` 即可）。

```bash
pnpm install                        # 安装 Node 工作区依赖

# .NET 侧（解决方案 Astra.sln）
dotnet build -warnaserror           # 警告即错误
dotnet test --no-build              # xunit，每个 src 项目对应一个测试项目
dotnet run --project src/Astra.Server -- serve   # 直接运行服务端

# Node 侧
pnpm -r --if-present lint           # eslint
pnpm -r --if-present typecheck      # tsc --noEmit
pnpm -r --if-present test           # vitest
pnpm -r --if-present build          # cli、web、docs、desktop、packages

# 只调试单个包，例如
pnpm --filter @aidotnet/web dev     # 管理控制台，http://localhost:5173
pnpm --filter astragate test        # CLI 测试
pnpm --filter @aidotnet/docs dev    # 文档站
```

### 仓库结构

| 路径 | 说明 |
| --- | --- |
| `src/Astra.Core` | 领域逻辑：计费、定价、模型、隐私（零 NuGet 依赖） |
| `src/Astra.Data` | SQLite 持久化（Dapper、WAL）与迁移 |
| `src/Astra.Clients` | 客户端配置适配器与接管 / 还原 |
| `src/Astra.Providers` | 提供商模板 |
| `src/Astra.Gateway` | 网关管道与四种协议编解码器 |
| `src/Astra.Server` | ASP.NET Core 宿主（`astra-server`）、管理 API、安全中间件 |
| `cli/` | `astragate` npm 包（`astra` 命令）及其平台服务端包 |
| `web/` | 中英双语管理控制台，Vite + React |
| `desktop/` | Electron 桌面端 |
| `docs/` | Fumadocs 文档站与官网 |
| `npm/` | 平台包 `@aidotnet/server-*` / `@aidotnet/desktop-*` |
| `packages/update-core` | 共享更新引擎（CLI 与桌面端共用） |
| `tests/` | xunit 测试项目，与 `src` 一一对应 |

协议转换是中心辐射式（hub-and-spoke）：每种线上协议先解码为统一中间表示
（`src/Astra.Gateway/Protocol/Ir.cs`）再重新编码，绝不做两两直转。

## 文档

文档在 [`docs/content/docs`](docs/content/docs)，由 `docs/` 下的站点提供
（`pnpm --filter @aidotnet/docs dev`）。重点页面：
[安装](docs/content/docs/guide/install.mdx)、
[第一个请求](docs/content/docs/guide/first-request.mdx)、
[控制台教程](docs/content/docs/console/index.mdx)、
[CLI 参考](docs/content/docs/cli/index.mdx) 与
[隐私](docs/content/docs/advanced/privacy.mdx)。

## 许可证

MIT。发布说明见 [CHANGELOG.md](CHANGELOG.md)。
