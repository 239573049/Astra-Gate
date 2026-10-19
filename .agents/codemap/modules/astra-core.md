---
module: astra-core
sources: ["src/Astra.Core/**"]
commit: 6324e30
updated: 2026-10-18
---
# astra-core — the domain assembly

`src/Astra.Core` holds every domain type and pure function the rest of the server shares: billing and pricing,
model resolution, provider/account/token/client shapes, privacy detection and chunked redaction, alert contracts,
token limits, the `~/.astra` layout, the JSON option sets with their source-generated registry, and the embedded
model seed. It has **zero PackageReferences and zero ProjectReferences** (`src/Astra.Core/Astra.Core.csproj:Astra.Core`) —
only the BCL and the SDK's generated regex/JSON source generators — so every other `src` assembly may depend on it
and it may depend on none of them.

**Not responsible for:** persistence and migrations (SQLite + Dapper.AOT in `astra-data`); the request pipeline,
routing, codecs and the billing write path (`astra-gateway`); provider templates, quota probing, subscription
token refresh (`astra-providers`); writing client config files (`astra-clients`); running magpie plugins and Bun
(`astra-plugins`); the ASP.NET host, admin API, security middleware, tunnel and the alert engine/sweeper
(`astra-server`). This module declares *what* a price, a hit, a limit or an alert is — the sibling modules decide
*when* and *where* it is computed and stored.

## Entry points

| Symbol | File | Role |
|---|---|---|
| `Astra.Core` assembly | `src/Astra.Core/Astra.Core.csproj` | The only shipped project with no references; `IsAotCompatible=true`, embeds `Seed/models.json` as `Astra.Core.Seed.models.json`. |
| `AstraPaths` | `src/Astra.Core/AstraPaths.cs:AstraPaths` | Resolves every path under the data dir; constructed once in `astra-server` and injected into siblings (`src/Astra.Server/Program.cs:28`). |
| `Json` / `JsonContexts` | `src/Astra.Core/Json.cs:Json`, `src/Astra.Core/JsonContexts.cs:JsonContexts` | The shared serializer entry points and the registry every assembly's `JsonSerializerContext` appends itself to. |
| `AppSettings` | `src/Astra.Core/AppSettings.cs:AppSettings` | The runtime-editable settings blob (`settings` table, key `app`). |
| `BillingEngine.Calculate` | `src/Astra.Core/Billing/BillingEngine.cs:BillingEngine` | Pure usage × schedule × context → itemized cost with an explainable trace. |
| `PrivacyDetector` | `src/Astra.Core/Privacy/PrivacyDetector.cs:PrivacyDetector` | Built-in + custom rule detection, text and JSON redaction, masked samples. |
| `AlertContracts` | `src/Astra.Core/Alerts/AlertContracts.cs:AlertEvents`, `:AlertConditions` | Event descriptors, condition normalization/validation and pure per-event evaluation. |
| `SeedCatalog` | `src/Astra.Core/Seed/SeedCatalog.cs:SeedCatalog` | Lazily loads the embedded model/pricing seed and projects it onto `SystemModel`. |

## Key types and functions

