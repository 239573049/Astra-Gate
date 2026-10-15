---
name: key-self-service-requested-model
description: The token holder's /key view may show request metadata and per-requested-model stats, but never anything upstream
type: project
updated: 2026-10-15
source: session
evidence: [".plan/alerts-webhooks-token-limits.md §2.1", "GitHub issue 239573049/Astra-Gate#2", "src/Astra.Gateway/Http/KeyInfoEndpoints.cs"]
---
On 2026-10-15 the user decided that a token holder, using only their `sk-astra-…` key on the self-service
page (`/key`, served by `/v1/astra/key/...`), may see their own request log and statistics: time, the model
the client itself sent (`requests.requested_model`), client kind, status, an error *category*, token counts,
cost and latency, plus a breakdown by that requested model and by client.

This reverses part of the earlier token-limits design (`.plan/alerts-webhooks-token-limits.md` §2.1, "不返回按模型
拆分的明细"), and only for `requested_model`. Everything else that rule protected still holds: never the
provider or account (ids or names), `upstream_model`, `system_model_id`, `response_model`, `error_message`
(gateway messages name providers), client IP, user agent, captured bodies, privacy data or trace/session ids.

**Why:** The holder already knows the model they asked for, so it reveals nothing about the upstream, while the
canonical/upstream model and provider names would expose the pool topology the token's owner set up (issue #2
pools are meant to hide it). The holder needs the log to see why requests failed or cost what they did.
**How to apply:** The rule itself is in AGENTS.md (Error and security boundaries, token-holder views). Holder queries must group by `requested_model`, never reuse `RequestRepository.TimeseriesAsync(by:"model")`
or `TopModelsAsync` (they use `COALESCE(system_model_id, requested_model)`). Map errors through a category
function instead of returning `error_message`/`error_type`. Keep tests asserting the raw holder JSON contains none
of the forbidden fields (see `TokenLimitTests.The_Key_View_Shows_Its_Own_Usage_And_Nothing_Upstream`).
