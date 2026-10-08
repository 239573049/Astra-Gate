import type { Item } from 'fumadocs-core/page-tree';
import { source } from '@/lib/source';
import { appName, githubUrl, siteDescription, siteUrl } from '@/lib/shared';

export const revalidate = false;

/**
 * llms.txt per https://llmstxt.org: an H1 title, a blockquote summary, then H2
 * sections of annotated links. Sections mirror the sidebar's top-level groups
 * so agents see the same structure human readers do.
 */
function link(node: Item): string {
  const name = typeof node.name === 'string' ? node.name : String(node.name);
  const description = node.description ? `: ${String(node.description)}` : '';
  return `- [${name}](${new URL(node.url, siteUrl).href})${description}`;
}

function render(): string {
  const tree = source.getPageTree();
  const lines: string[] = [`# ${appName}`, '', `> ${siteDescription}`, ''];

  for (const node of tree.children) {
    if (node.type === 'separator') {
      const name = node.name ? String(node.name) : '';
      if (name) lines.push(`## ${name}`, '');
      continue;
    }

    if (node.type === 'folder') {
      lines.push(`## ${String(node.name)}`, '');
      if (node.description) lines.push(String(node.description), '');
      if (node.index) lines.push(link(node.index));
      for (const child of node.children) {
        if (child.type === 'page') lines.push(link(child));
      }
      lines.push('');
      continue;
    }

    if (node.type === 'page') lines.push(link(node), '');
  }

  lines.push(
    '## Optional',
    '',
    `- [llms-full.txt](${siteUrl}/llms-full.txt): 全部文档合并为一个 Markdown 文件，适合一次性摄取`,
    `- [Markdown 单页](${siteUrl}/llms.mdx/docs/<路径>/content.md): 任意文档页的纯 Markdown 版本（如 ${siteUrl}/llms.mdx/docs/guide/install/content.md）`,
    `- [sitemap.xml](${siteUrl}/sitemap.xml): 全站 URL 列表`,
    `- [GitHub](${githubUrl}): 源码仓库`,
    '',
  );

  return lines.join('\n');
}

export async function GET() {
  return new Response(render(), {
    headers: {
      'Content-Type': 'text/plain; charset=utf-8',
    },
  });
}
