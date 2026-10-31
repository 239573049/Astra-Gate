import type { JsonObject } from '../contracts.js';

/** Claude Code's model selection slots (Astra.Core ClaudeCodeModels). */
export const CLAUDE_CODE_MODELS_KEY = 'models';

/** Slot env var names, in UI order: the default model, then the fable / opus / sonnet / haiku tiers. */
export const CLAUDE_CODE_SLOTS: readonly string[] = [
  'ANTHROPIC_MODEL',
  'ANTHROPIC_DEFAULT_FABLE_MODEL',
  'ANTHROPIC_DEFAULT_OPUS_MODEL',
  'ANTHROPIC_DEFAULT_SONNET_MODEL',
  'ANTHROPIC_DEFAULT_HAIKU_MODEL',
];

/** The non-empty slot → model entries of an extras object, in slot order. */
export function parseClaudeCodeModels(extras: JsonObject | null | undefined): Array<[string, string]> {
  const result: Array<[string, string]> = [];
  const map = extras?.[CLAUDE_CODE_MODELS_KEY];
  if (map === null || typeof map !== 'object' || Array.isArray(map)) return result;
  for (const slot of CLAUDE_CODE_SLOTS) {
    const model = (map as JsonObject)[slot];
    if (typeof model === 'string' && model.trim() !== '') result.push([slot, model.trim()]);
  }
  return result;
}

/** The 1M-context marker Claude Code understands as a model-name suffix. */
export const ONE_MILLION_SUFFIX = '[1m]';

/**
 * The model with the `[1m]` suffix; unchanged when it already has it. The Haiku tier never gets it — Haiku has
 * no 1M variant; the Fable tier gets it like Opus and Sonnet.
 */
export const claudeCodeWith1M = (slot: string, model: string): string =>
  slot === 'ANTHROPIC_DEFAULT_HAIKU_MODEL' || model.toLowerCase().endsWith(ONE_MILLION_SUFFIX)
    ? model
    : model + ONE_MILLION_SUFFIX;
