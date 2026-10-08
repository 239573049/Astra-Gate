import type { Metadata } from 'next';
import { Landing } from '@/components/home/landing';
import { appName } from '@/lib/shared';

export const metadata: Metadata = {
  title: { absolute: `${appName} — Local AI gateway` },
  description:
    'Astra Gate is an AI gateway that runs on your own machine: aggregate providers, take over Codex / Claude Code / Gemini CLI, translate protocols, track usage and cost, and guard your privacy.',
  alternates: {
    canonical: '/en',
    languages: {
      'zh-CN': '/',
      en: '/en',
      'x-default': '/',
    },
  },
  openGraph: {
    locale: 'en_US',
  },
};

export default function EnglishHomePage() {
  return <Landing lang="en" />;
}
