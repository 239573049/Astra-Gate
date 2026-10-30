---
module: astra-data
sources: ["src/Astra.Data/**"]
commit: 6324e30
updated: 2026-10-19
---
# astra-data

The SQLite persistence layer: it owns the database file, the numbered DDL migrations, the embedded seed
import, and every repository the server reads and writes through. Dapper.AOT source generation,
WAL, `foreign_keys=ON` and ISO-8601 text timestamps are deliberate choices, not defaults.
**Not responsible for:** domain types, JSON options, `AstraPaths` and the seed catalog itself (`astra-core`),
the HTTP surface and workers that call these repositories (`astra-server`), protocol/billing logic that
decides what a row should say (`astra-gateway`), and the TS/Docker side that only ever talks HTTP.

## Entry points
| Symbol | File | Role |
|---|---|---|
| `AstraDatabase.InitializeAsync` | `src/Astra.Data/AstraDatabase.cs:AstraDatabase.InitializeAsync` | The one call that matters: `paths.EnsureCreated()` → `MigrationRunner.MigrateAsync` → `SeedImporter.ImportAsync`, then hands back the wired repository container. |
| `AstraDatabase` properties | `src/Astra.Data/AstraDatabase.cs:AstraDatabase` | `Settings`, `Models`, `Providers`, `Accounts`, `Clients`, `Tokens`, `ClientConfigState`, `Requests`, `Traces`, `Plugins`, `Alerts`, `Migrations`, `Seed` — constructed in the private ctor, registered in DI as one singleton (`src/Astra.Server/Hosting/AstraApp.cs:45`, `:77`). |
| `MigrationRunner.MigrateAsync` | `src/Astra.Data/MigrationRunner.cs:MigrationRunner.MigrateAsync` | Applies pending migrations, backing the file up first when it is not a fresh database. |
| `SeedImporter.ImportAsync` | `src/Astra.Data/SeedImporter.cs:SeedImporter.ImportAsync` | Upserts `SeedCatalog.Current` into `models`/`model_prices`; a no-op when the stored `seed_version` already matches. |
| `SqliteConnectionFactory.OpenAsync` / `Open` | `src/Astra.Data/SqliteConnectionFactory.cs:SqliteConnectionFactory.OpenAsync` | The only way connections are made: WAL, `busy_timeout=5000`, `foreign_keys=ON`; `Open()` exists for the synchronous `IClientConfigStateStore`. A connection that changed rows advances `Generation` when it closes (after its commit). |
| `SqliteConnectionFactory.OpenUntrackedAsync` | `src/Astra.Data/SqliteConnectionFactory.cs:SqliteConnectionFactory.OpenUntrackedAsync` | Same configuration, but its writes leave `Generation` alone — used only by `RequestRepository.InsertBatchAsync`, which commits many times a second. |
| `ForeignSqliteOpener.Open` | `src/Astra.Data/Foreign/ForeignSqlite.cs:ForeignSqliteOpener.Open` | Strictly read-only handle (`mode=ro`, `query_only=ON`, no pooling) onto another app's DB, used by `astra-clients` imports. |

