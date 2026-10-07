import { HomeLayout } from 'fumadocs-ui/layouts/home';
import { homeOptions } from '@/lib/layout.shared';

export default function Layout({ children }: LayoutProps<'/en'>) {
  return <HomeLayout {...homeOptions('en')}>{children}</HomeLayout>;
}
