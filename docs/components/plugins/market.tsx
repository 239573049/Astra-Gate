import { AppWindow, ShieldCheck } from 'lucide-react';
import Link from 'next/link';

import type { PluginKind, RegistryListing } from '@/lib/plugin-market/types';

export type MarketLang = 'zh' | 'en';

/** The market's pages in both languages: /plugins (zh) and /en/plugins (English). */
export function pluginBase(lang: MarketLang): string {
  return lang === 'en' ? '/en/plugins' : '/plugins';
}

const COPY = {
  zh: {
    kind: { provider: '提供商', middleware: '中间件', agent: '客户端', pi: 'pi 扩展' } as Record<PluginKind, string>,
    listEyebrow: 'Astra — 插件市场',
    listTitle: '插件市场',
    listIntro: 'Astra 的插件与 magpie 兼容。这里列出社区清单和 Astra 发布的插件；Astra 控制台里的插件市场读取的就是同一份清单。',
    search: '搜索',
    searchPlaceholder: '搜索名称、包名、标签',
    filterLabel: '按类型筛选',
    all: '全部',
    empty: '没有匹配的插件。',
    trust: '提供商插件在插件宿主中拥有你的全部权限，中间件能读取并改写所有请求与回复。只安装你信任的插件。想发布自己的插件？见',
    trustLink: '插件市场与发布',
    back: '← 插件市场',
    originAstra: 'Astra 发布',
    originCommunity: '社区',
    installTitle: '在 Astra 中安装',
    installCli: '命令行：',
    installCopy: '复制命令',
    installCopied: '已复制',
    installConsole: '或在本机控制台安装（端口见 ~/.astra/runtime.json，桌面端在设置里可见）：',
    installPort: '控制台端口',
    installOpen: '打开控制台安装',
    installNeeds: '需要 Astra {version} 或更高版本。',
    latest: '最新版本',
    license: '许可证',
    author: '作者',
    providers: '提供商',
    replaces: '替代内置',
    homepage: '主页',
    repository: '仓库',
    tags: '标签',
    source: '来源',
    hosted: '可下载的版本',
    version: '版本',
    published: '发布时间',
    size: '大小',
    notFound: '插件未找到',
    notFoundTitle: '插件未找到',
    notFoundBody: '市场里没有这个包。它可能已经下架，或名字写错了。',
    backToMarket: '返回插件市场',
    footer: '插件以 --ignore-scripts 安装，安装脚本不会运行，但插件本身能看到经过它的请求。只安装你信任的插件。',
    metadataTitle: '插件市场',
    metadataDescription: '为 Astra 找插件：提供商插件、网关中间件和客户端插件。聚合 astragate-community 社区仓库的插件清单，也托管 Astra 发布的插件包。',
  },
  en: {
    kind: { provider: 'Provider', middleware: 'Middleware', agent: 'Client', pi: 'pi extension' } as Record<PluginKind, string>,
    listEyebrow: 'Astra — Plugin market',
    listTitle: 'Plugin market',
    listIntro: "Astra's plugins are magpie-compatible. This lists the community registry and the plugins Astra publishes; the market in the Astra console reads the same registry.",
    search: 'Search',
    searchPlaceholder: 'Search name, package, tags',
    filterLabel: 'Filter by kind',
    all: 'All',
    empty: 'No plugins match.',
    trust: 'Provider plugins have all your permissions inside the plugin host, and middleware can read and rewrite every request and reply. Install only what you trust. Publishing your own? See',
    trustLink: 'the market and publishing',
    back: '← Plugin market',
    originAstra: 'Astra',
    originCommunity: 'Community',
    installTitle: 'Install in Astra',
    installCli: 'Command line:',
    installCopy: 'Copy command',
    installCopied: 'Copied',
    installConsole: "Or install it in your own console (the port is in ~/.astra/runtime.json; the desktop app shows it in settings):",
    installPort: 'Console port',
    installOpen: 'Open console to install',
    installNeeds: 'Needs Astra {version} or newer.',
    latest: 'Latest',
    license: 'License',
    author: 'Author',
    providers: 'Providers',
    replaces: 'Replaces',
    homepage: 'Homepage',
    repository: 'Repository',
    tags: 'Tags',
    source: 'Source',
    hosted: 'Downloadable versions',
    version: 'Version',
    published: 'Published',
    size: 'Size',
    notFound: 'Plugin not found',
    notFoundTitle: 'Plugin not found',
    notFoundBody: "The market doesn't list this package. It may have been taken down, or the name may be misspelled.",
    backToMarket: 'Back to the plugin market',
    footer: 'Plugins are installed with --ignore-scripts, so install scripts never run — but a plugin does see the requests that pass through it. Install only what you trust.',
    metadataTitle: 'Plugin market',
    metadataDescription: 'Find plugins for Astra: provider plugins, gateway middleware and client plugins. Aggregates the astragate-community plugin registry and hosts the packages Astra publishes.',
  },
} as const;

