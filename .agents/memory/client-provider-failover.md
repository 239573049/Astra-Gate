---
name: client-provider-failover
description: issue #28 — clients bound to several providers fail over along the binding order; global switch, default on (user decision 2026-11-09)
metadata:
  type: project
---

A client with several bindings retries a failed request (same `ProviderFailover.Classify` rules as token pools) on the next bound provider serving the model. One global `AppSettings.ClientProviderFailover`, default **on** — chosen over a per-client option because per-client options are mirrored in the TS `client-config` port and golden fixtures.

**Why:** unattended Agent sessions broke on one provider's 5xx/429; pools only covered bare-token direct calls.
**How to apply:** the pipeline reuses the pool loop (`poolPlan`, `PoolRouting.PlanBindingsAsync`); the web and Avalonia settings pages both have the switch. Behaviour change for existing users: billing may land on a later provider.
