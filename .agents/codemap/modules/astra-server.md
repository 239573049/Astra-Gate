---
module: astra-server
sources: ["src/Astra.Server/**"]
commit: 6324e30
updated: 2026-10-18
---
# astra-server

The ASP.NET Core host published as the `astra-server` Native AOT binary: it owns the subcommand
front-end, the composition root (`AstraApp.Create`) that wires every library into one DI container
and middleware pipeline, the admin `/api` surface, the request guard, remote access through a
Cloudflare Tunnel, the alert engine + webhook dispatcher, and the background workers.
**Not responsible for:** protocol conversion and the gateway pipeline itself (`astra-gateway` —
mapped here via `MapGatewayEndpoints`), domain types/settings/alert contracts and JSON options
(`astra-core`), SQLite repositories and migrations (`astra-data`), provider quota/subscription
mechanics (`astra-providers`), client config rewrite and install (`astra-clients`), plugin runtime
and Bun host (`astra-plugins`), and the CLI/web/desktop/docs/packaging side (`cli`, `web-ui`,
`desktop-app`, `docs-site`, `packaging-release`).

## Entry points
| Symbol | File | Role |
|---|---|---|
| `Program.Main` | `src/Astra.Server/Program.cs:24` | Subcommand dispatch; first non-`-` arg is the command, default `serve`; exit 64 on unknown command/`ArgumentException`. |
| `Program.ServeAsync` | `src/Astra.Server/Program.cs:68` | `serve`: load+apply options, refuse non-loopback without a password, resolve the port, `AstraApp.Create`, start, wait for shutdown. |
| `Program.HealthCheckAsync` | `src/Astra.Server/Program.cs:118` | `healthcheck`: GET `options.LocalUrl + /api/health` with `UseProxy = false`, 3 s timeout; 0 healthy / 1 not. |
| `Program.SetPassword` | `src/Astra.Server/Program.cs:137` | `set-password`: one line from stdin, ≥ 8 chars, PBKDF2 hash into `config.json`. |
| `Program.PluginSelfTest` | `src/Astra.Server/Program.cs:61` | `plugin-selftest`: runs `Plugins.Diagnostics.PluginSelfTest.Run`, one line per check, 0 only when all pass. |
| `Commands.MigrateAsync` | `src/Astra.Server/Commands.cs:14` | `migrate`: `AstraDatabase.InitializeAsync(paths)` then exit. |
| `Commands.RestoreAllAsync` | `src/Astra.Server/Commands.cs:21` | `restore-all [--purge]`: offline restore of every client config (no HTTP, no plugin code). |
| `AstraApp.Create` | `src/Astra.Server/Hosting/AstraApp.cs:39` | Composition root: Kestrel binding, DI, middleware order, endpoint groups, SPA fallback, runtime file — also the integration-test host. |
| `SecurityMiddleware.InvokeAsync` | `src/Astra.Server/Security/Security.cs:91` | Host allowlist → remote marking → `/api` header/session/remote gates. |

