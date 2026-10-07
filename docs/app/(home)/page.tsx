import Link from 'next/link';
import { ArrowRight, BookOpen, Monitor, SquareTerminal, ShieldCheck } from 'lucide-react';
import { Cards, Card } from 'fumadocs-ui/components/card';

export default function HomePage() {
  return (
    <div className="flex flex-1 flex-col justify-center px-6 py-24">
      <div className="mx-auto w-full max-w-4xl">
        <p className="text-sm font-medium text-fd-muted-foreground">Astra — 本地 AI 网关</p>
        <h1 className="mt-3 bg-gradient-to-b from-fd-foreground to-fd-muted-foreground bg-clip-text text-4xl font-bold text-transparent sm:text-5xl">
          文档与使用教程
        </h1>
        <p className="mt-4 max-w-2xl text-fd-muted-foreground">
          Astra 在本机聚合所有 AI 服务商，把 Codex、Claude Code、Gemini CLI 等客户端
          一键接入统一网关；自动换算协议、统计 Token 用量与费用，并在请求发出前拦截敏感信息。
        </p>
        <div className="mt-6 flex flex-wrap gap-3">
          <Link
            href="/docs/guide"
            className="inline-flex items-center gap-1.5 rounded-lg bg-fd-primary px-4 py-2 text-sm font-medium text-fd-primary-foreground transition-colors hover:opacity-90"
          >
            快速开始 <ArrowRight className="size-4" />
          </Link>
          <Link
            href="/docs/console"
            className="inline-flex items-center gap-1.5 rounded-lg border px-4 py-2 text-sm font-medium transition-colors hover:bg-fd-muted"
          >
            管理控制台截图教程
          </Link>
        </div>
        <div className="mt-12">
          <Cards>
            <Card
              title="快速开始"
              description="安装 Astra、启动服务、发出第一个请求。"
              href="/docs/guide"
              icon={<BookOpen />}
            />
            <Card
              title="管理控制台"
              description="按页面分模块的图文教程，每一处操作都有截图对照。"
              href="/docs/console"
              icon={<Monitor />}
            />
            <Card
              title="CLI 参考"
              description="astra 命令行：启动、客户端接管、自启动与卸载。"
              href="/docs/cli"
              icon={<SquareTerminal />}
            />
            <Card
              title="进阶与架构"
              description="协议转换、数据目录、隐私护栏原理与常见问题。"
              href="/docs/advanced"
              icon={<ShieldCheck />}
            />
          </Cards>
        </div>
      </div>
    </div>
  );
}
