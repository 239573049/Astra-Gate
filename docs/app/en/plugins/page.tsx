import type { Metadata } from 'next';

import { PluginListView } from '@/components/plugins/plugin-list';
import { copy } from '@/components/plugins/market';

export const dynamic = 'force-dynamic';

const t = copy('en');

export const metadata: Metadata = {
  title: t.metadataTitle,
  description: t.metadataDescription,
  alternates: {
    canonical: '/en/plugins',
    languages: { 'zh-CN': '/plugins', en: '/en/plugins', 'x-default': '/plugins' },
  },
  openGraph: { locale: 'en_US' },
};

export default async function EnglishPluginsPage({ searchParams }: { searchParams: Promise<{ kind?: string; q?: string }> }) {
  return <PluginListView lang="en" searchParams={searchParams} base="/en/plugins" docsUrl="/docs/advanced/plugin-market" />;
}
