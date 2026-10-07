-- Reasoning configuration each client explicitly set on its request, logged as plain metadata
-- (plan §6.5): effort the client named (Chat reasoning_effort, Responses reasoning.effort,
-- Anthropic output_config.effort, Gemini thinkingLevel), mode (Anthropic thinking.type:
-- adaptive / enabled / disabled; Gemini thinkingBudget semantics: -1 = auto, 0 = disabled) and
-- the raw thinking budget tokens (never mapped back to a level, never guessed from provider
-- defaults or token counts).
-- All three are nullable: rows written before this migration have no reasoning metadata, and a
-- request whose client set nothing records null. Nothing is back-filled.
-- Plain ALTER TABLE (NOT idempotent by design — 0001 is the only idempotent script).

ALTER TABLE requests ADD COLUMN reasoning_effort        TEXT;
ALTER TABLE requests ADD COLUMN reasoning_mode          TEXT;
ALTER TABLE requests ADD COLUMN reasoning_budget_tokens INTEGER;
