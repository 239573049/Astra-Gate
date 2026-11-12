import { Puzzle, Search } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router';

import { api } from '../api/client';
import type { PluginV2Contributions } from '../api/types';
import { Dialog, DialogContent } from '../components/arc/dialog/dialog';
import { NAV_GROUPS } from '../components/layout/nav';
import { errorText, useFeedback } from '../components/ui/overlays';
import { useI18n, type MessageKey } from '../i18n';
import { isMac } from '../shell/bridge';

/** One entry of the command palette: a host page, a plugin page, or a plugin command. */
export interface PaletteItem {
  id: string;
  label: string;
  /** what it belongs to: the console, or the plugin's id */
  group: string;
  keybinding?: string | null;
  run: { kind: 'path'; path: string } | { kind: 'command'; plugin: string; command: string };
}

/**
 * What the palette offers (design/plugin-v2 05 §8): the console's pages, v2 plugin pages, and the commands plugins add to
 * `command.palette` (ui.menus) — plus any command with a keybinding, so a shortcut can always be looked up.
 */
export function paletteItems(
  contributions: PluginV2Contributions | undefined,
  t: (key: MessageKey) => string,
): PaletteItem[] {
  const items: PaletteItem[] = NAV_GROUPS.flatMap((g) => g.items).map((n) => ({
    id: `nav:${n.id}`,
    label: t(n.label),
    group: 'astra',
    run: { kind: 'path', path: n.path },
  }));
  if (!contributions) return items;
  for (const p of contributions.pages)
    items.push({
      id: `page:${p.plugin}/${p.id}`,
      label: p.title,
      group: p.plugin,
      run: { kind: 'path', path: `/plugins/v2/${p.plugin}/${p.id}` },
    });
  const seen = new Set<string>();
  for (const m of contributions.menus.filter((m) => m.menu === 'command.palette')) {
    seen.add(`${m.plugin}:${m.command}`);
    const keybinding = contributions.commands.find(
      (c) => c.plugin === m.plugin && c.id === m.command,
    )?.keybinding;
    items.push({
      id: `cmd:${m.plugin}:${m.command}`,
      label: m.title,
      group: m.plugin,
      keybinding,
      run: { kind: 'command', plugin: m.plugin, command: m.command },
    });
  }
  for (const c of contributions.commands.filter(
    (c) => c.keybinding && !seen.has(`${c.plugin}:${c.id}`),
  ))
    items.push({
      id: `cmd:${c.plugin}:${c.id}`,
      label: c.title,
      group: c.plugin,
      keybinding: c.keybinding,
      run: { kind: 'command', plugin: c.plugin, command: c.id },
    });
  return items;
}

/** Items whose label, group or id contain every word of the query (case-insensitive). */
export function filterPalette(items: PaletteItem[], query: string): PaletteItem[] {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return items;
  return items.filter((i) => {
    const hay = `${i.label} ${i.group} ${i.id}`.toLowerCase();
    return words.every((w) => hay.includes(w));
  });
}

export interface Chord {
  mod: boolean;
  shift: boolean;
  alt: boolean;
  key: string;
}

/** "mod+shift+g" → a chord; null when it isn't one (`mod` is ⌘ on macOS, Ctrl elsewhere; a chord needs mod or alt). */
export function parseKeybinding(text: string | null | undefined): Chord | null {
  if (!text) return null;
  const parts = text
    .toLowerCase()
    .split('+')
    .map((p) => p.trim())
    .filter(Boolean);
  const key = parts.pop();
  if (!key || key.length === 0) return null;
  const chord: Chord = { mod: false, shift: false, alt: false, key };
  for (const p of parts) {
    if (p === 'mod' || p === 'cmd' || p === 'ctrl') chord.mod = true;
    else if (p === 'shift') chord.shift = true;
    else if (p === 'alt' || p === 'option') chord.alt = true;
    else return null;
  }
  return chord.mod || chord.alt ? chord : null;
}

export function matchesChord(
  e: Pick<KeyboardEvent, 'key' | 'code' | 'metaKey' | 'ctrlKey' | 'altKey' | 'shiftKey'>,
  chord: Chord,
  mac: boolean,
): boolean {
  const mod = mac ? e.metaKey && !e.ctrlKey : e.ctrlKey && !e.metaKey;
  if (mod !== chord.mod || e.shiftKey !== chord.shift || e.altKey !== chord.alt) return false;
  const key = /^Key[A-Z]$/.test(e.code)
    ? e.code.slice(3).toLowerCase()
    : /^Digit\d$/.test(e.code)
      ? e.code.slice(5)
      : e.key.toLowerCase();
  return key === chord.key;
}

/** How a chord reads on this platform ("⌘⇧G" / "Ctrl+Shift+G"). */
export function chordLabel(text: string, mac = isMac): string {
  const c = parseKeybinding(text);
  if (!c) return text;
  const key = c.key.length === 1 ? c.key.toUpperCase() : c.key;
  return mac
    ? `${c.mod ? '⌘' : ''}${c.alt ? '⌥' : ''}${c.shift ? '⇧' : ''}${key}`
    : [c.mod && 'Ctrl', c.alt && 'Alt', c.shift && 'Shift', key].filter(Boolean).join('+');
}

