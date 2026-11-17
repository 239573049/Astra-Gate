---
name: provider-model-list-whitelist
description: Once a provider has a model list the gateway serves only its enabled models (user decision 2026-11-17); unlisted ids pass only for a provider with no list
type: project
updated: 2026-11-17
source: session
evidence: ["src/Astra.Gateway/Pipeline/EffectiveModelResolver.cs:ServesAsync", "src/Astra.Gateway/Pipeline/GatewayPipeline.cs:PrepareAsync", "tests/Astra.Server.IntegrationTests/GatewayPipelineTests.cs:A_Disabled_Or_Removed_Model_Is_Refused_Once_The_Provider_Has_A_Model_List"]
---
Until 0.14.3112 the gateway forwarded any model id to the provider it routed to: a model switched off or deleted in a provider's
list vanished from `/v1/models` and client configs but was still served (the user reported "disabling or deleting a model has no
effect"). On 2026-11-17 the user chose: a provider with at least one model row treats the list as a whitelist — a disabled row, a
row whose system model is disabled, or a missing row is a 404 `not_found_error` from `PrepareAsync` before anything goes upstream
(a pool / group / bindings member is skipped instead). A provider with an empty list still takes any id verbatim; the relay
(`RelayRuntime.ServesAsync`) takes every id.

**Why:** the user expects the list to decide what is served. Options declined: keep verbatim forwarding; a per-provider opt-in switch.

**How to apply:** a new caller that sends a model id upstream outside `PrepareAsync` should ask `IEffectiveModelResolver.ServesAsync`
too. Not covered yet: forwarded calls (embeddings / rerank / audio), images and count_tokens still forward unlisted ids — embedding
models are often absent from chat lists. Known risk: clients that send ids nobody listed (a Claude Code background Haiku id, a Codex
helper model) now get a 404 until the model is added. Separate gap: `ClientService.SyncModelListCoreAsync` skips a client whose
bound providers are all disabled, so its config keeps the old list (the gateway refuses it with 503); an empty list was not written
because no golden case covers an empty `models` extra.
