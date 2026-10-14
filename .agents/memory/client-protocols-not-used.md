---
name: client-protocols-not-used
description: Client protocol modes verified 2026-10-14 but deliberately not written (Zed Anthropic, Hermes Responses, OpenCode Gemini, WorkBuddy Responses)
type: project
updated: 2026-10-14
source: session
evidence: ["src/Astra.Core/Clients/ClientProtocols.cs:ClientProtocols.For", "src/Astra.Clients/Adapters/HermesAgentClientAdapter.cs", "src/Astra.Clients/Adapters/OpenCodeClientAdapter.cs"]
---
On 2026-10-14 every client's custom-endpoint protocols were checked against its installed build or upstream source, and
each adapter now writes a model's native protocol where the client allows it (`ClientProtocols.For`). Four modes that
exist were left out on purpose:
- **Zed `language_models.anthropic_compatible`** (Zed ≥ 1.8.2): a second provider whose key Zed keys by `api_url` in the
  keychain (or `ASTRA_ANTHROPIC_API_KEY`), so the user would have to paste the token a second time, and older Zed drops the
  models. Claude models stay on `openai_compatible` (translated). Fit for an opt-in client option, not a default.
- **Hermes `codex_responses`**: `provider: custom` ignores it for any non-OpenAI host (`_resolve_plain_custom_api_mode`).
  Named `providers:` entries with `transport` would work, but whether a leftover `model.base_url` beats them was not
  verified, so Responses models stay on Chat.
- **OpenCode / MiMo Gemini (`@ai-sdk/google`)**: needs a `/v1beta` base via per-model `provider.api`, which loses to the
  provider's `options.baseURL` (`resolveSDK`); moving the base to provider-level `api` was not tried for older versions.
- **WorkBuddy Responses**: CodeBuddy's docs show a per-model `"api":"openai-responses"`, but WorkBuddy AI 5.7.6 never reads
  it (only `OpenAIChatCompletionsModel` is used).

**Why:** each would break an existing setup or depends on behaviour nobody verified; translation is the safe fallback.
**How to apply:** revisit when the blocker is gone (new client version, a verified precedence); add the protocol to
`ClientProtocols.For` and the adapter together, with a test.
