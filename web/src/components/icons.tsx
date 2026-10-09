import { type IconAvatarProps } from '@lobehub/icons';
import { Anthropic, DeepSeek, Gemini, LmStudio, Minimax, Moonshot, Ollama, OpenAI, OpenRouter, Qwen, SiliconCloud, Volcengine, XAI, Zhipu } from '@lobehub/icons';
import { Claude, GithubCopilot, Grok, HermesAgent, Kimi, Meta, OpenCode, Pi, XiaomiMiMo } from '@lobehub/icons';
import { Code, Monitor, SquareTerminal, type LucideIcon } from 'lucide-react';
import type { ComponentType, CSSProperties } from 'react';
import type { ClientKind } from '../api/types';
// Vendors without a @lobehub/icons brand component ship their own downloaded mark.
// See assets/providers/sources.json for provenance.
import routinLogo from '../assets/providers/routin.png';
import nextcoworkLogo from '../assets/providers/nextcowork.png';
// Clients without a @lobehub/icons mark: the vendor's own icon, provenance in assets/clients/sources.json.
import crushLogo from '../assets/clients/crush.png';
import droidLogo from '../assets/clients/droid.svg';
import nextcoworkClientLogo from '../assets/clients/nextcowork.png';
import ompLogo from '../assets/clients/omp.png';
import vscodeInsidersLogo from '../assets/clients/vscode-insiders.png';
import vscodiumLogo from '../assets/clients/vscodium.svg';
import workbuddyLogo from '../assets/clients/workbuddy.svg';
import zedLogo from '../assets/clients/zed.png';

export const CLIENT_META: Record<ClientKind, { name: string }> = {
  codex: { name: 'Codex' },
  'claude-code': { name: 'Claude Code' },
  'gemini-cli': { name: 'Gemini CLI' },
  opencode: { name: 'OpenCode' },
  'claude-desktop': { name: 'Claude Desktop' },
  'grok-build': { name: 'Grok Build' },
  pi: { name: 'Pi' },
  'hermes-agent': { name: 'Hermes Agent' },
  'minimax-code': { name: 'MiniMax Code' },
  'copilot-cli': { name: 'Copilot CLI' },
  'vscode-copilot': { name: 'VS Code Copilot' },
  crush: { name: 'Crush' },
  'qwen-code': { name: 'Qwen Code' },
  droid: { name: 'Droid' },
  'kimi-code': { name: 'Kimi Code' },
  zed: { name: 'Zed' },
  'vscode-insiders': { name: 'VS Code Insiders' },
  vscodium: { name: 'VSCodium' },
  omp: { name: 'omp (oh-my-pi)' },
  'mimo-code': { name: 'MiMo Code' },
  'deepseek-harness': { name: 'DeepSeek Harness' },
  workbuddy: { name: 'WorkBuddy' },
  'muse-code': { name: 'Muse Code' },
  nextcowork: { name: 'NextCoWork' },
};

export const CLIENT_ORDER: ClientKind[] = [
  'codex', 'claude-code', 'gemini-cli', 'opencode', 'claude-desktop', 'grok-build',
  'pi', 'hermes-agent', 'minimax-code', 'copilot-cli', 'vscode-copilot',
  'crush', 'qwen-code', 'droid', 'kimi-code', 'zed',
  'vscode-insiders', 'vscodium', 'omp', 'mimo-code', 'deepseek-harness', 'workbuddy', 'muse-code', 'nextcowork',
];

type BrandMark = ComponentType<{ size?: number | string; 'aria-hidden'?: boolean }>;

