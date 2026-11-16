---
name: routing-groups-decisions
description: magpie-parity (2026-11-16): quota-aware account switch modes and named routing groups `group/<slug>` — what was ported, what was simplified, what is unverified
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Core/Routing/QuotaRanking.cs", "src/Astra.Core/Routing/ConversationStays.cs", "src/Astra.Gateway/Routing/GroupRouter.cs", "src/Astra.Gateway/Routing/GroupScheduler.cs", "src/Astra.Gateway/Pipeline/AccountScheduler.cs:Pick", "tests/Astra.Server.IntegrationTests/RoutingGroupGatewayTests.cs"]
---
Ported from magpie (cb4ad12, MIT): the 90% / 98% thresholds, smart (soonest-renewing window, by the hour), pace (share of the week left per
hour, 10% "alike" band), usage, the rotate / weight / order group modes, nesting, and `stays` (auto / session / turn / off / strict). Decisions
and simplifications, so nobody has to rediscover them:

- **Quota data is never required.** An account snapshot older than 6 h, a window whose reset already passed, or no snapshot at all is *unknown*;
  unknown ranks behind known candidates but ahead of 90%+ ones, and when nothing is known the failover / given order stands. magpie's "an unknown
  Claude / ChatGPT account goes first once to learn what it has left" was **not** ported (Astra's quota sync already reads those).
- **A group member's standing** is the best of its usable accounts (smart ranking) or the provider's balance plans (`Provider.Quota.plans[].usedPercent`);
  an API-key provider with no plan percentage is unknown. A nested group stands for its best member and only the member that goes first spends the
  nested group's rotation / weights.
- **Affinity** = `ConversationStays`: auto holds within a turn, across turns only while the last answer is < 5 min old and cached >= 1,024 tokens
  (null = nothing reported yet, stays). Account-level affinity has no turn notion (`SendAsync` is not handed one): quota modes use the cache rule only.
  Rotate with `stays=auto` behaves as `turn` (otherwise it would never rotate inside a warm cache). `strict` only keeps a member that is still a
  candidate: a member the health gate dropped (cooling) is not forced back in — deliberate, a request must not be sent to a circuit-open provider.
- **Not ported:** speed-first (`fast`) mode, "sink rate-limited accounts", "use before expiry" by credits expiring before the window renews, per-conversation
  load spreading ("fewest other conversations of the last 30 min"), auto groups for a model id several providers serve (the task called it optional),
  group-of-decision-models / `/v1/systemone`, `listed=no` hiding member models from lists, per-client shortlist.
- **The slug never changes** after create (clients, other groups and smart-routing grades name it); delete is refused while another group holds it.
- **Smart-routing grades** may name `group/<slug>` through the API (`SmartRoutingService.CheckAgainst`); the web / Avalonia grade pickers do not offer groups yet.
- **Why the group's decision is also `astra.route.*` (source `group`)**: every trace view (web + Avalonia) already renders a route decision from those attributes, so the
  groups needed no trace UI of their own; the request log's `routing_json` carries `reason` (a `RankReasons` code).
- **Unverified:** Native AOT publish of the new code (JSON contexts are registered and `JsonRegistrationTests` covers the admin API on the JIT, Dapper row type is public like
  `LocalMcpServer`), a real Claude / Codex quota snapshot driving `smart` / `pace` (tests use synthetic ones), the Avalonia pages on a real display (built, unit-tested, not run).

**Why:** the user asked for quota-aware selection and named groups as one feature (magpie parity); the pipeline already had a failover loop (pools, issue #28) so groups reuse it
instead of a second retry engine.
**How to apply:** a new mode is a `QuotaModes`/`GroupModes` constant plus a branch in `QuotaRanking` / `GroupScheduler.Order`; a new reason is a `RankReasons` constant plus the zh/en
`routingGroups.reason.*` key (a test fails without it). Never rewrite `body["model"]` for a member — set `GatewayRoute.Routed` (`GroupRouter.RouteFor`).