| Symbol | File | Role |
|---|---|---|
| `PricingResolver.Resolve` | `src/Astra.Core/Billing/PricingResolver.cs:PricingResolver` | Picks provider override → provider price key → system default → none, and reports which (`ResolvedPricing`, `PricingSource`). |
| `UsageNormalizer.Normalize` | `src/Astra.Core/Billing/UsageNormalizer.cs:UsageNormalizer` | Turns one protocol's `usage` object into disjoint `NormalizedUsage` counters (+ notes when the upstream cannot be split). |
| `TokenTypes` | `src/Astra.Core/Billing/TokenTypes.cs:TokenTypes` | Token category constants, display order, input-like test, and the price fallback chain. |
| `NormalizedUsage` | `src/Astra.Core/Billing/TokenTypes.cs:NormalizedUsage` | The counts themselves: `Tokens`/`Calls`, `TotalInput`, `MergeMax`. |
| `ModelMerger.Merge` | `src/Astra.Core/Models/EffectiveModel.cs:ModelMerger` | Per-field system-model → provider-model overlay producing `EffectiveModel` + `Origins`. |
| `ModelIdMatcher` | `src/Astra.Core/Models/ModelIdMatcher.cs:ModelIdMatcher` | Links an upstream model id to a system model: exact → index → alias → `Normalize`. Its immutable `Index` precomputes the catalog for repeated matches, preserving tier priority and first-candidate ties. |
| `Provider` | `src/Astra.Core/Models/Provider.cs:Provider` | Provider instance incl. `EndpointFor`, `ModelMap`/`MapModel`, `SettingFlag`, `PriceKey`, `PriceMultiplier`. |
| `ProviderAccount` | `src/Astra.Core/Models/ProviderAccount.cs:ProviderAccount` | Subscription/plugin account: encrypted tokens, status, current/cooldown/sort-order switching state. Auth cooldowns prefix `LastError` with `401 ` / `403 `; quota headroom must not lift them. |
| `EffectivePrivacyPolicy` | `src/Astra.Core/Privacy/PrivacyTypes.cs:EffectivePrivacyPolicy` | Per-request rule set compiled from settings; `ActionFor` resolves rule → category → client → default. |
| `ChunkRestorer` | `src/Astra.Core/Privacy/ChunkRestorer.cs:ChunkRestorer` | Re-expands placeholders across streaming chunks with a tail hold-back. |
| `TokenLimits` / `UsagePeriods` | `src/Astra.Core/Tokens/TokenLimits.cs:TokenLimits`, `:UsagePeriods` | Budget validation/parse, `Counts` (refusals are not billed), and local-day/week/month window arithmetic. |
| `TokenRecord` | `src/Astra.Core/Tokens/TokenRecord.cs:TokenRecord` | Gateway token incl. `LimitsJson`, `AllowedClientsJson`, `ProviderId`/`AccountId` for direct calls. |
| `RequestRecord` | `src/Astra.Core/Requests/RequestRecord.cs:RequestRecord` | One logged gateway request: usage, cost, pricing snapshot, billing trace, privacy report, plus its agent-link columns (`TraceId`, `SessionKey`, `TurnId`, `ParentRequestId`, `CallKind`, `LinkSource`, `FinishReason`) and `Spans`. `LiveStage` is for the live feed only and is never stored. |
| `TraceSpan` / `TraceSpanNames` | `src/Astra.Core/Requests/TraceSpan.cs:TraceSpan`, `:TraceSpanNames` | One span row of `trace_spans`, and the nine span names (`gateway.request` … `upstream.attempt` … `gateway.response`). `TraceCallKinds` / `TraceLinkSources` name the link. |
| `TraceStats` | `src/Astra.Core/Requests/TraceStats.cs:TraceStats` | Nearest-rank percentile and the window a range id covers (`live` = 5 min, else `1h`/`24h`/`7d`/`30d`). |
| `SystemProxy` | `src/Astra.Core/SystemProxy.cs:SystemProxy` | Process-wide outbound proxy policy (settings > env > OS), `Dynamic` proxy for long-lived clients. |
| `ApiProtocol` / `ApiProtocols` | `src/Astra.Core/ApiProtocol.cs:ApiProtocol` | The four wire protocols and their string ids with a custom JSON converter. |
| `Money` / `Ulid` | `src/Astra.Core/Json.cs:Money`, `:Ulid` | nanodollar conversion and formatting; sortable ULID ids. |

## Dependencies

- Uses: **nothing.** No `ProjectReference` or `PackageReference` anywhere in `src/Astra.Core/Astra.Core.csproj`;
  `Directory.Build.props` only supplies the TFM, `Nullable`, `ImplicitUsings` and warnings-as-errors. Even the
  JSON resolver fallback is BCL (`src/Astra.Core/JsonContexts.cs:JsonContexts`).
- Used by: `astra-data` via compile-time reference — `SeedImporter` reads `SeedCatalog.Current` and stores values
  through `Json.Serialize`/`Json.Deserialize` (`src/Astra.Data/Repositories/SettingsRepository.cs:SettingsRepository`,
  `src/Astra.Data/SeedImporter.cs:38`); `UsagePeriods.DayKey` and `TokenLimits.Counts` are called from
  `src/Astra.Data/Repositories/RequestRepository.cs:383`.
- Used by: `astra-gateway` via compile-time reference and **method call** — `BillingEngine.Calculate` from
  `src/Astra.Gateway/Pipeline/RequestBilling.cs:28`, `PrivacyDetector`/`EffectivePrivacyPolicy` from
  `src/Astra.Gateway/Pipeline/PrivacyGuardService.cs:81`, `ModelMerger.Merge` from
  `src/Astra.Gateway/Pipeline/EffectiveModelResolver.cs:16`, and the `IAlertSignals.Report` push from
  `src/Astra.Gateway/Pipeline/TokenLimiter.cs:54`.
