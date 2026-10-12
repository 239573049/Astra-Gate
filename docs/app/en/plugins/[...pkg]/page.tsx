import type { Metadata } from 'next';

import { PluginDetailView, PluginNotFoundView } from '@/components/plugins/plugin-detail';
import { copy } from '@/components/plugins/market';
import { detailText, loadListing } from '@/lib/plugin-market/pages';

export const dynamic = 'force-dynamic';

export async function generateMetadata({ params }: { params: Promise<{ pkg: string[] }> }): Promise<Metadata> {
  const t = copy('en');
  const plugin = await loadListing(params);
  if (!plugin) return { title: t.notFoundTitle, alternates: { canonical: '/en/plugins' } };
  return {
    title: `${plugin.name} · ${t.metadataTitle}`,
    description: detailText(plugin, 'en').description,
    alternates: { canonical: `/en/plugins/${plugin.package}` },
    openGraph: { locale: 'en_US' },
  };
}

export default async function EnglishPluginDetailPage({ params }: { params: Promise<{ pkg: string[] }> }) {
  const plugin = await loadListing(params);
  if (!plugin) return <PluginNotFoundView lang="en" backUrl="/en/plugins" />;
  return <PluginDetailView plugin={plugin} lang="en" summary={detailText(plugin, 'en').summary} backUrl="/en/plugins" />;
}
