import { useEffect, useRef } from 'react';

import { getBridge, isDesktop, isMac, type MenuCommand, type NavTarget } from './bridge';

export const NAV_ORDER: NavTarget[] = ['overview', 'requests', 'clients', 'providers', 'models', 'settings'];

/**
 * Maps a keyboard event to a command. Desktop (non-mac; mac uses the native menu): Ctrl+1–6, Ctrl+F,
 * Ctrl+R, Ctrl+N, Ctrl+, … Web: only combos that don't fight the browser — Alt+1–6 and "/" for search.
 */
export function commandForKey(
  e: Pick<KeyboardEvent, 'key' | 'code' | 'metaKey' | 'ctrlKey' | 'altKey' | 'shiftKey'>,
  opts: { desktop: boolean; mac: boolean },
  inEditable: boolean,
): MenuCommand | null {
  const digit = /^Digit([1-6])$/.exec(e.code)?.[1];
  if (opts.desktop) {
    if (opts.mac) return null; // the native menu owns accelerators on macOS
    if (!e.ctrlKey || e.metaKey) return null;
    if (digit && !e.altKey) return `nav:${NAV_ORDER[Number(digit) - 1]!}`;
    if (e.altKey && e.code === 'KeyS') return 'toggle-sidebar';
    if (e.altKey || e.shiftKey) return null;
    switch (e.code) {
      case 'KeyF':
        return 'find';
      case 'KeyR':
        return 'refresh';
      case 'KeyN':
        return 'new-provider';
      case 'Comma':
        return 'nav:settings';
      default:
        return null;
    }
  }
  if (digit && e.altKey && !e.ctrlKey && !e.metaKey) return `nav:${NAV_ORDER[Number(digit) - 1]!}`;
  if (e.key === '/' && !inEditable && !e.ctrlKey && !e.metaKey && !e.altKey) return 'find';
  return null;
}

function isEditable(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null;
  if (!el) return false;
  return el.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName);
}

/** Single subscription point for keyboard shortcuts and native menu commands. */
export function useCommands(handler: (command: MenuCommand) => void): void {
  const ref = useRef(handler);
  useEffect(() => {
    ref.current = handler;
  });
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const cmd = commandForKey(e, { desktop: isDesktop, mac: isMac }, isEditable(e.target));
      if (!cmd) return;
      e.preventDefault();
      ref.current(cmd);
    };
    window.addEventListener('keydown', onKey);
    const off = getBridge()?.onMenuCommand((cmd) => ref.current(cmd));
    return () => {
      window.removeEventListener('keydown', onKey);
      off?.();
    };
  }, []);
}

/** Pages listen for page-scoped commands (find, refresh, new-provider) through this bus. */
type Listener = (command: MenuCommand) => void;
const listeners = new Set<Listener>();

export function emitCommand(command: MenuCommand): void {
  for (const l of listeners) l(command);
}

export function useCommandListener(handler: Listener): void {
  const ref = useRef(handler);
  useEffect(() => {
    ref.current = handler;
  });
  useEffect(() => {
    const l: Listener = (c) => ref.current(c);
    listeners.add(l);
    return () => {
      listeners.delete(l);
    };
  }, []);
}