## Key types and functions
| Symbol | File | Role |
|---|---|---|
| `AstraApp.BuildOptions` | `src/Astra.Server/Hosting/AstraApp.cs:25` | `Paths`, `Server`, `ConfigureBuilder` (tests plug TestServer/fake homes), `WriteRuntimeFile`, pre-made `Database`. |
| `ServerOptions` / `ApplyEnvironment` / `ApplyArgs` | `src/Astra.Server/Hosting/ServerOptions.cs:9`, `:85`, `:123` | Startup config; precedence flags > env > `config.json`; `LocalUrl`/`GatewayBaseUrl` (public URL else local), `IsLoopback`, `Version` from the assembly. |
| `PortPicker.FindFree` / `CanListen` | `src/Astra.Server/Hosting/PortPicker.cs:15`, `:25` | Probe-only (listener stopped immediately); `MaxAttempts = 20`; unparsable hosts are left to Kestrel. |
| `RuntimeFile.Write` / `Delete` | `src/Astra.Server/Hosting/RuntimeFile.cs:32`, `:54` | Atomic `runtime.json` (tmp + move, owner-only) with pid/port/host/version/apiVersion/startedBy + `RuntimeToken`; delete only if the pid still matches. |
| `AdminSessions` | `src/Astra.Server/Security/Security.cs:37` | `astra_session` cookie (time-limited DataProtection protector, HttpOnly, SameSite=Strict, Secure iff https). |
| `PasswordHasher` | `src/Astra.Server/Security/Security.cs:13` | PBKDF2-SHA256, 210k iterations, `pbkdf2-sha256$iter$salt$hash`; constant-time verify. |
| `AlertEngine` | `src/Astra.Server/Alerts/AlertEngine.cs:27` | `IAlertSignals` sink + `BackgroundService`: 1-minute sweep after a 20 s initial delay, bounded signal queue (4096, DropOldest, single reader), per rule×subject state machine, one background reader so reporting never blocks. |
| `AlertEngine.SweepAsync` | `src/Astra.Server/Alerts/AlertEngine.cs:114` | Stored-data-only observations: provider balance snapshot, subscription account status/quota/failures, token budgets via `TokenLimiter.Observation`. |
| `AlertEngine.ProcessAsync` / `StepAsync` | `src/Astra.Server/Alerts/AlertEngine.cs:159`, `:180` | Evaluate + state transitions (ok→firing with cooldown anti-flap, repeat, firing→ok "resolved"); each notification inserts one pending delivery per target webhook. |
| `WebhookDispatcher` | `src/Astra.Server/Alerts/WebhookDispatcher.cs:22` | Background sender: due deliveries (50 per pass, 4 in parallel), 4 attempts with 1/5/30-minute backoff, hourly prune (7 days / 1000 per webhook), `Wake()` for immediacy. |
| `WebhookFormatter` | `src/Astra.Server/Alerts/WebhookFormatter.cs:24` | Render-at-send-time per format (generic/Feishu/DingTalk/WeCom/Slack/Discord), platform signing, and `Rejection` for 200-with-error-code answers. |
| `AlertService` | `src/Astra.Server/Alerts/AlertEndpoints.cs:39` | Admin side: webhook/rule CRUD, secret reset, `TestAsync`, deliveries, `RedeliverAsync`, states; masks secrets and header values. |
| `AlertMessages.Format` | `src/Astra.Server/Alerts/AlertMessages.cs:13` | The webhook `message` string in `AppSettings.Locale` (zh/en kept in one switch). |
| `SecurityMiddleware` | `src/Astra.Server/Security/Security.cs:83` | Loopback Host allowlist (+ tunnel hostname), `X-Astra-Admin: 1` on mutations, session cookie for non-local requests, origin marking (remote / network via `FromThisMachine`), remote-mode restrictions (`AdoptCloudflareHeaders`). |
| `TunnelService` | `src/Astra.Server/Tunnel/TunnelService.cs:20` | Supervises the `cloudflared` child process (quick vs named), backoff restart, 200-line ring buffer, `PublicHost`, pid file, `KillStale`, `Supported` false in containers. |
| `CloudflaredRuntime` | `src/Astra.Server/Tunnel/CloudflaredRuntime.cs:19` | Resolution order `ASTRA_CLOUDFLARED` > settings path > downloaded; multi-source download with stall timeout, sha256 vs `Sums`, tgz extraction, `--version` probe cache. |
| `AdminApiErrorFilter` | `src/Astra.Server/Api/AdminApiErrorFilter.cs:13` | Endpoint filter: `AdminApiException` → its status; `JsonException/ArgumentException/FormatException/EditorException` → 400; IO/UnauthorizedAccess → 409. |
| `ApiJson` | `src/Astra.Server/Api/ApiJson.cs:15` | `Results.Json(..., JsonContexts.Info<T>(Json.Api))` — the AOT-safe way to return JSON with a status other than that of a plain `Results.Ok`. |
| `ServerJsonContext` | `src/Astra.Server/Api/ServerJsonContext.cs:229` | Source-generated metadata for every server‑owned payload/document; registered into `JsonContexts` by a `[ModuleInitializer]`. |

