---
flow: gateway-request
modules: [astra-gateway, astra-core, astra-data, astra-providers, astra-plugins, astra-server]
commit: 6324e30
updated: 2026-10-28
---
# Gateway request

**Trigger:** an AI client posts a generation request to the gateway (`/v1/chat/completions`, `/v1/responses`,
`/v1/messages`, `/v1beta/models/<model>:generateContent|streamGenerateContent`).
**Outcome:** the client got the upstream's answer (streamed or buffered) in its own protocol, with privacy redactions
restored, and a request row, its span tree and usage counters were queued for persistence.
**Hosts:** the server reaches the pipeline through `MapGatewayEndpoints`; `astra-server relay`
(`src/Astra.Server/Relay/RelayApp.cs`) maps its own routes onto the same `HandleAsync` and answers the pipeline's
contracts (`src/Astra.Gateway/Pipeline/PipelineContracts.cs`) per request — the caller's own credential, one template's
provider, its model list fetched with that credential — with no privacy guard, capture, caps, pools, middleware or
plugin providers (null): step 3 is `RelayRuntime.ResolveAsync`, steps 4, 6 and 7 are skipped, and nothing is
persisted at the end.

## Steps

1. `src/Astra.Gateway/Http/GatewayEndpoints.cs:MapGatewayEndpoints` — route asked for the inbound `ApiProtocol` and
   calls `GatewayPipeline.HandleAsync`; `/v1beta` path variants pass the model, `stream` and `?alt=sse` from the URL.
2. `src/Astra.Gateway/Pipeline/GatewayPipeline.cs:GatewayPipeline.HandleAsync` — opens the request record (ULID,
   `StartedAtUtc`, `Remote` from `RequestOrigin.IsRemote`, client IP), sets `x-astra-request-id`, and publishes it to
   `Pipeline/LiveRequestFeed.cs:LiveRequestFeed.Start` so the console shows it immediately.
3. `Pipeline/GatewayRouter.cs:GatewayRouter.ResolveAsync` — resolves the bearer token/key into a `GatewayRoute`
   (token, client kind, provider); the token, provider and client rows come from `astra-data`'s `RowCache`, so a steady
   request reads no SQLite here (an admin write invalidates it at once). A request marked remote always needs a token even when
   `RequireGatewayKey=false`; that switch only opens the gateway to this machine (`Pipeline/GatewayRouter.cs`). A bare
   token with a provider pool (`tokens.pool_json`, `Core/Tokens/TokenPool.cs`, issue #2) gets a route whose `Targets`
   are its usable members and whose `Pool` is set; `<token>.<kind>` calls never use the pool.
4. `Pipeline/TokenLimiter.cs:TokenLimiter.CheckAsync` — token budgets and the RPM window are checked once, before
   anything leaves the machine; a refusal throws `GatewayException` (`permission_error` / `rate_limit_error`).
5. `Pipeline/RequestBody.cs:RequestBody.ReadAsync` — the body is read once into a pooled UTF-8 buffer; everything
   after this reads that buffer or one JSON tree over it.
6. ⇢ in-process call into `astra-plugins`: `Pipeline/GatewayPipeline.cs:GatewayPipeline.HandleAsync` calls
   `MiddlewareEngine.BeginAsync` (`src/Astra.Plugins/Middleware/MiddlewareEngine.cs`) with the agent's own body; the
   request info (model and stream flag, `GatewayMiddleware.Info`, a scan of the whole body) is built only when a
   middleware is loaded.
   A hook may rewrite the bytes or reject; a rewrite is `RequestBody.Replace`, a reply is wrapped by
   `Plugins/MiddlewareResponseStream.cs:MiddlewareResponseStream.Install`. The reject is thrown in the agent's own
   error shape (`Plugins/GatewayMiddleware.cs:GatewayMiddleware.Rejected`).
7. `Pipeline/PrivacyGuardService.cs:PrivacyGuardService.InspectJson` (or `Inspect` for a non-object body) — runs
   after the middleware and before anything is sent. A `block` action throws; redactions are written into the parsed
   tree in place and the placeholder→original map becomes `RestoreMap`.
8. `Pipeline/RequestReasoning.cs:RequestReasoning.Capture` — log-only metadata (reasoning effort etc.), read before
   any codec derives upstream budgets from it. Never touches the wire.
9. ⇢ network (only for the virtual model, `SmartRoutingSettings.VirtualModel`, default `astra-auto`; the span
   `gateway.resolve` is already open): `Routing/SmartRouter.cs:SmartRouter.DecideAsync` reads the session's pin or the
   turn's grade from memory (`Routing/DecisionStore.cs`, keyed by token + `TraceLink.SessionKey` / `TurnId`; a pin of the
   lowest grade does not hold a new turn), else `Routing/AskExtractor.cs:AskExtractor.Extract` takes the user's own words
   out of the *guarded* body and an `IRoutingJudge` (`Routing/IRoutingJudge.cs`; TypeSafe System One or OpenAI
   Decisions, posted by `Routing/JudgeHttp.cs` on the judge's own long-lived HTTP/2 client,
   `GatewayHttpClients.ForJudge()`) picks one grade. A grade names its provider; `GatewayRouter.GradeTargetAsync` says
   whether this caller may use it (any provider for a token that allows any client, only its own targets for a limited
   one) and whether it serves the model — a grade the caller cannot use moves to the nearest one it can, stronger first
   (`RoutingDecision.AdjustedFrom`). The result sets `GatewayRoute.Routed`, which `ClientModelFor` honours, and
   `GatewayRouter.WithTarget` points the route at the grade's provider alone — so steps 10–13 see the grade's model and
   provider while the record keeps the virtual one as `requested_model` (and the decision as `routing_json`). A judge that
   fails, is not confident or has nothing to read falls back to `rules.fallbackGrade` (never pinned); off, the virtual
   model is refused with a 400. Attributes `astra.route.*` go on the `gateway.resolve` span; recent outcomes are kept in
   memory (`Routing/RoutingActivity.cs`) for the console's status. Settings: `Routing/SmartRoutingService.cs` (key
   `smart_routing`).
