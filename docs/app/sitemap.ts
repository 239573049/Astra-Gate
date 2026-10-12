import type { MetadataRoute } from 'next';
import { source } from '@/lib/source';
import { siteUrl } from '@/lib/shared';
import { buildListings } from '@/lib/plugin-market/registry';

// The plugin pages depend on what is published at run time (DATA_DIR), not on the build.
export const dynamic = 'force-dynamic';

export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const pages = source.getPages();
  const plugins = await buildListings().catch(() => []);

  return [
    { url: `${siteUrl}/`, changeFrequency: 'weekly', priority: 1 },
    { url: `${siteUrl}/en`, changeFrequency: 'weekly', priority: 0.9 },
    { url: `${siteUrl}/download`, changeFrequency: 'weekly', priority: 0.9 },
    { url: `${siteUrl}/plugins`, changeFrequency: 'weekly', priority: 0.8 },
    { url: `${siteUrl}/en/plugins`, changeFrequency: 'weekly', priority: 0.7 },
    ...plugins.flatMap((p) => [
      {
        url: `${siteUrl}/plugins/${p.package}`,
        changeFrequency: 'weekly' as const,
        priority: 0.5,
      },
      {
        url: `${siteUrl}/en/plugins/${p.package}`,
        changeFrequency: 'weekly' as const,
        priority: 0.4,
      },
    ]),
    ...pages.map((page) => ({
      url: `${siteUrl}${page.url}`,
      changeFrequency: 'monthly' as const,
      // the getting-started trail matters more than reference pages
      priority: page.slugs[0] === 'guide' ? 0.8 : 0.7,
    })),
  ];
}
