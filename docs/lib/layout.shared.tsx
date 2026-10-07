import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { AstraLogo } from '@/components/astra-logo';
import { homeMessages, type HomeLang } from '@/components/home/messages';
import { appName, githubUrl } from './shared';

export function baseOptions(): BaseLayoutProps {
  return {
    githubUrl,
    nav: {
      title: (
        <span className="inline-flex items-center gap-2 font-semibold">
          <AstraLogo className="size-6 rounded-[6px] border border-black/10" />
          {appName}
        </span>
      ),
    },
  };
}

/** Website (HomeLayout) options: localized top links plus a language switch (`/` = zh, `/en` = en). */
export function homeOptions(lang: HomeLang): BaseLayoutProps {
  const base = baseOptions();
  const t = homeMessages[lang].nav;
  return {
    ...base,
    nav: { ...base.nav, url: lang === 'en' ? '/en' : '/', transparentMode: 'top' },
    links: [
      { text: t.docs, url: '/docs/guide', active: 'nested-url' },
      { text: t.cli, url: '/docs/cli', active: 'nested-url' },
      { text: t.download, url: '/download', active: 'url' },
      { text: t.switchLang, url: t.switchLangUrl },
    ],
  };
}
