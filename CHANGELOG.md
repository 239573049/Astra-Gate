# 更新日志

本项目的所有重要变更都记录在此文件中。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

发布流程会读取对应版本的小节，作为 GitHub Release 说明、官网下载页说明和应用内更新提示。

## [0.3.0] - 2026-10-09

### 新增

- **新增 10 个客户端**：Crush、Qwen Code、Droid、Kimi Code、Zed、VS Code Insiders、VSCodium、omp（oh-my-pi）、MiMo Code、DeepSeek Harness（官方桌面应用 / `dsh web`，写入 profile 的 `cordis.patch.yml` 与 `~/.dsh/.env`）可由 Astra 接管（写入前备份、禁用时还原、跳过你后来改过的值），并会随提供商的模型增减同步模型列表；Zed 不会从设置文件读取密钥，启用后需在 Zed 里粘贴一次令牌（卡片有提示）。Crush、Qwen Code、Droid、Kimi Code、MiMo Code 以 npm 包分发，可在客户端页安装、检查并更新；Zed、VS Code Insiders、VSCodium、omp、DeepSeek Harness 只显示版本与下载主页，Astra 不会替你运行安装命令。
- **新增 WorkBuddy 客户端**：在 `~/.codebuddy/models.json` 里为所绑定提供商的每个模型新增一条自定义模型（显示名 `Astra: <模型>`，不动你已有的模型），WorkBuddy 与 CodeBuddy Code CLI / IDE 都会读取；只显示下载主页，不代为安装。
- **从其他应用导入提供商**：提供商页新增「从其他应用导入」，只读扫描 CC Switch、Alma、Claude Code、Codex、Magpie 的本地配置并预览（`GET /api/providers/import/sources`），勾选后由服务端重新读取并创建提供商（`POST /api/providers/import`）。API Key 不经过浏览器，预览只返回遮掩值；会自动匹配内置模板、合并同一中转的多个端点，并识别已存在的提供商。CLI 同步新增 `astra provider import [--from <来源>] [--yes] [--only <ref>…]`。不导入订阅 / OAuth 登录、路由、限流、余额与会话数据，也不改动任何客户端配置。
- **订阅账号切换**：同一提供商可绑定多个订阅账号（OAuth），支持启用 / 停用、指定当前账号与排序；触发冷却的账号在冷却期内不再被选用，请求自动落到其余可用账号，每条请求会记录实际服务的账号。
- **模型级上游协议**：模型可声明仅支持某一种上游 API；来自其它协议的请求会先转换成该协议再转发，而不是原样透传。
- **出站代理**：设置页新增「网络代理」，支持跟随系统（默认，读 `HTTPS_PROXY` / `HTTP_PROXY` / `ALL_PROXY` 与操作系统代理）、自定义（`http` / `https` / `socks5`，可带用户名密码与「不走代理的地址」）与不使用三种模式；保存即生效，代理密码加密保存，本机地址（如 Ollama、LM Studio）始终直连。
- **请求页实时刷新**：请求日志改为实时推送，请求一到达即出现在列表中并原地更新状态（等待首 token、TTFT 与输出速度），无需手动刷新。
- **请求日志实时速率**：日志页头部新增 RPM / TPM / 缓存命中率读数，统计最近一个窗口（默认 60 秒，可在 10–600 秒间调整）的平均每分钟请求数与令牌数，跟随当前筛选、每 5 秒自动刷新；进行中的请求计入 RPM，TPM 与缓存命中率只统计已完成请求。
- **Docker 镜像**：发布到 `ghcr.io/239573049/astra-gate`（`linux/amd64` + `linux/arm64`，标签 `latest`、`x.y.z`、`x.y`），内含服务端与管理控制台，复用与 npm 平台包相同的构建产物。仓库根目录新增 `Dockerfile` 与 `compose.yaml`，发布流程会在推送前对镜像做冒烟测试。
- 服务端新增环境变量配置：`ASTRA_HOST`、`ASTRA_PORT`、`ASTRA_ADMIN_PASSWORD`（或 `ASTRA_ADMIN_PASSWORD_FILE`）、`ASTRA_PUBLIC_URL`、`ASTRA_STRICT_PORT`。优先级为命令行参数 > 环境变量 > `config.json`；密码仅在内存中哈希，不会写入配置文件。
- `ASTRA_STRICT_PORT`：端口被占用时直接退出，而不是自动换到下一个端口，避免容器的端口映射悄悄失效。
- `ASTRA_PUBLIC_URL`：指定对外可访问的网关地址，控制台与客户端配置不再显示容器内的 `127.0.0.1`。
- `astra-server healthcheck` 子命令：请求 `/api/health`，健康时退出码为 0，供 Docker `HEALTHCHECK` 使用。

### 变更

