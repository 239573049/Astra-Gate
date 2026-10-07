import { describe, expect, it } from 'vitest';

import { onAccentColor, rgbToHex } from '../shell/appearance';
import { detectPlatform, detectShell, sidebarRailWidth } from '../shell/bridge';
import { commandForKey } from '../shell/commands';
import { dirnameOf } from '../shell/systemActions';

const key = (code: string, mods: Partial<Record<'metaKey' | 'ctrlKey' | 'altKey' | 'shiftKey', boolean>> = {}, k = '') => ({
  key: k,
  code,
  metaKey: false,
  ctrlKey: false,
  altKey: false,
  shiftKey: false,
  ...mods,
});

describe('commandForKey', () => {
  it('web: Alt+digit navigates and "/" searches, without hijacking browser shortcuts', () => {
    const web = { desktop: false, mac: true };
    expect(commandForKey(key('Digit3', { altKey: true }), web, false)).toBe('nav:clients');
    expect(commandForKey(key('Slash', {}, '/'), web, false)).toBe('find');
    expect(commandForKey(key('Slash', {}, '/'), web, true)).toBeNull();
    expect(commandForKey(key('KeyR', { metaKey: true }), web, false)).toBeNull();
    expect(commandForKey(key('KeyN', { ctrlKey: true }), web, false)).toBeNull();
  });

  it('desktop on Windows/Linux: Ctrl shortcuts mirror the macOS menu', () => {
    const win = { desktop: true, mac: false };
    expect(commandForKey(key('Digit1', { ctrlKey: true }), win, false)).toBe('nav:overview');
    expect(commandForKey(key('Digit6', { ctrlKey: true }), win, false)).toBe('nav:settings');
    expect(commandForKey(key('KeyR', { ctrlKey: true }), win, false)).toBe('refresh');
    expect(commandForKey(key('KeyN', { ctrlKey: true }), win, false)).toBe('new-provider');
    expect(commandForKey(key('Comma', { ctrlKey: true }), win, false)).toBe('nav:settings');
    expect(commandForKey(key('KeyS', { ctrlKey: true, altKey: true }), win, false)).toBe('toggle-sidebar');
    expect(commandForKey(key('KeyF', { ctrlKey: true, shiftKey: true }), win, false)).toBeNull();
  });

  it('desktop on macOS leaves accelerators to the native menu', () => {
    expect(commandForKey(key('Digit1', { metaKey: true }), { desktop: true, mac: true }, false)).toBeNull();
  });
});

describe('shell detection', () => {
  it('detects the desktop bridge and platform', () => {
    const bridge = { shell: 'desktop', platform: 'win32' } as Parameters<typeof detectShell>[0];
    expect(detectShell(bridge)).toBe('desktop');
    expect(detectShell(undefined)).toBe('web');
    expect(detectPlatform(bridge)).toBe('win32');
    expect(detectPlatform(undefined, { userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 15_0)', platform: 'MacIntel' } as Navigator)).toBe('darwin');
    expect(detectPlatform(undefined, { userAgent: 'Mozilla/5.0 (X11; Linux x86_64)', platform: 'Linux x86_64' } as Navigator)).toBe('linux');
  });

  it('widens the collapsed rail only where the macOS traffic lights sit inside it', () => {
    // Lights span x 20…74; the rail starts at x 8 → 12px of glass on both sides.
    expect(sidebarRailWidth(true, 'darwin')).toBe(78);
    expect(8 + sidebarRailWidth(true, 'darwin') - 74).toBe(20 - 8);
    expect(sidebarRailWidth(false, 'darwin')).toBe(68);
    expect(sidebarRailWidth(true, 'win32')).toBe(68);
  });
});

describe('accent helpers', () => {
  it('parses computed colors and picks readable text', () => {
    expect(rgbToHex('rgb(0, 122, 255)')).toBe('#007aff');
    expect(rgbToHex('rgba(165 80 167 / 1)')).toBe('#a550a7');
    expect(rgbToHex('transparent')).toBeNull();
    expect(onAccentColor('#007aff')).toBe('#ffffff');
    expect(onAccentColor('#ffc600')).toBe('#1d1d1f');
  });

  it('derives config folders', () => {
    expect(dirnameOf('~/.codex/config.toml')).toBe('~/.codex');
    expect(dirnameOf('C:\\Users\\me\\.gemini\\.env')).toBe('C:\\Users\\me\\.gemini');
  });
});
