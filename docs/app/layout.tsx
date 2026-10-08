import { RootProvider } from 'fumadocs-ui/provider/next';
import type { Metadata } from 'next';
import { appName, siteDescription, siteUrl } from '@/lib/shared';
import './global.css';

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl),
  title: { default: `${appName} — 本地 AI 网关`, template: `%s | ${appName}` },
  description: siteDescription,
  applicationName: appName,
  icons: {
    icon: '/favicon.png',
    apple: '/apple-touch-icon.png',
  },
  openGraph: {
    type: 'website',
    siteName: appName,
    locale: 'zh_CN',
  },
  twitter: {
    card: 'summary_large_image',
  },
  robots: {
    index: true,
    follow: true,
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
