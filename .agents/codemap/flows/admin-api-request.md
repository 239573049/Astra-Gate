---
flow: admin-api-request
modules: [web-ui, cli, astra-server, astra-data, astra-core]
commit: 6324e30
updated: 2026-10-14
---
# Admin API request (web UI / CLI → `/api/**` → JSON)

**Trigger:** the web admin UI (browser or the desktop shell), the CLI, or the desktop tray calls any
`/api/**` endpoint — e.g. drawing a provider list, patching settings, or enabling a client.
**Outcome:** the caller has a DTO JSON body with HTTP 200 (or 204 with no body), or a typed denial —
`{ error, details? }` with 4xx (missing admin header, no session, remote admin disabled, validation) or
a 500. Secrets are masked, never echoed; the server never returns a pole of the database row verbatim.

## Steps
1. `web/src/api/client.ts:30` — `api(method, path, body?, signal?)` builds the headers: always `Accept:
   application/json`, `X-Astra-Admin: 1` on **every** non-GET (`:32`), `Content-Type` only when there is
   a body, and `credentials: 'include'` (the session cookie) (`:41`) (`web-ui`)
2. `web/src/api/client.ts:23` — the origin: `''` in Vite dev (the `web/vite.config.ts:20` proxy forwards
   `/api` to the running server) or, in the desktop shell, `window.__ASTRA__.apiBase` injected by
   `desktop/src/preload.ts:21` (default `http://127.0.0.1:17321`, overridable with the `--astra-api-base`
   switch) (`web-ui`, `desktop-app`)
3. ⇢ crosses **process/network** boundary via HTTP: `fetch(apiBase() + path)` against the loopback
   listener; in the packaged desktop app the page is `app://astra`, so the request is cross-origin and its
   CSP must admit the server origin — `desktop/src/shared/csp.ts:15` `connect-src 'self' <apiOrigin>`
4. **CLI equivalent:** `cli/src/lib/http.ts:45` `apiRequest` — `adminHeaders(mutating, runtimeToken)`
   (`:28`, same `X-Astra-Admin: 1` rule, plus `X-Astra-Runtime-Token` for privileged calls), an
   `AbortSignal.timeout(timeoutMs)` (`:61`) and an `AstraError` when the server is unreachable; the base
   URL and token come from `runtime.json`, never from the configured port (`cli/src/lib/server-lifecycle.ts:57`
   `probeRunning`, `:111` `waitForStartedServer`, `cli/src/lib/runtime.ts:7`) (`cli`)
5. **Desktop main process equivalent:** `desktop/src/api.ts:21` `requestJson` — `adminHeaders`
   (`desktop/src/shared/requestHeaders.ts:6`, always sends the header, no mutating flag) for
   `PUT /api/clients/{kind}/binding`, `POST /api/admin/shutdown`, `POST /api/update/check` (`desktop-app`)
6. `src/Astra.Server/Hosting/AstraApp.cs:258` — `UseMiddleware<SecurityMiddleware>()` runs after `UseCors`
   (`:257`, only the `app://astra` origin, cookies allowed) and before `UseRateLimiter` (`:259`) and the
   static-file/SPA chain; it is the first thing every `/api` call meets (`astra-server`)
7. `src/Astra.Server/Security/Security.cs:91` `SecurityMiddleware.InvokeAsync` — (a) loopback mode admits
   only `127.0.0.1` / `localhost` / `::1` Host headers (`:89`, `:95`) or the tunnel's own hostname
   `TunnelService.PublicHost` (`src/Astra.Server/Tunnel/TunnelService.cs:51`), else **421** "Host not
   allowed" (DNS-rebinding defense); (b) `remote = tunnelHost || ViaCloudflare(...)` (`:102`, `Cf-Ray` /
   `Cf-Connecting-IP` at `:148`) → `RequestOrigin.MarkRemote` (`src/Astra.Gateway/Http/RequestOrigin.cs:14`,
   a `ctx.Items` flag) and, in loopback mode, `AdoptCloudflareHeaders` copies the visitor's IP and
   https scheme so rate limits and the Secure cookie see the real visitor; (c) off-loopback, anything not
   `FromThisMachine` (loopback peer + loopback Host + no proxy header) → `RequestOrigin.MarkNetwork`
   (`astra-server`, `astra-gateway`)
