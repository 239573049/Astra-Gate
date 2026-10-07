import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';

import { getBridge, type ThemeSource } from './bridge';

/** macOS accent colors (System Settings → Appearance), used for the manual override. */
export const ACCENT_PRESETS = [
  { id: 'blue', color: '#007aff' },
  { id: 'purple', color: '#a550a7' },
  { id: 'pink', color: '#f74f9e' },
  { id: 'red', color: '#ff5257' },
  { id: 'orange', color: '#f7821b' },
  { id: 'yellow', color: '#ffc600' },
  { id: 'green', color: '#62ba46' },
  { id: 'graphite', color: '#8c8c8c' },
] as const;

export const DEFAULT_ACCENT = '#007aff';
const THEME_KEY = 'astra.theme';
const ACCENT_KEY = 'astra.accent';

/** "system" or a preset color. */
export type AccentChoice = 'system' | string;

export interface Appearance {
  theme: ThemeSource;
  resolvedTheme: 'light' | 'dark';
  setTheme(theme: ThemeSource): void;
  accentChoice: AccentChoice;
  accent: string;
  systemAccent: string | null;
  setAccentChoice(choice: AccentChoice): void;
}

const AppearanceContext = createContext<Appearance | null>(null);

function readStorage(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStorage(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // private mode etc.
  }
}

/** Web: the CSS system color `AccentColor` where supported (Safari, Firefox), else null. */
export function readCssAccentColor(): string | null {
  if (typeof document === 'undefined' || !globalThis.CSS?.supports?.('color', 'AccentColor')) return null;
  const probe = document.createElement('span');
  probe.style.color = 'AccentColor';
  probe.style.display = 'none';
  document.body.appendChild(probe);
  const rgb = getComputedStyle(probe).color;
  probe.remove();
  return rgbToHex(rgb);
}

export function rgbToHex(rgb: string): string | null {
  const m = /rgba?\((\d+)[,\s]+(\d+)[,\s]+(\d+)/.exec(rgb);
  if (!m) return null;
  return `#${[m[1], m[2], m[3]].map((n) => Number(n).toString(16).padStart(2, '0')).join('')}`;
}

/** Relative luminance check: yellow / light accents need dark text on top. */
export function onAccentColor(hex: string): string {
  const m = /^#([0-9a-f]{6})$/i.exec(hex);
  if (!m) return '#ffffff';
  const n = parseInt(m[1]!, 16);
  const [r, g, b] = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  const lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  return lum > 0.45 ? '#1d1d1f' : '#ffffff';
}

/** Applies the stored theme before React mounts (no light flash in dark mode). */
export function applyInitialTheme(root: HTMLElement = document.documentElement): void {
  const stored = readStorage(THEME_KEY);
  const dark = stored === 'dark' || (stored !== 'light' && (globalThis.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false));
  root.dataset.theme = dark ? 'dark' : 'light';
}

export function AppearanceProvider({ children }: { children: ReactNode }) {
  const bridge = getBridge();
  const [theme, setThemeState] = useState<ThemeSource>(() => {
    const v = readStorage(THEME_KEY);
    return v === 'light' || v === 'dark' ? v : 'system';
  });
  const [systemDark, setSystemDark] = useState(
    () => globalThis.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false,
  );
  const [accentChoice, setAccentChoiceState] = useState<AccentChoice>(() => readStorage(ACCENT_KEY) ?? 'system');
  const [systemAccent, setSystemAccent] = useState<string | null>(() => bridge?.accentColor ?? null);

  useEffect(() => {
    const mq = globalThis.matchMedia?.('(prefers-color-scheme: dark)');
    if (!mq) return;
    const onChange = () => setSystemDark(mq.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);

  // System accent: live from the desktop bridge, or CSS AccentColor in the browser.
  useEffect(() => {
    if (bridge) {
      void bridge.getAccentColor().then((c) => setSystemAccent(c));
      return bridge.onAccentColorChanged((c) => setSystemAccent(c));
    }
    setSystemAccent(readCssAccentColor());
    return undefined;
  }, [bridge]);

  const resolvedTheme: 'light' | 'dark' = theme === 'system' ? (systemDark ? 'dark' : 'light') : theme;
  const accent = accentChoice === 'system' ? (systemAccent ?? DEFAULT_ACCENT) : accentChoice;

  useEffect(() => {
    const root = document.documentElement;
    root.dataset.theme = resolvedTheme;
    root.style.setProperty('--accent', accent);
    root.style.setProperty('--on-accent', onAccentColor(accent));
  }, [resolvedTheme, accent]);

  const setTheme = useCallback(
    (t: ThemeSource) => {
      setThemeState(t);
      writeStorage(THEME_KEY, t);
      void bridge?.setThemeSource(t);
    },
    [bridge],
  );

  // Keep the native window (background, Windows overlay) in sync with the stored choice on start.
  useEffect(() => {
    void bridge?.setThemeSource(theme);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const setAccentChoice = useCallback((c: AccentChoice) => {
    setAccentChoiceState(c);
    writeStorage(ACCENT_KEY, c);
  }, []);

  const value = useMemo<Appearance>(
    () => ({ theme, resolvedTheme, setTheme, accentChoice, accent, systemAccent, setAccentChoice }),
    [theme, resolvedTheme, setTheme, accentChoice, accent, systemAccent, setAccentChoice],
  );
  return <AppearanceContext.Provider value={value}>{children}</AppearanceContext.Provider>;
}

export function useAppearance(): Appearance {
  const ctx = useContext(AppearanceContext);
  if (!ctx) throw new Error('useAppearance outside AppearanceProvider');
  return ctx;
}
