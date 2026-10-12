import type { Metadata } from 'next';

import { PluginDetailView, PluginNotFoundView } from '@/components/plugins/plugin-detail';
import { copy } from '@/components/plugins/market';
import { detailText, loadListing } from '@/lib/plugin-market/pages';

export const dynamic = 'force-dynamic';

export async function generateMetadata({ params }: { params: Promise<{ pkg: string[] }> }): Promise<Metadata> {
  const t = copy('zh');
  const plugin = await loadListing(params);
  if (!plugin) return { title: t.notFoundTitle, alternates: { canonical: '/plugins' } };
  return {
    title: `${plugin.name} · ${t.metadataTitle}`,
    description: detailText(plugin, 'zh').description,
    alternates: { canonical: `/plugins/${plugin.package}` },
  };
}

export default async function PluginDetailPage({ params }: { params: Promise<{ pkg: string[] }> }) {
  const plugin = await loadListing(params);
  if (!plugin) return <PluginNotFoundView lang="zh" backUrl="/plugins" />;
  return <PluginDetailView plugin={plugin} lang="zh" summary={detailText(plugin, 'zh').summary} backUrl="/plugins" />;
}