8. `Security.cs:110` — for `/api` (not OPTIONS): remote + loopback + `!settings.Current.Tunnel.AllowAdmin`
   + not a probe path → **403** "Remote admin access is disabled"; probes are only `/api/health`,
   `/api/version`, `/api/auth/status` (`IsRemoteProbe`, `:144`). Then a mutating method (not GET/HEAD)
   without `X-Astra-Admin: 1` → **403** `Missing X-Astra-Admin header` (`:120`), and
   `SessionRequired(ctx)` (true unless `RequestOrigin.IsLocal` — remote or network requests) without the
   `astra_session` cookie → **401** "Login required" (`:130`), except the open path list at `:127`
   (`/api/health`, `/api/version`, `/api/auth`, `/api/admin/shutdown`, `/api/update/status`). All denials
   are `Problem()` → `WriteAsJsonAsync(new ErrorOnlyDto(msg), ApiJson.Info<ErrorOnlyDto>())` (`:164`)
9. `src/Astra.Server/Hosting/AstraApp.cs:272`–`:287` — endpoint groups mapped by `Map*Endpoints` on the
   public `IEndpointRouteBuilder`; routing matches `/api/providers/{id}` etc. to the handler; the SPA
   fallback (`:290`) answers any unmatched `/api/**` with 404 `{ error: "Not found: GET /api/nope" }` (`astra-server`)
10. Handler shape — DI parameters resolve from `AstraApp.Create`'s registrations (`AstraDatabase`,
    `ProviderTemplateCatalog`, `ISecretProtector`, `SettingsService`, `ClientService`, `ProviderProbe`,
    `BodyStore`, …), plus `HttpContext` / `CancellationToken`. Three real examples:
    - `src/Astra.Server/Api/ProviderEndpoints.cs:57` — `MapGroup("/api/providers")` + `AdminApiErrorFilter`
      + `ProviderWriteFilter`; `GET ""` (`:59`) loops `db.Providers.ListAsync` and projects each row
      through `ToDtoAsync` (`:425`), which unprotects the key only to build a mask (`:431`); `POST ""`
      (`:69`) reads a `JsonObject` body and inserts via `db.Providers.InsertWithModelsAsync` (`:109`)
    - `src/Astra.Server/Api/RequestEndpoints.cs:92` — `GET /api/requests` parses query strings into a
      `RequestQuery` (`:96`), calls `db.Requests.QueryAsync(query, ct)` (`:108`) and returns
      `Results.Ok(new RequestPageDto(...))`; `GET /api/requests/{id}` (`:171`) prefers the in-flight row in
      `LiveRequestFeed` and falls back to `db.Requests.GetAsync`; its captured bodies are previews
      (`BodyPreviewBytes`, 64 KB each, plus full `sizes`), and `GET /api/requests/{id}/bodies/{part}` streams one whole
      part from its file (`BodyStore.FileFor`); `PATCH /api/settings` (`:255`) mutates an
      `AppSettings` blob through `SettingsService.UpdateAsync`
    - `src/Astra.Server/Api/ClientEndpoints.cs:15` — `MapGroup("/api/clients")` + `AdminApiErrorFilter`;
      every handler delegates to `ClientService`, and the list endpoint deliberately materialises
      `(await clients.ListAsync(ct)).ToList()` (`:18`) before returning
11. Persistence — `src/Astra.Data/AstraDatabase.cs:33`–`:43` exposes one repository per aggregate
    (`Requests`, `Providers`, `Models`, `Clients`, `Tokens`, `Settings`, `Plugins`, `Alerts`, …); handlers
    never open a connection. `ProviderRepository.ListAsync` (`src/Astra.Data/Repositories/ProviderRepository.cs:32`)
    and `InsertAsync` (`:47`) open a `SqliteConnection` through `SqliteConnectionFactory.OpenAsync`
    (`src/Astra.Data/SqliteConnectionFactory.cs:51`) and run Dapper.AOT-rewritten SQL
    (`[module: DapperAot]`, `:11`); row types are `internal` at namespace level and are mapped to domain
    records (`r.ToProvider()`) inside the repository (`astra-data`)
12. Wire contract out — `ApiJson.Result` (`src/Astra.Server/Api/ApiJson.cs:18`) and
    `ApiJson.Info<T>()` (`:22`) resolve source-generated metadata via `JsonContexts.Info<T>(Json.Api)`
    (`src/Astra.Core/JsonContexts.cs:70`) instead of the `RequiresDynamicCode`-annotated
    `Results.Json(value, options)` overloads; plain `Results.Ok(value)` goes through ASP.NET's pipeline,
    configured once at `AstraApp.cs:231` with `Json.Api`'s naming policy, ignore-nulls and
    `TypeInfoResolver = JsonContexts.Resolver`. `Json.Api` (`src/Astra.Core/Json.cs:25`) is camelCase with
    relaxed escaping; persisted documents use `Json.Storage` (snake_case) (`:12`) (`astra-core`, `astra-server`)
