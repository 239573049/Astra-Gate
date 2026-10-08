import type { Metadata } from 'next';
import { Landing } from '@/components/home/landing';

export const metadata: Metadata = {
  alternates: {
    canonical: '/',
    languages: {
      'zh-CN': '/',
      en: '/en',
      'x-default': '/',
    },
  },
};

export default function HomePage() {
  return <Landing lang="zh" />;
}