- Used by: `astra-providers` via compile-time reference — provider templates are filtered/expanded from the seed
  (`src/Astra.Providers/Templates/ProviderTemplateCatalog.cs:98`).
- Used by: `astra-clients` via compile-time reference — the adapters implement `IClientConfigStateStore` and read
  `ClientOptions`/`ClaudeCodeModels`/`ClaudeDesktopRoles` (`src/Astra.Clients/Adapters/ClientAdapterRegistry.cs:18`),
  `ImportSupport` consumes `IForeignDbOpener` (`src/Astra.Clients/Import/ImportSupport.cs:13`).
- Used by: `astra-plugins` via compile-time reference — `PluginCatalog(IPluginListStore, AstraPaths)` in
  `src/Astra.Plugins/PluginCatalog.cs:14`; the host reads the files `AstraPaths.PluginsDir` points at.
- Used by: `astra-server` via compile-time reference and DI — `DataProtectionSecretProtector : ISecretProtector`
  (`src/Astra.Server/Security/DataProtectionSecretProtector.cs:7`), `AddSingleton<IAlertSignals>(…)` in
  `src/Astra.Server/Hosting/AstraApp.cs:126`.
- Used by: `cli`, `web-ui`, `desktop-app` — **not** by import. See "Contracts it exposes": those surfaces are
  mirrored by hand.

## State it owns

- **Whole data-dir layout** — `AstraPaths` (`src/Astra.Core/AstraPaths.cs:AstraPaths`): `Root` (`ASTRA_HOME` or
  `~/.astra`), `astra.db`, `config.json`, `runtime.json`, `install.json`, `keys/`, `logs/`, `backups/{client-configs,db}/`,
  `bodies/`, `tools/<name>-<version>/`, and the plugin file tree `plugins/{plugin-auth.json,packages,runtime,host,models-dev.json,market}`.
  `EnsureCreated` creates them and `RestrictToOwner` chmods `keys/` and `plugins/` to owner-only (POSIX best effort).
  The CLI and the desktop shell keep hand-mirrored copies of the same layout (AGENTS.md linked update 5).
- **The JSON contract for everything persisted** — `Json.Storage` (snake_case, no nulls, comment/trailing-comma
  tolerant, numbers readable from strings) vs `Json.Api` (camelCase) vs `Json.Wire` vs `Json.Raw`
  (`src/Astra.Core/Json.cs:Json`). `Json.Raw` exists only so a value embedded in a client config file escapes
  byte-identically to the stock `JsonSerializer`.
- **Type registration** — `JsonContexts` (`src/Astra.Core/JsonContexts.cs:JsonContexts`) is the registry;
  `JsonContext.Default` (`src/Astra.Core/JsonContext.cs:JsonContext`) is Core's own contribution and is added in the
  static constructor. Nothing else may register Core types, and other assemblies add theirs through
  `[ModuleInitializer]` (`src/Astra.Providers/Templates/ProviderJsonContext.cs:32`,
  `src/Astra.Clients/Config/ClientJsonContext.cs:21`, `src/Astra.Plugins/PluginsJsonContext.cs:32`,
  `src/Astra.Server/Api/ServerJsonContext.cs:236`).
- **Settings keys** — `AppSettings.StorageKey = "app"`, `PrivacySettings.StorageKey = "privacy"`, both rows of the
  `settings(key, value_json)` table read/written by `astra-data`'s `SettingsRepository` (which serializes with
  `Json.Storage`). In-memory derived caches live in `astra-gateway`: `SettingsService.Current` and
  `PrivacyGuardService`'s snapshot — those are caches, never write them directly.
- **The seed catalog** — `Seed/models.json` (embedded resource, `seed_version: 4`) is the source of truth for the
  system model list and official pricing; it replaces neither `models` nor `model_prices` rows, which remain the
  user-editable truth after import (`src/Astra.Data/SeedImporter.cs`).
- **No mutable static state of substance besides `SystemProxy._override`** — process-wide by design, pushed from
  settings by `src/Astra.Gateway/Pipeline/SettingsService.cs:53`.

## Contracts it exposes