13. Error paths (two styles, both in use):
    - `src/Astra.Server/Api/AdminApiErrorFilter.cs:13` — `AddEndpointFilter<AdminApiErrorFilter>()` on a
      group catches `AdminApiException(status, message, details?)` (`:6`) → `ErrorBody { error, details }`
      at that status (`:18`), plus `JsonException|ArgumentException|FormatException|EditorException` → 400
      (`:19`) and `IOException|UnauthorizedAccessException` → 409 "Configuration file could not be
      updated" (`:21`). Groups using it: `/api/providers` (+ `ProviderWriteFilter`), `/api/clients`,
      `/api/models/sync`, `/api/plugins`, `/api/plugin-providers/{id}`,
      `/api/clients/claude-code/direct`, `/api/webhooks`, `/api/alerts`
    - `src/Astra.Server/Api/ModelEndpoints.cs:278`/`:280` — `NotFound(what)` / `Bad(message)` helpers
      returning `ApiJson.Result(new ErrorBody(...), 404|400)`; `/api/models` and the un-grouped
      `/api/requests`, `/api/stats/*`, `/api/settings` use this value-returning style
    - `Results.NotFound/BadRequest(new ErrorOnlyDto(...))` (`src/Astra.Server/Api/Dtos.cs:22`) is the
      oldest style and survives in `/api/subscription*` (`SubscriptionEndpoints.cs:226`,
      `SubscriptionEndpoints.Import.cs:38`) and `/api/privacy/dry-run` (`PrivacyEndpoints.cs:98`) — those
      messages are Chinese, matching those files
14. AOT guard — every type crossing this boundary is listed in `src/Astra.Server/Api/ServerJsonContext.cs`
    (`[JsonSerializable]` per DTO, `:24`–`:228`), which registers itself into `JsonContexts` from a
    `[ModuleInitializer]` (`:237`); an unregistered type silently takes the reflection fallback on the JIT
    (`src/Astra.Core/JsonContexts.cs:96`) and throws `NotSupportedException` under Native AOT.
    `tests/Astra.Server.IntegrationTests/JsonRegistrationTests.cs:22` drives ~60 endpoints and asserts
    `JsonContexts.FallbackTypes` is empty (`:106`) (`astra-server`, `astra-core`)
15. ⇢ crosses back over HTTP; the UI turns the body into a typed DTO that is **hand-mirrored** in
    `web/src/api/types.ts` (`:1` documents the contract: camelCase, `PricingSchedule` the one snake_case
    exception, `X-Astra-Admin` on mutating calls, `{ error, details? }` on failure) (`web-ui`)
16. `web/src/api/client.ts:59` — a non-2xx response becomes `ApiError(message, status, details)` (message
    from `body.error`), a fetch rejection becomes `status: 0` (`isNetwork`); 204 returns `undefined` (`:49`).
    TanStack Query retries only once and never on 4xx (`web/src/main.tsx:24` after `ApiError`)
17. `web/src/api/hooks.ts:93` — `keys` (query keys) + per-endpoint hooks; a mutation's `onSuccess` either
    seeds the cache (`qc.setQueryData(keys.settings, s)`, `:186`) or invalidates the affected families —
    `useClientMutation` (`:856`) invalidates `clients`, `providers`, `provider`, `client-models`; the
    provider/model helpers do the same for `models`/`price-keys` (`:270`) and providers (`:365`) (`web-ui`)
18. `web/src/App.tsx:92` — the shell renders `LoginPage` when `/api/auth/status` says
    `required && !signedIn`, and `AuthStatusDto.AdminBlocked` (`Dtos.cs:32`, set in
    `SystemEndpoints.cs:55`) is how a remote browser learns that signing in would not help (`web-ui`)

## Sequence
```mermaid
sequenceDiagram
  participant U as web-ui (or cli / desktop-app)
  participant M as SecurityMiddleware
  participant E as Api/*Endpoints.cs
  participant D as Astra.Data repository
  participant J as Json.Api + ServerJsonContext
  U->>M: GET /api/providers (cookie: astra_session) — or PUT + X-Astra-Admin: 1
  M->>M: Host allowlist (421) → remote? (MarkRemote) → remote-admin block (403)
  M->>M: mutating needs X-Astra-Admin (403) → session unless from this machine (401)
  M->>E: next(ctx) — routing picks the handler
  E->>D: db.Providers.ListAsync(ct) → SqliteConnectionFactory.OpenAsync
  D-->>E: rows → domain records (Secrets stay protected)
  E->>J: Results.Ok(List<ProviderDto>) / ApiJson.Result(ErrorBody, 4xx)
  J-->>U: camelCase JSON (masked api key, no secret)
  U->>U: ApiError on !ok; Query cache seeded / invalidated
```