## Key types and functions
| Symbol | File | Role |
|---|---|---|
| `MigrationRunner.LoadEmbedded` / `MigrationFileRegex` | `src/Astra.Data/MigrationRunner.cs:MigrationRunner.LoadEmbedded` | Finds migration scripts as **manifest resources** of the assembly, matched by `\.Migrations\.(\d+)_[A-Za-z0-9_]+\.sql$`, ordered by the numeric prefix. |
| `MigrationRunner.BackupDatabase` / `HasExistingSchemaAsync` | `src/Astra.Data/MigrationRunner.cs:MigrationRunner.BackupDatabase` | Before applying pending migrations to an existing schema: WAL checkpoint (best effort) then a copy to `<backups>/db/astra-<yyyyMMdd-HHmmss>-v<fromVersion>.db`. |
| `SqliteConnectionFactory.ConfigureConnectionAsync` | `src/Astra.Data/SqliteConnectionFactory.cs:SqliteConnectionFactory.ConfigureConnectionAsync` | `busy_timeout=5000`, `foreign_keys=ON`, `journal_mode=WAL` once per **physical** (pooled) connection, remembered by its SQLite handle — re-running them on every open tripled the statements each query prepared. |
| `SqliteSetup.Ensure` | `src/Astra.Data/SqliteSetup.cs:SqliteSetup` | Process-wide, before the first connection: turns SQLite's memory statistics off, whose global mutex every allocation took (concurrent requests queued on it). |
| `RowCache<TKey,TValue>` / `SqliteConnectionFactory.Generation` | `src/Astra.Data/RowCache.cs:RowCache` | Read-through cache behind the per-request reads (`TokenRepository.FindByPrefixAsync`, `ProviderRepository.GetAsync`/`GetModelAsync`/`ListModelsAsync`, `ModelRepository.GetAsync`/`ListPricesAsync`/`GetUpstreamIdIndexAsync`/`GetMatcherCandidatesAsync`, `ClientRepository.GetAsync`/`ListBindingsAsync`). An entry is served only while the file's `Generation` (shared by every factory of the process on that file) is unchanged and for at most `MaxAge` (2 s, for other processes' writes). Caches raw rows, copies or immutable matcher/index snapshots, never shared mutable entities. Only provider-model lookups opt into bounded negative caching. |
| `DateTimeOffsetHandler` (`ToStorage`, `Parse`, `SetValue`) | `src/Astra.Data/SqliteConnectionFactory.cs:DateTimeOffsetHandler` | `yyyy-MM-dd'T'HH:mm:ss.fff'Z'` UTC text, milliseconds, invariant — the storage format for every timestamp column; `ToStorage` is also used for explicit parameter binding. |
| `[module: DapperAot]` / `[module: TypeHandler(...)]` | `src/Astra.Data/SqliteConnectionFactory.cs:11` | Module-scoped attributes that turn on build-time row mapping and route `DateTimeOffset` through the handler (the AOT-safe replacement for `SqlMapper.AddTypeHandler`). Must precede all other elements. |
| `SettingsRepository` | `src/Astra.Data/Repositories/SettingsRepository.cs:SettingsRepository` | Key → JSON blob. Holds `AppSettings` under key `"app"` (`Astra.Core/AppSettings.cs:AppSettings.StorageKey`) and the seed version under `"seed_version"`. |
| `ModelRepository.ApplySyncBatchAsync` | `src/Astra.Data/Repositories/ModelRepository.cs:ModelRepository.ApplySyncBatchAsync` | Model-sync batch: expectations re-read under a **non-deferred** transaction, then inserts/updates/price upserts commit together or not at all. |
| `ProviderRepository.UpdateQuotaAsync` | `src/Astra.Data/Repositories/ProviderRepository.cs:ProviderRepository.UpdateQuotaAsync` | Touches only `quota_json` + `quota_checked_at_utc` — a background refresh must not bump `updated_at` or race a settings edit. |
| `ProviderAccountStore.SetCurrentAsync` | `src/Astra.Data/Repositories/ProviderAccountStore.cs:ProviderAccountStore.SetCurrentAsync` | Makes one account the provider's only `is_current=1` and lifts its cooldown, in one transaction; `is_current`/`cooldown_until_utc` are deliberately not written by `UpdateAsync`. |
| `RequestRepository.InsertBatchAsync` | `src/Astra.Data/Repositories/RequestRepository.cs:RequestRepository.InsertBatchAsync` | One transaction per batch: `requests` (including the tracing columns) + each row's `trace_spans` + `request_usage_items` + provider credential lifetime counters + token lifetime/daily counters + `tokens.last_used_at`; fills missing ids with ULIDs. The request, span and usage-item rows go through `RowInserter` (one statement per table/batch, bound by ordinal on the same transaction); the counters are summed per token / day / credential before one Dapper upsert each. |
| `RowInserter<T>` | `src/Astra.Data/RowInserter.cs:RowInserter` | Multi-row insert from a column → value list (the single source of INSERT text and ordinals): SQLitePCL binds directly by ordinal, avoiding repeated parameter-name scans and per-execution command allocations. Accepts null/string/int/long/bool/double (timestamps through `DateTimeOffsetHandler.ToStorage`); checks each native result and cancellation per row; statements are disposed inside the caller’s transaction. |
| `RequestRepository.ProviderAccountUsageAsync` | `src/Astra.Data/Repositories/RequestRepository.cs:RequestRepository.ProviderAccountUsageAsync` | Each current account (including disabled/unused accounts), or the provider's own API key: persisted lifetime requests/tokens/cost plus today's request-log sums in a half-open UTC window supplied by the caller. |
| `TraceRepository` | `src/Astra.Data/Repositories/TraceRepository.cs:TraceRepository` | Read side of tracing: `ListSpansAsync` / `ListTurnSpansAsync`, `ListSessionsAsync` / `GetSessionAsync` / `ListTurnsAsync`, and `TopologyAsync`. Spans are written by `RequestRepository`, not here. |
| `RequestRepository.QueryAsync` / `QueryPrivacyEventsAsync` / `TimeseriesAsync` | `src/Astra.Data/Repositories/RequestRepository.cs:RequestRepository.QueryAsync` | The read side: fixed SQL text with null-means-no-filter parameters (Dapper.AOT cannot read `DynamicParameters`). |
| `TokenRepository` | `src/Astra.Data/Repositories/TokenRepository.cs:TokenRepository` | `tokens` plus its `token_usage_totals` / `token_usage_daily` counters; `DeleteAsync` refuses the default token. |
| `ClientConfigStateStore` | `src/Astra.Data/Repositories/ClientConfigStateStore.cs:ClientConfigStateStore` | Synchronous Dapper implementation of `Astra.Core.Clients.IClientConfigStateStore` (`src/Astra.Core/Clients/ClientContracts.cs:143`) — the original/applied value of every key Astra writes into a client config file. |
| `PluginRepository` | `src/Astra.Data/Repositories/PluginRepository.cs:PluginRepository` | `plugins` + `plugin_provider_prefs`; implements `IPluginListStore` (`src/Astra.Core/Plugins/PluginContracts.cs:65`); `ReplaceAsync` carries sort order, added time and provider picks to a new spec. |
| `AlertRepository` | `src/Astra.Data/Repositories/AlertRepository.cs:AlertRepository` | `webhooks`, `alert_rules`, `alert_rule_webhooks`, `alert_states`, `webhook_deliveries`; rule+links are written in one transaction. |
| `ClaudeDirectRequestRepository` | `src/Astra.Data/Repositories/ClaudeDirectRequestRepository.cs:ClaudeDirectRequestRepository` | `claude_direct_requests`: client-reported telemetry, `ON CONFLICT(id) DO NOTHING` so a replayed upload is idempotent. Constructed directly from `db.Factory`, not exposed on `AstraDatabase` (`src/Astra.Server/Api/ClaudeDirectService.cs:51`). |
| `ForeignSqlite` | `src/Astra.Data/Foreign/ForeignSqlite.cs:ForeignSqlite` | Raw ADO.NET reader over another app's DB (no Dapper, no pooling); table names are validated against `^[A-Za-z_][A-Za-z0-9_]*$` before being interpolated. |

