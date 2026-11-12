---
name: codex-model-list-slug
description: The Codex subscription's "fetch models" failed with "invalid model list" (issue #35) because chatgpt.com/backend-api/codex/models returns {"models":[{"slug",…}]}, not data[].id; what was verified and what was not
type: project
updated: 2026-11-13
source: session
evidence: ["src/Astra.Server/Api/ProviderProbe.cs:ProviderProbe.ModelsAsync", "tests/Astra.Server.IntegrationTests/ProviderApiTests.cs:Remote_Models_Read_The_Codex_Backends_Slug_Rows", "~/.codex/models_cache.json (the Codex CLI's own cache of this response)"]
---
Issue #35: "获取模型" on the `openai-subscription` (ChatGPT / Codex) provider showed the 502
"Upstream returned an invalid model list". The template has no `modelListEndpoint`, so the probe asked
`<base>/models` and read rows by `id` / `name` only; the Codex backend names a row's id **`slug`**
(`{"models":[{"slug","display_name","visibility","supported_in_api",…}]}`, seen in the Codex CLI's
`~/.codex/models_cache.json`, which also records the `client_version` it asked with).

Fix (2026-11-13): `ModelsAsync` reads `id ?? slug ?? name`; for this provider it also sends the CLI's
`client_version` (`ProviderProbe.CodexModelListClientVersion`) and its User-Agent
(`SubscriptionQuotaService.CodexClientUserAgent`), as the CLI does.

**Why:** the first part is verified (the new test fails with the issue's exact message without the `slug` fallback).
`client_version` and the User-Agent are **not** verified against a real account: an unauthenticated request to the
endpoint answers 401 JSON with or without them, so whether the backend requires them (or filters the list by the version)
is unknown — they follow what the CLI sends and the quota service's note that chatgpt.com blocks bare clients.

**How to apply:**
- Do not add a `modelListEndpoint` to the Codex template without also teaching the relay's `RelayCatalog.Parse`
  (`data[].id` / Gemini `models[].name` only) the `slug` shape: the relay would start fetching the list and refuse
  every Codex request with "上游返回的模型列表格式不正确".
- The list includes `visibility: "hide"` rows (`codex-auto-review`, `gpt-reserve`) on purpose — it is a pick list of what
  the upstream reports. Filter in the UI, not in the probe, if that turns out to be noise.
- `CodexModelListClientVersion` goes stale like the quota User-Agent's 0.48.0; bump it from a fresh `models_cache.json`.
- Post-login model sync (`SyncModelsFromUpstreamAsync`) is Copilot-only, so Codex logins still do not auto-add models.
