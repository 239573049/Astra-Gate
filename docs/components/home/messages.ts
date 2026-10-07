/**
 * Landing-page copy. The landing page is bilingual (`/` = zh, `/en` = en); the docs content under
 * `content/docs` is Chinese only, so every docs link points at the same pages in both languages.
 * `zh` defines the shape — `en` must provide every key.
 */

export type HomeLang = 'zh' | 'en';

const zh = {
  nav: {
    docs: '文档',
    cli: 'CLI',
    download: '下载',
    switchLang: 'English',
    switchLangUrl: '/en',
  },
  hero: {
    badge: '开源 · 本地优先 · 数据不出本机',
    titleLead: '你的 AI，',
    titleAccent: '汇于一处。',
    subtitle:
      'Astra Gate 是跑在你电脑上的 AI 网关：聚合所有服务商，一键接管 Codex、Claude Code、Gemini CLI，自动转换协议，统计用量与费用，并在请求发出前守住你的隐私。',
    primary: '快速开始',
    secondary: '下载桌面版',
    copy: '复制',
    copied: '已复制',
  },
  clients: {
    title: '接入你已经在用的工具',
    providersTitle: '以及 20+ 服务商模板',
  },
  features: {
    eyebrow: '核心能力',
    title: '一个网关，管好所有 AI 调用',
    subtitle: '不改代码、不换工具，只是在客户端和服务商之间多了一层安静可靠的本机中转。',
    items: {
      protocol: {
        title: '协议自动转换',
        desc: 'OpenAI Chat、OpenAI Responses、Anthropic Messages、Gemini 四种协议统一解码为中间表示再重新编码——客户端与服务商不必同构。',
      },
      clients: {
        title: '一键接管客户端',
        desc: '不用手改 ~/.codex、~/.claude 配置。点「启用」自动写入并备份，「禁用并还原」随时回到原样。',
      },
      billing: {
        title: '用量与费用看得见',
        desc: '输入、输出、缓存、推理 Token 与费用逐条入库，按时间、客户端、模型汇总。',
      },
      privacy: {
        title: '隐私护栏',
        desc: '请求发出前在本机检测密钥、邮箱、内网地址，可警告、拦截或脱敏，原值永不出本机。',
      },
      accounts: {
        title: '多账号与订阅',
        desc: '同一服务商可挂多个 Key 与订阅账号（Claude Pro/Max、ChatGPT、Kimi…），客户端一键切换。',
      },
    },
  },
  flow: {
    eyebrow: '工作原理',
    title: '客户端 → Astra Gate → 任意服务商',
    subtitle: '所有请求经过 127.0.0.1 上的同一个入口：在这里完成协议换算、计费与隐私检查，再转发到上游。',
    clients: '你的客户端',
    providers: '上游服务商',
    gateway: '本机网关',
    steps: ['协议转换', '计费入库', '隐私护栏'],
  },
  tour: {
    eyebrow: '管理控制台',
    title: '一切尽在一个原生般的控制台',
    subtitle: '浏览器或桌面端打开，同一套液态玻璃界面，同一份本机数据。',
    tabs: {
      overview: '概览',
      clients: '客户端',
      requests: '请求日志',
      privacy: '隐私护栏',
      providers: '提供商',
    },
  },
  start: {
    eyebrow: '快速开始',
    title: '三步，跑通第一条请求',
    steps: [
      { title: '安装', desc: '需要 Node.js ≥ 18，自动下载当前平台的服务端二进制。' },
      { title: '启动并打开控制台', desc: '后台常驻，默认监听 127.0.0.1:17321。' },
      { title: '接管客户端', desc: '也可以在控制台「客户端」页一键启用。' },
    ],
    more: '阅读完整教程',
  },
  cta: {
    title: '让 AI 调用回到你自己的机器上',
    subtitle: 'MIT 开源，免费使用。支持 macOS、Linux 与 Windows。',
    primary: '开始使用',
    secondary: '在 GitHub 上查看',
  },
  footer: {
    tagline: '本地 AI 网关',
    product: '产品',
    resources: '资源',
    guide: '快速开始',
    console: '管理控制台',
    cli: 'CLI 参考',
    desktop: '桌面端',
    download: '下载客户端',
    advanced: '进阶与架构',
    releases: '版本发布',
    license: 'MIT License',
  },
};

