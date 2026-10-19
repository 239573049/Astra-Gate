---
name: harness-reasoning-efforts-field
description: DeepSeek Harness shows a thinking-effort picker only for models that declare `reasoningEfforts`; `reasoning` is a route default, not a capability (the adapter declares low/medium/high for thinking models since 2026-10-20)
type: project
updated: 2026-10-20
source: session
evidence: ["src/Astra.Clients/Adapters/DeepSeekHarnessClientAdapter.cs:ModelsJson", "src/Astra.Clients/Adapters/PiAiModels.cs:AddHints", "github.com/deepseek-ai/deepseek-harness packages/llm/llm-pi-ai/src/config.ts (modelFields.reasoningEfforts)", "github.com/deepseek-ai/deepseek-harness .agents/notes/archived/feature/2026-08-08-pi-ai-per-model-reasoning-declarations.md", "GitHub issue 239573049/Astra-Gate#7"]
---
Harness's `llm-pi-ai` route model (`providers.<key>.models[]`) declares thinking support with `reasoningEfforts` — a dict of
level → wire spelling (`off:` valueless = Off offered, nothing sent; `high: high`), or `false` for a non-reasoning model.
A hand-declared model that leaves it out materializes as `reasoning: false`, so the supported levels are just `["off"]` and
the composer shows **no effort picker**. The route-level `reasoning` key is only the default *selection*, and `PiAiModels.AddHints`
writes the Pi / omp field `reasoning: bool`, which Harness does not read as a capability.

Until 2026-10-20 `DeepSeekHarnessClientAdapter.ModelsJson` (and its TS port) wrote only `id`, `name`, `contextWindow`,
`maxTokens`, `input`, so every Astra model in Harness had no picker (issue #7, found by reading code and upstream source; the
fix is verified by the golden case `deepseek-harness/reasoning-efforts`, not seen in the Harness app). It now writes
`reasoningEfforts: {low, medium, high}` for models whose `reasoning` hint is true, on all three routes. Deliberately no `off`:
only `off` may be valueless (resolution refuses an empty wire value for any other level), and a valueless `off` just omits the
field, so the model's own default (often thinking on) stays — offering it would promise a switch that does nothing. No `max` /
`xhigh`: the gateway passes the string through, but the adapter cannot tell which upstream accepts it. The user's
`agent-default-model.reasoningEffort` is still dropped while enabled (an effort the model does not declare is refused).

**Why:** the hint `reasoning` (`ClientService.ContextAsync`, only when `Capabilities.Reasoning` is known) is what decides it, so
a model whose capability is unknown (e.g. from a plugin provider) still gets no picker.
**How to apply:** the field name differs per client (`reasoning` for Pi / omp, `reasoningEfforts` for Harness); a change to the
declared levels is C#, `packages/client-config` and the golden cases together (linked update 20).
