import { type IconAvatarProps } from '@lobehub/icons';
import { Anthropic, DeepSeek, Gemini, LmStudio, Minimax, Moonshot, Ollama, OpenAI, OpenRouter, Qoder, Qwen, SiliconCloud, Volcengine, XAI, Zhipu } from '@lobehub/icons';
import { Claude, GithubCopilot, Grok, HermesAgent, Kimi, Meta, OpenCode, Pi, XiaomiMiMo, ZAI } from '@lobehub/icons';
import { Baichuan, ChatGLM, Cohere, Doubao, Gemma, Hunyuan, LongCat, Mistral, Stepfun, Wenxin, Yi } from '@lobehub/icons';
import { Box, Code, Globe, Monitor, Puzzle, SquareTerminal, type LucideIcon } from 'lucide-react';
import type { ComponentType, CSSProperties } from 'react';
import type { ApiProtocol, ClientKind } from '../api/types';
// Vendors without a @lobehub/icons brand component ship their own downloaded mark.
// See assets/providers/sources.json for provenance.
import routinLogo from '../assets/providers/routin.png';
import nextcoworkLogo from '../assets/providers/nextcowork.png';
// Clients without a @lobehub/icons mark: the vendor's own icon, provenance in assets/clients/sources.json.
import crushLogo from '../assets/clients/crush.png';
import droidLogo from '../assets/clients/droid.svg';
import nextcoworkClientLogo from '../assets/clients/nextcowork.png';
import paseoLogo from '../assets/clients/paseo.png';
import tkcodeLogo from '../assets/clients/tkcode.png';
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
  'workbuddy-ai': { name: 'WorkBuddy AI' },
  'muse-code': { name: 'Muse Code' },
  nextcowork: { name: 'NextCoWork' },
  paseo: { name: 'Paseo' },
  zcode: { name: 'ZCode' },
  tkcode: { name: 'TKCode' },
};

/**
 * A client's display name. Agent plugins add clients of kind "plugin-<id>" that no table here knows: they show the
 * name the server reports (<paramref>fallback</paramref>), else their plugin id.
 */
export function clientName(kind: string, fallback?: string | null): string {
  return CLIENT_META[kind as ClientKind]?.name ?? (fallback || (kind.startsWith('plugin-') ? kind.slice('plugin-'.length) : kind));
}

export const CLIENT_ORDER: ClientKind[] = [
  'codex', 'claude-code', 'gemini-cli', 'opencode', 'claude-desktop', 'grok-build',
  'pi', 'hermes-agent', 'minimax-code', 'copilot-cli', 'vscode-copilot',
  'crush', 'qwen-code', 'droid', 'kimi-code', 'zed',
  'vscode-insiders', 'vscodium', 'omp', 'mimo-code', 'deepseek-harness', 'workbuddy', 'workbuddy-ai', 'muse-code', 'nextcowork', 'paseo', 'zcode', 'tkcode',
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
  // the international build (workbuddy.ai) shares the China build's mark: told apart by a globe badge
  'workbuddy-ai': { ...logo(workbuddyLogo), badge: Globe },
  'muse-code': { Mark: Meta.Color, Avatar: Meta.Avatar, badge: SquareTerminal },
  nextcowork: logo(nextcoworkClientLogo),
  paseo: logo(paseoLogo),
  // ZCode is Z.ai's desktop agent: the Z.ai mark with a desktop badge
  zcode: { Mark: ZAI, Avatar: ZAI.Avatar, badge: Monitor },
  tkcode: logo(tkcodeLogo),
};

// An agent plugin's client: a puzzle piece on a tile colored by its id.
function pluginBrand(kind: string): { Mark: BrandMark; Avatar: ComponentType<IconAvatarProps>; badge?: LucideIcon } {
  return {
    Mark: function PluginMark({ size = 18 }) {
      return <Puzzle aria-hidden style={{ width: size, height: size }} />;
    },
    Avatar: function PluginAvatar({ size = 28, style }: IconAvatarProps) {
      const px = Number(size);
      return (
        <span className="inline-flex items-center justify-center text-white" style={{ width: px, height: px, background: hashColor(kind), ...style }}>
          <Puzzle style={{ width: px * 0.55, height: px * 0.55 }} />
        </span>
      );
    },
  };
}