10. `Pipeline/GatewayRouter.cs:GatewayRouter.ForModelAsync` — a client bound to several providers gets the first one
   serving this model (as an enabled model or through its mapping), else its primary. A pool route instead gets an
   ordered candidate list from `Pipeline/PoolRouting.cs:PoolRouting.PlanAsync` (members serving the model, minus those
   `ProviderHealth` cools or without a usable account, ordered by `ProviderPoolScheduler` with sticky sessions).
   `SubscriptionSupport.EnforceClientPolicy` (`Pipeline/UpstreamAuthResolver.cs`) rejects before capability discovery
   can send anything.
11. `Pipeline/EffectiveModelResolver.cs` + `IModelProtocolResolver` — the upstream model and the protocols the
    provider actually supports are resolved without merging billing data (`ResolveProviderModelAsync`; capability discovery may hit the network). Unlisted names use the cached immutable matcher; provider-model misses share the 2-second/generation invalidation and 4,096-entry cache bound. Effective pricing is merged once afterwards, against the then-current catalog.
12. `Pipeline/GatewayRouter.cs:GatewayRouter.SelectEndpoint` + `Protocol/GatewayCodecs.cs:GatewayCodecs.Register` —
    the upstream protocol is chosen per model; `passthrough = upstreamProtocol == inbound`. With passthrough the
    client's own bytes (or the middleware's rewrite) go upstream; otherwise `IProtocolCodec.DecodeRequest` builds the
    IR (`Protocol/Ir.cs`) and the upstream codec re-encodes it. Missing codec → 501.
13. Same-host rewrites while the body tree is still ours: model mapping, `stream_options.include_usage` injection for
    OpenAI Chat streaming, the `force_stream` Responses shape, and the Claude Code mimicry / Relay `cache_control`
    TTL. Every stage that mutates the tree must set `upstreamDirty`, or the client's original bytes go upstream.
14. ⇢ network: `Pipeline/GatewayPipeline.cs:GatewayPipeline.SendAsync` sends through `Pipeline/AccountScheduler.cs`
    (`StickyKeys` picks the account; a `Lease` holds the in-flight slot in balanced mode), `UpstreamAuthResolver`
    supplies credentials, and `Pipeline/Upstream.cs:GatewayHttpClients` performs the call. That handler deliberately
    disables `ActivityHeadersPropagator` so the agent's `traceparent` never leaks to the provider. Steps 11–13 run in
    `HandleAsync`'s local `PrepareAsync`, once per candidate: a pool request whose attempt fails before anything reached
    the client (`Pipeline/ProviderFailover.cs` classifies the status / exception) is prepared again for the next member
    and resent (`astra.attempt.retry = provider_failover`); the client's guarded tree is cloned, never rewritten, while
    another attempt may follow. Every route reports its outcome to `ProviderHealth`.
    Account recovery is separate: `UpstreamAuth.CanRefresh` permits one renewal/reload on 401/403; direct tokens and
    API keys skip it. Failover/balanced modes cool a still-rejected account for `ProviderHealth.AuthCooldown` (10 min)
    and try an untried usable account (`auth_failover`), even after a successful renewal. Credential-resolution
    failures also hand over in those modes. Manual mode returns an upstream rejection without cooling or switching.
15. Response side: `Protocol/Ir.cs` decode (or passthrough), `Pipeline/PrivacyGuardService.cs:ChunkRestorer`
    restores placeholders (streaming: `Pipeline/GatewayPipeline.cs:StreamAsync`; buffered: `BufferAsync`;
    forced-stream-when-client-wanted-JSON: `CollapseStreamAsync`), `Pipeline/RequestBilling.cs` prices the usage, and
    an upstream failure goes through `ForwardErrorAsync` — rendered in the **inbound** protocol's error shape.
