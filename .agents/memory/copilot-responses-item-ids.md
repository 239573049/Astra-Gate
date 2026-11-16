---
name: copilot-responses-item-ids
description: Copilot rejects encrypted reasoning items issued by an earlier session with 401 "input item does not belong to this connection"; ids themselves are opaque and harmless (measured 2026-11-03)
type: project
updated: 2026-11-16
source: session
evidence: ["live calls via a scratch-home source build, provider github-copilot-subscription, model gpt-6.1-sol, store:false + reasoning.encrypted_content", "reasoning items from ~/.codex rollouts dated 2026-10-16/19/22 replayed on 2026-11-03", "src/Astra.Gateway/Protocol/Responses/ResponsesItemIds.cs"]
---
User report: Codex → Astra → GitHub Copilot GPT model fails with `401 input item does not belong to this
connection`. **Reproduced on 2026-11-03** by sending Copilot a `reasoning` item (with `encrypted_content`)
taken from a Codex session Copilot had answered 12–18 days earlier:
- item as-is → 401 `input item ID does not belong to this connection`
- `id` removed, `encrypted_content` kept → 401 `input item does not belong to this connection` (the user's text)
- `encrypted_content` removed → 200
So the binding is in the encrypted reasoning content, not the id. Stripping ids does not help.

What does NOT trigger it (all 200): turn 2 minutes after turn 1, also after a 150 s idle gap (new upstream
connection), after a forced Copilot session-token refresh, with a different `Copilot-Integration-Id`, and a
real Codex CLI 0.166.0 `exec` + `exec resume` run. Copilot ids are opaque base64-like strings; a made-up
`oi_…` id → 400 "Expected an ID that begins with 'rs'", an OpenAI-format `rs_…` id with Copilot content →
400 "item_id did not match". What the binding is tied to (time window? GitHub token/login?) is unknown.
Untested: items from the ChatGPT/Codex subscription continued on Copilot (that account was expired).

Decision (user, 2026-11-08): this is the caller's parameter error — `GatewayPipeline.SendAsync` recognises the message,
does not refresh, cool or switch the account, and answers 400 `invalid_request_error` with the reason and the fix.

Revised (user, 2026-11-16, after a user hit it in a non-Codex agent app): a **global** setting `AppSettings.StripStaleReasoning`
(default on; chosen over a per-client option — bare-token callers have no client — and over a provider setting) makes the gateway,
on that 401 only, drop `encrypted_content` from the reasoning input items (`ResponsesReasoningItems.WithoutEncryptedContent`) and
resend once on the same account (`astra.attempt.retry = stale_reasoning`). Requests are never stripped in advance, so normal turns
keep their reasoning continuity and cache. Off, nothing to strip, or refused again → the 400 as before.

**Why:** Long-lived or resumed sessions (and likely sessions that switch provider) carry reasoning items Copilot no longer
accepts; before 2026-11-08 the gateway took the 401 for a dead credential and refreshed in vain.
**How to apply:** Keep the fix the measured one (drop `encrypted_content`, keep ids). Dropping whole reasoning items is
untested against Copilot (OpenAI can refuse a function_call whose reasoning item is missing). Keep the 400's fix text current.