## Dependencies
- Uses: `astra-core` via import — `AstraPaths` (`src/Astra.Data/AstraDatabase.cs:49`), `Json.Serialize/Deserialize` (`src/Astra.Data/Repositories/SettingsRepository.cs:19`), `AppSettings`, the model/provider/client/token/plugin/alert record types stored in these tables, `SeedCatalog` (`src/Astra.Data/SeedImporter.cs:38`), `Ulid`/`TokenLimits`/`UsagePeriods` (`src/Astra.Data/Repositories/RequestRepository.cs:292`, `:362`, `:383`), and the interfaces `IClientConfigStateStore`, `IPluginListStore`, `IProviderAccountStore`, `IForeignDbOpener`/`IForeignDb` (`src/Astra.Data/Foreign/ForeignSqlite.cs:13`).
- Used by: `astra-server` via import — composition + DI (`src/Astra.Server/Hosting/AstraApp.cs:45`, `:77`), `migrate`/`restore-all` subcommands (`src/Astra.Server/Commands.cs:16`, `:23`), every `Api/*Endpoints.cs` and the alert engine/dispatcher (`src/Astra.Server/Alerts/AlertEndpoints.cs:51`).
- Used by: `astra-gateway` via import — `SettingsService` reads/writes `settings` (`src/Astra.Gateway/Pipeline/SettingsService.cs:18`), `UsageWriter` writes request rows, `TokenUsageLedger`/`TokenLimiter` read token counters.
- Used by: `astra-clients` via import — adapters take `IClientConfigStateStore`, implemented by `ClientConfigStateStore` (`src/Astra.Clients/Adapters/CodexClientAdapter.cs:22`).
- Used by: `astra-providers` via import — quota snapshots land in `providers.quota_json`/`quota_checked_at_utc` through `ProviderRepository.UpdateQuotaAsync`; subscription rows go through `IProviderAccountStore`.
- Used by: `astra-plugins` via DB — `PluginCatalog` reads only `IPluginListStore`, i.e. the `plugins` table (`src/Astra.Plugins/PluginCatalog.cs:46`).
- Used by: `cli`, `web-ui`, `desktop-app` via HTTP — neither side references this assembly; they reach it only through `astra-server`'s admin API.

