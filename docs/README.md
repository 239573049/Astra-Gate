# @aidotnet/docs

Astra 文档站（[fumadocs](https://fumadocs.dev) + Next.js）。

```bash
pnpm --filter @aidotnet/docs dev     # http://localhost:3000
pnpm --filter @aidotnet/docs build
```

> 构建是完整的 Next.js production build；如果 shell 里导出了非标准的 `NODE_ENV`（如 `development`），请先取消再构建，否则静态导出阶段会报 React hook 错误。

## 模块结构

内容在 `content/docs/`。左侧菜单是**单栏分组侧边栏**：根 `meta.json` 决定分组顺序，每个目录是一个
分组（目录的 `meta.json` 提供 `title` / `description` / `icon` / `defaultOpen`），`pages` 里的
`---[图标名]名称---` 条目是组内小节分隔条。页面 URL 与目录层级一致，调整分组不会改变 URL：

| 目录 | 分组 | 内容 |
| --- | --- | --- |
| `guide/` | 快速开始 | 介绍、安装、第一个请求 |
| `console/` | 管理控制台 | 按页面划分的截图教程（组内按「服务商与模型 / 客户端与令牌 / 运行与安全」分小节） |
| `cli/` | CLI 参考 | astra 全部子命令（按「服务管理 / 客户端与提供商 / 安装与维护」分小节） |
| `desktop/` | 桌面端 | Electron 应用 |
| `advanced/` | 进阶 | 架构、配置、隐私护栏原理、常见问题 |

| Route | Description |
| --- | --- |
| `app/(home)` | 落地页 |
| `app/docs` | 文档布局与页面 |
| `app/api/search/route.ts` | 搜索 |
| `app/sitemap.ts` / `app/robots.ts` | sitemap.xml / robots.txt |
| `app/llms.txt` / `app/llms-full.txt` / `app/llms.mdx/**` | 面向 AI 的文档索引 / 全文 / 单页 Markdown（GEO） |

## 站点 URL

canonical、hreflang、sitemap.xml、robots.txt、llms.txt 都需要绝对 URL。站点域名**固定为**
`https://astra-gate.si`（`lib/shared.ts` 的 `siteUrl` 常量，不走环境变量，本地构建与 Docker
构建输出一致）。要换域名就改这一个常量。

## 截图教程的截图

控制台教程引用 `public/screenshots/*.png`。截图**从真实管理界面截取**（不是手工画的示意图），
需要 astra-server 与 web dev server 同时在运行：

```bash
# 终端 1：本地服务
astra start
# 终端 2：控制台 dev server（http://localhost:5173）
pnpm --filter @aidotnet/web dev

# 截图（playwright-core + 缓存的 Chromium headless shell，1440×900 @2x）
pnpm dlx playwright install chromium   # 首次需要
node docs/scripts/snap.mjs             # → docs/public/screenshots/*.png
```

`node docs/scripts/snap.mjs` 会遍历各页面并处理交互（打开「添加提供商」弹层、点开请求详情、
切换提供商「设置」标签等）。截图默认强制中文界面（`astra.locale`）。

唯一例外是 `login.png`：环回访问不出现登录页，无法实机截取，由 `shots/login.html` 样稿生成
（`node docs/scripts/snap-mock.mjs login`）。

文档中用 `<Screenshot src="/screenshots/overview.png" alt="…" caption="…" />` 嵌入
（组件在 `components/screenshot.tsx`，已注册进 `components/mdx.tsx`）。

> 注意：截图包含你本机的真实用量数据。公开文档仓库前请确认画面里没有敏感信息。
