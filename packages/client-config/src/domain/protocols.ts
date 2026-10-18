import type { JsonObject } from '../contracts.js';
import { ClientKinds, protocolOf } from './kinds.js';

/** The four wire protocols (Astra.Core ApiProtocol). */
export type ApiProtocol = 'openai-chat' | 'openai-responses' | 'anthropic' | 'gemini';

export const ApiProtocols = {
  openAIChat: 'openai-chat',
  openAIResponses: 'openai-responses',
  anthropic: 'anthropic',
  gemini: 'gemini',
} as const satisfies Record<string, ApiProtocol>;

/** Parses a protocol id or one of its aliases; null when unknown (ApiProtocols.TryParse). */
export function parseApiProtocol(id: string | null | undefined): ApiProtocol | null {
  switch (id?.trim().toLowerCase()) {
    case 'openai-chat':
    case 'openai':
    case 'chat':
      return 'openai-chat';
    case 'openai-responses':
    case 'responses':
      return 'openai-responses';
    case 'anthropic':
    case 'anthropic-messages':
      return 'anthropic';
    case 'gemini':
    case 'google':
      return 'gemini';
    default:
      return null;
  }
}

/** Key of the user's per-model protocol choices in an extras object. */
export const PROTOCOL_OVERRIDES_KEY = 'protocolOverrides';

const OPENAI_AND_ANTHROPIC: ApiProtocol[] = ['openai-chat', 'openai-responses', 'anthropic'];
const ALL_FOUR: ApiProtocol[] = ['openai-chat', 'openai-responses', 'anthropic', 'gemini'];

/**
 * The protocols Astra can write for one model of this client, the client's default first. A single entry means
 * every model goes over that protocol (Astra.Core ClientProtocols.For).
 */
export function clientProtocolsFor(kind: string): ApiProtocol[] {
  const K = ClientKinds;
  switch (kind) {
    case K.NextCoWork:
    case K.Droid:
    case K.KimiCode:
    case K.OpenCode:
    case K.MiMoCode:
    case K.CopilotCli:
    case K.MiniMaxCode:
    case K.DeepSeekHarness:
    case K.VsCodeCopilot:
    case K.VsCodeInsiders:
    case K.VsCodium:
    case K.ZCode:
    case K.TKCode:
      return OPENAI_AND_ANTHROPIC;
    case K.Pi:
    case K.Omp:
    case K.QwenCode:
    case K.Crush:
      return ALL_FOUR;
    case K.GrokBuild:
      return ['openai-responses', 'openai-chat', 'anthropic'];
    case K.Zed:
      return ['openai-chat', 'openai-responses'];
    case K.HermesAgent:
      return ['openai-chat', 'anthropic'];
    case K.Paseo:
      return ['anthropic', 'openai-responses'];
    default:
      return [protocolOf(kind)];
  }
}

/** The user's per-model protocol choices in an extras object; entries that do not parse are skipped. */
export function protocolOverrides(extras: JsonObject | null | undefined): Map<string, ApiProtocol> {
  const result = new Map<string, ApiProtocol>();
  const overrides = extras?.[PROTOCOL_OVERRIDES_KEY];
  if (overrides === null || typeof overrides !== 'object' || Array.isArray(overrides)) return result;
  for (const [model, value] of Object.entries(overrides as JsonObject)) {
    const protocol = typeof value === 'string' ? parseApiProtocol(value) : null;
    if (protocol) result.set(model, protocol);
  }
  return result;
}
