import type { Metadata } from 'next';

import { PluginListView } from '@/components/plugins/plugin-list';
import { copy } from '@/components/plugins/market';
import { docsRoute } from '@/lib/shared';

export const dynamic = 'force-dynamic';

const t = copy('zh');

export const metadata: Metadata = {
  title: t.metadataTitle,
  description: t.metadataDescription,
  alternates: {
    canonical: '/plugins',
    languages: { 'zh-CN': '/plugins', en: '/en/plugins', 'x-default': '/plugins' },
  },
};

export default async function PluginsPage({ searchParams }: { searchParams: Promise<{ kind?: string; q?: string }> }) {
  return <PluginListView lang="zh" searchParams={searchParams} base="/plugins" docsUrl={`${docsRoute}/advanced/plugin-market`} />;
}
