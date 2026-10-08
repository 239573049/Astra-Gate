/** Claude Code model slots the console writes into ~/.claude/settings.json (plan §7.3). */
export const CLAUDE_CODE_MODELS = ['ANTHROPIC_MODEL', 'ANTHROPIC_DEFAULT_OPUS_MODEL', 'ANTHROPIC_DEFAULT_SONNET_MODEL', 'ANTHROPIC_DEFAULT_HAIKU_MODEL'] as const;
export type ClaudeCodeModel = (typeof CLAUDE_CODE_MODELS)[number];
export type ModelSlots = Partial<Record<ClaudeCodeModel, string>>;

/** The non-empty, known slots of a client's extras.models. */
export function modelSlotsOf(extras: Record<string, unknown> | null | undefined): ModelSlots {
  const raw = extras?.models;
  const out: ModelSlots = {};
  if (!raw || typeof raw !== 'object') return out;
  for (const slot of CLAUDE_CODE_MODELS) {
    const v = (raw as Record<string, unknown>)[slot];
    if (typeof v === 'string' && v.trim()) out[slot] = v.trim();
  }
  return out;
}

/** Stable comparison key of a model slot map. */
export const modelSlotsKey = (m: ModelSlots) => CLAUDE_CODE_MODELS.map((s) => m[s] ?? '').join('\n');