## Dependencies
- Uses: `astra-data` via DB/import — `AstraDatabase.InitializeAsync` at `src/Astra.Server/Hosting/AstraApp.cs:45`, repositories `db.Alerts`, `db.Settings`, `db.Providers`, `db.Accounts`, `db.Tokens`, `db.Clients`.
- Uses: `astra-gateway` via import — `GatewayCodecs.Register` / `GatewayPipeline` / `GatewayRouter` (`src/Astra.Server/Hosting/AstraApp.cs:107`), `MapGatewayEndpoints` / `MapKeyInfoEndpoints` (`:283`), `RequestOrigin` (`src/Astra.Server/Security/Security.cs:105`), `SettingsService` and `TokenLimiter` (`src/Astra.Server/Alerts/AlertEngine.cs:149`).
- Uses: `astra-core` via import — `AstraPaths`, `Json`/`JsonContexts`, `AppSettings`, `AlertEvents/AlertConditions/AlertSignal`, `ISecretProtector` (`src/Astra.Server/Hosting/AstraApp.cs:90`, `:196`).
- Uses: `astra-providers` via import — `ProviderQuotaManager`/`SubscriptionQuotaSync` driven by `ProviderQuotaWorker` (`src/Astra.Server/Hosting/ProviderQuotaWorker.cs:78`), `SubscriptionTokenService` driven by `SubscriptionRefreshWorker` (`src/Astra.Server/Hosting/SubscriptionRefreshWorker.cs:70`).
- Uses: `astra-clients` via import — `ClientEnvironment.Real`, `ClientService`, `ClientInstallService`, adapters/applier in `restore-all` (`src/Astra.Server/Commands.cs:32`).
- Uses: `astra-plugins` via import — `PluginCatalog`, `BunRuntime`, `PluginHost`, `PluginJobs`, `PluginMarket`, `MiddlewareEngine` (`src/Astra.Server/Hosting/AstraApp.cs:140`–`:185`), plugin diagnostics (`src/Astra.Server/Program.cs:63`).
- Used by: `cli` via HTTP + `runtime.json` + subcommands — endpoints in `src/Astra.Server/Api/SystemEndpoints.cs:35`, `RuntimeFile` (`src/Astra.Server/Hosting/RuntimeFile.cs:22`).
- Used by: `web-ui` via HTTP — every `/api/*` group below (`src/Astra.Server/Hosting/AstraApp.cs:270`–`:286`), static SPA files at `:259`.
- Used by: `desktop-app` via HTTP/IPC-ish — loopback API + `POST /api/admin/shutdown` with `X-Astra-Runtime-Token` (`src/Astra.Server/Api/SystemEndpoints.cs:39`), CORS origin `app://astra` (`src/Astra.Server/Hosting/AstraApp.cs:242`).
- Used by: `docs-site` via HTTP — the cloudflared mirror serves exactly the asset names `CloudflaredRuntime.Asset` returns (`src/Astra.Server/Tunnel/CloudflaredRuntime.cs:212`).
- Used by: `packaging-release` via the published binary — release runs `plugin-selftest` on each AOT binary (`src/Astra.Server/Program.cs:61`).

