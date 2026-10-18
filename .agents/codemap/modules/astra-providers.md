---
module: astra-providers
sources: ["src/Astra.Providers/**"]
commit: 6324e30
updated: 2026-10-18
---
# astra-providers — templates, quota probes, subscription credentials

`src/Astra.Providers` is the "data, not code" layer of the provider system: versioned provider templates
(`Templates/catalog.json`), the API-key balance/quota probe with its declarative extractor (`Quota/`), and the
subscription side — OAuth/PKCE/device-code flows, credential import classification, per-account token refresh and
per-family usage snapshots (`Subscription/`). It talks to nothing but `Astra.Core` and the ASP.NET Core shared
framework (`src/Astra.Providers/Astra.Providers.csproj` has one `ProjectReference` and one `FrameworkReference`),
so **all scheduling, persistence, HTTP endpoints and upstream auth application live in `astra-server` / `astra-gateway`**.

**Not responsible for:** the `providers` / `provider_accounts` rows and their SQL (`astra-data`); the admin endpoints,
the background workers, `ProviderQuotaManager`/`SubscriptionQuotaSync` bookkeeping and the alert engine
(`astra-server`); applying credentials to upstream requests and account stickiness/failover (`astra-gateway`);
plugin providers that mirror into the provider list (`astra-plugins`); client config take-over (`astra-clients`).

## Entry points

