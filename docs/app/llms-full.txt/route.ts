import type { Node, Root } from 'fumadocs-core/page-tree';
import { docsLlms, source } from '@/lib/source';
import { appName, siteDescription } from '@/lib/shared';

export const revalidate = false;

type DocPage = ReturnType<typeof source.getPages>[number];

/** Pages in sidebar order (the tree), not collection file order. */
function orderedPages(): DocPage[] {
  const byUrl = new Map(source.getPages().map((page) => [page.url, page]));
  const out: DocPage[] = [];

  const visit = (node: Root | Node): void => {
    if (node.type === 'page') {
      const page = byUrl.get(node.url);
      if (page) out.push(page);
    } else if (node.type === 'folder' || node.type === 'root') {
      if ('index' in node && node.index) visit(node.index);
      for (const child of node.children) visit(child);
    }
  };

  visit(source.getPageTree());
  return out;
}

export async function GET() {
  const header = `# ${appName} — 完整文档

> ${siteDescription}

以下按侧边栏分组顺序合并了全部文档页，每页以「# 标题 (URL)」开头。

---

`;
  const pages = orderedPages().length > 0 ? orderedPages() : source.getPages();
  const body = (await Promise.all(pages.map((page) => docsLlms.page(page)))).join('\n\n');

  return new Response(header + body, {
    headers: {
      'Content-Type': 'text/plain; charset=utf-8',
    },
  });
}
