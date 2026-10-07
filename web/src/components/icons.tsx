import { type IconAvatarProps } from '@lobehub/icons';
import { Anthropic, DeepSeek, Gemini, LmStudio, Minimax, Moonshot, Ollama, OpenAI, OpenRouter, Qwen, SiliconCloud, Volcengine, XAI, Zhipu } from '@lobehub/icons';
import { Claude, Grok, OpenCode } from '@lobehub/icons';
import { Monitor, SquareTerminal, type LucideIcon } from 'lucide-react';
import type { ComponentType } from 'react';
import type { ClientKind } from '../api/types';
// Vendors without a @lobehub/icons brand component ship their own downloaded mark.
// See assets/providers/sources.json for provenance.
import routinLogo from '../assets/providers/routin.png';

export const CLIENT_META: Record<ClientKind, { name: string }> = {
  codex: { name: 'Codex' },
  'claude-code': { name: 'Claude Code' },
  'gemini-cli': { name: 'Gemini CLI' },
  opencode: { name: 'OpenCode' },
  'claude-desktop': { name: 'Claude Desktop' },
  'grok-build': { name: 'Grok Build' },
};

export const CLIENT_ORDER: ClientKind[] = ['codex', 'claude-code', 'gemini-cli', 'opencode', 'claude-desktop', 'grok-build'];

type BrandMark = ComponentType<{ size?: number | string; 'aria-hidden'?: boolean }>;

// Client marks come from @lobehub/icons and show the vendor's brand mark (as cc-switch does): product
// mascots such as the Claude Code crab or the Codex cloud read as noise at toolbar size. The two Claude
// clients share the Claude mark and are told apart by a corner badge (terminal vs. desktop app).
const CLIENT_BRANDS: Record<ClientKind, { Mark: BrandMark; Avatar: ComponentType<IconAvatarProps>; badge?: LucideIcon }> = {
  codex: { Mark: OpenAI, Avatar: OpenAI.Avatar },
  'claude-code': { Mark: Claude.Color, Avatar: Claude.Avatar, badge: SquareTerminal },
  'gemini-cli': { Mark: Gemini.Color, Avatar: Gemini.Avatar },
  opencode: { Mark: OpenCode, Avatar: OpenCode.Avatar },
  'claude-desktop': { Mark: Claude.Color, Avatar: Claude.Avatar, badge: Monitor },
  'grok-build': { Mark: Grok, Avatar: Grok.Avatar },
};

function ClientBadge({ kind, size }: { kind: ClientKind; size: number }) {
  const BadgeIcon = CLIENT_BRANDS[kind].badge;
  if (!BadgeIcon) return null;
  const box = Math.max(9, Math.round(size * 0.5));
  return (
    <span
      data-badge={kind}
      className="absolute -right-0.5 -bottom-0.5 inline-flex items-center justify-center rounded-[3px] bg-[var(--surface)] text-[var(--foreground)] shadow-[0_0_0_1px_var(--border)]"
      style={{ width: box, height: box }}
    >
      <BadgeIcon style={{ width: box * 0.78, height: box * 0.78 }} strokeWidth={2.5} />
    </span>
  );
}

/**
 * A client's brand mark without a tile, for segmented filters, tabs and table cells. Mono marks take the
 * foreground color so every mark has the same weight whether or not its segment is selected. Decorative:
 * the library's SVG carries its own <title>, so it is hidden and callers supply the accessible name.
 */
export function ClientGlyph({ kind, size = 18 }: { kind: ClientKind; size?: number }) {
  const { Mark } = CLIENT_BRANDS[kind];
  return (
    <span aria-hidden className="relative inline-flex shrink-0 items-center justify-center text-[var(--foreground)]" style={{ width: size, height: size }}>
      <Mark size={size} aria-hidden />
      <ClientBadge kind={kind} size={size} />
    </span>
  );
}