// Client marks come from @lobehub/icons and show the vendor's brand mark (as cc-switch does): product
// mascots such as the Claude Code crab or the Codex cloud read as noise at toolbar size. Clients that share a
// vendor mark (the two Claude clients, the two Copilot clients) are told apart by a corner badge.
// Clients without a @lobehub/icons brand mark (Crush, Droid, Zed, omp, the VS Code builds, WorkBuddy) show the vendor's
// own icon, bundled locally (never a remote URL); the avatar is the same image filling the tile.
function logoMark(src: string): BrandMark {
  return function LogoMark({ size = 18 }) {
    return <img data-client-mark="logo" src={src} alt="" draggable={false} decoding="async" width={size} height={size} className="object-contain" style={{ borderRadius: Math.round(Number(size) * 0.22) }} />;
  };
}

function logoAvatar(src: string): ComponentType<IconAvatarProps> {
  return function LogoAvatar({ size = 28, style }: IconAvatarProps) {
    const px = Number(size);
    const box: CSSProperties = { width: px, height: px, ...style };
    return (
      <span className="inline-flex items-center justify-center overflow-hidden bg-white shadow-[inset_0_0_0_0.5px_rgb(0_0_0/0.08)]" style={box}>
        <img data-client-mark="logo" src={src} alt="" draggable={false} decoding="async" className="size-full object-contain" />
      </span>
    );
  };
}

const logo = (src: string) => ({ Mark: logoMark(src), Avatar: logoAvatar(src) });

const CLIENT_BRANDS: Record<ClientKind, { Mark: BrandMark; Avatar: ComponentType<IconAvatarProps>; badge?: LucideIcon }> = {
  codex: { Mark: OpenAI, Avatar: OpenAI.Avatar },
  'claude-code': { Mark: Claude.Color, Avatar: Claude.Avatar, badge: SquareTerminal },
  'gemini-cli': { Mark: Gemini.Color, Avatar: Gemini.Avatar },
  opencode: { Mark: OpenCode, Avatar: OpenCode.Avatar },
  'claude-desktop': { Mark: Claude.Color, Avatar: Claude.Avatar, badge: Monitor },
  'grok-build': { Mark: Grok, Avatar: Grok.Avatar },
  pi: { Mark: Pi, Avatar: Pi.Avatar },
  'hermes-agent': { Mark: HermesAgent, Avatar: HermesAgent.Avatar },
  'minimax-code': { Mark: Minimax.Color, Avatar: Minimax.Avatar },
  'copilot-cli': { Mark: GithubCopilot, Avatar: GithubCopilot.Avatar, badge: SquareTerminal },
  'vscode-copilot': { Mark: GithubCopilot, Avatar: GithubCopilot.Avatar, badge: Code },
  crush: logo(crushLogo),
  'qwen-code': { Mark: Qwen.Color, Avatar: Qwen.Avatar, badge: SquareTerminal },
  droid: logo(droidLogo),
  'kimi-code': { Mark: Kimi.Color, Avatar: Kimi.Avatar, badge: SquareTerminal },
  zed: logo(zedLogo),
  'vscode-insiders': logo(vscodeInsidersLogo),
  vscodium: logo(vscodiumLogo),
  omp: logo(ompLogo),
  'mimo-code': { Mark: XiaomiMiMo, Avatar: XiaomiMiMo.Avatar, badge: SquareTerminal },
  'deepseek-harness': { Mark: DeepSeek.Color, Avatar: DeepSeek.Avatar, badge: Monitor },
  workbuddy: logo(workbuddyLogo),
  'muse-code': { Mark: Meta.Color, Avatar: Meta.Avatar, badge: SquareTerminal },
  nextcowork: logo(nextcoworkClientLogo),
};

function ClientBadge({ kind, size }: { kind: ClientKind; size: number }) {
  const BadgeIcon = CLIENT_BRANDS[kind].badge;
  if (!BadgeIcon) return null;
  const box = Math.max(9, Math.round(size * 0.5));
  return (
    <span
      data-badge={kind}
      className="absolute -right-0.5 -bottom-0.5 inline-flex items-center justify-center rounded-[3px] bg-[var(--surface-raised)] text-[var(--foreground)] shadow-[0_0_0_1px_var(--border)]"
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
  copilot: GithubCopilot.Avatar,
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
  nextcowork: nextcoworkLogo,
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