export type HomeMessages = typeof zh;

const en: HomeMessages = {
  nav: {
    docs: 'Docs',
    cli: 'CLI',
    download: 'Download',
    switchLang: '中文',
    switchLangUrl: '/',
  },
  hero: {
    badge: 'Open source · Local-first · Your data stays home',
    titleLead: 'All your AI,',
    titleAccent: 'one local gateway.',
    subtitle:
      'Astra Gate is an AI gateway that runs on your own machine: aggregate every provider, take over Codex, Claude Code and Gemini CLI in one click, translate protocols, track tokens and cost, and guard your privacy before a request ever leaves.',
    primary: 'Get started',
    secondary: 'Download desktop app',
    copy: 'Copy',
    copied: 'Copied',
  },
  clients: {
    title: 'Works with the tools you already use',
    providersTitle: 'plus 20+ provider templates',
  },
  features: {
    eyebrow: 'Features',
    title: 'One gateway for every AI call',
    subtitle:
      'No code changes, no new tools — just a quiet, reliable local hop between your clients and your providers.',
    items: {
      protocol: {
        title: 'Automatic protocol translation',
        desc: 'OpenAI Chat, OpenAI Responses, Anthropic Messages and Gemini all decode into one intermediate representation and re-encode from it — clients and providers no longer need to match.',
      },
      clients: {
        title: 'One-click client takeover',
        desc: 'No more hand-editing ~/.codex or ~/.claude. Enable writes the config with a backup; Disable & restore puts it back exactly as it was.',
      },
      billing: {
        title: 'See usage and cost',
        desc: 'Input, output, cache and reasoning tokens plus cost are recorded per request and rolled up by time, client and model.',
      },
      privacy: {
        title: 'Privacy guardrails',
        desc: 'Keys, emails and internal addresses are detected locally before a request is sent — warn, block or redact; originals never leave your machine.',
      },
      accounts: {
        title: 'Multiple accounts & subscriptions',
        desc: 'Attach several keys and subscriptions (Claude Pro/Max, ChatGPT, Kimi…) to one provider and switch clients between them instantly.',
      },
    },
  },
  flow: {
    eyebrow: 'How it works',
    title: 'Clients → Astra Gate → any provider',
    subtitle:
      'Every request goes through one entry point on 127.0.0.1, where it is translated, metered and checked before being forwarded upstream.',
    clients: 'Your clients',
    providers: 'Upstream providers',
    gateway: 'Local gateway',
    steps: ['Protocol translation', 'Usage metering', 'Privacy guardrails'],
  },
  tour: {
    eyebrow: 'Admin console',
    title: 'Everything in one native-feeling console',
    subtitle: 'Open it in a browser or the desktop app — same liquid-glass UI, same local data.',
    tabs: {
      overview: 'Overview',
      clients: 'Clients',
      requests: 'Requests',
      privacy: 'Privacy',
      providers: 'Providers',
    },
  },
  start: {
    eyebrow: 'Quick start',
    title: 'Your first request in three steps',
    steps: [
      { title: 'Install', desc: 'Requires Node.js ≥ 18; the server binary for your platform is pulled in automatically.' },
      { title: 'Start and open the console', desc: 'Runs in the background on 127.0.0.1:17321 by default.' },
      { title: 'Take over a client', desc: 'Or enable it with one click on the console’s Clients page.' },
    ],
    more: 'Read the full guide',
  },
  cta: {
    title: 'Bring your AI traffic back to your own machine',
    subtitle: 'MIT licensed and free. Runs on macOS, Linux and Windows.',
    primary: 'Get started',
    secondary: 'View on GitHub',
  },
  footer: {
    tagline: 'Local AI gateway',
    product: 'Product',
    resources: 'Resources',
    guide: 'Quick start',
    console: 'Admin console',
    cli: 'CLI reference',
    desktop: 'Desktop app',
    download: 'Download',
    advanced: 'Advanced',
    releases: 'Releases',
    license: 'MIT License',
  },
};

export const homeMessages: Record<HomeLang, HomeMessages> = { zh, en };
