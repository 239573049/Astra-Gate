---
name: smart-routing-decisions
description: Why smart routing uses a virtual model, a judge that is not a Provider, a session pin by default, and an OpenAI dialect that is still a draft
type: project
updated: 2026-10-18
source: session
evidence: ["src/Astra.Gateway/Routing/SmartRouter.cs:SmartRouter.DecideAsync", "src/Astra.Gateway/Routing/SmartRouter.cs:SmartRouter.ReachableAsync", "src/Astra.Gateway/Pipeline/GatewayRouter.cs:GatewayRouter.GradeTargetAsync", "src/Astra.Data/Migrations/0020_request_routing.sql", "src/Astra.Gateway/Pipeline/GatewayRouter.cs:GatewayRoute.Routed", "src/Astra.Gateway/Routing/DecisionsJudge.cs (class comment)", "https://docs.typesafe.ai/api.md", "https://docs.litellm.ai/docs/decisions"]
---
On 2026-10-18 smart routing (pick a model by how demanding a request is) was designed and built. A judge
model — TypeSafe's Jev, or OpenAI's Decisions — answers one multiple-choice question; the request goes to the model
of the grade it picks. The decisions, and the alternatives that were rejected:

- **A virtual model triggers it** (`astra-auto`, configurable). Clients opt in by choosing it; a real model name is
  never rewritten. Rejected for v1: routing every request of a token or client automatically, which would override
  models people chose on purpose.
- **The judge is not a Provider.** It is a decision model, not a chat protocol; a new `ApiProtocol` would have
  polluted the four-protocol hub-and-spoke IR. Its address, model and key live in the `smart_routing` settings
  (key protected with `ISecretProtector`), with one `IRoutingJudge` per wire dialect.
- **Session by default, per turn on request.** "Every time" means every new user turn: requests that only answer
  tool calls keep the turn's grade, because switching models mid-turn drops the prompt cache and may break thinking
  signatures. A literal "every HTTP request" was rejected. A side call (`aux`) is judged on its own and never pins
  the session; a sub-agent follows the main turn.
- **A fallback is never pinned.** A judge that fails, is not confident enough, or has nothing to read sends the
  request to the fallback grade (default: the middle one) and the session is judged again next time. The judge
  never fails the user's request.
- **Nor is the lowest grade** (added 2026-10-18, after the first real session began with "您好" and only then asked for an
  architecture review): in session mode a session judged lowest so far is judged again on its next turn, and the first
  turn judged higher pins it. A request inside a turn follows the turn's own grade before the session's pin. Rejected:
  "upgrade only" for every turn (a judge call per turn and a model switch whenever a turn needs more).
- **A grade names its provider** (added 2026-10-18, after "light → deepseek-v4.1-flash · WorkBuddy" was sent to the only
  provider TKCode was bound to, Grok, and failed): the console's picker showed a provider next to each model, but only the
  model id was saved and the caller's bindings chose the provider. Now `RoutingGrade.ProviderId` is saved and the request
  goes there. A token that allows any client may use any grade's provider — it can already reach every client's providers
  — but a token limited to some clients only its own targets, so the virtual model cannot carry a limited (perhaps shared)
  token past its reach. Rejected: requiring the grade's provider to be bound to every client (the user would have to bind
  each grade's provider to each client) and letting every token use any grade's provider.
- **A grade the caller cannot use gives way** (plan §7 risk 7): its provider out of the token's reach, switched off, no
  longer serving the model or Claude-Code-only — the request moves to the nearest grade it can use, stronger first, since
  the judge named the cheapest grade that does the work well, and the move is reported with its reason — a `GradeProblems`
  code, plus the provider's name (`astra.route.adjusted_reason`, the console status, the request log's mark); the user
  could not act on a bare "cannot use" (2026-10-18). The session and turn keep the decision from before the move.
- **The request log marks every routed request** (`requests.routing_json`, migration 0020): green when it went the
  judge's way, orange when it fell back or moved — asked for by the user after "1+1 → standard" looked like a misjudgement
  that only the trace could explain.
- **Changing the judge's address or dialect drops the saved key** unless a new one comes with it: a key belongs to
  the service that issued it, and must not be sent to another host.
- **The model is replaced through `GatewayRoute.Routed`**, never through `body["model"]` (that is the
  `upstreamDirty` trap in `.agents/codemap/flows/gateway-request.md`, and it would overwrite `requested_model`).
- **The OpenAI Decisions dialect is a draft.** OpenAI's own documentation refused the fetch (403) while it was
  written, so the fields follow LiteLLM's description of OpenAI's format. The fixtures in
  `tests/Astra.Gateway.Tests/Fixtures/routing/decisions-*.json` are not OpenAI's examples.
- **Unverified:** how Claude Code and Codex treat an unknown model name, whether Anthropic accepts thinking
  signatures from another model when the model changes between turns, and Jev's accuracy on Chinese text
  (TypeSafe only says "lower"). The arXiv paper 2608.14641 gives no evidence on session versus per-task routing.

**Why:** Each rule closes a way a routing decision could surprise someone: a model they did not choose, a key sent to
the wrong host, a request lost to a judge outage, a model switched in the middle of a task.
**How to apply:** Do not make the judge a Provider, do not rewrite the request's model in the tree, and do not pin a
fallback or the lowest grade. Verify the `openai` dialect against OpenAI's reference before relying on it, then replace
the fixtures with its examples. Related: [[key-self-service-requested-model]] (token holders see the virtual model as
`requested_model`), [[smart-routing-cold-judge]] (why the judge has its own client).
