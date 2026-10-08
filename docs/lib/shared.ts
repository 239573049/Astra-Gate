import { createGetUrl } from 'fumadocs-core/source';

export const appName = 'Astra Gate';
export const githubUrl = 'https://github.com/239573049/Astra-Gate';
export const siteDescription =
  'Astra Gate 是一个跑在本机的 AI 网关：聚合服务商、一键接管 Codex / Claude Code / Gemini CLI，自动转换协议、统计用量费用，并在请求发出前保护隐私。';
/**
 * Canonical site origin, used by metadataBase, sitemap.xml, robots.txt,
 * hreflang alternates and llms.txt. Fixed (not env-driven) so every build —
 * local, Docker or preview — emits the same absolute URLs.
 */
export const siteUrl = 'https://astra-gate.si';
export const releasesUrl = `${githubUrl}/releases`;
export const docsRoute = '/docs';
export const docsImageRoute = '/og/docs';
export const docsContentRoute = '/llms.mdx/docs';

const getContentUrl = createGetUrl(docsContentRoute);

export function getPageMarkdownUrl(page: { slugs: string[]; locale?: string }) {
  const segments = [...page.slugs, 'content.md'];

  return { segments, url: getContentUrl(segments, page.locale) };
}

const getImageUrl = createGetUrl(docsImageRoute);

export function getPageImageUrl(page: { slugs: string[]; locale?: string }) {
  const segments = [...page.slugs, 'image.png'];

  return { segments, url: getImageUrl(segments, page.locale) };
}