function useRunItem() {
  const navigate = useNavigate();
  const feedback = useFeedback();
  const { t } = useI18n();
  return (item: PaletteItem) => {
    if (item.run.kind === 'path') {
      navigate(item.run.path);
      return;
    }
    const { plugin, command } = item.run;
    void api<{ result: unknown }>(
      'POST',
      `/api/plugins/v2/${encodeURIComponent(plugin)}/commands/${encodeURIComponent(command)}`,
      { args: null },
    ).then(
      (r) =>
        feedback.toast(
          typeof r?.result === 'string' && r.result
            ? r.result
            : t('pluginUi.commandRan', { name: item.label }),
          'success',
        ),
      (e: unknown) => feedback.toast(errorText(e), 'error'),
    );
  };
}

/**
 * Runs plugin commands from their keybindings. The host's own shortcuts win: `hostTakes` says whether the console
 * already handles the key (its command map), and then the plugin's binding is skipped.
 */
export function usePluginKeybindings(
  contributions: PluginV2Contributions | undefined,
  hostTakes: (e: KeyboardEvent) => boolean,
) {
  const run = useRunItem();
  const ref = useRef({ contributions, hostTakes, run });
  useEffect(() => {
    ref.current = { contributions, hostTakes, run };
  });
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const { contributions: c, hostTakes: host, run: go } = ref.current;
      if (!c || e.defaultPrevented || host(e)) return;
      for (const cmd of c.commands) {
        const chord = parseKeybinding(cmd.keybinding);
        if (chord && matchesChord(e, chord, isMac)) {
          e.preventDefault();
          go({
            id: cmd.id,
            label: cmd.title,
            group: cmd.plugin,
            run: { kind: 'command', plugin: cmd.plugin, command: cmd.id },
          });
          return;
        }
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);
}

/** The palette dialog: type to filter, ↑/↓ to choose, Enter to run. */
export function CommandPalette({
  open,
  onOpenChange,
  contributions,
}: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  contributions: PluginV2Contributions | undefined;
}) {
  const { t } = useI18n();
  const run = useRunItem();
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const all = useMemo(() => paletteItems(contributions, t), [contributions, t]);
  const shown = useMemo(() => filterPalette(all, query), [all, query]);
  const choose = (item: PaletteItem | undefined) => {
    if (!item) return;
    onOpenChange(false);
    run(item);
  };
  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        onOpenChange(o);
        if (!o) {
          setQuery('');
          setActive(0);
        }
      }}
    >
      <DialogContent title={t('palette.title')}>
        <div className="flex items-center gap-2 rounded-md border border-[var(--border-subtle)] px-2.5 py-1.5">
          <Search className="size-4 text-[var(--text-muted)]" aria-hidden="true" />
          <input
            autoFocus
            role="combobox"
            aria-expanded="true"
            aria-controls="astra-palette-list"
            aria-label={t('palette.placeholder')}
            placeholder={t('palette.placeholder')}
            className="min-w-0 flex-1 bg-transparent text-[13px] outline-none"
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setActive(0);
            }}
            onKeyDown={(e) => {
              if (e.key === 'ArrowDown') {
                e.preventDefault();
                setActive((a) => Math.min(a + 1, shown.length - 1));
              } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                setActive((a) => Math.max(a - 1, 0));
              } else if (e.key === 'Enter') {
                e.preventDefault();
                choose(shown[active]);
              }
            }}
          />
        </div>
        <ul
          id="astra-palette-list"
          role="listbox"
          aria-label={t('palette.title')}
          className="mt-2 max-h-[50vh] overflow-y-auto"
        >
          {shown.length === 0 && (
            <li className="px-2.5 py-3 text-[12px] text-[var(--text-muted)]">
              {t('palette.empty')}
            </li>
          )}
          {shown.map((item, i) => (
            <li
              key={item.id}
              role="option"
              aria-selected={i === active}
              className={`flex cursor-pointer items-center gap-2 rounded-md px-2.5 py-1.5 text-[13px] ${i === active ? 'bg-[var(--surface-muted)]' : ''}`}
              onMouseEnter={() => setActive(i)}
              onClick={() => choose(item)}
            >
              {item.group !== 'astra' && (
                <Puzzle className="size-3.5 text-[var(--text-muted)]" aria-hidden="true" />
              )}
              <span className="min-w-0 flex-1 truncate">{item.label}</span>
              {item.group !== 'astra' && (
                <span className="truncate text-[11px] text-[var(--text-muted)]">{item.group}</span>
              )}
              {item.keybinding && (
                <kbd className="text-[11px] text-[var(--text-muted)]">
                  {chordLabel(item.keybinding)}
                </kbd>
              )}
            </li>
          ))}
        </ul>
      </DialogContent>
    </Dialog>
  );
}