- **In-process interfaces other assemblies implement or consume**: `ISecretProtector`
  (`src/Astra.Core/Clients/ClientContracts.cs:ISecretProtector`, implemented by `astra-server`),
  `IProviderAccountStore` (`src/Astra.Core/Models/ProviderAccount.cs:IProviderAccountStore`),
  `IClientConfigStateStore` (`src/Astra.Core/Clients/ClientContracts.cs:IClientConfigStateStore`),
  `IPluginListStore` (`src/Astra.Core/Plugins/PluginContracts.cs:IPluginListStore`),
  `IAgentPluginCatalog` (`src/Astra.Core/Plugins/AgentPluginContracts.cs:IAgentPluginCatalog`),
  `IAlertSignals` (`src/Astra.Core/Alerts/AlertContracts.cs:IAlertSignals`),
  `IForeignDb`/`IForeignDbOpener` (`src/Astra.Core/Import/ImportModels.cs:IForeignDbOpener`, implemented by
  `src/Astra.Data/Foreign/ForeignSqlite.cs:13`).
- **Persisted document shapes** — these are what `Json.Storage` writes, so a field rename is a data migration:
  `PricingSchedule`/`PriceSet` (flat object with a `per_call` member, `src/Astra.Core/Billing/Pricing.cs:PriceSet`),
  `ModelOverrides`, `TokenLimits` (`limits_json`), `PrivacySettings`, `AgentPluginDescriptor`, `AppSettings`,
  `TunnelSettings`.
- **Wire/route-adjacent contracts mirrored outside the .NET tree** — protocol ids `openai-chat` /
  `openai-responses` / `anthropic` / `gemini` (`src/Astra.Core/ApiProtocol.cs:ApiProtocols.ToId`); alert event ids
  under `alerts.event.<id>` in `web/src/i18n/messages.ts`; alert condition parameter names and choices
  (`src/Astra.Core/Alerts/AlertContracts.cs:AlertEvents.All`); the webhook payload shape described in the docs site.
  The CLI and web UI re-declare these; nothing generates them from Core.
- **Observations, not events** — `AlertSignal` is an in-process push (`IAlertSignals.Report`); the shapes are
  documented on the type (`src/Astra.Core/Alerts/AlertContracts.cs:AlertSignal`) and produced by
  `TokenLimiter.Observation` (`src/Astra.Gateway/Pipeline/TokenLimiter.cs:20`).
- **No routes, no CLI flags, no tables of its own.** Tables named in Core doc comments (`requests`,
  `request_usage_items`, `tokens`, `token_usage_daily`, `plugins`, `plugin_provider_prefs`, `webhooks`,
  `alert_rules`, `alert_rule_webhooks`, `alert_states`, `webhook_deliveries`) are declared by migrations in
  `astra-data`; Core only shapes the rows.

## How to extend

- To add a **token category**: add the constant, the `Ordered` entry, the `InputLike` membership if it is input,
  and a `FallbackOf` branch in `src/Astra.Core/Billing/TokenTypes.cs:TokenTypes`, then teach the relevant
  `UsageNormalizer.From*` method to emit it. Missing the fallback branch silently prices the type at 0.
- To add a **protocol**: add the enum member and its id in `src/Astra.Core/ApiProtocol.cs:ApiProtocols`, then the
  decoder in `UsageNormalizer.Normalize` (`src/Astra.Core/Billing/UsageNormalizer.cs:UsageNormalizer`) and the codec
  in `astra-gateway` (`GatewayCodecs.Register`). A protocol with no `Normalize` branch bills as 0 with no note.
- To add a **pricing knob**: a property on `PricingSchedule`/`PriceSet`/`ContextTier`/`TimeWindowRule`
  (`src/Astra.Core/Billing/Pricing.cs`), its validation in `PricingSchedule.Validate`, and its application in
  `BillingEngine.Calculate` plus a trace step rendered by `BillingDescription.RenderStep`
  (`src/Astra.Core/Billing/BillingDescription.cs`). Skipping the description leaves the archived bill unexplainable.
- To add a **JSON type**: an owning-assembly `[JsonSerializable]` entry (`src/Astra.Core/JsonContext.cs:JsonContext`)
  **and** a `Seed`/DTO field registration for each collection used as a root. Missing it stays silent on the JIT and
  fails under Native AOT; `JsonContexts.FallbackTypes` is the tripwire the integration test asserts on.
- To add an **alert event**: a descriptor in `AlertEvents.All`, its branch in `AlertConditions.Evaluate`, the
  observation field its subject reports, then — outside this module — a message in `astra-server`'s `AlertMessages`,
  `alerts.event.<id>[.hint]` in **both** locales of `web/src/i18n/messages.ts`, and the docs event table.
  `AlertConditionsTests` asserts every event has a descriptor and valid defaults
  (`tests/Astra.Core.Tests/AlertConditionsTests.cs:91`).
