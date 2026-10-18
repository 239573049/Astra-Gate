---
name: smart-routing-cold-judge
description: Why the judge has its own long-lived HTTP/2 client — the first real use timed out on every cold connection and silently routed "1+1" to the fallback grade
type: project
updated: 2026-10-18
source: session
evidence: ["src/Astra.Gateway/Pipeline/Upstream.cs:GatewayHttpClients.ForJudge", "src/Astra.Gateway/Routing/JudgeHttp.cs:JudgeHttp.PostAsync", "src/Astra.Gateway/Routing/IRoutingJudge.cs:JudgeEndpoint.ConnectAllowance", "src/Astra.Gateway/Routing/RoutingActivity.cs", ".plan/smart-routing.md §0.1"]
---
On 2026-10-18, the first real use of smart routing (TKCode through a relay of TypeSafe's Jev, behind a local HTTP proxy)
routed even "1+1" to the middle grade. The judge was not wrong — warm, it answered in 260–320 ms and put "1+1" on the
lowest grade with confidence 1.0 — it never answered at all: every live judgement timed out at the 2 s deadline, the
breaker opened after three, and each request went to the fallback grade without a trace outside the span attributes.

The cause was the connection, not the judge. The judge used the shared upstream client (`GatewayHttpClients.For(null)`),
whose pool drops a connection after 2 idle minutes; in session mode judgements are often minutes apart, so nearly every
one opened a new connection, and a TLS handshake through the proxy took 0.9–4.8 s (twice more than 5 s). Measured with
the same .NET handler: idle connections stayed reusable after 150 s and 330 s; an idle HTTP/1.1 connection on the
direct path died silently and hung the next request until its deadline, while HTTP/2 with keep-alive pings survived on
both paths; a request cancelled while connecting leaves .NET to finish the connection and pool it.

So the judge got its own client (`ForJudge`: one-hour idle pool, HTTP/2 when offered, 30 s pings), a call that has to
connect first gets `JudgeEndpoint.ConnectAllowance` (3 s) on top of its timeout (signalled by `ConnectCallback`), and the
console shows what recent decisions came to (`RoutingActivity`), because a fallback never fails a request and is
otherwise invisible. Rejected: warming up at startup or on a timer (it contacts a third party when nobody asked), pinning
a judgement that arrived after its deadline (a chained Responses request could move to another provider), a proxy setting
of the judge's own (the global custom proxy's bypass list already lets a directly reachable judge skip the proxy), and a
larger default timeout (one cold call must not make every slow judgement wait longer).

**Why:** A judge on a cold connection loses to its own deadline, and the loss looks exactly like a bad classification.
**How to apply:** Post judge calls only through `JudgeHttp.PostAsync`; when "everything lands on the fallback grade",
read `astra.route.source` / `fallback_reason` / `judge.new_connection` before touching grade descriptions or thresholds.
Related: [[smart-routing-decisions]].
