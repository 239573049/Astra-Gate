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
| `app/(home)` | 落地页（`/` = zh，`/en` = en） |
| `app/en` | 英文落地页，插件市场英文版 `/en/plugins/**` |
| `app/docs` | 文档布局与页面 |
| `app/api/search/route.ts` | 搜索 |
| `app/(home)/plugins/**` | 插件市场网页（列表、详情、「在 Astra 中安装」）；英文版 `app/en/plugins/**`，页面文案与卡片都在 `components/plugins/` |
| `app/api/plugin-market/**` | 插件市场接口：`registry.json`（Astra 客户端默认读取）、`search`、`packages`（发布 / 下载 tarball）、`entries`（发布条目 / 下架） |
| `app/sitemap.ts` / `app/robots.ts` | sitemap.xml / robots.txt |
| `app/llms.txt` / `app/llms-full.txt` / `app/llms.mdx/**` | 面向 AI 的文档索引 / 全文 / 单页 Markdown（GEO） |

## 插件市场

实现在 `lib/plugin-market/`，数据存放在 `${DATA_DIR}/plugin-market/`（与客户端发布共用 `/app/data` 卷）。
注册表 = magpie-community 上游清单（附 npm 最新版本）+ 站内发布的条目和插件包；站内同名条目覆盖上游。

| 环境变量 | 作用 |
| --- | --- |
| `PLUGIN_UPLOAD_TOKEN` | 发布 / 下架用的 Bearer token；未设置时写接口返回 503（与 `CLIENT_UPLOAD_TOKEN` 分开） |
| `PLUGIN_MARKET_UPSTREAM` | 上游清单地址，默认 magpie-community 的 `registry.json`；设为 `off` 只用站内条目和仓库内置快照 |
| `GITHUB_TOKEN` | 可选，提高搜索代理访问 GitHub 的限额 |

`lib/plugin-market/upstream-fallback.json` 是上游清单的内置快照（上游不可达且没有缓存时使用），内容与
`src/Astra.Plugins/Market/market.json` 相同；两处要一起更新。发布方法见 `content/docs/advanced/plugin-market.mdx`。

页面分中英文两套（`/plugins` 与 `/en/plugins`），文案与卡片组件在 `components/plugins/`，两处共用；这些
组件会被客户端引用，所以不要从里面 import `lib/plugin-market/registry` 之类用到 `node:fs` 的模块。

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