| Symbol | File | Role |
|---|---|---|
| `ProviderTemplateCatalog.All` / `.Get(id)` | `src/Astra.Providers/Templates/ProviderTemplateCatalog.cs:ProviderTemplateCatalog` | Loads every embedded `Templates/*.json` on construction, validates and enriches it; the only source of provider templates. |
| `ProviderQuotaService.QueryAsync` | `src/Astra.Providers/Quota/ProviderQuotaService.cs:ProviderQuotaService.QueryAsync` | Runs one balance/quota request and normalizes the answer into a snapshot; never throws, never mutates provider state. |
| `ProviderQuotaService.Effective` | `src/Astra.Providers/Quota/ProviderQuotaService.cs:ProviderQuotaService.Effective` | Resolves config → quota template (explicit id, `custom`, or the catalog's suggestion). |
| `QuotaTemplateCatalog.Suggest` | `src/Astra.Providers/Quota/QuotaTemplate.cs:QuotaTemplateCatalog.Suggest` | Picks the default template by provider-template id first, then by endpoint host. |
| `QuotaConfig.From` / `.ToNode()` / `.Validate` | `src/Astra.Providers/Quota/QuotaConfig.cs:QuotaConfig` | The `settings.quota` document: read leniently, write back, validate (used by `ProviderQuotaManager`). |
| `SubscriptionQuotaService.FetchAsync` | `src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService.FetchAsync` | Per-family usage probe, persists `extra.quota`, marks the account revoked when the grant is dead. |
| `SubscriptionTokenService.GetValidAccessTokenAsync` / `.CanRefresh` | `src/Astra.Providers/Subscription/SubscriptionTokenService.cs:SubscriptionTokenService.GetValidAccessTokenAsync` | Returns a usable access token; renewal grants refresh shortly before expiry, direct tokens last until actual expiry, and API keys are used as provided. `CanRefresh` identifies renewal grants for gateway auth recovery. |
| `SubscriptionTokenService.RefreshAsync` | `src/Astra.Providers/Subscription/SubscriptionTokenService.cs:SubscriptionTokenService.RefreshAsync` | The only writer of account status on OAuth errors (serialized per account). |
| `OAuthClient` | `src/Astra.Providers/Subscription/OAuthClient.cs:OAuthClient` | Every OAuth hop: authorize URL, code exchange, refresh, device code, ZCode CLI, Copilot second hop, identity. |
| `SubscriptionCatalog.All` / `.Effective` / `.IsReady` | `src/Astra.Providers/Subscription/SubscriptionOAuth.cs:SubscriptionCatalog` | The per-family OAuth endpoints and the `settings.subscription_oauth` override merge. |
| `CredentialImport.Classify` | `src/Astra.Providers/Subscription/CredentialImport.cs:CredentialImport.Classify` | Pure classification of a pasted credential into a `CredentialKinds` value (no network). |

## Key types and functions

| Symbol | File | Role |
|---|---|---|
| `ProviderTemplate` | `src/Astra.Providers/Templates/ProviderTemplateCatalog.cs:ProviderTemplate` | The template document: id/version/name/category/icon/links, endpoints, preferred protocols, `auth.scheme`, `requiresApiKey`, model list or `priceKey`/`modelVendor`, `adapter`, default headers/settings, `variants`, `modelListEndpoint`. |
| `ProviderTemplate.Clone` / `.WithVariant` | `src/Astra.Providers/Templates/ProviderTemplateCatalog.cs:ProviderTemplate.WithVariant` | Round-trips through `Json.Serialize`/`Json.Deserialize`; a variant may swap endpoints and the price key. |
| `TemplateAuth` / `TemplateVariant` / `TemplateModelListEndpoint` | `src/Astra.Providers/Templates/ProviderTemplateCatalog.cs:TemplateVariant` | The nested template parts; `AuthScheme` is a computed convenience property for API consumers. |
| `ProviderJsonContext` | `src/Astra.Providers/Templates/ProviderJsonContext.cs:ProviderJsonContext` | Source-gen metadata for templates + quota templates, self-registered into `JsonContexts` via `[ModuleInitializer]`. |
| `QuotaTemplate` / `QuotaRequestSpec` / `QuotaParamSpec` / `QuotaAuth` | `src/Astra.Providers/Quota/QuotaTemplate.cs:QuotaTemplate` | A built-in quota query: `kind` (`balance` \| `plan`), `appliesTo`, `hosts`, params, one request spec and an extractor. |
| `QuotaExpression` | `src/Astra.Providers/Quota/QuotaExpression.cs:QuotaExpression` | The AOT-safe path/operator evaluator (`$var`, `$scale`, `$pctUsed`, `$case`, `$time`, …) the extractor is written in. |
| `QuotaExtractor.Extract` / `.Validate` | `src/Astra.Providers/Quota/QuotaExtractor.cs:QuotaExtractor` | Applies an `extract` spec; cc-switch-shaped field names, drops any plan without a numeric field. |
| `QuotaErrors` | `src/Astra.Providers/Quota/ProviderQuotaService.cs:QuotaErrors` | Machine-readable failure codes; `IsConfiguration` is what makes the endpoint answer 400. |
| `SubscriptionOAuthConfig` | `src/Astra.Providers/Subscription/SubscriptionOAuth.cs:SubscriptionOAuthConfig` | One family's OAuth contract incl. `Style` (`""`/`claude-loopback`/`deviceauth`/`github-copilot`/`zcode`/`zcli`), `BusinessLoginUrl`, `BizHost`, `ExtraHeaders`, `Verified`. |
| `ProxiedHttpClients` | `src/Astra.Providers/Subscription/ProxiedHttpClients.cs:ProxiedHttpClients` | Proxy pool + `ProxyOf(account, config)` (account proxy > provider proxy > system) and URL validation. |
| `CodexAuthFile` / `LocalCopilotLogin` / `XaiSsoNormalizer` / `XaiSsoDeviceFlow` | `src/Astra.Providers/Subscription/CodexAuthFile.cs:CodexAuthFile`, `…/LocalCopilotLogin.cs`, `…/XaiSsoNormalizer.cs`, `…/XaiSsoDeviceFlow.cs` | Local-credential import paths: `~/.codex/auth.json`, VS Code keychain/env GitHub tokens, Grok SSO cookie → OAuth tokens. |
| `Pkce` | `src/Astra.Providers/Subscription/SubscriptionOAuth.cs:Pkce` | RFC 7636 verifier/challenge, plus the Claude-Code-shaped base64url form. |
| `OAuthProtocolException` / `SubscriptionAuthException` | `src/Astra.Providers/Subscription/SubscriptionOAuth.cs:OAuthProtocolException` | Protocol failure (with `IsInvalidGrant`) vs "the user must log in again"; the server maps both to HTTP. |

## Dependencies

- Uses: `astra-core` via import — `Json.DeserializeApi<T>`/`Json.Serialize`
  (`src/Astra.Providers/Templates/ProviderTemplateCatalog.cs:ProviderTemplateCatalog.Load`), `SeedCatalog.Current`
  to fill a template's model list (`…:ProviderTemplateCatalog.Load`), `Provider`/`ProviderEndpoint`/`AuthSchemes`,
  `ProviderAccount`/`AccountStatus`/`AccountKinds`/`IProviderAccountStore`, `ISecretProtector`, and
  `JsonContexts.Add` (`src/Astra.Providers/Templates/ProviderJsonContext.cs:ProviderJsonContext.Register`).
- Used by: `astra-gateway` via import — `UpstreamAuthResolver(… SubscriptionTokenService tokens)`
  (`src/Astra.Gateway/Pipeline/UpstreamAuthResolver.cs:UpstreamAuthResolver`), `SubscriptionSupport.EffectiveConfig`
  → `SubscriptionCatalog.Effective` (`src/Astra.Gateway/Pipeline/UpstreamAuthResolver.cs:SubscriptionSupport`),
  `SubscriptionTokenService.ChatGptAccountId` (`…:UpstreamAuthResolver.ResolveAsync`).
- Used by: `astra-server` via DI and direct calls — `AddSingleton<ProviderTemplateCatalog>`
  (`src/Astra.Server/Hosting/AstraApp.cs:85`), `OAuthClient`/`SubscriptionTokenService`/`SubscriptionQuotaService`
  (`…:101-103`), `QuotaTemplateCatalog`/`ProviderQuotaService` (`…:117-118`), `ProviderQuotaManager` wrapping
  `ProviderQuotaService`, `SubscriptionQuotaSync.RefreshAsync` → `SubscriptionQuotaService.FetchAsync`, the
  account/quota endpoints in `src/Astra.Server/Api/SubscriptionEndpoints.cs`, and the workers
  `src/Astra.Server/Hosting/ProviderQuotaWorker.cs`, `…/SubscriptionRefreshWorker.cs`.
- Used by: `cli`, `web-ui`, `desktop-app` via HTTP — `/api/provider-templates`, `/api/provider-quota/templates`
  (`src/Astra.Server/Api/ProviderEndpoints.cs:36`, `:409`) and the per-provider/per-account quota routes; these
  clients never import the assembly.
- Not a dependency: `astra-data` — no repository or Dapper reference; row writes go through `IProviderAccountStore`
  and the server's `ProviderQuotaManager`.

## State it owns

- **Provider and quota templates are read-only data**: `Templates/catalog.json` and `Quota/quota-templates.json`,
  embedded by glob (`src/Astra.Providers/Astra.Providers.csproj:10-11`) and parsed into a fresh list per
  `new ProviderTemplateCatalog()` (`…:ProviderTemplateCatalog.Load`, an *instance* field initializer, not a cache).
  `Templates/placeholder.json` (`{}`) also matches that glob and is loaded then skipped as an empty template.
- **`settings_json.quota`** — the provider's quota settings document, including `secrets` encrypted with
  `ISecretProtector`. Declared and shaped by `src/Astra.Providers/Quota/QuotaConfig.cs:QuotaConfig`; written only by
  `PUT /api/providers/{id}/quota/config` through `ProviderQuotaManager.FromInput`.
- **`providers.quota_json` + `quota_checked_at_utc`** — the API-key snapshot (and the last failure). Migration:
  `src/Astra.Data/Migrations/0007_provider_quota.sql`. The only writer is `ProviderRepository.UpdateQuotaAsync`
  (`src/Astra.Data/Repositories/ProviderRepository.cs:193`); never written by a full-row update.
- **`provider_accounts.extra_json`** keys this module writes: `quota` (successful snapshot, also `credits`) and
  `setup_token` (`SubscriptionQuotaService.SetupTokenFlag`); the server adds `quotaSync`. Token columns
  (`access_token_enc`, `refresh_token_enc`, `expires_at_utc`) are written through `IProviderAccountStore.UpdateTokensAsync`.
- **All outbound HTTP clients**: named clients `astra-provider-quota`, `astra-subscription-quota`, `astra-oauth`
  (registered with timeouts in `src/Astra.Server/Hosting/AstraApp.cs:209-213`), plus ad-hoc proxied clients in
  `ProxiedHttpClients` / `ProviderQuotaService.ProxyClient`.

## Contracts it exposes

- **Provider template JSON** (camelCase on the wire, `ProviderTemplate` in
  `src/Astra.Providers/Templates/ProviderTemplateCatalog.cs:ProviderTemplate`): `id`, `version`, `name`, `category`,
  `icon`/`website`/`apiKeyUrl`/`docsUrl`, `endpoints[] {protocol, baseUrl, fullUrl}`, `preferredUpstreamProtocols[]`,
  `auth {scheme}`, `requiresApiKey`, `models[]` (or `priceKey` / `modelVendor`, filled from the seed when empty),
  `priceKey`, `defaultPriceMultiplier`, `adapter`, `defaultHeaders`, `settings`, `variants[]`, `modelListEndpoint`.
  Validation on load: non-empty `id`/`name`, `version >= 1`, at least one endpoint, no duplicate id.
- **Quota template JSON** (`src/Astra.Providers/Quota/QuotaTemplate.cs:QuotaTemplate`): `id`, `name`, `kind`,
  `appliesTo[]`, `hosts[]`, `params[] {name, secret, required}`, `request {method, url, auth, headers}` (URL uses
  `{{name}}` placeholders; `auth` is `provider` | `none`), `extract {isValid, invalidMessage, planLabel, plans[]|flat}`.
  Each template is validated with `QuotaConfig.ValidateRequest` + `QuotaExtractor.Validate` at load.
- **`settings.quota` shape** — documented on `src/Astra.Providers/Quota/QuotaConfig.cs:QuotaConfig`:
  `enabled`, `template` (id | `custom` | null), `interval_minutes` (clamped 0..1440; 0 = never, null = global),
  `timeout_sec` (clamped 1..60), `base_url`, `params{}`, `secrets{}` (encrypted), `request{}`, `extract{}`.
  The API never returns secret values, only `ProviderQuotaConfigDto.Secrets` names.
- **API-key snapshot** (`src/Astra.Providers/Quota/ProviderQuotaService.cs:ProviderQuotaService.QueryAsync`) —
  `{fetchedAtUtc, template, kind, isValid, invalidMessage?, planLabel?, plans:[{name?, unit?, remaining?, total?,
  used?, usedPercent?, resetsAtUtc?, windowMinutes?, extra?}]}`; on failure the stored document is the previous one
  plus `{error, errorCode, errorAtUtc, failures}` (`src/Astra.Server/Api/ProviderQuotaManager.cs:ProviderQuotaManager.Merge`).
- **Subscription snapshot** (`src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService`) —
  `{fetchedAtUtc, session:{usedPercent, resetsAtUtc}, weekly:{…}, credits:{usedPercent, monthlyLimit, prepaidBalance},
  planLabel, entitlement?, remaining?, overageUsed?, overagePermitted?, account?}`; `ProbedFamilies` lists exactly the
  families with a probe, and `CanProbe` additionally excludes `setup_token` accounts.
- **Account lifecycle values** live in `astra-core` (`src/Astra.Core/Models/ProviderAccount.cs:AccountStatus` =
  `active|expired|revoked`, `:AccountKinds` = `oauth|api_key|plugin`), but the *decisions* are here:
  `invalid_grant` → revoked, other OAuth errors → expired, transport errors → unchanged.
- **Credential import contract**: `CredentialKinds` / `CredentialImportFormats` /
  `CredentialImport.FormatsOf(providerKey)`
  (`src/Astra.Providers/Subscription/CredentialImport.cs:CredentialImport.FormatsOf`) — the ids the UI turns into hints.
- **OAuth override keys**: `settings.subscription_oauth.{verified, client_id, authorize_url, token_url,
  device_code_url, device_token_url, cli_init_url, cli_poll_url, scopes, use_pkce, style, business_login_url,
  biz_host, api_key_name, redirect_uri, identity_url, extra_authorize_params, extra_device_params, extra_headers}`
  (`src/Astra.Providers/Subscription/SubscriptionOAuth.cs:SubscriptionCatalog.Effective`).
- **Alert signals**: this module reports none. It writes the state `astra-server`'s `AlertEngine.SweepAsync` reads
  (`providers.quota`, `accounts.extra.quota`, `SubscriptionQuotaSync.Failures`) — `IAlertSignals` is implemented by
  `src/Astra.Server/Alerts/AlertEngine.cs:AlertEngine`, not here.

## How to extend

- To add a **provider template**: a new entry (or a new `Templates/*.json` file — the loader globs
  `Templates/**.json` and every file is read) in `src/Astra.Providers/Templates/catalog.json`, with a unique id and at
  least one endpoint; then a `docs`/web icon if it has one. Missing the endpoint or reusing an id throws at the first
  `new ProviderTemplateCatalog()`, i.e. at server startup (`src/Astra.Providers/Templates/ProviderTemplateCatalog.cs:ProviderTemplateCatalog.Load`).
- To give a provider a **default quota query**: add a row to `Quota/quota-templates.json` with `appliesTo` (template id)
  and/or `hosts`, then (optional) a zh/en hint. It is picked up by `QuotaTemplateCatalog.Suggest`, which prefers
  `appliesTo` and falls back to the endpoint host; a `custom-*` template only wins on hosts it lists.
- To add a **subscription family**: (1) a `SubscriptionOAuthConfig` in `SubscriptionCatalog.All` keyed by the exact
  template id; (2) a template entry `*-subscription` in `Templates/catalog.json` — the template id **is** the family
  key (`ProviderKeyOf` = templateId ?? priceKey ?? id); (3) `CredentialImport.FormatsOf` branch; (4) a
  `SubscriptionQuotaService` probe branch plus `ProbedFamilies`. Missing (2) makes every family lookup fall back to
  `priceKey`/instance id → no import formats, no probe, no OAuth config.
- To add a **quota probe**: the `switch` in `SubscriptionQuotaService.FetchAsync` **and** the `ProbedFamilies` set
  (the comment there says to keep them in sync); the probe must return `null` only for a rejected grant so the forced
  refresh path runs, and should normalize via a `NewSnapshot()` helper.
- To add a **new value to `settings.quota`**: a property on `QuotaConfig` plus its `From`/`ToNode` branch, its
  `Validate` rule, and the `ProviderQuotaManager.FromInput` mapping (camelCase API name). Missing `ToNode` silently
  drops the setting on the next save; missing `Validate` lets an unusable request reach the upstream.
- To add a **new pasted credential format**: a `CredentialKinds` constant, a branch in `CredentialImport.Classify` /
  `ClassifyJson`, the format id in `FormatsOf`, and the actual exchange/normalization on the server side
  (`src/Astra.Server/Api/SubscriptionEndpoints.Import.cs`) — classification here is pure and talks to no network.

## Invariants and traps

- **`Quota/*.json` must stay a single file** — `QuotaTemplateCatalog.Load` resolves it with `Single(n => n.EndsWith(".Quota.quota-templates.json"))`, while the csproj embeds `Quota\*.json`; a second JSON file breaks the catalog with an `InvalidOperationException` at startup — see `src/Astra.Providers/Quota/QuotaTemplate.cs:QuotaTemplateCatalog.Load`.
- **The subscription family key is recomputed locally, on purpose** — `TemplateId ?? PriceKey ?? Id` appears in
  `SubscriptionQuotaService` and again in the gateway because "Providers 不引用 Gateway" — keep the two in step; a
  mismatch silently disables probes for that provider — see `src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService.FetchAsync`.
- **A short secret is not redacted** — `QueryAsync` only masks secrets/keys of length ≥ 4 in the returned URL and raw
  body, so a shorter one can appear in `QuotaQueryResult.Url`/`Raw` (returned by the quota *test* endpoint) — see
  `src/Astra.Providers/Quota/ProviderQuotaService.cs:ProviderQuotaService.QueryAsync`.
- **A quota failure must never change the provider** — a balance endpoint that refuses a key says nothing about whether
  the key can call models; failures are merged next to the last good snapshot and only `QuotaErrors.IsConfiguration`
  answers 400 — see `src/Astra.Providers/Quota/ProviderQuotaService.cs:ProviderQuotaService` and
  `src/Astra.Server/Api/ProviderEndpoints.cs:ProviderEndpoints.MapProviderEndpoints`.
- **401/403 only counts as a dead grant when the body is JSON** — `IsAuthRejection` deliberately excludes Cloudflare
  HTML refusals; treating them as auth failures would refresh and then mark healthy accounts revoked — see
  `src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService.IsAuthRejection`.
- **`ProbedFamilies` and the probe switch are two lists** — a family with a probe but not in the set is never refreshed
  in the background and never checked by alerts — see `src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService.ProbedFamilies`.
- **A Claude `setup-token` account must not be probed** — the token's scope does not cover the usage endpoint, so the
  403 would read as a dead grant — see `src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService.SetupTokenFlag`.
- **The quota service re-reads the account before writing the snapshot** — a concurrent token refresh (which rewrites
  the same row) would otherwise be lost — see `src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService.FetchAsync` and `src/Astra.Server/Api/SubscriptionQuotaSync.cs:SubscriptionQuotaSync.RefreshAsync`.
- **The outbound proxy travels in an `AsyncLocal`, not in the arguments** — a query that reaches `tokens.*` without
  setting `_proxy.Value` first goes through the system proxy — see `src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService` (`ProxiedHttpClients.ProxyOf` is the account > provider > system order).
- **`SubscriptionQuotaService.IsFresh` reads `TimeProvider.System`, not the injected clock** — under a fake clock the
  "is this snapshot fresh" answer follows real time — see `src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService.IsFresh`.
- **A window that cannot be assembled is dropped, never zeroed** — ZCode/Kimi/Grok normalizers return a snapshot with
  only `fetchedAtUtc` rather than inventing `0` usage — see `src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService.NormalizeZcode`.
- **chatgpt.com/Copilot need the vendor client's identity headers** — `codex_cli_rs/0.48.0` + `chatgpt-account-id` for
  Codex, the `Editor-*`/`Copilot-Integration-Id` set for Copilot; without them the WAF answers 403+HTML — see
  `src/Astra.Providers/Subscription/SubscriptionQuotaService.cs:SubscriptionQuotaService.CodexClientUserAgent`.
- **Claude's authorize URL is parameter-order sensitive** and its token endpoint takes a JSON body (not a form) — see
  `src/Astra.Providers/Subscription/OAuthClient.cs:OAuthClient.BuildAuthorizeUrl` and `:OAuthClient.PostTokenJsonAsync`.
- **Template JSON needs its own source-gen context** because `Astra.Core` cannot reference this assembly: the context
  announces itself through a `[ModuleInitializer]`, and under Native AOT an unregistered `ProviderTemplate` throws
  (`src/Astra.Providers/Templates/ProviderJsonContext.cs:ProviderJsonContext.Register`). `JsonContexts.FallbackTypes`
  is the tripwire asserted by `tests/Astra.Server.IntegrationTests/JsonRegistrationTests.cs`.
- **Templates are loaded with `Json.Api` but cloned with `Json.Storage`** — `Load` uses `Json.DeserializeApi` (camelCase)
  while `ProviderTemplate.Clone`/`WithVariant` round-trip through `Json.Serialize`/`Json.Deserialize` (snake_case,
  omits nulls) — see `src/Astra.Providers/Templates/ProviderTemplateCatalog.cs:ProviderTemplate.Clone`.
- **`Templates/placeholder.json` is `{}` and is embedded by the same glob** — it is parsed on every load and skipped as
  an empty template; the commit that added it is the initial import (no comment explains it). Treat it as load-bearing
  only if you verify why — see `src/Astra.Providers/Templates/placeholder.json`.

## Tests

`tests/Astra.Providers.Tests` (xunit 2.9.3 + `Microsoft.NET.Test.Sdk` 17.14.1, one `ProjectReference` to the module,
no fixture files, no Dapper). 120 `[Fact]`/`[Theory]` cases across 10 files, one class per concern, method names in
`Sentence_Case_Describing_Behaviour`: `ProviderQuotaServiceTests` (13), `QuotaExpressionTests` (10),
`SubscriptionQuotaServiceTests` (25), `SubscriptionTokenServiceTests` (13), `OAuthClientTests` (21),
`PkceAndCatalogTests` (13, incl. `ProviderTemplateCatalogTests` and `SubscriptionCatalogTests`),
`CodexAuthFileTests` (4), `CredentialImportTests` (7), `LocalCopilotLoginTests` (8), `XaiSsoDeviceFlowTests` (6).
Each file keeps its own private fakes (`FakeClock : TimeProvider`, `FakeProtector : ISecretProtector`,
`InMemoryAccountStore : IProviderAccountStore`, a `StubHandler`/`RoutingHandler` + `IHttpClientFactory`), and
`ProxiedHttpClients.HandlerFactory` is the seam for proxy tests. There is **no** test for the embedded
`Templates/catalog.json` data beyond the catalog assertions in `PkceAndCatalogTests.cs`.

One file: `dotnet test tests/Astra.Providers.Tests --filter FullyQualifiedName~ProviderQuotaServiceTests`
(build first, or drop `--no-build`); whole project: `dotnet test tests/Astra.Providers.Tests`.

## Related flows

- [provider-quota-refresh](../flows/provider-quota-refresh.md) — `ProviderQuotaService` → `ProviderQuotaManager` →
  `providers.quota_json`, driven by the worker / the manual endpoint.
- [subscription-account-refresh](../flows/subscription-account-refresh.md) — `SubscriptionTokenService` +
  `SubscriptionQuotaService` → `provider_accounts.extra.quota` and the account status.
- [gateway-request](../flows/gateway-request.md) — the gateway asks this module for subscription tokens
  (`UpstreamAuthResolver`) and applies the account's own proxy.
- [admin-api-request](../flows/admin-api-request.md) — `/api/provider-templates`, `/api/providers/{id}/quota*`,
  `/api/provider-accounts/{id}/quota` surface these contracts.
