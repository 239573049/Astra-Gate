import { describe, expect, it } from 'vitest';

import {
  NAV_ORDER,
  TOOLBAR_HEIGHT,
  isThemeSource,
  menuLabels,
  normalizeAccentColor,
  sanitizeContextMenuItems,
  themeBackground,
  windowChromeOptions,
} from '../src/shared/chrome';

describe('windowChromeOptions', () => {
  it('uses hiddenInset with traffic lights inside the sidebar on macOS', () => {
    const o = windowChromeOptions('darwin', false);
    expect(o.titleBarStyle).toBe('hiddenInset');
    expect(o.trafficLightPosition).toEqual({ x: 20, y: 20 });
    expect(o.titleBarOverlay).toBeUndefined();
    expect(o.backgroundColor).toBe(themeBackground(false));
  });

  it('uses a transparent title bar overlay on Windows', () => {
    const o = windowChromeOptions('win32', true);
    expect(o.titleBarStyle).toBe('hidden');
    expect(o.titleBarOverlay).toEqual({ color: '#00000000', symbolColor: '#FFFFFF', height: TOOLBAR_HEIGHT });
    expect(o.backgroundColor).toBe(themeBackground(true));
  });

  it('keeps the system frame on Linux', () => {
    const o = windowChromeOptions('linux', false);
    expect(o.titleBarStyle).toBeUndefined();
    expect(o.titleBarOverlay).toBeUndefined();
  });
});

describe('normalizeAccentColor', () => {
  it('converts RRGGBBAA to #rrggbb', () => {
    expect(normalizeAccentColor('007AFFFF')).toBe('#007aff');
    expect(normalizeAccentColor('#A550A7')).toBe('#a550a7');
  });

  it('rejects missing or malformed values', () => {
    expect(normalizeAccentColor(undefined)).toBeNull();
    expect(normalizeAccentColor('')).toBeNull();
    expect(normalizeAccentColor('blue')).toBeNull();
    expect(normalizeAccentColor('12345')).toBeNull();
  });
});

describe('sanitizeContextMenuItems', () => {
  it('accepts normal, checkbox and separator items', () => {
    expect(
      sanitizeContextMenuItems([
        { id: 'copy', label: 'Copy' },
        { type: 'separator', id: 'ignored' },
        { id: 'pin', label: 'Pin', type: 'checkbox', checked: true, enabled: false },
      ]),
    ).toEqual([
      { id: 'copy', label: 'Copy', type: 'normal', enabled: true, checked: undefined },
      { type: 'separator' },
      { id: 'pin', label: 'Pin', type: 'checkbox', enabled: false, checked: true },
    ]);
  });

  it('rejects untrusted garbage', () => {
    expect(sanitizeContextMenuItems(null)).toBeNull();
    expect(sanitizeContextMenuItems([])).toBeNull();
    expect(sanitizeContextMenuItems([{ label: 'no id' }])).toBeNull();
    expect(sanitizeContextMenuItems([{ id: 'x', label: 'x'.repeat(201) }])).toBeNull();
    expect(sanitizeContextMenuItems(Array.from({ length: 51 }, (_, i) => ({ id: `${i}`, label: 'a' })))).toBeNull();
  });
});

describe('misc', () => {
  it('validates theme sources', () => {
    expect(isThemeSource('dark')).toBe(true);
    expect(isThemeSource('auto')).toBe(false);
  });

  it('localizes menu labels and covers every nav target', () => {
    expect(menuLabels('zh-CN').nav.clients).toBe('客户端');
    expect(menuLabels('en-US').nav.clients).toBe('Clients');
    expect(Object.keys(menuLabels('en').nav)).toEqual([...NAV_ORDER]);
  });
});
