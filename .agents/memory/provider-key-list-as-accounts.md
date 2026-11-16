---
name: provider-key-list-as-accounts
description: magpie-parity (2026-11-16): several API keys per provider are api_key accounts behind a settings.key_pool flag, not a new table; a single key stays in Provider.ApiKeyEnc
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Server/Api/ProviderKeys.cs", "src/Astra.Core/Models/Provider.cs:KeyPool", "src/Astra.Gateway/Pipeline/UpstreamAuthResolver.cs:ResolveKeyAsync", "tests/Astra.Server.IntegrationTests/ProviderKeysTests.cs"]
---
`provider_accounts` already had `kind = api_key` rows (subscription endpoints that take a key), so a provider's key list reuses them:
weight / protocol / notes live in `extra_json` (no migration), the account switch modes, cooldown and per-account usage come free.
A provider holding one key is untouched (`Provider.ApiKeyEnc`, no flag); the second key flips `settings.key_pool` and `ApiKeyEnc` becomes a
mirror of the first enabled key so an older binary still routes.

**Why:** a parallel key system would have duplicated scheduling, cooldown, usage and trace attribution; the flag keeps every existing provider
on the old path with no data migration and lets the pipeline decide "account-based?" without a database read (`Provider.UsesAccounts`).
**How to apply:** any new place that asks "does this provider use accounts?" must use `Provider.UsesAccounts`, not `AuthSchemes.UsesAccounts(scheme)`;
never write `settings.key_pool` outside `ProviderKeys`; a provider write that replaces `settings` must keep it (`PATCH /api/providers/{id}` does).
Not done: the test run's HTTP client still uses the provider's proxy, not the chosen key's.
