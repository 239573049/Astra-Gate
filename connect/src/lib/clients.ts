import {
  CLIENTS_WITH_MODEL_LIST,
  ClientKinds,
  EnableContext,
  type ClientDetection,
  type IClientAdapter,
  type JsonObject,
} from '@aidotnet/client-config';
import type { ConnectContext, ContextModel } from './gateway.js';

/** Display names (ClientService.NameOf). */
const NAMES: Record<string, string> = {
  [ClientKinds.Codex]: 'Codex',
  [ClientKinds.ClaudeCode]: 'Claude Code',
  [ClientKinds.GeminiCli]: 'Gemini CLI',
  [ClientKinds.OpenCode]: 'OpenCode',
  [ClientKinds.ClaudeDesktop]: 'Claude Desktop',
  [ClientKinds.GrokBuild]: 'Grok Build',
  [ClientKinds.Pi]: 'Pi',
  [ClientKinds.HermesAgent]: 'Hermes Agent',
  [ClientKinds.MiniMaxCode]: 'MiniMax Code',
  [ClientKinds.CopilotCli]: 'Copilot CLI',
  [ClientKinds.VsCodeCopilot]: 'VS Code Copilot',
  [ClientKinds.Crush]: 'Crush',
  [ClientKinds.QwenCode]: 'Qwen Code',
  [ClientKinds.Droid]: 'Droid',
  [ClientKinds.KimiCode]: 'Kimi Code',
  [ClientKinds.Zed]: 'Zed',
  [ClientKinds.VsCodeInsiders]: 'VS Code Insiders',
  [ClientKinds.VsCodium]: 'VSCodium',
  [ClientKinds.Omp]: 'omp (oh-my-pi)',
  [ClientKinds.MiMoCode]: 'MiMo Code',
  [ClientKinds.DeepSeekHarness]: 'DeepSeek Harness',
  [ClientKinds.WorkBuddy]: 'WorkBuddy',
  [ClientKinds.WorkBuddyAi]: 'WorkBuddy AI',
  [ClientKinds.MuseCode]: 'Muse Code',
  [ClientKinds.NextCoWork]: 'NextCoWork',
  [ClientKinds.Paseo]: 'Paseo',
  [ClientKinds.ZCode]: 'ZCode',
  [ClientKinds.TKCode]: 'TKCode',
};

export const clientName = (kind: string): string => NAMES[kind] ?? kind;

/** Which model family a client is built for: its default model is the first of the key's models in that family. */
const FAMILY: Array<{ kinds: string[]; pattern: RegExp }> = [
  { kinds: [ClientKinds.ClaudeCode, ClientKinds.ClaudeDesktop, ClientKinds.Paseo], pattern: /claude/i },
  { kinds: [ClientKinds.GeminiCli], pattern: /gemini/i },
  { kinds: [ClientKinds.Codex, ClientKinds.MuseCode], pattern: /gpt|codex|^o\d/i },
  { kinds: [ClientKinds.GrokBuild], pattern: /grok/i },
  { kinds: [ClientKinds.QwenCode], pattern: /qwen/i },
  { kinds: [ClientKinds.KimiCode], pattern: /kimi/i },
  { kinds: [ClientKinds.MiniMaxCode], pattern: /minimax/i },
  { kinds: [ClientKinds.DeepSeekHarness], pattern: /deepseek/i },
];

/**
 * The default model for a client when the user did not pick one: the first model of the client's own family
 * (Claude Code → a Claude model, Codex → a GPT one …), else the first model of the key. The gateway translates
 * between protocols, so any model works; this only avoids surprising pairings.
 */
export function defaultModelFor(kind: string, models: readonly ContextModel[]): string | null {
  if (models.length === 0) return null;
  const family = FAMILY.find((f) => f.kinds.includes(kind));
  return (family && models.find((m) => family.pattern.test(m.id))?.id) ?? models[0]!.id;
}

export interface DetectedClient {
  adapter: IClientAdapter;
  name: string;
  detection: ClientDetection;
  files: string[];
}

/** The adapters that can configure a client today, with what was found on this machine. */
export function detectClients(adapters: readonly IClientAdapter[]): DetectedClient[] {
  return adapters
    .filter((a) => a.availability === 'available')
    .map((adapter) => ({
      adapter,
      name: clientName(adapter.kind),
      detection: adapter.detect(),
      files: adapter.configPaths(),
    }));
}

/**
 * The enable context for one client (ClientService.ContextAsync, with the company's key). The key written into the
 * client is the bare token: it routes by the token's own provider (a client-suffixed key would need the client to
 * be enabled and bound on the server).
 */
export function buildContext(
  kind: string,
  gatewayUrl: string,
  apiKey: string,
  context: ConnectContext,
  model: string | null,
): EnableContext {
  const extras: JsonObject = {};
  if (kind === ClientKinds.ClaudeCode) {
    // One env var per model slot; only the default model is pinned, an empty slot stays the client's own.
    if (model) extras.models = { ANTHROPIC_MODEL: model };
  } else if (kind === ClientKinds.ClaudeDesktop) {
    // Claude Desktop only accepts Claude role ids: the gateway maps the role to the chosen model.
    if (model) extras.roleMap = { sonnet: model };
  } else if (CLIENTS_WITH_MODEL_LIST.includes(kind)) {
    const models: JsonObject = {};
    for (const m of context.models) {
      const entry: JsonObject = { id: m.id, name: m.name };
      if (m.contextWindow !== undefined) entry.contextWindow = m.contextWindow;
      if (m.maxOutputTokens !== undefined) entry.maxOutputTokens = m.maxOutputTokens;
      if (m.vision !== undefined) entry.vision = m.vision;
      if (m.reasoning !== undefined) entry.reasoning = m.reasoning;
      if (m.pdf !== undefined) entry.pdf = m.pdf;
      if (m.protocols && m.protocols.length > 0) entry.protocols = m.protocols;
      models[m.id] = entry;
    }
    extras.models = models;
  }
  return new EnableContext({
    gatewayBaseUrl: gatewayUrl,
    localKey: apiKey,
    model,
    extras: Object.keys(extras).length > 0 ? extras : null,
  });
}
