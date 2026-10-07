import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';

import { en, zh, type MessageKey } from './messages';

export type Locale = 'zh' | 'en';
const LOCALE_KEY = 'astra.locale';

const catalogs: Record<Locale, Record<MessageKey, string>> = { zh, en };

export function initialLocale(): Locale {
  try {
    const stored = localStorage.getItem(LOCALE_KEY);
    if (stored === 'zh' || stored === 'en') return stored;
  } catch {
    // ignore
  }
  return (globalThis.navigator?.language ?? 'zh').toLowerCase().startsWith('zh') ? 'zh' : 'en';
}

/** Replaces {name} placeholders. */
export function format(template: string, vars?: Record<string, string | number>): string {
  if (!vars) return template;
  return template.replace(/\{(\w+)\}/g, (m, k: string) => (k in vars ? String(vars[k]) : m));
}

export function translate(locale: Locale, key: MessageKey, vars?: Record<string, string | number>): string {
  return format(catalogs[locale][key] ?? catalogs.zh[key] ?? key, vars);
}

export type TFunction = (key: MessageKey, vars?: Record<string, string | number>) => string;

interface I18nValue {
  locale: Locale;
  setLocale: (l: Locale) => void;
  t: TFunction;
}

const I18nContext = createContext<I18nValue | null>(null);

export function I18nProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<Locale>(initialLocale);
  const setLocale = useCallback((l: Locale) => {
    setLocaleState(l);
    document.documentElement.lang = l === 'zh' ? 'zh-CN' : 'en';
    try {
      localStorage.setItem(LOCALE_KEY, l);
    } catch {
      // ignore
    }
  }, []);
  const value = useMemo<I18nValue>(
    () => ({ locale, setLocale, t: (key, vars) => translate(locale, key, vars) }),
    [locale, setLocale],
  );
  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nValue {
  const ctx = useContext(I18nContext);
  if (!ctx) throw new Error('useI18n outside I18nProvider');
  return ctx;
}

export function useT(): TFunction {
  return useI18n().t;
}

export type { MessageKey };