const pluginBrands = new Map<string, ReturnType<typeof pluginBrand>>();
// one component pair per plugin kind, so a re-render doesn't remount its icon
function brandOf(kind: ClientKind) {
  const known = CLIENT_BRANDS[kind] as (typeof CLIENT_BRANDS)[ClientKind] | undefined;
  if (known) return known;
  let brand = pluginBrands.get(kind);
  if (!brand) pluginBrands.set(kind, (brand = pluginBrand(kind)));
  return brand;
}

function ClientBadge({ kind, size }: { kind: ClientKind; size: number }) {
  const BadgeIcon = brandOf(kind).badge;
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
  const { Mark } = brandOf(kind);
  return (
    <span aria-hidden className="relative inline-flex shrink-0 items-center justify-center text-[var(--foreground)]" style={{ width: size, height: size }}>
      <Mark size={size} aria-hidden />
      <ClientBadge kind={kind} size={size} />
    </span>
  );
}

/** A client's brand avatar (mark on its brand tile), for headers and larger identity spots. */
export function ClientIcon({ kind, size = 28 }: { kind: ClientKind; size?: number }) {
  const { Avatar } = brandOf(kind);
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
  qoder: Qoder.Avatar,
};

// Providers the icon library does not ship: a locally bundled vendor image, keyed by the same
// template `icon` id. Kept out of LOBE_PROVIDER_AVATARS so the library lookup stays uniform.
const LOCAL_PROVIDER_LOGOS: Record<string, string> = {
  nextcowork: nextcoworkLogo,
  routin: routinLogo,
  workbuddy: workbuddyLogo,
};

// magpie's icon names (plugin market listings, plugin providers' fallback icon) that Astra ships under another name
const PROVIDER_ICON_ALIASES: Record<string, string> = { xai: 'grok', zcode: 'glm', 'kiro-color': 'kiro', 'workbuddy-color': 'workbuddy' };

function LogoTile({ src, size }: { src: string; size: number }) {
  return (
    <span
      aria-hidden
      className="relative inline-flex shrink-0 items-center justify-center overflow-hidden bg-white shadow-[inset_0_0_0_0.5px_rgb(0_0_0/0.08)]"
      style={{ width: size, height: size, borderRadius: Math.round(size * 0.28) }}
    >
      <img src={src} alt="" draggable={false} decoding="async" className="size-full object-contain" />
    </span>
  );
}

// Regional builds of one vendor share its mark (WorkBuddy / WorkBuddy AI, Qoder / Qoder CN): the variant whose name
// carries the region suffix gets a corner badge, "CN" for a China build and a globe for an international one.
const CN_VARIANT = /(?:[\s-]|^)(?:cn|china)$|(?:国内|中国)版?$/i;
const INTL_VARIANT = /(?:[\s-]|^)(?:intl|international|global)$|国际版?$/i;
// brands whose international build is named "<brand> AI" (a generic " AI" suffix would also match "Moonshot AI")
const AI_SUFFIX_INTL: Record<string, RegExp> = { workbuddy: /workbuddy[\s-]?ai$/i };

function providerRegion(name: string, key: string | undefined): 'cn' | 'intl' | null {
  const n = name.trim();
  if (CN_VARIANT.test(n)) return 'cn';
  if (INTL_VARIANT.test(n) || (key && AI_SUFFIX_INTL[key]?.test(n))) return 'intl';
  return null;
}

function RegionBadge({ region, size }: { region: 'cn' | 'intl'; size: number }) {
  const box = Math.max(9, Math.round(size * 0.45));
  return (
    <span
      data-region-badge={region}
      className="absolute -right-0.5 -bottom-0.5 inline-flex items-center justify-center rounded-[3px] bg-[var(--surface-raised)] font-semibold leading-none text-[var(--foreground)] shadow-[0_0_0_1px_var(--border)]"
      style={{ minWidth: box, height: box, fontSize: Math.max(6, Math.round(box * 0.6)), paddingInline: region === 'cn' ? 1 : 0 }}
    >
      {region === 'cn' ? 'CN' : <Globe style={{ width: box * 0.78, height: box * 0.78 }} strokeWidth={2.5} />}
    </span>
  );
}