## State it owns
- **Source of truth:** the SQLite file itself, `AstraPaths.Database` = `<root>/astra.db` (`src/Astra.Core/AstraPaths.cs:14`), plus `astra.db-wal` / `astra.db-shm` in WAL mode. `AstraPaths` and `EnsureCreated` are `astra-core`'s, not this module's.
- **Tables** (created by migrations 0001–0019): `schema_version`, `settings`, `models`, `model_prices`, `providers`, `provider_models`, `provider_accounts`, `clients`, `client_bindings`, `client_config_state`, `requests`, `request_usage_items`, `request_attempts`, `trace_spans`, `tokens`, `token_usage_totals`, `token_usage_daily`, `provider_account_usage_totals`, `claude_direct_requests`, `plugins`, `plugin_provider_prefs`, `webhooks`, `alert_rules`, `alert_rule_webhooks`, `alert_states`, `webhook_deliveries`.
- **Files it writes:** `<backups>/db/astra-<stamp>-v<n>.db` before a migration of an existing DB (`src/Astra.Core/AstraPaths.cs:22`, `src/Astra.Data/MigrationRunner.cs:BackupDatabase`).
- **Settings keys it stores:** `"app"` (`AppSettings.StorageKey`, `src/Astra.Core/AppSettings.cs:6`), `"privacy"` (`PrivacySettings.StorageKey`, `src/Astra.Core/Privacy/PrivacyTypes.cs:72`), `"seed_version"` (`SeedImporter.SeedVersionKey`, `src/Astra.Data/SeedImporter.cs:30`) — plus the one-shot `"token_migration"` marker migration 0006 inserts.
- **Derived, never written directly:** the SQL read models returned by `RequestRepository` (`RequestPage`, `RequestSummary`, `RateWindow`, `TimeseriesPoint`, `PrivacyEventStat*`) and `TokenRepository.UsageAsync` — they aggregate `requests` at query time; `token_usage_totals`/`token_usage_daily` and `provider_account_usage_totals` are the persisted counters, written by `RequestRepository.InsertBatchAsync` and backfilled by migrations 0006/0013/0019. Credential costs sum the request's original bill, never current catalog prices; token-limit refusals are excluded.
- **Read-only foreign state:** another application's SQLite files, opened through `ForeignSqliteOpener` — never written.