## State it owns
- `~/.astra/config.json` (`ServerOptions.Load/Save`, `src/Astra.Server/Hosting/ServerOptions.cs:61`) — host/port/log level/admin password hash/startedBy only; flags and env vars are deliberately never written back (`src/Astra.Server/Api/SystemEndpoints.cs:82`).
- `~/.astra/runtime.json` (`RuntimeFile`, `src/Astra.Server/Hosting/RuntimeFile.cs:32`) — authoritative for the **actual** port after drift, plus the `runtime_token` that `/api/admin/shutdown` requires; deleted on stop only when the pid matches.
- `~/.astra/logs/astra-YYYYMMDD.log` — `FileLoggerProvider`, 14-day cleanup, bounded 10k-line queue (`src/Astra.Server/Hosting/FileLogger.cs:9`).
- `~/.astra/keys/` — DataProtection key ring for `DataProtectionSecretProtector` (`src/Astra.Server/Hosting/AstraApp.cs:71`, `src/Astra.Server/Security/DataProtectionSecretProtector.cs:7`); losing it makes stored secrets and the admin cookie unreadable.
- `~/.astra/tools/cloudflared-<version>/` and `~/.astra/tools/cloudflared.pid` (`src/Astra.Server/Tunnel/CloudflaredRuntime.cs:84`, `src/Astra.Server/Tunnel/TunnelService.cs:307`).
- SQLite (through `astra-data`): alert rules/states/deliveries/webhooks, settings blobs `app` (via `SettingsService`), `update.state` (`src/Astra.Server/Hosting/UpdateCheckService.cs:48`), and the `token_migration` marker key (`src/Astra.Server/Hosting/TokenMigrationWorker.cs:17`).
- In-memory only, never persisted: alert rule cache and per rule×subject `_states` (`src/Astra.Server/Alerts/AlertEngine.cs:43`), thresholds; the tunnel state/log/page (`TunnelService.Snapshot`); the cloudflared version probe cache keyed by path+mtime (`src/Astra.Server/Tunnel/CloudflaredRuntime.cs:50`).

## Contracts it exposes
- Subcommands (`src/Astra.Server/Program.cs:8` doc comment, dispatch at `:32`): `serve [--port n] [--host h] [--started-by cli|desktop|autostart]`, `restore-all [--purge]`, `migrate`, `set-password` (stdin), `healthcheck`, `version`, `plugin-selftest`.
- Env vars (`ServerOptions.ApplyEnvironment`, `src/Astra.Server/Hosting/ServerOptions.cs:85`): `ASTRA_HOST`, `ASTRA_PORT`, `ASTRA_PUBLIC_URL`, `ASTRA_STRICT_PORT`, `ASTRA_ADMIN_PASSWORD`/`_FILE`, `ASTRA_PLUGINS` (`:98`); tunnel-side `ASTRA_CLOUDFLARED` (`src/Astra.Server/Tunnel/CloudflaredRuntime.cs:78`).
- Admin routes, each mapped in `AstraApp.Create` (`src/Astra.Server/Hosting/AstraApp.cs:270`–`:286`):
  `/api/health|/api/version|/api/admin/shutdown|/api/auth/*|/api/server/listen` (`Api/SystemEndpoints.cs`),
  `/api/models`, `/api/billing/simulate` (`Api/ModelEndpoints.cs:30`), `/api/models/sync/*` (`Api/ModelSyncEndpoints.cs:8`),
  `/api/requests*`, `/api/stats/*` incl. `/api/stats/providers` (`Api/RequestEndpoints.cs:92`), GET/PATCH `/api/settings` (`Api/RequestEndpoints.cs:252`), `/api/clients/*` (`Api/ClientEndpoints.cs:15`),
  `/api/clients/claude-code/direct/*` (`Api/ClaudeDirectService.cs:342`), `/api/tokens/*` (`Api/TokenEndpoints.cs:10`),
  `/api/providers/*` + `/api/provider-templates` + `/api/price-keys` (`Api/ProviderEndpoints.cs:36`), `/api/privacy*` (`Api/PrivacyEndpoints.cs:32`),
  `/api/providers/{id}/accounts*`, `/api/provider-accounts/*`, `/api/subscription/*`, `/api/oauth/callback` (`Api/SubscriptionEndpoints.cs:212`),
  `/api/update/status|/check` (`Api/UpdateEndpoints.cs:16`), `/api/plugins*`, `/api/plugin-providers/{id}/*` (`Api/PluginEndpoints.cs:14`),
  `/api/webhooks*`, `/api/alerts/*`, `/api/webhook-deliveries/{id}/redeliver` (`Alerts/AlertEndpoints.cs:419`),
  `/api/tunnel*` (`Api/TunnelEndpoints.cs:40`), GET/PUT `/api/server/autostart` (`Api/AutostartEndpoints.cs`; PUT from this machine only — the login entry `astra autostart` manages, written by `Autostart/AutostartService.cs` without starting or stopping anything now), `/api/openapi.json` (`AstraApp.cs:270`), plus the gateway's `/v1`, `/v1beta`, `/v1/messages`, key-info routes.