export function ProviderIcon({ name, icon, colorKey, size = 28 }: { name: string; icon?: string | null; colorKey?: string | null; size?: number }) {
  const key = icon ? (PROVIDER_ICON_ALIASES[icon] ?? icon) : undefined;
  const region = providerRegion(name, key);
  const mark = <ProviderMark name={name} icon={icon} colorKey={colorKey} size={size} />;
  if (!region) return mark;
  return (
    <span className="relative inline-flex shrink-0" style={{ width: size, height: size }}>
      {mark}
      <RegionBadge region={region} size={size} />
    </span>
  );
}

function ProviderMark({ name, icon, colorKey, size }: { name: string; icon?: string | null; colorKey?: string | null; size: number }) {
  // a plugin provider's own picture (PluginProviderSync keeps only https: and raster data:image URLs)
  if (icon && /^(https:|data:image\/)/i.test(icon)) return <LogoTile src={icon} size={size} />;
  const key = icon ? (PROVIDER_ICON_ALIASES[icon] ?? icon) : undefined;
  const Avatar = key ? LOBE_PROVIDER_AVATARS[key] : undefined;
  if (Avatar) {
    return <Avatar size={size} shape="square" style={{ borderRadius: Math.round(size * 0.28) }} />;
  }
  const logo = key ? LOCAL_PROVIDER_LOGOS[key] : undefined;
  if (logo) return <LogoTile src={logo} size={size} />;
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

// Model-id patterns → the model family's brand mark, first match wins. Model ids carry no vendor field, so this is a
// best-effort guess from the name (case-insensitive, matched anywhere so "anthropic/claude-…" and "us.anthropic.…"
// work too); an id matching nothing shows a neutral box.
const MODEL_MARKS: [RegExp, BrandMark][] = [
  [/claude|anthropic/, Claude.Color],
  [/gemma/, Gemma.Color],
  [/gemini/, Gemini.Color],
  [/\bgpt|\bo[1-9]\b|o[1-9]-|codex|openai|dall-e|whisper|chatgpt/, OpenAI],
  [/deepseek/, DeepSeek.Color],
  [/qwen|qwq|qvq/, Qwen.Color],
  [/kimi|moonshot/, Kimi.Color],
  [/glm|zhipu|chatglm/, ChatGLM.Color],
  [/minimax|abab/, Minimax.Color],
  [/grok|xai/, Grok],
  [/llama|meta/, Meta.Color],
  [/mistral|mixtral|codestral|devstral|magistral|pixtral/, Mistral.Color],
  [/doubao|\bseed-/, Doubao.Color],
  [/hunyuan/, Hunyuan.Color],
  [/ernie|wenxin/, Wenxin.Color],
  [/\byi-/, Yi.Color],
  [/baichuan/, Baichuan.Color],
  [/command|cohere/, Cohere.Color],
  [/step-/, Stepfun],
  [/mimo/, XiaomiMiMo],
  [/longcat/, LongCat.Color],
];

/** The brand mark of a model's family, guessed from its id (see MODEL_MARKS). Decorative, like ClientGlyph. */
export function ModelGlyph({ model, size = 16 }: { model?: string | null; size?: number }) {
  const id = (model ?? '').toLowerCase();
  const Mark = id ? MODEL_MARKS.find(([re]) => re.test(id))?.[1] : undefined;
  return (
    <span aria-hidden data-model-mark={Mark ? 'brand' : 'none'} className="inline-flex shrink-0 items-center justify-center text-[var(--foreground)]" style={{ width: size, height: size }}>
      {Mark ? <Mark size={size} aria-hidden /> : <Box className="text-[var(--text-secondary)] opacity-60" style={{ width: size * 0.85, height: size * 0.85 }} />}
    </span>
  );
}

// Wire protocol → the vendor whose API it is, for the small marks in the request log's protocol line.
const PROTOCOL_MARKS: Record<ApiProtocol, BrandMark> = {
  'openai-chat': OpenAI,
  'openai-responses': OpenAI,
  anthropic: Anthropic,
  gemini: Gemini.Color,
};

/** The vendor mark of a wire protocol. Decorative; the protocol name is shown beside it. */
export function ProtocolGlyph({ protocol, size = 11 }: { protocol: ApiProtocol; size?: number }) {
  const Mark = PROTOCOL_MARKS[protocol] as BrandMark | undefined;
  if (!Mark) return null;
  return (
    <span aria-hidden data-protocol-mark={protocol} className="inline-flex shrink-0 items-center justify-center text-current" style={{ width: size, height: size }}>
      <Mark size={size} aria-hidden />
    </span>
  );
}
