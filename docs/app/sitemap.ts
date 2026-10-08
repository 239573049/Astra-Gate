import type { MetadataRoute } from 'next';
import { source } from '@/lib/source';
import { siteUrl } from '@/lib/shared';

export default function sitemap(): MetadataRoute.Sitemap {
  const pages = source.getPages();

  return [
    { url: `${siteUrl}/`, changeFrequency: 'weekly', priority: 1 },
    { url: `${siteUrl}/en`, changeFrequency: 'weekly', priority: 0.9 },
    { url: `${siteUrl}/download`, changeFrequency: 'weekly', priority: 0.9 },
    ...pages.map((page) => ({
      url: `${siteUrl}${page.url}`,
      changeFrequency: 'monthly' as const,
      // the getting-started trail matters more than reference pages
      priority: page.slugs[0] === 'guide' ? 0.8 : 0.7,
    })),
  ];
}