- GET `/api/providers/{id}/usage` (`Api/ProviderEndpoints.cs`) returns each credential's lifetime/today requests,
  input/output/total tokens and nano-USD cost. `day` and `timeZone` use `UsageTimeZone` (server local by default);
  subscription cost is its saved model-priced equivalent. This is an admin-only view.
- POST `/api/providers/{id}/template-switch` (`Api/ProviderEndpoints.cs`) moves an API-key provider onto another service template
  or variant (issue #9): the target's endpoints, protocol order and identity replace the provider's, the rest goes through the
  same three-way merge as `/template-update` (`FollowDefaults`). Account providers and subscription/custom targets get 400.
  `POST /api/providers` also takes the wizard's extra fields (preferred protocols, headers, proxy, multiplier, notes).
- Headers/status codes: `X-Astra-Admin: 1` on every mutating `/api` call, `X-Astra-Runtime-Token` for shutdown, `astra_session` cookie, 421 for a disallowed Host, 403/401 `{ error }` envelopes, session `Secure` when Cloudflare says https (`src/Astra.Server/Security/Security.cs:46`).
- Webhook payload (camelCase, `WebhookDispatcher.Payload`, `src/Astra.Server/Alerts/WebhookDispatcher.cs:255`): `id, event, status, repeat, occurredAt, rule{id,name}, subject{type,id,name}, condition, observed, message, astra{version,url}` with `X-Astra-Event` / `-Delivery` / `-Timestamp` / `-Signature` headers.
- runtime.json shape (`RuntimeInfo`, `src/Astra.Server/Hosting/RuntimeFile.cs:8`) — the file CLI and desktop read to find a running server.

## How to extend
- To add a subcommand: add the `case` + doc line in `Program.Main` (`src/Astra.Server/Program.cs:32`), put the offline logic in `Commands` (`src/Astra.Server/Commands.cs:12`) if it must work without HTTP, and update the unknown-command message at `:50` — otherwise `--help`/error text lies and the command falls through to exit 64.
- To add an admin endpoint: write `MapXxxEndpoints` in `src/Astra.Server/Api/*Endpoints.cs` with `AddEndpointFilter<AdminApiErrorFilter>()` (or return values and use `Results.NotFound/BadRequest(new ErrorOnlyDto(...))` to match the group you are editing), register it in `AstraApp.Create` (`src/Astra.Server/Hosting/AstraApp.cs:270`), then add the DTO to `ServerJsonContext` (`src/Astra.Server/Api/ServerJsonContext.cs:229`) and the web hooks/types/i18n. Missing the `Map*` call makes the route 404 while the code compiles; missing the JSON registration only fails in `JsonRegistrationTests` and on Native AOT.
- To add a mutating endpoint: nothing extra — `SecurityMiddleware` applies `X-Astra-Admin` to every non-GET/HEAD `/api` path. A new *open* (session-free) read path must be added to the explicit list at `src/Astra.Server/Security/Security.cs:127`.
- To add a background task: implement `BackgroundService`, register both the singleton and `AddHostedService` when two consumers need the same instance (the alert engine/webhook dispatcher pattern, `src/Astra.Server/Hosting/AstraApp.cs:123`), and pick an explicit initial delay — workers deliberately stagger (15 s subscription, 30 s quota/update, 20 s alerts, 2 min check-ins).
- To add an alert event: descriptor in `AlertEvents.All` and the evaluation in `AlertConditions` (`astra-core`), the observation reported by the subject type (`AlertEngine.SweepAsync` or `TokenLimiter.Observation`), the zh/en message in `AlertMessages.Format` (`src/Astra.Server/Alerts/AlertMessages.cs:19`), web i18n keys, and the docs event table. Missing the `AlertMessages` case silently yields the generic test-notification text.
- To add a tunnel setting: the property on `TunnelSettings`, the `TunnelConfigRequest`/DTO in `src/Astra.Server/Api/TunnelEndpoints.cs:34`, the restart decision at `:99`, and — if it changes what `cloudflared` runs with — `TunnelService.RunOnceAsync` (`src/Astra.Server/Tunnel/TunnelService.cs:195`).

## Invariants and traps
- The configured port may drift: `Program.ServeAsync` rewrites `options.Port` to the free port and writes *that* into `runtime.json`; never assume `config.json`'s port (`src/Astra.Server/Program.cs:98`, `src/Astra.Server/Hosting/RuntimeFile.cs:36`). `ASTRA_STRICT_PORT` turns drift into exit code 3.
- `AstraApp.Create` runs migrations and the seed import synchronously before the host starts (`GetAwaiter().GetResult()`, `src/Astra.Server/Hosting/AstraApp.cs:45`) and `SettingsService`/`PrivacyGuardService`/default-token setup between `Build()` and the middleware (`:251`); an awaited call added there can deadlock in a console context.
- `ServerOptions.ApplyEnvironment` hashes `ASTRA_ADMIN_PASSWORD` in memory only — it must never reach `config.json` (comment at `src/Astra.Server/Hosting/ServerOptions.cs:83`), which is why `POST /api/auth/password` reloads the file instead of saving the running options (`src/Astra.Server/Api/SystemEndpoints.cs:82`).
- The loopback Host allowlist admits exactly `127.0.0.1`, `localhost`, `[::1]`, `::1` plus the running tunnel's public hostname; anything else gets 421 (`src/Astra.Server/Security/Security.cs:89`).
- Remote marking is header-based (`Cf-Connecting-IP` or `Cf-Ray`) or tunnel-hostname based, and in loopback mode it also *rewrites* the client IP and scheme from Cloudflare's headers (`src/Astra.Server/Security/Security.cs:148`, `:156`) — trust flows from "the connection is local", not from the headers.
- Remote visitors get only health/version/auth-status while remote admin is off; that static probe list is separate from the session open-path list (`src/Astra.Server/Security/Security.cs:144`).
- `POST /api/auth/password`, `PUT /api/server/listen` and `PUT /api/tunnel`'s `cloudflaredPath` answer only callers from this machine (`RequestOrigin.IsLocal`; `src/Astra.Server/Api/SystemEndpoints.cs`, `src/Astra.Server/Api/TunnelEndpoints.cs:72`) — a remote or LAN admin must not be able to change the password, the listen address or name a program to run.
- `PUT /api/server/listen` writes `config.json`'s host only (reload-then-save like the password); the running server keeps its address until it restarts, and the CLI, the desktop app and autostart all pick the new one up from `config.json` (the desktop restarts through the `restartService` bridge call). `ASTRA_HOST` locks it (`ServerOptions.HostFromEnvironment`).
- `ApiJson` (not `Results.Json` with ad-hoc options) is the only AOT-safe way to return JSON with a status code; `Results.Json(value, options, …)` is annotated RequiresDynamicCode (`src/Astra.Server/Api/ApiJson.cs:6`).
- An interface-typed response root (`IReadOnlyList<T>`) makes the serializer probe the whole interface graph; materialise a `List<T>` at the endpoint (`src/Astra.Server/Api/TokenEndpoints.cs:11`).
- Two error styles coexist on purpose: value-returning handlers use `Results.NotFound/BadRequest(new ErrorOnlyDto(...))`, filter-based groups throw `AdminApiException` and return `ErrorBody`; only 9 endpoint files install the filter (`src/Astra.Server/Api/AdminApiErrorFilter.cs:13`).
- `TunnelService` does nothing inside a container (`Supported` false, `src/Astra.Server/Tunnel/TunnelService.cs:45`) and only starts after `ApplicationStarted` so the port is final (`:108`); the pid file is used to kill a leftover cloudflared from a previous run, but only when the process still looks like cloudflared (`:336`).
- The tunnel's named-mode token goes into `TUNNEL_TOKEN` in the child's environment, never the command line, and is stored encrypted (`src/Astra.Server/Tunnel/TunnelService.cs:219`).
- `CloudflaredRuntime` downloads follow redirects by hand (https only, ≤ 5 hops) and the SHA-256 from `Sums` decides what is installed; the extracted file is moved into place via `.part` (`src/Astra.Server/Tunnel/CloudflaredRuntime.cs:224`, `:272`).
- Alert notifications are rows first: a crash between the state change and the send leaves a `pending` delivery that is retried after restart (`src/Astra.Server/Alerts/WebhookDispatcher.cs:14`). Bot formats sign at send time because Feishu/DingTalk reject stale timestamps (`src/Astra.Server/Alerts/WebhookFormatter.cs:10`).
- The alert rule cache is invalidated by an explicit `AlertEngine.Invalidate()` from the admin side; a rule changed without it keeps its compiled form until restart (`src/Astra.Server/Alerts/AlertEngine.cs:56`).
- `TokenMigrationWorker` clears its marker only after the rewrite ran; a failure keeps it so the next start retries, and drifted configs are skipped on purpose (`src/Astra.Server/Hosting/TokenMigrationWorker.cs:35`).
- `ClientProbeWarmup` yields before its first work so it stays off the startup path (`src/Astra.Server/Hosting/ClientProbeWarmup.cs:14`).
- `SubscriptionQuotaSync.IsCooledDue` / `ReleaseIfRecoveredAsync` recover rate-limit cooldowns only. Gateway auth
  rejections start `ProviderAccount.LastError` with `401 ` / `403 ` and must keep their cooldown even when the usage
  endpoint reports headroom: a quota query cannot validate inference access to a model/channel.

## Tests
`tests/Astra.Server.IntegrationTests/**` (xunit, one class per file, `*Tests.cs`, `[Fact]`/`[Theory]`):
- `TestHost.StartAsync` is the shared fixture — it reuses `AstraApp.Create` with a temp `AstraPaths`, an injected pre-built `AstraDatabase`, `WriteRuntimeFile = false`, `UseTestServer()`, a fake `ClientEnvironment`, and pre-sets `X-Astra-Admin: 1` (`tests/Astra.Server.IntegrationTests/TestHost.cs:49`). `ClaudeDirectTests.LocalHost()` adds a loopback address filter.
- `JsonRegistrationTests` is the AOT guard: it walks the admin API and asserts `JsonContexts.FallbackTypes` is empty; it lives in the `"JSON registration guard"` collection with `DisableParallelization = true` because fallback tracking is process-global (`tests/Astra.Server.IntegrationTests/JsonRegistrationTests.cs:7`).
- The assembly opts out of Dapper.AOT in `ModelSyncApiTests.cs:17` (`[module: DapperAot(false)]`), so `VanillaDapper.cs` restores `MatchNamesWithUnderscores` + the `DateTimeOffset` handler in a `[ModuleInitializer]` — mirrored in `tests/Astra.Data.Tests/VanillaDapper.cs`, keep the two copies in sync.
- Background services are driven directly rather than waited on: `AlertEngine.ProcessAsync`/`SweepAsync`, `WebhookDispatcher.DeliverDueAsync`/`AttemptAsync` (see `tests/Astra.Server.IntegrationTests/AlertTests.cs:181`, `:300`), with a `WebhookReceiver : HttpMessageHandler` as the fake endpoint.
- Run one file: `dotnet test tests/Astra.Server.IntegrationTests --filter FullyQualifiedName~JsonRegistrationTests` (all: `dotnet test --no-build` after `dotnet build -warnaserror`).

## Related flows
- [gateway-request](../flows/gateway-request.md)
- [request-tracing](../flows/request-tracing.md)
- [server-startup](../flows/server-startup.md)
- [admin-api-request](../flows/admin-api-request.md)
- [provider-quota-refresh](../flows/provider-quota-refresh.md)
- [subscription-account-refresh](../flows/subscription-account-refresh.md)
- [privacy-redaction](../flows/privacy-redaction.md)
- [token-limits-enforcement](../flows/token-limits-enforcement.md)
- [plugin-install-and-run](../flows/plugin-install-and-run.md)
- [release-and-update](../flows/release-and-update.md)
../flows/release-and-update.md)