## Contracts it exposes
- **Migrations** — `src/Astra.Data/Migrations/NNNN_name.sql`, embedded via `<EmbeddedResource Include="Migrations\*.sql" />` (`src/Astra.Data/Astra.Data.csproj:17`). The numbered glob and the regex are what `MigrationRunner` discovers and orders by. Files that exist at this commit:
  - `0001_init.sql` — the full initial schema (`schema_version`, `settings`, `models`, `model_prices`, `providers`, `provider_accounts`, `provider_models`, `clients`, `client_bindings`, `client_config_state`, `requests`, `request_usage_items`, `request_attempts` + the `idx_*` indexes). The only idempotent script (`IF NOT EXISTS` everywhere).
  - `0005_request_reasoning.sql` — nullable `requests.reasoning_effort|reasoning_mode|reasoning_budget_tokens` (never back-filled).
  - `0006_tokens.sql` — `tokens`, `token_usage_totals`, `clients.token_id`, `requests.token_id|token_name`, moves every client + all history to the `default` token, backfills totals, sets the `token_migration` marker.
  - `0007_provider_quota.sql` — `providers.quota_json`, `quota_checked_at_utc`.
  - `0008_subscription_accounts.sql` — account switching columns on `provider_accounts` (`enabled`, `is_current`, `sort_order`, `cooldown_until_utc`, `last_error`), plus `requests.account_id` and `client_bindings.account_id`.
  - `0009_model_upstream_protocols.sql` — `provider_models.upstream_protocols_json`.
  - `0010_claude_direct_requests.sql` — the Claude-client telemetry table (deliberately outside `requests`).
  - `0011_account_kinds.sql` — `provider_accounts.kind|source|http_proxy`.
  - `0012_plugins.sql` — `plugins`, `plugin_provider_prefs` (Astra's replacement for magpie's `plugins.json`).
  - `0013_token_limits.sql` — `tokens.limits_json|allowed_clients_json`, `token_usage_daily`, backfilled from the request log.
  - `0014_alerts.sql` — `webhooks`, `alert_rules`, `alert_rule_webhooks`, `alert_states`, `webhook_deliveries` + two indexes.
  - `0015_webhook_format.sql` — `webhooks.format` (`generic` default).
  - `0016_request_origin.sql` — `requests.remote` / `requests.client_ip`.
  - `0017_tracing.sql` — the agent-link columns on `requests` (`trace_id`, `parent_span_id`, `session_key`, `chain_key`, `turn_id`, `parent_request_id`, `step`, `call_kind`, `link_source`, `finish_reason`, `tool_calls`) and the `trace_spans` table (`ON DELETE CASCADE`, so request-log retention removes both). All new columns are nullable or defaulted: rows logged before this migration have no trace, and nothing is back-filled. `request_attempts` (0001) stays unused — attempts are `upstream.attempt` spans.
  - `0018_token_pool.sql` — `tokens.pool_json` (a `TokenPool`; NULL = the single `provider_id`). Member provider ids are not foreign keys; the first member is mirrored in `provider_id` / `account_id` for older binaries.
  - `0019_provider_account_usage.sql` — `provider_account_usage_totals`, keyed by provider and account (`''` = the provider's own API key). Backfilled once from retained request history; log cleanup never reduces it. The newest migration.
- **Repository methods other modules depend on:** `SettingsRepository.GetAsync/SetAsync` (settings blob), `ModelRepository.GetPriceTableAsync`/`GetUpstreamIdIndexAsync`/`GetMatcherCandidatesAsync`/`GetMatcherIndexAsync` (pricing + linking inputs; upstream-ID dictionaries and matcher indexes are immutable), `ProviderRepository.UpdateQuotaAsync`, `ProviderAccountStore.UpdateTokensAsync`/`SetCooldownAsync`, `ClientRepository.ListBindingsAsync`/`ReplaceBindingsAsync`, `TokenRepository.FindByPrefixAsync`, `RequestRepository.InsertBatchAsync`, `IPluginListStore`, `IClientConfigStateStore`.
- **A row-level contract:** every timestamp column is the `DateTimeOffsetHandler` text format; every money column is an INTEGER in nanodollars.

## How to extend
- To add a **schema change**: add `src/Astra.Data/Migrations/NNNN_name.sql` with the next number, then (only if a repository column list changes) update that repository's `SELECT`/`INSERT` and its namespace-level row type, then a test. Missing the repository update makes the new column silently invisible to reads; missing the migration file makes the query fail with "no such column" at runtime.
- To add a **repository/table**: add the SQL in a new migration, then the class in `src/Astra.Data/Repositories/`, then expose it as a property on `AstraDatabase` (`src/Astra.Data/AstraDatabase.cs:16`) and register/consume it from `astra-server`. A repository that is not on `AstraDatabase` has to be built from `db.Factory` by hand (the precedent is `ClaudeDirectRequestRepository`, `src/Astra.Server/Api/ClaudeDirectService.cs:51`).
- To add a **row type** for a query: declare it `internal sealed` at namespace level (record or class) next to the repository, never nested/private inside it. A nested private type is **silently** not intercepted by Dapper.AOT — no warning — and then dies at runtime under Native AOT.
- To add a **timestamp-carrying column**: bind parameters with `DateTimeOffsetHandler.ToStorage(...)` and give the row type a `DateTimeOffset` property; nullable timestamps travel as `string?` and are parsed in the row's `ToX()` mapper (`src/Astra.Data/Repositories/ProviderRepository.cs:410`).
- To change **what the seed import skips**: change `SeedImporter.ImportAsync` and bump `SeedCatalog.SeedVersion`; without the version bump the whole pass is skipped (`src/Astra.Data/SeedImporter.cs:41`).

## Invariants and traps
- **Never edit an already-applied migration.** `MigrationRunner` applies only files whose numeric prefix is greater than `MAX(version)` in `schema_version`, so an edit to an old file is invisible to every existing install while new installs get the edited text — the two schemas drift. See `src/Astra.Data/MigrationRunner.cs:ReadCurrentVersionAsync` and the "Plain DDL (NOT idempotent by design — 0001 is the only idempotent script)" note at the top of every file after 0001 (e.g. `src/Astra.Data/Migrations/0016_request_origin.sql:5`).
- **Never call vanilla Dapper from a shipped assembly** — no `SqlMapper.AsList`, `SqlMapper.AddTypeHandler`, `DefaultTypeMap`. They are not intercepted, so they root vanilla Dapper's reflection machinery into the AOT closure and `ilc` fails with `MSB3077` pointing at Dapper, not at your line. Use `.ToList()`, or a `new List<T>(...)` ctor when `DAP028` objects (`src/Astra.Data/Repositories/ClientConfigStateStore.cs:24`), and the module-level `[module: TypeHandler]` instead of `AddTypeHandler` (`src/Astra.Data/SqliteConnectionFactory.cs:11`).
- **Row types must be `internal` at namespace level.** A nested private one is not intercepted and there is no warning. The same sentence is repeated at the bottom of each repository file, e.g. `src/Astra.Data/Repositories/ProviderRepository.cs:378`.
- **Every connection goes through the factory.** A raw `new SqliteConnection` skips WAL, `foreign_keys=ON` and `busy_timeout`, so cascades, per-connection FK enforcement and the DateTimeOffset handler all change behaviour — and its writes do not invalidate `RowCache` (served stale for up to 2 s).
- **Write cached tables through `Open`/`OpenAsync`, never `OpenUntrackedAsync`.** Tracking is per connection, not per method, so any write there invalidates every cached read at once; `OpenUntrackedAsync` exists for the request log alone, whose only cached column is `tokens.last_used_at` (which `FindByPrefixAsync` may therefore lag on — read `ListAsync`/`GetAsync` to show it).
- **A cached read hands out its caller's own copy.** Cache the row type and map it per call (`ProviderRow.ToProvider`), or clone a flat entity (`TokenRecord.Clone`); a shared `Provider` would share its `JsonObject` settings, which are not safe to read from two threads.
- **Dapper.AOT 1.1.0 casts a sequence parameter to its declared collection type of `object`:** a `List<anonymous>` argument throws `InvalidCastException` at run time (`List<object>`); pass the `IEnumerable<anonymous>` (covariant) as `InsertBatchAsync` does, or a named row type.
- **`DateTimeOffset` is stored as text, so string comparison is the ordering.** The format is fixed-width UTC, which is why `tokens.last_used_at = MAX(COALESCE(last_used_at, ''), @used_at)` works (`src/Astra.Data/Repositories/RequestRepository.cs:237`).
- **A backup is taken only when the DB already has tables** (`HasExistingSchemaAsync`) — a fresh file gets none, which the tests pin (`tests/Astra.Data.Tests/MigrationTests.cs:85`).
- **Dapper.AOT cannot read `DynamicParameters`**, so every filterable query uses constant SQL text plus a null-means-no-filter parameter shape rather than string-built predicates (`src/Astra.Data/Repositories/RequestRepository.cs:241`).
- **`ForeignSqlite` must stay outside the AOT interceptor path**: it uses raw ADO.NET on purpose because another app's schema cannot be intercepted, and table names are interpolated only after `Safe()` validation (`src/Astra.Data/Foreign/ForeignSqlite.cs:57`).
- **The test assembly opts out** with `[module: DapperAot(false)]` and therefore needs the vanilla-Dapper globals in `VanillaDapper`; that file is mirrored in `tests/Astra.Server.IntegrationTests/VanillaDapper.cs` and the two must be kept in sync (`src/Astra.Data/Astra.Data.csproj:7` explains the `InterceptorsNamespaces` opt-in that production has and tests drop).

## Tests
- `tests/Astra.Data.Tests/` — xunit, one file per repository (`RequestRepositoryTests.cs`, `TraceRepositoryTests.cs`, `ProviderRepositoryTests.cs`, `ProviderAccountStoreTests.cs`, `ClientRepositoryTests.cs`, `ModelRepositoryTests.cs`, `PluginRepositoryTests.cs`, `ClaudeDirectRequestRepositoryTests.cs`, `ForeignSqliteTests.cs`, `ProviderAccountSwitchingTests.cs`, `SeedImportTests.cs`) plus `MigrationTests.cs`.
- `RowCacheTests.cs` pins that every cached read sees a write at once (and another database instance's), hands out copies, and that the request log does not invalidate; `SqliteConnectionFactoryTests.cs` that a reused pooled connection keeps its PRAGMAs.
- `TestDb.NewPaths` / `TestDb.InitializeAsync` (`tests/Astra.Data.Tests/TestDb.cs:8`) give each test a fresh temp-directory database; the seed import is off by default. No test touches a real `~/.astra`.
- `MigrationTests` deliberately rolls a migrated DB back a few versions in place to prove each migration upgrades, backs up (filename contains `-v<n>`), and that re-running is a no-op.
- Run all: `dotnet test tests/Astra.Data.Tests` (after `dotnet build -warnaserror`; `dotnet test --no-build` from the root runs everything). Run one file: `dotnet test tests/Astra.Data.Tests --filter "FullyQualifiedName~MigrationTests"`.

## Related flows
- [server-startup](../flows/server-startup.md) — `AstraDatabase.InitializeAsync` migrates, backs up and seeds before the host starts.
- [gateway-request](../flows/gateway-request.md) — request/usage rows are written through `RequestRepository.InsertBatchAsync`; settings come from `SettingsRepository`.
- [request-tracing](../flows/request-tracing.md) — the span tree rides the same batch, and `TraceRepository` is what `/api/traces` reads.
- [provider-quota-refresh](../flows/provider-quota-refresh.md) — quota snapshots land via `ProviderRepository.UpdateQuotaAsync`.
- [subscription-account-refresh](../flows/subscription-account-refresh.md) — token rotation, cooldown and `is_current` live in `ProviderAccountStore`.
- [privacy-redaction](../flows/privacy-redaction.md) — `requests.privacy_json` is written with the row and read back by `QueryPrivacyEventsAsync`/`PrivacyEventStatsAsync`.
- [token-limits-enforcement](../flows/token-limits-enforcement.md) — `token_usage_totals` / `token_usage_daily` are the counters the limiter reads.
- [client-config-takeover](../flows/client-config-takeover.md) — `client_config_state` is the restore bookkeeping.
- [plugin-install-and-run](../flows/plugin-install-and-run.md) — the installed list is the `plugins` table.
- [admin-api-request](../flows/admin-api-request.md) — every admin endpoint group reaches its table through one of these repositories.
very admin endpoint group reaches its table through one of these repositories.
