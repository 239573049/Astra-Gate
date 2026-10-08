import type { MetadataRoute } from 'next';
import { siteUrl } from '@/lib/shared';

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      {
        userAgent: '*',
        allow: '/',
        // release feed + upload endpoints are for clients, not crawlers
        disallow: '/api/',
      },
    ],
    sitemap: `${siteUrl}/sitemap.xml`,
  };
}