## Errors and cancellation
- **Denials before the handler** are `SecurityMiddleware.Problem` (`Security.cs:164`): 421 wrong Host, 403
  missing `X-Astra-Admin`, 403 remote admin disabled, 401 login required — always `ErrorOnlyDto` JSON, so
  `api()` surfaces them as `ApiError` with that message.
- **In-handler failures**: `/api/providers`-style groups throw `AdminApiException` and let
  `AdminApiErrorFilter` render `{ error, details }`; value-returning groups (`/api/models`,
  `/api/requests`, `/api/settings`, `/api/subscription*`) return `Bad` / `NotFound` /
  `Results.BadRequest(new ErrorOnlyDto(...))` directly. An uncaught exception is a 500 whose body is not
  an `ErrorDto` — `api()` then falls back to `HTTP <status>`.
- **Truncated/blocked bodies**: `api()` catches JSON parse failure and passes the raw text (`:55`); the CLI
  parses leniently (`cli/src/lib/http.ts:74`).
- **Cancellation**: every handler takes `CancellationToken` (the request's `RequestAborted`) and passes it
  into the repository; `ProviderWriteFilter` (`ProviderEndpoints.cs:545`) serialises provider writes with a
  `SemaphoreSlim` acquired with `RequestAborted`, and never holds it across an upstream probe (`:555`). The
  web client aborts a query with its own `AbortSignal` (`api(..., signal)`) — the same `signal` React Query
  passes in (`hooks.ts:152`).
- **SSE endpoints** (`/api/requests/live`, `POST /api/providers/{id}/test`) fail as plain JSON *before*
  the first byte and as a `done` event after; the viewer leaving is caught as
  `OperationCanceledException`/`IOException` (`RequestEndpoints.cs:135`, `ProviderEndpoints.cs:350`).
- Timeouts differ per client: CLI 10 s default (`cli/src/lib/http.ts:46`, 60 s for plugins via
  `cli/src/commands/plugin.ts:58`), desktop 3 s (`desktop/src/api.ts:28`), web has none.

## Where to hook in
- **New admin endpoint** — add `MapXxxEndpoints` in `src/Astra.Server/Api/*Endpoints.cs` and register it in
  `AstraApp.Create` (`AstraApp.cs:272`–`:287`); pick the group's existing error style rather than
  converting it; register every new DTO (and every `List<T>` / `IReadOnlyList<T>` root) in
  `ServerJsonContext.cs`, and mirror the DTO by hand in `web/src/api/types.ts` plus a hook in
  `web/src/api/hooks.ts` with zh/en i18n keys.
- **New mutating endpoint** — nothing to do for the header check (it is method-based) but a new *public
  read* path must be deliberately added to the open list at `Security.cs:127` and, if it should be
  reachable remotely while `Tunnel.AllowAdmin` is off, to `IsRemoteProbe` (`:144`).
- **New client** — see the [client-config-takeover](client-config-takeover.md) flow; a new *caller* (e.g.
  the desktop main process) needs its own header helper, because `desktop/src/shared/requestHeaders.ts:6`
  always sends `X-Astra-Admin` while `web/src/api/client.ts:32` derives it from the method — dropping the
  header from a mutating call is a hard 403.
- **Tempting but wrong**: `Results.Json(value, JsonSerializerOptions, …)` (annotated
  `RequiresDynamicCode`) — use `ApiJson.Result`/`ApiJson.Info` and register the type; a LINQ iterator or
  `IReadOnlyList<T>` as the response root (materialise `.ToList()` — `ClientEndpoints.cs:18`,
  `ModelEndpoints.cs:38`); a new ad-hoc `JsonSerializerOptions` instead of `Json.Api`/`Json.Storage`; a
  DTO mirror that "fixes" a field in only one of `web/src/api/types.ts` and the C# record; and reading the
  configured port instead of `runtime.json` (`cli/src/lib/server-lifecycle.ts:57`), since the server may
  drift to a free port at startup.

## Related
- Modules: [astra-server](../modules/astra-server.md), [astra-data](../modules/astra-data.md),
  [astra-core](../modules/astra-core.md), [web-ui](../modules/web-ui.md), [cli](../modules/cli.md),
  [desktop-app](../modules/desktop-app.md)
- Also touched: [astra-gateway](../modules/astra-gateway.md) (`RequestOrigin` under `Http/`, the `/v1`
  surface the same middleware also guards), [astra-clients](../modules/astra-clients.md)
  (`/api/clients/*` handlers), [astra-plugins](../modules/astra-plugins.md) (`/api/plugins/*`),
  [docs-site](../modules/docs-site.md) (`/api/plugin-market/**` and `/api/client-releases/**` are the
  site's own routes, not this server's)
- Flows: [client-config-takeover](client-config-takeover.md) is the write-heavy variant of this path
