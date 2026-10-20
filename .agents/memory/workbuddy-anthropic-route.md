---
name: workbuddy-anthropic-route
description: WorkBuddy (China) answers hy3 / hy3-x natively on POST /v1/messages (not /v2), every other model 500s there, WorkBuddy AI has no route; the plugin declares those two as @ai-sdk/anthropic with no reasoning levels (probed 2026-10-20)
type: project
updated: 2026-10-20
source: session
evidence: ["astra-gate-plugins packages/workbuddy/src/sites.mjs:speaksAnthropic", "astra-gate-plugins packages/workbuddy/src/plugin.mjs:loader fetch (messagesUrl)", "src/Astra.Plugins/Host/PluginProviders.cs:ProtocolOf", "src/Astra.Plugins/Host/PluginFetchHandler.cs:SendAsync (loaderBase wins over a model's url)"]
---
Probed with a real China-build account on 2026-10-20. `https://copilot.tencent.com/v1/messages` speaks Anthropic's own stream
(`message_start` … `message_stop`, thinking, `tool_use` / `input_json_delta`, `cache_read_input_tokens`; also non-stream) for
`hy3` and `hy3-x` only; the other 18 models of the plan answer 500 `internal_code 10000` every time while their chat
completion works, `/v2/messages` is a 404, and www.workbuddy.ai has no Anthropic path at all. The route takes what the chat
route refuses (the Claude Code system prompt, tool names with `.`, schemas with no `type`, every `tool_choice`), and ignores
every thinking switch (`thinking: disabled`, `budget_tokens`, `output_config.effort`, `reasoning_effort: none`), where the chat
route's `reasoning_effort: "none"` does turn thinking off.

The plugin (separate repo, `astragate-community/astra-gate-plugins`, `packages/workbuddy`) now lists those two models with
`api.npm = @ai-sdk/anthropic`, which makes `PluginProviders.ProtocolOf` give `ApiProtocol.Anthropic`, so the provider row has an
`anthropic` endpoint beside `openai-chat` and the gateway passes Anthropic clients through. The address matters:
`PluginFetchHandler` puts the loader's base (`/v2`) before the model's `url`, so the request arrives as `/v2/messages` and the
loader's fetch moves it to `/v1/messages` (`messagesUrl`); a per-model `url` alone would not work. Verified end to end on a
throwaway `ASTRA_HOME` (request log: `hy3 anthropic -> anthropic`, deepseek still `anthropic -> openai-chat`).

**Why:** native passthrough avoids the lossy Anthropic → chat translation for Claude clients. The price: these two models can
no longer have thinking switched off or tuned (so the plugin gives them no reasoning variants), and OpenAI-shaped clients are
now translated chat → Anthropic for them. A model's protocol is one per provider model, so this applies to every client.
**How to apply:** to add a model, POST it to `/v1/messages` with `{"model","max_tokens","stream":true,"messages"}` and look for
200 `text/event-stream`; then change `speaksAnthropic`'s pattern in the plugin and `docs/lib/plugin-market` / `Market/market.json`
(linked update 10) only after a plugin release. If the thinking-off loss matters more, drop the two ids from that pattern.
See also [[client-protocols-not-used]] (the client side of WorkBuddy, which stays Chat).