/** A client's brand avatar (mark on its brand tile), for headers and larger identity spots. */
export function ClientIcon({ kind, size = 28 }: { kind: ClientKind; size?: number }) {
  const { Avatar } = CLIENT_BRANDS[kind];
  return (
    <span aria-hidden className="relative inline-flex shrink-0" style={{ width: size, height: size }}>
      <Avatar size={size} shape="square" style={{ borderRadius: Math.round(size * 0.28) }} />
      <ClientBadge kind={kind} size={Math.round(size * 0.8)} />
    </span>
  );
}

const PROVIDER_COLORS = ['#0a84ff', '#30b0c7', '#5e5ce6', '#ff9f0a', '#34c759', '#ff375f', '#af52de', '#8e8e93'];

export function hashColor(key: string): string {
  let h = 0;
  for (const ch of key) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return PROVIDER_COLORS[h % PROVIDER_COLORS.length]!;
}

export function initials(name: string): string {
  const words = name.replace(/[^\p{L}\p{N} ]/gu, ' ').split(/\s+/).filter(Boolean);
  if (words.length === 0) return '?';
  if (/\p{Script=Han}/u.test(words[0]!)) return words[0]!.slice(0, 1);
  return (words.length > 1 ? words[0]![0]! + words[1]![0]! : words[0]!.slice(0, 2)).toUpperCase();
}

// Provider-template `icon` ids (src/Astra.Providers/Templates/catalog.json) → official logo avatars
// from @lobehub/icons. Explicit components instead of the library's string lookup: its fallback
// renders through antd-style CSS vars that stay invisible outside an antd theme (e.g. "gemini").
// Ids absent here (e.g. "custom") fall back to the colored-initials tile.
type BrandAvatar = ComponentType<IconAvatarProps>;
const LOBE_PROVIDER_AVATARS: Record<string, BrandAvatar> = {
  openai: OpenAI.Avatar,
  anthropic: Anthropic.Avatar,
  gemini: Gemini.Avatar,
  grok: XAI.Avatar,
  deepseek: DeepSeek.Avatar,
  kimi: Moonshot.Avatar,
  glm: Zhipu.Avatar,
  qwen: Qwen.Avatar,
  minimax: Minimax.Avatar,
  volcengine: Volcengine.Avatar,
  openrouter: OpenRouter.Avatar,
  siliconflow: SiliconCloud.Avatar,
  ollama: Ollama.Avatar,
  'lm-studio': LmStudio.Avatar,
};

// Providers the icon library does not ship: a locally bundled vendor image, keyed by the same
// template `icon` id. Kept out of LOBE_PROVIDER_AVATARS so the library lookup stays uniform.
const LOCAL_PROVIDER_LOGOS: Record<string, string> = {
  routin: routinLogo,
};

export function ProviderIcon({ name, icon, colorKey, size = 28 }: { name: string; icon?: string | null; colorKey?: string | null; size?: number }) {
  const Avatar = icon ? LOBE_PROVIDER_AVATARS[icon] : undefined;
  if (Avatar) {
    return <Avatar size={size} shape="square" style={{ borderRadius: Math.round(size * 0.28) }} />;
  }
  const logo = icon ? LOCAL_PROVIDER_LOGOS[icon] : undefined;
  if (logo) {
    return (
      <span
        aria-hidden
        className="relative inline-flex shrink-0 items-center justify-center overflow-hidden bg-white shadow-[inset_0_0_0_0.5px_rgb(0_0_0/0.08)]"
        style={{ width: size, height: size, borderRadius: Math.round(size * 0.28) }}
      >
        <img src={logo} alt="" draggable={false} decoding="async" className="size-full object-contain" />
      </span>
    );
  }
  // No brand logo: neutral initials mark (colorKey kept for API compatibility).
  void colorKey;
  return (
    <span
      aria-hidden
      className="inline-flex shrink-0 items-center justify-center border border-[var(--border)] bg-[var(--surface-muted)] font-medium text-[var(--text-secondary)]"
      style={{ width: size, height: size, borderRadius: Math.round(size * 0.28), fontSize: Math.round(size * 0.38) }}
    >
      {initials(name)}
    </span>
  );
}
