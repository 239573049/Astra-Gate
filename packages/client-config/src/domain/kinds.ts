import type { ApiProtocol } from './protocols.js';

/** Supported client kinds (Astra.Core ClientKinds). */
export const ClientKinds = {
  Codex: 'codex',
  ClaudeCode: 'claude-code',
  GeminiCli: 'gemini-cli',
  OpenCode: 'opencode',
  ClaudeDesktop: 'claude-desktop',
  GrokBuild: 'grok-build',
  Pi: 'pi',
  HermesAgent: 'hermes-agent',
  MiniMaxCode: 'minimax-code',
  CopilotCli: 'copilot-cli',
  VsCodeCopilot: 'vscode-copilot',
  Crush: 'crush',
  QwenCode: 'qwen-code',
  Droid: 'droid',
  KimiCode: 'kimi-code',
  Zed: 'zed',
  VsCodeInsiders: 'vscode-insiders',
  VsCodium: 'vscodium',
  Omp: 'omp',
  MiMoCode: 'mimo-code',
  DeepSeekHarness: 'deepseek-harness',
  WorkBuddy: 'workbuddy',
  WorkBuddyAi: 'workbuddy-ai',
  MuseCode: 'muse-code',
  NextCoWork: 'nextcowork',
  Paseo: 'paseo',
  ZCode: 'zcode',
  TKCode: 'tkcode',
} as const;

export const ALL_CLIENT_KINDS: readonly string[] = [
  ClientKinds.Codex,
  ClientKinds.ClaudeCode,
  ClientKinds.GeminiCli,
  ClientKinds.OpenCode,
  ClientKinds.ClaudeDesktop,
  ClientKinds.GrokBuild,
  ClientKinds.Pi,
  ClientKinds.HermesAgent,
  ClientKinds.MiniMaxCode,
  ClientKinds.CopilotCli,
  ClientKinds.VsCodeCopilot,
  ClientKinds.Crush,
  ClientKinds.QwenCode,
  ClientKinds.Droid,
  ClientKinds.KimiCode,
  ClientKinds.Zed,
  ClientKinds.VsCodeInsiders,
  ClientKinds.VsCodium,
  ClientKinds.Omp,
  ClientKinds.MiMoCode,
  ClientKinds.DeepSeekHarness,
  ClientKinds.WorkBuddy,
  ClientKinds.WorkBuddyAi,
  ClientKinds.MuseCode,
  ClientKinds.NextCoWork,
  ClientKinds.Paseo,
  ClientKinds.ZCode,
  ClientKinds.TKCode,
];

/** Clients whose configuration lists the bound provider's models (ClientKinds.WithModelList). */
export const CLIENTS_WITH_MODEL_LIST: readonly string[] = [
  ClientKinds.OpenCode,
  ClientKinds.Pi,
  ClientKinds.MiniMaxCode,
  ClientKinds.CopilotCli,
  ClientKinds.VsCodeCopilot,
  ClientKinds.Crush,
  ClientKinds.QwenCode,
  ClientKinds.Droid,
  ClientKinds.KimiCode,
  ClientKinds.Zed,
  ClientKinds.VsCodeInsiders,
  ClientKinds.VsCodium,
  ClientKinds.Omp,
  ClientKinds.MiMoCode,
  ClientKinds.DeepSeekHarness,
  ClientKinds.WorkBuddy,
  ClientKinds.WorkBuddyAi,
  ClientKinds.NextCoWork,
  ClientKinds.Paseo,
  ClientKinds.ZCode,
  ClientKinds.TKCode,
  ClientKinds.MuseCode,
  ClientKinds.Codex,
  ClientKinds.GrokBuild,
  ClientKinds.HermesAgent,
];

/** The inbound protocol each client speaks to the gateway. */
export function protocolOf(kind: string): ApiProtocol {
  switch (kind) {
    case ClientKinds.Codex:
    case ClientKinds.GrokBuild:
    case ClientKinds.MuseCode:
      return 'openai-responses';
    case ClientKinds.ClaudeCode:
    case ClientKinds.ClaudeDesktop:
    case ClientKinds.Paseo:
      return 'anthropic';
    case ClientKinds.GeminiCli:
      return 'gemini';
    default:
      return 'openai-chat';
  }
}
