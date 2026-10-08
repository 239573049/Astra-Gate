import { source } from '@/lib/source';
import { DocsLayout } from 'fumadocs-ui/layouts/docs';
import { baseOptions } from '@/lib/layout.shared';
import { SidebarSeparator } from './sidebar-components';

export default function Layout({ children }: LayoutProps<'/docs'>) {
  return (
    <DocsLayout
      tree={source.getPageTree()}
      sidebar={{ components: { Separator: SidebarSeparator } }}
      {...baseOptions()}
    >
      {children}
    </DocsLayout>
  );
}
