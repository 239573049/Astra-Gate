---
name: relay-decisions
description: Why `astra-server relay` hosts the same pipeline, serves one template per process, takes credentials per request, never takes an address from a request, and relays the model list as the upstream wrote it
type: project
updated: 2026-10-28
source: session
evidence: ["src/Astra.Server/Relay/RelayApp.cs:RelayApp.Create", "src/Astra.Server/Relay/RelayTarget.cs:RelayTarget.Caller", "src/Astra.Server/Relay/RelayTransport.cs:RelayTransport.SendAsync", "src/Astra.Gateway/Pipeline/PipelineContracts.cs", "src/Astra.Gateway/Pipeline/GatewayPipeline.cs:GatewayPipeline.SendAsync (diagnostics log gated on AccountId)", "src/Astra.Core/JsonContexts.cs:Registry.GetTypeInfo"]
---
On 2026-10-27 the relay (`astra-server relay`) was added: the gateway pipeline without storage, so Astra can run as a
stateless (serverless-container) gateway where every request brings its own upstream credential. The decisions:

- **A second host of the same pipeline, not a second pipeline.** An earlier out-of-tree host patched
  `GatewayPipeline.cs` from a submodule; the seams it needed became `PipelineContracts.cs` (contracts both hosts answer,
  nullable optional features), so protocol conversion stays in one place.
- **One template per process** (`--provider`). Path prefixes (`/{template}/v1/...`) for several templates in one
  process were considered and deferred: one container per template is the serverless shape anyway.
- **Credentials are the request's**: sent as they came — never stored, exchanged, refreshed or logged (not even the
  last 4 characters the subscription diagnostics log shows for stored accounts; that log is gated on `AccountId`).
  They go only to the template's (or variant's) origin or the operator's `--base-url`; a request may pick a declared
  variant (`x-astra-variant`) but never name an address.
- **The template keeps its own `AuthScheme`**, so subscription handling (Claude OAuth headers and client policy, Codex
  stream forcing, …) applies as on the server; account machinery stays idle because the credential has no account.
- **The model list, embeddings and token counts are the upstream's answer, relayed as is** (status, body,
  diagnostics): callers read fields Astra does not model (a model's policy state, its vendor), so it is not re-rendered.
- **Its startup exposed a latent bug**: `JsonContexts.Registry` enumerated its list while a module initializer added to
  it on the same thread (the server loads every assembly first and never hit it); it now walks by index.

**Why:** a gateway that keeps no state scales and redeploys freely, and per-request credentials let one deployment
serve many subscriptions without holding any of them.

**How to apply:** a new pipeline dependency follows linked update 21 in AGENTS.md; nothing a request carries may become
an upstream address; keep the relayed routes raw.