export type MarketCopy = (typeof COPY)[MarketLang];

export function copy(lang: MarketLang): MarketCopy {
  return COPY[lang];
}

export function KindBadge({ kind, lang }: { kind: PluginKind; lang: MarketLang }) {
  return (
    <span className="shrink-0 rounded-full bg-fd-muted px-2 py-0.5 text-xs text-fd-muted-foreground">
      {copy(lang).kind[kind]}
    </span>
  );
}

function initials(name: string): string {
  const words = name.replace(/[^\p{L}\p{N} ]/gu, ' ').split(/\s+/).filter(Boolean);
  if (words.length === 0) return '?';
  if (/\p{Script=Han}/u.test(words[0]!)) return words[0]!.slice(0, 1);
  return (words.length > 1 ? words[0]![0]! + words[1]![0]! : words[0]!.slice(0, 2)).toUpperCase();
}

/**
 * A plugin's picture, by the console's rules (web PluginsPage `PluginIcon`): its https icon on a white tile, else its
 * kind's glyph for middleware / clients, else initials. Icon names (e.g. `factory`) are brands the console bundles;
 * the site has no copy of those marks, so they fall back to initials too.
 */
export function PluginIcon({ plugin, size = 36 }: { plugin: RegistryListing; size?: number }) {
  const radius = Math.round(size * 0.28);
  const box = { width: size, height: size, borderRadius: radius };
  if (plugin.icon && /^https:/i.test(plugin.icon)) {
    return (
      <span aria-hidden className="inline-flex shrink-0 items-center justify-center overflow-hidden bg-white ring-1 ring-black/5" style={box}>
        <img src={plugin.icon} alt="" loading="lazy" decoding="async" draggable={false} className="size-full object-contain" />
      </span>
    );
  }
  if (plugin.kind === 'middleware' || plugin.kind === 'agent') {
    const Glyph = plugin.kind === 'middleware' ? ShieldCheck : AppWindow;
    return (
      <span
        aria-hidden
        className="inline-flex shrink-0 items-center justify-center text-white"
        style={{ ...box, background: plugin.kind === 'middleware' ? '#0a84ff' : '#30b0a0' }}
      >
        <Glyph className="size-[55%]" strokeWidth={1.75} />
      </span>
    );
  }
  return (
    <span
      aria-hidden
      className="inline-flex shrink-0 items-center justify-center border border-fd-border bg-fd-muted font-medium text-fd-muted-foreground"
      style={{ ...box, fontSize: Math.round(size * 0.38) }}
    >
      {initials(plugin.name)}
    </span>
  );
}

export function PluginCard({ plugin, lang, summary }: { plugin: RegistryListing; lang: MarketLang; summary: string }) {
  const t = copy(lang);
  return (
    <Link
      href={`${pluginBase(lang)}/${plugin.package}`}
      className="flex flex-col gap-2 rounded-xl border border-fd-border p-4 transition-colors hover:bg-fd-accent/50"
    >
      <div className="flex items-center gap-3">
        <PluginIcon plugin={plugin} />
        <span className="min-w-0 flex-1 truncate font-medium">{plugin.name}</span>
        <KindBadge kind={plugin.kind} lang={lang} />
      </div>
      <code className="truncate text-xs text-fd-muted-foreground">{plugin.package}</code>
      {summary && <p className="line-clamp-2 text-sm text-fd-muted-foreground">{summary}</p>}
      <div className="mt-auto flex items-center gap-2 pt-1 text-xs text-fd-muted-foreground">
        {plugin.latest && <span>v{plugin.latest}</span>}
        <span>·</span>
        <span>{plugin.origin === 'astra' ? t.originAstra : t.originCommunity}</span>
        {plugin.tarballs && plugin.tarballs.length > 0 && (
          <>
            <span>·</span>
            <span>{lang === 'en' ? 'hosted here' : '站内托管'}</span>
          </>
        )}
      </div>
    </Link>
  );
}