- To add a **client option**: a `ClientOptionDescriptor` in `src/Astra.Core/Clients/ClientOptions.cs:ClientOptions`,
  its `ConfigKeyPaths` entry when the scope is `config`, then its consumer (the adapter in `astra-clients`, or the
  gateway reading `route.Client.ExtraJson`) and zh/en `clients.option.<key>[.hint]` keys.
- To add a **seed model**: edit the generator's output contract and regenerate (`scripts/gen-seed.mjs`, bump
  `SEED_VERSION` there) — never hand-edit `Seed/models.json`. `SeedImporter` skips re-import while the stored seed
  version matches.
- To add a **token limit metric or period**: `TokenLimitMetrics`/`TokenLimitPeriods`
  (`src/Astra.Core/Tokens/TokenLimits.cs`), the arithmetic in `UsagePeriods.Window`, and the enforcement in
  `astra-gateway`'s `TokenLimiter`/`TokenUsageLedger`.

## Invariants and traps

- **Money is integer nanodollars, never a float** — `Money.ToNanos`/`FromNanos`, 1e9 per USD, rounded away from zero
  (`src/Astra.Core/Json.cs:Money`); unit prices travel as decimal strings inside pricing JSON and
  `RequestUsageItem.UnitPrice`. A float anywhere loses the round trip.
- **Never build a `JsonSerializerOptions` by hand** — every option set must carry `TypeInfoResolver = JsonContexts.Resolver`,
  and generic `JsonSerializer.Serialize<T>(value, options)` is avoided in favour of `JsonContexts.Info<T>`
  because the former is `RequiresDynamicCode` (`src/Astra.Core/Json.cs:Json`, `src/Astra.Core/JsonContexts.cs:JsonContexts.Info`).
  Custom converters must do the same (`src/Astra.Core/Billing/Pricing.cs:PriceSetJsonConverter`).
- **An unregistered type is invisible on the JIT and fatal under AOT** — the reflection fallback only exists where
  dynamic code does (`src/Astra.Core/JsonContexts.cs:ReflectionFallback`); the guard test is
  `tests/Astra.Server.IntegrationTests/JsonRegistrationTests.cs:106`.
- **`JsonNode.Add<T>` / `ReplaceWith<T>` are AOT-hostile** — use `JsonNodes.AddNode` / `ReplaceTextWith`
  (`src/Astra.Core/JsonNodes.cs:JsonNodes`).
- **The provider multiplier never scales an instance override** — `PricingResolver.Resolve` sets `Multiplier = 1m`
  on the `ProviderOverride` branch and `BillingContext.From` re-asserts it
  (`src/Astra.Core/Billing/PricingResolver.cs:PricingResolver`, `src/Astra.Core/Billing/BillingEngine.cs:BillingContext`).
- **`NormalizedUsage.MergeMax` is a max, not a sum** — streams repeat cumulative usage objects; summing
  double-counts (`src/Astra.Core/Billing/TokenTypes.cs:NormalizedUsage`).
- **Built-in privacy patterns are linear or hinted, and the budget differs by rule origin** — built-ins get 5 s
  behind vectorized literal hints, user patterns only 500 ms (`src/Astra.Core/Privacy/PrivacyDetector.cs:BuiltinTimeoutMs`,
  `:Compile`, `:AnyRuleMatches`). Rewriting a built-in to a backtracking pattern reintroduces the quadratic blowup
  the hints exist to avoid.
- **Redaction counter state is per `PrivacyApplyResult`, shared across one JSON body on purpose** — placeholders
  must stay unique body-wide; per-string counters would collide into duplicate `#1`s
  (`src/Astra.Core/Privacy/PrivacyDetector.cs:ApplyInto`).
- **The privacy clean-cache stores only digests of clean strings, per policy, and clears itself at 16 384 entries** —
  it must never be extended to remember matches (`src/Astra.Core/Privacy/PrivacyDetector.cs:PolicyState`, `:Digest`).
- **`ChunkRestorer` holds back `longestPlaceholder - 1` characters** and returns `""` until enough has arrived —
  a caller that treats an empty return as stream end truncates output; `Flush()` is what ends it
  (`src/Astra.Core/Privacy/ChunkRestorer.cs:ChunkRestorer.Push`).
