'use client';

import type { Separator } from 'fumadocs-core/page-tree';

/**
 * Sub-group headers inside a sidebar folder (meta.json `---[Icon]Name---`
 * entries). Client component so it can be passed into the client-side
 * DocsLayout's `sidebar.components` slot from a server layout.
 */
export function SidebarSeparator({ item }: { item: Separator }) {
  return (
    <p className="inline-flex items-center gap-2 mb-1.5 px-2 mt-6 empty:mb-0 text-xs font-semibold text-fd-foreground [&_svg]:size-4 [&_svg]:shrink-0">
      {item.icon}
      {item.name}
    </p>
  );
}