- **服务端以 Native AOT 发布**：`astra-server` 变为自包含的原生可执行文件（约 30 MB），目标机器无需安装 .NET 运行时，启动更快；JSON 序列化与 SQLite 访问全部改为编译期源生成。npm 平台包、桌面安装包与自动更新流程会同步携带原生 SQLite 伴生库（`libe_sqlite3`）。

## [0.2.3] - 2026-10-08

### 修复

- 管理控制台点击"检查更新"后，更新卡片可能显示为空或缺少当前版本、可用版本、发布渠道等字段：检查接口（`POST /api/update/check`）的返回结构与状态接口不一致，而控制台会把检查结果直接缓存为状态。现两个接口返回相同的结构，手动检查完成后立即显示完整信息，无需等待下一次自动刷新。

### 文档

- 新增项目 README（英文与简体中文），介绍安装、快速开始与功能特性。

## [0.2.2] - 2026-10-07

### 新增

- **桌面端常驻托盘**：关闭窗口后应用不再退出，而是在后台继续运行，窗口同时从 macOS 程序坞 / Windows 与 Linux 任务栏中隐藏，只保留菜单栏 / 托盘图标；首次关闭时会提示一次。退出请使用托盘菜单中的"退出 Astra"（macOS 也可按 ⌘Q）。
- **重新设计的托盘菜单**（中英双语）：
  - 显示版本与服务状态（启动 / 停止过程中实时提示）、API 版本不匹配与可用更新；
  - 打开主窗口，或直接前往概览、请求日志、客户端、提供商、模型、设置；
  - 按客户端快速切换所绑定的提供商，并显示当前绑定；
  - 启动 / 停止 / 重启服务、复制 API 地址、在浏览器中打开控制台、打开日志目录、检查更新；
  - macOS / Windows 新增"登录时启动"，开机自动启动时只驻留托盘、不弹出窗口。
- macOS 使用随系统明暗自动着色的菜单栏图标；Windows 单击托盘图标打开窗口，右键打开菜单。
- 官网下载页会根据访问者的系统与架构推荐对应的安装包。

### 修复

- macOS 安装包版本在窗口打开时无法完成"重启以安装"自动更新。
- 桌面端启动器改为安装到用户目录（`~/Applications`、`~/.local/share/applications` 等），卸载时会一并清理 0.2.1 及更早版本写入的启动器。
- 通过 `npx` 安装桌面端时，npm 12 因继承的环境变量拒绝执行内部 npm 命令。
- 发布时等待各平台包在 npm 上可安装后再发布 CLI，避免 `@latest` 安装时缺少服务端依赖。

## [0.2.1] - 2026-10-07

0.2.0 的桌面端存在版本号错误，请直接升级到本版本。

### 新增

- **更多客户端接管**：新增 Pi、Hermes Agent、MiniMax Code、Copilot CLI 与 VS Code Copilot。
  - Pi、MiniMax Code 会列出所绑定提供商的模型并保持同步；
  - Hermes Agent 切换 `config.yaml` 的模型配置；
  - Copilot CLI 与 VS Code Copilot 以独立条目写入，不改动你已有的配置。

### 修复

- 桌面端（安装包与 npm 包）内部版本号错误地显示为 0.1.0，导致自动更新反复提示、服务端更新检查出错。
- 官网下载页的安装包被截断为 10 MB，无法正常安装。

## [0.2.0] - 2026-10-07

首个公开发布版本。

### 新增

- **本地 AI 网关**：一个本机服务统一接入 OpenAI Chat、OpenAI Responses、Anthropic Messages 与 Gemini 四种协议，请求可在协议间互相转换。
- **客户端接管**：一键把 Codex、Claude Code、Gemini CLI、OpenCode、Claude Desktop、Grok Build 指向 Astra；接管前自动备份，卸载或 `astra restore-all` 可完整还原。
- **管理控制台**：中英双语 Web 界面，管理提供商、模型与客户端，查看请求记录与用量。
- **命令行 `astra`**：启动 / 停止服务、开机自启、客户端与提供商管理、更新与卸载。
- **桌面端**：Electron 应用，内置控制台、菜单栏状态与服务生命周期管理。
- **自动更新**：服务端更新采用"下载 → 校验 → 切换 → 重启"，失败自动回滚；控制台界面资源随服务端一起更新。

### 安装方式

- npm：`npm install -g @aidotnet/astra-gate`（`astragate` 为同一个包的另一名称）。
- 桌面端一条命令安装：`npx @aidotnet/astra-gate install --desktop`；`install` / `update` / `uninstall` 新增 `--desktop`，`--client` 保留为别名。
- 安装包：官网下载页提供 macOS `.dmg`、Windows 安装程序（`.exe`）与 Linux `AppImage`，内置服务端，无需 Node.js。
- 支持 macOS、Windows、Linux 的 x64 与 arm64。

### 已知限制

- Windows / Linux 安装包版本暂不支持应用内自动更新桌面端，请从下载页下载新版覆盖安装。
- Windows 安装程序暂未代码签名，首次运行可能出现 SmartScreen 提示。
