---
flow: server-startup
modules: [astra-server, cli, desktop-app, astra-data]
commit: 6324e30
updated: 2026-10-14
---
# server-startup

**Trigger:** something runs the `astra-server` binary — `astra start` / any admin CLI subcommand
(`ensureServerRunning`), the desktop app's `ensureRunning()`, an autostart entry (`--started-by
autostart`), a container's `CMD ["serve"]`, or a user running it by hand. **Outcome:** the process
has settled host/port (possibly not the configured one), the SQLite database is migrated + seeded,
the composition root is wired and listening, `~/.astra/runtime.json` names the pid/port/version it
actually came up as, background workers are on their staggered timers, and a later shutdown of the
host deletes that file after draining the request log.

## Steps

1. `src/Astra.Server/Program.cs:Main` — the first non-`-` arg is the command (default `serve`);
   the switch (`Program.cs:32`) dispatches `serve` / `relay` / `migrate` / `restore-all` / `set-password` /
   `healthcheck` / `version` / `plugin-selftest` (`relay` skips everything below: `RelayApp.RunAsync` opens no
   database and writes no `runtime.json`), and `ArgumentException` becomes exit 64
   (`astra-server`) — the list is documented in the file's own header comment (`Program.cs:8-21`)
2. `src/Astra.Server/Program.cs:ServeAsync` — `paths.EnsureCreated()` then the three-layer option
   resolution, in this order: `ServerOptions.Load(paths)` (`ServerOptions.cs:61`, reads
   `~/.astra/config.json`; a `JsonException` becomes an `InvalidOperationException` → exit via the
   `ArgumentException` handler), `ApplyEnvironment` (`ServerOptions.cs:85`), `ApplyArgs`
   (`ServerOptions.cs:123`) — **precedence flags > env > config.json** (`astra-server` ← `astra-core`)
3. `src/Astra.Core/AstraPaths.cs:AstraPaths` — the data dir itself is resolved before any of that:
   ctor arg (tests) > `ASTRA_HOME` > `<home>/.astra`; `EnsureCreated` (`AstraPaths.cs:53`) creates
   root/keys/logs/backups/bodies/plugins and chmods `keys/` and `plugins/` to owner-only
   (`astra-core`; the same layout is mirrored in `cli/src/lib/paths.ts` and `desktop/src/shared/paths.ts`)
4. ⇢ branch on the resolved options, all *before* anything is wired (`Program.cs:74-101`):
   - non-loopback host with no `AdminPasswordHash` ⇒ stderr + exit 2
   - `ASTRA_STRICT_PORT` set and `PortPicker.CanListen` false ⇒ exit 3
   - otherwise `PortPicker.FindFree(host, port)` (`PortPicker.cs:15`, `MaxAttempts = 20`) picks the
     first free port; a different one is announced on stdout and written to `options.Port`. The
     probe opens and immediately stops a `TcpListener` per address (`PortPicker.cs:25`), so a port
     free at probe time can still be taken by the time Kestrel binds — and that bind failure is
     caught as `IOException` at `Program.cs:108` → exit 3
5. `src/Astra.Server/Hosting/AstraApp.cs:Create` — composition root, synchronous up to `Build`:
   `paths.EnsureCreated()`, then `AstraDatabase.InitializeAsync(paths).GetAwaiter().GetResult()`
   (`AstraApp.cs:45`). This is deliberately a console context: there is no
   `SynchronizationContext`, so blocking is safe; the same awaits would deadlock if this code ever
   ran on a UI thread. Integration tests inject a pre-made `Database` and `WriteRuntimeFile = false`
   (`tests/Astra.Server.IntegrationTests/TestHost.cs:54-59`) (`astra-server` → `astra-data`)
6. `src/Astra.Data/AstraDatabase.cs:InitializeAsync` — `paths.EnsureCreated()`, `MigrateAsync`,
   `Seed.ImportAsync`. `MigrationRunner.MigrateAsync` (`src/Astra.Data/MigrationRunner.cs:27`) loads
   every embedded `Migrations/NNNN_*.sql` (regex at `MigrationRunner.cs:103` — the numbering is a
   hard requirement), applies only versions `> MAX(version)` in one transaction each, and, when the
   file already holds a schema, first checkpoints the WAL and copies
   `backups/db/astra-<stamp>-v<from>.db` (`MigrationRunner.cs:65`). `SeedImporter.ImportAsync`
   (`src/Astra.Data/SeedImporter.cs:31`) returns immediately when the stored settings key
   `seed_version` matches `SeedCatalog.Current.SeedVersion` and never overwrites user/sync-owned
   models (`astra-data`)
7. `src/Astra.Server/Hosting/AstraApp.cs:55-247` — Kestrel (`localhost` → `ListenLocalhost`,
   `0.0.0.0`/`*` → `ListenAnyIP`, else `Listen(IPAddress.Parse(host))`), log level from
   `ServerOptions.LogLevel` + a `FileLoggerProvider` into `~/.astra/logs`, DataProtection keys into
   `~/.astra/keys` (`AstraApp.cs:71`), then the DI registrations: `paths`/`server`/`database`
   singletons, client/provider/gateway/plugin services, `RuntimeFile` (`:189`), `TunnelService`
   (`:195`), plus ~20 `AddHostedService` registrations and a `SystemProxy`-backed `HttpClient`
   per upstream (`astra-server`)
8. `src/Astra.Server/Hosting/AstraApp.cs:251-255` — after `builder.Build()`, three blocking loads
   that must precede the first request: `SettingsService.LoadAsync()` (also pushes the stored proxy
   into process-wide `SystemProxy`, `src/Astra.Gateway/Pipeline/SettingsService.cs:16`),
   `PrivacyGuardService.LoadAsync()`, `TokenService.EnsureDefaultAsync()` — the 0006 migration
   inserts the default token without a key, so it is generated here before the gateway can serve
9. `src/Astra.Server/Hosting/AstraApp.cs:257-303` — fixed middleware order: `UseCors` →
   `SecurityMiddleware` (Host allowlist, `X-Astra-Admin` on mutations, session cookie) →
   `UseRateLimiter` → `UseDefaultFiles`/`UseStaticFiles` when `wwwroot` exists (hashed `/assets/*`
   immutable, everything else `no-cache`) → the `Map*Endpoints()` groups → the SPA fallback, which
   serves `wwwroot/index.html` for anything that is not `/api`, `/v1`, `/v1beta` and otherwise
   writes an `ErrorOnlyDto` 404. `WebRootPath` is `<executable dir>/wwwroot` — a `pnpm --filter
   @aidotnet/web build` alone is not enough; the files have to be staged beside the binary
10. `src/Astra.Server/Program.cs:106` — `app.StartAsync()` on the Kestrel host. `ApplicationStarted`
    then fires the registrations made at `AstraApp.cs:305-310`:
    `RuntimeFile.Write(server)` and (`src/Astra.Server/Tunnel/TunnelService.cs:107`) `TunnelService`'s
    `KillStale()` + restart-if-enabled — the tunnel waits for this event precisely because the port
    is only final once Kestrel listens
11. `src/Astra.Server/Hosting/RuntimeFile.cs:Write` — atomic `runtime.json` (`.tmp` + owner-only +
    `File.Move(overwrite)`) carrying `pid`, `port`, `host`, `version`, `apiVersion`, `startedBy`,
    `startedAt` and a fresh 24-byte `RuntimeToken`; the token is the only credential
    `/api/admin/shutdown` accepts. `RuntimeInfo` and `ServerOptions` are registered in
    `src/Astra.Server/Api/ServerJsonContext.cs:199-200`, so this survives AOT
12. `src/Astra.Server/Program.cs:113-115` — prints `Astra <version> listening on <LocalUrl>` and
    `await app.WaitForShutdownAsync()`, which returns on SIGTERM/SIGINT/console close or when
    something calls `IHostApplicationLifetime.StopApplication()`
13. `src/Astra.Server/Hosting/AstraApp.cs:309` — `ApplicationStopped` → `RuntimeFile.Delete()`,
    which re-reads the file and unlinks it **only if `pid` still matches** (a stale file from a
    previous process is left for the CLI to clean up); the host then stops the hosted services, and
    `UsageWriter`'s `finally` (`src/Astra.Gateway/Pipeline/UsageWriter.cs:84-93`)
    drains the queue with `CancellationToken.None` so no finished request is lost

### Background workers (why startup is not blocked)

Registered as hosted services in `AstraApp.Create`; `BackgroundService.StartAsync` returns at the
first await inside `ExecuteAsync`, so each one is off the startup path, and each uses its own
initial delay instead of a shared scheduler:

| Worker | First run | Cadence | Evidence |
|---|---|---|---|
| `ClientProbeWarmup` | immediately | once | `Hosting/ClientProbeWarmup.cs:15` (`await Task.Yield()`) |
| `TokenMigrationWorker` | immediately | once | `Hosting/TokenMigrationWorker.cs:18` (marker `token_migration`) |
| `SubscriptionRefreshWorker` | 15 s | 5 min | `Hosting/SubscriptionRefreshWorker.cs:17` |
| `AlertEngine` | 20 s | 1 min sweep | `Alerts/AlertEngine.cs:37` (`SweepInterval` `:36`) |
| `ProviderQuotaWorker` | 30 s | 1 min tick | `Hosting/ProviderQuotaWorker.cs:21`, `MaxPerRound = 10` |
| `UpdateCheckWorker` | 30 s | 12 h, ×2 after failure up to 48 h | `Hosting/UpdateCheckWorker.cs:11-13` |
| `PluginCheckinWorker` | 2 min | 30 min | `Api/PluginCheckinService.cs:186-197` |
| `PluginProviderWorker` | event-driven | wake + 500 ms debounce | `Api/PluginProviderSync.cs:218-231` |
| `WebhookDispatcher` | immediate | 15 s idle poll | `Alerts/WebhookDispatcher.cs:45` |
| `RetentionService` | immediately (first tick fires at once) | 24 h `PeriodicTimer` | `src/Astra.Gateway/Pipeline/UsageWriter.cs:117-140` |
| `TunnelService` | on `ApplicationStarted` | supervised child + backoff | `Tunnel/TunnelService.cs:107-114` |

### How the CLI starts and adopts a server (`cli`)

14. `cli/src/lib/server-lifecycle.ts:startServer` — normalizes port/host from flags else `config.json`
    (writing them back so autostart and the desktop agree), then `probeRunning` (`:57`): runtime.json
    + `isPidAlive` + `GET /api/health`. A live server is adopted and returned with
    `alreadyRunning: true`; a stale runtime.json is deleted (`:164`)
15. `cli/src/lib/server-lifecycle.ts:166-176` — resolves the binary (`resolveServerBinary`: env >
    install.json > repo dev build > platform package), records `install.json`
    (`serverPath`/`serverVersion`), and spawns `<bin> serve --port <p> --host <h> --started-by cli`
    (detached + log fds, `--foreground` keeps stdio). `cli/src/lib/autostart.ts:109` (and the server's
    own `Autostart/AutostartService.cs`, the settings switch) uses `serve --started-by autostart` with
    **no** `--port`, so config.json wins there. On Windows the Run key starts the console binary in a
    window of its own: `Program.Main` lets go of it (`Autostart/WindowsConsole.cs`, `FreeConsole`) and
    sends stdout/stderr to `logs/server-stdout.log`
16. `cli/src/lib/server-lifecycle.ts:waitForStartedServer` — polls runtime.json (pid alive) and
    `checkHealth`, 400 ms until a 20 s deadline; the address is taken from runtime.json **not** from
    the port we passed, because the server may have drifted away from it (`:107-126`)
17. `cli/src/lib/runtime.ts:parseRuntimeJson` — the CLI's reader/validator for runtime.json (pid must
    be a positive integer, port 1-65535, host non-empty, `startedBy` whitelisted to
    `cli|desktop|autostart`); `desktop/src/shared/runtime.ts:parseRuntimeJson` is the desktop's
    independent copy (same rules, tolerates a missing port by defaulting it). The two must stay in
    step with `RuntimeFile.Write`

### How the desktop starts and adopts a server (`desktop-app`)

18. `desktop/src/main.ts:90` — `ServiceManager` is constructed with `paths`, `repoRoot`, the bundled
    server (`resources/server`, packaged builds only), `env` and `expectedApiMajor:
    EXPECTED_API_MAJOR`
19. `desktop/src/service.ts:ensureRunning` — `probe()` (`:117`) reads runtime.json + `/api/health`
    and then `/api/version`; if healthy it resolves the target binary and **adopts** the running
    server only when `shouldReplaceRunningServer` is false — i.e. versions match
    (`desktop/src/shared/resolveServerBinary.ts:92`). A version mismatch (an older `astra serve`, a
    previous install) is logged, `stopService()`d and replaced — or adopted after all when the stop
    fails, so there is never a second server on a drifted port; `target === null` keeps the running
    server rather than failing (`service.ts:156`)
20. `desktop/src/service.ts:startServer` — spawns `serve --started-by desktop --port <config.port>`
    detached with the log fd, watches `error`/`exit` for an early failure, then polls runtime.json +
    `/api/health` for 20 s, following the drifted port — only once runtime.json is the child's own
    entry (`isRuntimeFrom`, `:283`), never the configured port while another server may hold it
21. ⇢ crosses **process** boundary via the runtime token / HTTP: `GET /api/version` →
    `isApiVersionCompatible(apiVersion, EXPECTED_API_MAJOR = 1)` (`desktop/src/shared/version.ts:6-21`).
    A different major shows `showMismatchDialog` (`desktop/src/main.ts:255`) telling the user to run
    `astra update`; `app` and `server` are versioned separately, and `ServerOptions.ApiVersion`
    ("1.0") is deliberately not synced with the release version

### Graceful shutdown

22. `src/Astra.Server/Api/SystemEndpoints.cs:39` — `POST /api/admin/shutdown` accepts a loopback
    connection only, compares `X-Astra-Runtime-Token` (`SystemEndpoints.RuntimeTokenHeader`)
    constant-time against `RuntimeFile.Current.RuntimeToken`, answers `{"status":"stopping"}` and
    calls `lifetime.StopApplication()` in `Response.OnCompleted` so the body is flushed first
23. `cli/src/lib/http.ts:shutdownRequest` — the CLI POSTs that endpoint (5 s timeout) with the token
    from runtime.json (`cli/src/lib/server-lifecycle.ts:257`); it then waits 8 s for the pid to
    disappear, escalates to `terminatePid` (SIGTERM, or `taskkill /T /F` on Windows), waits 3 s,
    then `killPid` (SIGKILL) with a final 2 s wait (`cli/src/lib/process-utils.ts:92-132`), and
    finally deletes runtime.json itself
24. `desktop/src/service.ts:stopService` — same shape with the desktop's own budgets: 3 s HTTP,
    `SHUTDOWN_HTTP_GRACE_MS = 5000` of health polling, then `killTree` (SIGTERM / `taskkill /T /F`;
    `:314`). `stopIfOurs` (`:195`) runs it from `app.on('before-quit')`
    (`desktop/src/main.ts:147`) **only if this run spawned the server** (`weStarted`) — a server the
    user started themselves survives the desktop quitting
25. In-container the same path is SIGTERM straight to PID 1 (`Dockerfile:43-45`, exec-form
    ENTRYPOINT), which is what `WaitForShutdownAsync` treats as a graceful stop

### Container variant (`packaging-release`)

- `Dockerfile:19-41` — `mcr.microsoft.com/dotnet/runtime-deps:11.0` (glibc, on purpose: the release
  RIDs are `linux-x64`/`linux-arm64`), `COPY docker-bin/${TARGETARCH}/` (the unpacked
  `npm/server-linux-*` package, built by the `docker` job in `release.yml`), `ASTRA_HOME=/data`,
  `ASTRA_HOST=0.0.0.0`, `ASTRA_PORT=17321`, `ASTRA_STRICT_PORT=1`, non-root `app` user, `VOLUME /data`
- `HEALTHCHECK CMD ["/app/astra-server", "healthcheck"]` → `Program.HealthCheckAsync`
  (`Program.cs:118`), which rebuilds the options the same way and GETs `options.LocalUrl + /api/health`
  with `UseProxy = false`, 3 s timeout (the route is the unauthenticated `SystemEndpoints.cs:35`)
- `compose.yaml:5-25` — only `ASTRA_ADMIN_PASSWORD` is required; `ASTRA_PUBLIC_URL` (reverse proxy),
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, `HTTPS_PROXY`/`NO_PROXY` are commented hints. The image
  carries no config.json, so the container is env-only by construction
- `CMD` overridden with `relay --provider <id>` runs the stateless relay instead (`RelayApp`): no volume, no admin
  password, and the `HEALTHCHECK` falls back to `/readyz` when `/api/health` answers 404
- `TunnelService.Supported` is false when `DOTNET_RUNNING_IN_CONTAINER=true`
  (`Tunnel/TunnelService.cs:48-51`): the container does not spawn cloudflared

### Release/packaging coupling

- `scripts/pack-platform.mjs` copies the AOT binary **and its native companions**
  (`libe_sqlite3.so`/`.dylib`, `e_sqlite3.dll`) + `web/dist` → `bin/wwwroot` into `npm/server-*`;
  `release.yml`'s `build` job runs `dotnet publish … -p:PublishAot=true` (never
  `-p:PublishSingleFile=true`), `scripts/check-aot-warnings.mjs` and `astra-server plugin-selftest`
  on each binary — the startup path is where AOT breaks what the JIT never showed
- `scripts/gen-manifest.mjs` hashes only the executable, so the companions carry no integrity hash
  (known gap); the same publish output is what the desktop ships as `resources/server`

## Sequence

```mermaid
sequenceDiagram
  participant CLI as cli / desktop / docker
  participant P as Program.Main
  participant OPT as ServerOptions + PortPicker
  participant DB as AstraDatabase (astra-data)
  participant APP as AstraApp.Create
  participant K as Kestrel
  participant RF as RuntimeFile

  CLI->>P: astra-server serve --port/--host/--started-by
  P->>OPT: Load(config.json) → ApplyEnvironment → ApplyArgs
  OPT-->>P: host/port (+ PublicUrl, StrictPort, password hash)
  P->>P: refuse non-loopback w/o password (exit 2)
  P->>OPT: StrictPort ? CanListen : FindFree (exit 3 / drift)
  P->>APP: Create(BuildOptions{Paths, Server})
  APP->>DB: InitializeAsync().GetAwaiter().GetResult()
  DB->>DB: migrations (backup first) + seed import
  APP->>APP: DI, Settings/PrivacyGuard/TokenService loads, middleware, endpoints, SPA fallback
  P->>K: StartAsync()
  K-->>RF: ApplicationStarted → Write(runtime.json + RuntimeToken)
  Note over APP: hosted services start on their own 15-30 s staggers
  CLI->>RF: read runtime.json (port may have drifted)
  CLI->>K: GET /api/health then /api/version
  K-->>CLI: healthy, apiVersion "1.0"
  CLI->>K: POST /api/admin/shutdown (X-Astra-Runtime-Token)
  K->>APP: StopApplication() after the response
  APP->>RF: ApplicationStopped → Delete() (only if pid matches)
  CLI->>P: SIGTERM → SIGKILL if the pid is still alive
```

## Errors and cancellation

- **Exit codes are the contract** (`Program.Main`): 64 unknown command / `ArgumentException`
  (including `ASTRA_PORT`, `ASTRA_PUBLIC_URL`, `ASTRA_ADMIN_PASSWORD_FILE` and `--port` validation),
  2 non-loopback without a password, 3 port unusable or the Kestrel bind failed (`IOException`
  around `StartAsync`). `healthcheck` is 0/1.
- **Bad config.json is loud**: `ServerOptions.Load` wraps the `JsonException` in an
  `InvalidOperationException` (`ServerOptions.cs:70`) which `Program.Main` does not catch — the
  process dies with a stack trace rather than starting with defaults.
- **Port drift is the normal case, not a failure**: `FindFree` scans up to 20 ports and the new port
  only ever lives in memory + runtime.json — `config.json` keeps the configured port, which is why
  every consumer must read runtime.json. `ASTRA_STRICT_PORT=1` exists so a published container port
  cannot silently drift; loopback deployments leave it off.
- **Cancellation**: the host's `stoppingToken` is what every worker's `Task.Delay` and loop check
  uses; each catches `OperationCanceledException` and returns quietly, so a shutdown does not log a
  storm of failures. `UsageWriter` overrides that by draining with `CancellationToken.None`.
  `RequestEndpoints.cs:117` links `ctx.RequestAborted` with `lifetime.ApplicationStopping` so
  in-flight gateway streams end on shutdown.
- **Startup failure surfaces as exit 3 with a message**; the CLI turns a non-zero exit into
  `AstraError` naming the log file, and a timeout into "did not become healthy … within 20 seconds"
  (`cli/src/lib/server-lifecycle.ts:219-229`). The desktop raises `StartupError` carrying the day's
  log path (`desktop/src/service.ts:272`).
- **Stale runtime.json is expected after a crash**: `RuntimeFile.Delete` leaves a file whose pid no
  longer matches, and both clients treat it as stale — the CLI deletes it before starting
  (`server-lifecycle.ts:164`), the desktop falls back to `config.port` (`service.ts:119-121`).
- **Worker failures never reach the request path**: `UpdateCheckWorker`, `ProviderQuotaWorker`,
  `SubscriptionRefreshWorker`, `TokenMigrationWorker` and `AlertEngine` catch everything except
  their own cancellation and log; `TokenMigrationWorker` deliberately keeps its settings marker on
  failure so the next start retries.

## Where to hook in

- **A new subcommand**: add the case in `Program.Main` (`Program.cs:32`) and the header comment
  above it; offline ones belong in `Commands.cs` (they must work without the HTTP server).
- **A new startup setting**: a property on `ServerOptions` + (for containers) a variable in
  `ApplyEnvironment` and a line in the header comment, + a flag in `ApplyArgs`, + `docs/…/docker.mdx`
  and `compose.yaml` when the container surface changes. Runtime-editable settings are the other
  half: an `AppSettings` property (`src/Astra.Core/AppSettings.cs`) loaded by `SettingsService` — no
  migration.
- **Work that must run before the first request**: register the service and block in
  `AstraApp.Create` after `builder.Build()` the way `SettingsService`/`PrivacyGuardService`/
  `TokenService` do (`AstraApp.cs:252-255`). Work that may lag goes in a `BackgroundService` with its
  own initial delay in the table above.
- **A new worker**: `builder.Services.AddHostedService<T>()` in `AstraApp.Create`, and if it talks to
  a new upstream host, add its named `HttpClient` there too (the `SystemProxy` handler plus a
  per-client timeout is the convention, see `AstraApp.cs:199-229`).
- **A new endpoint group**: `MapXxxEndpoints()` in `AstraApp.Create` (`AstraApp.cs:271-287`); a
  public read endpoint additionally needs adding to `SecurityMiddleware`'s open-path list.
- **A new release-time consumer of the binary**: copy the native companions too
  (`scripts/pack-platform.mjs`, `packages/update-core`) — the AOT executable does not embed them.
- **Tempting but wrong**: `app.Run()`/`await` anything before the host starts (the migrate+seed call
  is intentionally `GetAwaiter().GetResult()` — an `await` there would move the code under a
  sync context it does not have); `-p:PublishSingleFile=true`; reading `config.json`'s port instead
  of runtime.json after start (drift); editing the layout of `~/.astra` in only one of the three
  path modules; hand-editing `runtime.json`; expecting the desktop to kill a server it did not
  start (`stopIfOurs`); spawning cloudflared from a container.

## Related

- Modules: [astra-server](../modules/astra-server.md), [cli](../modules/cli.md),
  [desktop-app](../modules/desktop-app.md), [astra-data](../modules/astra-data.md)
- Also touched: [astra-core](../modules/astra-core.md) (`AstraPaths`, `AppSettings`, `JsonContexts`),
  [astra-gateway](../modules/astra-gateway.md) (`SettingsService`, `UsageWriter`, `RetentionService`,
  `MapGatewayEndpoints`), [astra-providers](../modules/astra-providers.md) (quota / subscription
  workers), [astra-clients](../modules/astra-clients.md) (`ClientProbeWarmup`),
  [astra-plugins](../modules/astra-plugins.md) (Bun host + plugin workers),
  [packaging-release](../modules/packaging-release.md) (AOT publish, `pack-platform`, Dockerfile)
- Flows: [release-and-update](release-and-update.md) (how the binary that gets started is produced
  and replaced), [client-config-takeover](client-config-takeover.md) (`restore-all`, the offline
  sibling of `serve`)
