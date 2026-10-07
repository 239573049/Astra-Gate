import { describe, expect, it } from 'vitest';

import { format, translate } from '../i18n';
import { en, zh } from '../i18n/messages';

const placeholders = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

describe('i18n catalogs', () => {
  it('en provides exactly the zh key set', () => {
    expect(Object.keys(en).sort()).toEqual(Object.keys(zh).sort());
  });

  it('has no empty strings and matching placeholders', () => {
    for (const key of Object.keys(zh) as (keyof typeof zh)[]) {
      expect(zh[key].trim(), key).not.toBe('');
      expect(en[key].trim(), key).not.toBe('');
      expect(placeholders(en[key]), key).toEqual(placeholders(zh[key]));
    }
  });

  it('interpolates variables and leaves unknown ones', () => {
    expect(format('{a} + {b}', { a: 1 })).toBe('1 + {b}');
    expect(translate('en', 'requests.page', { page: 2, pages: 5 })).toBe('Page 2 of 5');
    expect(translate('zh', 'requests.page', { page: 2, pages: 5 })).toBe('第 2 / 5 页');
  });
});
