import { RootProvider } from 'fumadocs-ui/provider/next';
import type { Metadata } from 'next';
import { appName } from '@/lib/shared';
import './global.css';

export const metadata: Metadata = {
  title: { default: `${appName} — 本地 AI 网关`, template: `%s | ${appName}` },
  description:
    'Astra Gate 是一个跑在本机的 AI 网关：聚合服务商、一键接管 Codex / Claude Code / Gemini CLI，自动转换协议、统计用量费用，并在请求发出前保护隐私。',
  icons: {
    icon: '/favicon.png',
    apple: '/apple-touch-icon.png',
  },
};

export default function Layout({ children }: LayoutProps<'/'>) {
  return (
    <html lang="zh-CN" suppressHydrationWarning>
      <body className="flex flex-col min-h-screen">
        <RootProvider>{children}</RootProvider>
      </body>
    </html>
  );
}
