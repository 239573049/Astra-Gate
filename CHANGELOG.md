# 更新日志

本项目的所有重要变更都记录在此文件中。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

发布流程会读取对应版本的小节，作为 GitHub Release 说明、官网下载页说明和应用内更新提示。

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