16. `finally`: `Finish` builds the record (status, tokens, cost, protocol, passthrough), `FinishTrace` writes its
    spans, `usageWriter.EnqueueAsync(record)` hands it to `Pipeline/UsageWriter.cs` (a bounded channel: past
    `QueueCapacity` = 2,000 buffered records a finishing request waits for the writer; waiting producers still
    retain their records, so this is not a bound on all outstanding work), which batch-writes through
    `RequestRepository` in the same transaction that accumulates `token_usage_totals` / `token_usage_daily` and
    `provider_account_usage_totals` (the final account, or the provider's own API key), then
    tells `TokenUsageLedger.Persisted`. `LiveRequestFeed.Finish` closes the live row.
17. ⇢ HTTP: the console reads the same rows back via `src/Astra.Server/Api/RequestEndpoints.cs`; the live view stays
    on the SSE feed.

## Sequence

```mermaid
sequenceDiagram
  participant C as AI client
  participant P as astra-gateway
  participant MW as astra-plugins (Jint)
  participant PR as privacy guard
  participant U as upstream provider
  participant D as astra-data
  C->>P: POST /v1/... (token, body)
  P->>P: GatewayRouter.ResolveAsync
  P->>P: TokenLimiter.CheckAsync
  P->>MW: BeginAsync(body)
  MW-->>P: rewritten bytes or reject
  P->>PR: InspectJson(tree, clientKind)
  PR-->>P: redacted tree + restore map
  P->>P: ForModelAsync, SelectEndpoint, codec
  P->>U: send (pass-through or re-encoded)
  U-->>P: SSE chunks / JSON
  P->>PR: ChunkRestorer.Push / Restore
  PR-->>C: restored answer
  P->>D: UsageWriter.EnqueueAsync -> request row + counters
```

## Errors and cancellation

- `GatewayException` is rendered by `GatewayPipeline.WriteErrorAsync` in the inbound protocol's shape and sets the
  record's `ErrorType`; a token refusal additionally sets `RecordErrorType = TokenLimits.RefusedErrorType`
  (`"token_limit"`) so such a row is not counted against the budget again (`TokenLimits.Counts`).
- `ProtocolException` is a client-body problem (400). `UpstreamIdleTimeoutException` → 504, `HttpRequestException` → 502,
  anything else → 500; all of them still write a record.
- Client disconnect: `OperationCanceledException` when `RequestAborted` fired sets `ClientCancelled`, cancels the
  upstream call with it, and still bills the usage seen so far — unless the end of the answer (finish or error event)
  was already written (`ResponseObserver.Delivered`): agents leave on `[DONE]` / `response.completed` while the ChatGPT
  backend keeps its stream open, and that request succeeded (or is the upstream error it delivered).
- Streaming holds the response open; `MiddlewareResponseStream.CompleteAsync` runs in `finally` so a plugin's held
  reply is flushed even on the error paths (its IO failures are swallowed).

## Where to hook in

- **Supported:** a gateway middleware plugin (the magpie `onRequest` / `onEvent` / `onResponse` contract) — it sees
  the agent's body first and its output is still subject to the privacy guard.
- **Supported:** a new wire protocol is one `IProtocolCodec` plus a `GatewayCodecs.Register` entry and golden fixtures;
  a new lossy mapping belongs in `Protocol/Ir.cs`'s comments and in `ir.Warnings`.
- **Supported:** a new smart-routing judge dialect is one `IRoutingJudge` plus a `RoutingJudges.Register` entry, a
  `JudgeProtocols` constant and golden fixtures (`tests/Astra.Gateway.Tests/RoutingJudgeTests.cs`).
- **Looks tempting but breaks:** handing `GatewayPipeline` a storage-backed service directly — the relay host has no
  storage; a new dependency is a contract in `PipelineContracts.cs` both hosts answer, or a nullable optional feature
  (AGENTS.md linked update 21).
- **Looks tempting but breaks:** rewriting the request tree without setting `upstreamDirty` silently ships the
  client's original bytes — which is why smart routing changes the model through `GatewayRoute.Routed`, never through
  `body["model"]`; managing the `AccountScheduler.Lease` by hand double-disposes it (`SendAsync` already
  disposes on its throwing path); a new capability probe placed before `EnforceClientPolicy` sends a request the
  client policy forbids; a routing decision keyed by the session alone (`TraceLink.SessionKey` is not scoped to a
  token — `SmartRouter` prefixes the token id).

## Related
- Modules: [astra-gateway](../modules/astra-gateway.md), [astra-core](../modules/astra-core.md),
  [astra-data](../modules/astra-data.md), [astra-providers](../modules/astra-providers.md),
  [astra-plugins](../modules/astra-plugins.md), [astra-server](../modules/astra-server.md)
- [request-tracing](request-tracing.md), [privacy-redaction](privacy-redaction.md),
  [token-limits-enforcement](token-limits-enforcement.md), [plugin-install-and-run](plugin-install-and-run.md),
  [admin-api-request](admin-api-request.md)
