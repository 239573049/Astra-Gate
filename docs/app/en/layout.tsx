import { HomeLayout } from 'fumadocs-ui/layouts/home';
import { homeOptions } from '@/lib/layout.shared';

export const metadata = { title: { default: 'Astra Gate — Plugin market', template: '%s | Astra Gate' } };

export default function Layout({ children }: LayoutProps<'/en'>) {
  return <HomeLayout {...homeOptions('en')}>{children}</HomeLayout>;
}