- **A request refused by a token limit must not be counted** — `TokenLimits.Counts` skips
  `RefusedErrorType = "token_limit"`, otherwise the refusal would refill the budget that caused it
  (`src/Astra.Core/Tokens/TokenLimits.cs:TokenLimits.Counts`).
- **`ApiProtocol` serializes as a string, not a number** — its converter is applied wherever the enum appears,
  including inside `ProviderEndpoint`
  (`src/Astra.Core/ApiProtocol.cs:ApiProtocolJsonConverter`).
- **`PrivacySettings`/`AppSettings` are JSON blobs, so a new field needs no migration — but its zh/en UI string and
  endpoint wiring do** (`src/Astra.Core/AppSettings.cs:AppSettings`, `src/Astra.Core/Privacy/PrivacyTypes.cs:PrivacySettings`).
- **`SystemProxy` is process-global mutable state** — `Configure` replaces the override for the whole process, and
  the dynamic proxy re-resolves per request so toggling the OS proxy after startup takes effect
  (`src/Astra.Core/SystemProxy.cs:SystemProxy.Dynamic`).
- **`ImportCandidate.ApiKey` is deliberately never JSON-registered** (`src/Astra.Core/Import/ImportModels.cs:ImportCandidate`)
  — registering it would let a plaintext key reach a response or a log line.
- **`AstraPaths` deliberately does not know about clients**: it lists only Astra's own data dir; client config paths
  live in `astra-clients`' `ClientEnvironment` (AGENTS.md linked update 5 covers the CLI/desktop mirrors).

## Tests

`tests/Astra.Core.Tests` (xunit, `Microsoft.NET.Test.Sdk 17.14.1` + `xunit 2.9.3`), one file per area, class per
concern, method names in `Sentence_Case_Describing_Behaviour`. 98 `[Fact]`/`[Theory]` cases across
`PrivacyDetectorTests.cs` (37, including the `ChunkRestorerTests` class at line 523), `BillingEngineTests.cs` (19),
`UsageAndModelTests.cs` (12: usage normalization, `ModelIdMatcher`, effective model, seed sanity), `SystemProxyTests.cs`
(8), `AlertConditionsTests.cs` (6), `TokenLimitsTests.cs` (5), `ClientOptionsTests.cs`, `ClaudeDesktopRolesTests.cs`,
`CopilotModelIdsTests.cs`, `ClaudeCodeModelsTests.cs`, `UsageNormalizerNodeTests.cs`. No fixtures directory is in use
(the csproj still copies `Fixtures\**\*` if one appears).

Single file: `dotnet test tests/Astra.Core.Tests --filter FullyQualifiedName~PrivacyDetectorTests` (build first, or
drop `--no-build`). Whole project: `dotnet test tests/Astra.Core.Tests`. The csproj has no
`[module: DapperAot(false)]` — there is no Dapper here. Tests must not touch the real `~/.astra`: `AstraPaths`
takes a root parameter for that (`src/Astra.Core/AstraPaths.cs:AstraPaths`).

## Related flows

- [gateway-request](../flows/gateway-request.md) — effective-model resolution, pricing, billing and privacy
  inspection all run inside it.
- [request-tracing](../flows/request-tracing.md) — `TraceSpan`/`TraceSpanNames` are the rows the pipeline writes and the
  console reads back.
- [privacy-redaction](../flows/privacy-redaction.md) — `EffectivePrivacyPolicy` → `PrivacyDetector` →
  `ChunkRestorer`.
- [token-limits-enforcement](../flows/token-limits-enforcement.md) — `TokenLimits`, `UsagePeriods`, `TokenLimiter.Observation`.
- [provider-quota-refresh](../flows/provider-quota-refresh.md) and
  [subscription-account-refresh](../flows/subscription-account-refresh.md) — `Provider.Quota`, `ProviderAccount`
  status/cooldown fields and the alert observations built from them.
- [client-config-takeover](../flows/client-config-takeover.md) — `ClientOptions`, `ClientConfigStateEntry`,
  `IClientConfigStateStore`.
- [admin-api-request](../flows/admin-api-request.md) — the admin DTOs serialize through this module's
  `Json.Api`/`JsonContexts` and its DTO shapes live here.
- [server-startup](../flows/server-startup.md) — `AstraPaths.EnsureCreated`, `AppSettings` load, `SystemProxy.Configure`.
onfigure`.
