# AGENTS.md

Guidance for coding agents working in this repository. It covers the whole repo; no scoped
AGENTS.md files exist yet. Design rationale lives in `.plan/*.md` (local planning docs,
gitignored) — code comments cite them as "plan §N"; keep such comments true when you change
the behavior they describe.

## What this is

Astra (`astragate`) — a local AI gateway. One product, two toolchains:

- **.NET 10** (`Astra.sln`; `global.json` pins SDK 10.0.x): `src/Astra.Server` builds
  `astra-server`, an ASP.NET Core host published as a self-contained Native AOT binary (no .NET
  runtime on the target; a JIT single-file publish still works — see Native AOT). The other
  `src/Astra.*` projects are its libraries. Tests live in `tests/Astra.*.Tests` (xunit).
- **pnpm workspace** (`pnpm@9.15.0`, `pnpm-workspace.yaml`; Node >= 18 per `cli`, CI runs Node 24):
  `cli/` (npm package `astragate`, bin `astra`; `release.yml` also rebuilds and publishes it as
  `@aidotnet/astra-gate`, whose self-update targets its own baked-in name), `web/` (`@aidotnet/web`, Vite + React admin UI),
  `desktop/` (`@aidotnet/desktop`, Electron shell), `docs/` (`@aidotnet/docs`, Next.js/Fumadocs
  documentation site), `npm/*` (platform packages `@aidotnet/server-*` / `@aidotnet/desktop-*`),
  `packages/update-core` (shared update engine).

`docs/` is the fumadocs documentation site (`pnpm --filter @aidotnet/docs dev` / `build`; content in
`docs/content/docs`, shown as a single grouped sidebar: the root `meta.json` orders the module
folders, each folder's `meta.json` carries `title`/`description`/`icon`/`defaultOpen`, and
`---[Icon]Name---` entries inside `pages` add sub-group separators — no `"root": true` tabs).
Canonical/hreflang/sitemap/robots/llms URLs use the fixed origin `https://astra-gate.si`
(the `siteUrl` constant in `docs/lib/shared.ts` — not env-driven; change it there to rebrand).
It doubles as the official website and ships as its own Docker image: `.github/workflows/docs.yml`
builds `docs/Dockerfile` (Next standalone output; the build context is the repo root) and pushes to
`ghcr.io/<owner>/<repo>/docs` on every `main` push that touches `docs/**` (or the lockfile / workflow).
The site also serves the plugin market (`docs/lib/plugin-market/`, `/api/plugin-market/**`, pages under
`/plugins` and `/en/plugins` — both languages' copy and cards live in `components/plugins/`, kept free of the
registry store so the client components don't pull in `node:fs`): upstream community registry (astragate-community/astra-gate-plugins' `registry.json`, whose tarballs are GitHub Release assets; regenerate it and the two built-in copies with that repo's `scripts/release.mjs pack`) + published
entries/tarballs under `${DATA_DIR}/plugin-market/`; publishing needs `PLUGIN_UPLOAD_TOKEN` (see linked update 10).
Tutorial screenshots in `docs/public/screenshots/` are captured from the live UI (`web` dev server on
:5173 + a running astra-server) via `node docs/scripts/snap.mjs`; re-run it after UI changes instead of
editing PNGs. `login.png` is the only mockup-rendered shot (`shots/login.html`, loopback never shows a
login page). `docs` has no `lint`/`test` script and its typecheck is named `types:check`
(`next typegen && tsc --noEmit`), which `pnpm -r --if-present lint|typecheck|test` therefore skips
silently — run those two by hand when the site changes. Its `build` is a full Next.js build and
fails if the shell exports a non-standard `NODE_ENV` (unset it first).

Distribution couples the sides: the server binary and `web/dist` are packed into `npm/server-*`
(`scripts/pack-platform.mjs`), and `web/dist` is copied into `desktop/renderer`
(`desktop/scripts/copy-web.mjs`). Packaging changes can therefore span toolchains. The AOT
executable needs its native companion library beside it (see Native AOT), so anything that
copies the binary must copy the companions too — `scripts/pack-platform.mjs` and
`packages/update-core` both do.

The Docker image (root `Dockerfile`, `compose.yaml`, published to `ghcr.io/<owner>/<repo>` by the
`docker` job in `release.yml`) is a fourth consumer of that output: it compiles nothing, it
`COPY`s `docker-bin/<amd64|arm64>/` — the unpacked `npm/server-linux-{x64,arm64}` package
(binary + `libe_sqlite3.so` + `wwwroot/`) — onto `mcr.microsoft.com/dotnet/runtime-deps` (glibc, on
purpose: the release RIDs are not `linux-musl-*`). `Dockerfile.dockerignore` limits its context;
the root `.dockerignore` belongs to `docs/Dockerfile`.

## Commands

.NET, from the repo root (the single `Astra.sln` is picked up automatically):

- Build: `dotnet build -warnaserror` — warnings are errors (`Directory.Build.props` sets
  `TreatWarningsAsErrors`; CI adds the flag too). New warnings break the build.
- Test all: `dotnet test --no-build` (after building). Targeted: `dotnet test tests/Astra.Gateway.Tests`.
- Run: `dotnet run --project src/Astra.Server -- serve` — subcommands are listed in the
  `Program.cs` header comment (`serve`, `migrate`, `restore-all`, `set-password`, `healthcheck`, `version`,
  `plugin-selftest`).
- Publish (release): `dotnet publish src/Astra.Server -c Release -r <rid> --self-contained true
  -p:PublishAot=true -o out/publish` — Native AOT; never pass `-p:PublishSingleFile=true`
  (see Native AOT).

Node (run `pnpm install` first; CI uses `pnpm install --frozen-lockfile`):

- All packages: `pnpm -r --if-present lint` / `typecheck` / `test` / `build`.
- One package: `pnpm --filter astragate test`, `pnpm --filter @aidotnet/web dev`, etc.
- Version gate: `node scripts/sync-versions.mjs --check` (read-only); write mode also rewrites
  `<Version>` in `Directory.Build.props`. Versions are `x.y.z` (stable) or `x.y.z-preview.N`
  (preview) only — `scripts/release-channel.mjs` holds the rule and derives the channel.
- Releasing: follow `.agents/skills/release/SKILL.md` (cutting a stable or preview release),
  `.agents/skills/release-notes/SKILL.md` (the CHANGELOG section) and
  `.agents/skills/release-troubleshoot/SKILL.md` (a failed or partial run).

Command sources: package.json scripts plus `.github/workflows/ci.yml` and `release.yml`.

Known command hazards:

- `packages/update-core` is a source-only workspace package (`main`/`exports` point at
  `src/index.ts`; it has no build script and is never published). Consumers (cli via tsup,
  desktop via esbuild) bundle it from source, so its tests and typecheck run against `src`
  directly.
- `pnpm --filter @aidotnet/web build` runs `tsc --noEmit && vite build`: type errors block the build.
- `desktop` build/dev copies `web/dist` first and fails if the web app was not built (`pnpm dev`
  can skip via its `--optional` flag and load the renderer from the Vite dev server).

## Architecture

### .NET dependency direction (set by csproj references — do not invert)

```
Astra.Core  <-  Astra.Data, Astra.Clients, Astra.Providers, Astra.Plugins  <-  Astra.Gateway  <-  Astra.Server
(domain, zero      (persistence / client config take-over /                 (pipeline,         (host, wires
 NuGet refs)        provider templates / plugin runtime)                     protocol IR)       everything)
```

`Astra.Plugins` runs magpie-compatible plugins (magpie plugins must keep running unmodified — keep the
names plugins see: `magpie.*` in package.json, `x-magpie-*` headers, `MAGPIE_*_PROXY`). Design rationale:
`.plan/plugins.md`.

- Gateway middleware (`Middleware/`) and agent plugins (`Agents/`) run in process on Jint
  (`Js/JsRuntime.cs`; host interop is JSON text and `JsValue` only — never `AllowClr` or CLR wrappers,
  which is what keeps it AOT-safe). Middleware runs in `GatewayPipeline.HandleAsync` before the privacy
  guard, on agent requests only. Agent plugins are clients `plugin-<id>` (`PluginClientAdapter`); their
  row keeps the descriptor (`extras.plugin`) so `restore-all` works without the plugin.
- Provider plugins run in magpie's own `host.js` / `pi.js` (vendored unmodified in `Host/`, MIT;
  `Host/VENDORED.md` records commit and hashes, `HostAssetsTests` checks them, refresh with
  `scripts/vendor-magpie-host.mjs`) under the pinned Bun `BunRuntime` downloads (`ASTRA_BUN` overrides).
  `PluginProviderSync` mirrors them into providers `plugin-<id>` with `AuthSchemes.Plugin` (deliberately
  not in `AuthSchemes.All`), `plugin://` endpoints, and accounts whose `extra.plugin_key` names the
  sign-in in plugin-auth.json; the gateway sends their requests through `PluginFetchHandler`.
- Tests that need Bun use `[BunFact]` (skip without one); CI installs the pinned Bun.
- A plugin's run-time inputs are files under `~/.astra/plugins/` (see State ownership), so anything
  a plugin reads at request time resolves there or not at all — never relative to the process's
  working directory.

- Domain logic (billing, pricing, models, privacy) belongs in `Astra.Core`; persistence in
  `Astra.Data` repositories; HTTP and protocol handling in `Astra.Gateway`; endpoints in
  `Astra.Server/Api`.
- `src/Astra.Server/Hosting/AstraApp.cs` is the composition root: DI registrations, middleware
  order, and every `Map*Endpoints()` call. New services, hosted workers, and endpoint groups are
  registered there. Integration tests reuse `AstraApp.Create` via `BuildOptions`
  (`InternalsVisibleTo Astra.Server.IntegrationTests`).
- Most admin endpoint groups live in `src/Astra.Server/Api/*Endpoints.cs` (shared DTOs in
  `Api/Dtos.cs`), with one exception: `src/Astra.Server/Alerts/AlertEndpoints.cs` sits beside the
  alert service and its contracts. Register the group's `Map*Endpoints` in `AstraApp.Create`.
- The CLI (`cli/src/commands`) and the web admin UI (`web/src/api/`) are both clients of that
  public API, and `web/` is a private package with no library entry point: the desktop app consumes
  it as a build artifact (`web/dist`), never as a source dependency. Treat the HTTP surface — not a
  module boundary — as the contract between server, CLI and UI, and expect a server change that the
  UI needs to be mirrored in `web/src/api` rather than imported.

### Protocol conversion is hub-and-spoke, never pairwise

All four wire protocols (OpenAI Chat, OpenAI Responses, Anthropic Messages, Gemini) decode into
the IR in `src/Astra.Gateway/Protocol/Ir.cs` and re-encode from it — one `IProtocolCodec` per
protocol, registered in `GatewayCodecs.Register`. Adding a protocol or mapping means: extend the
IR if needed, implement/adjust the codec, and add golden tests with fixtures
(`tests/Astra.Gateway.Tests/Fixtures`, loaded from `AppContext.BaseDirectory`; the csprojs that
use fixtures copy them via a `Fixtures\**\*` entry, e.g. `Astra.Gateway.Tests.csproj`). Known lossy mappings are documented in
`Ir.cs` comments — update that documentation with the code.

### State ownership

- `~/.astra/` (override with `ASTRA_HOME`), layout in `Astra.Core/AstraPaths.cs`. `config.json`
  is read before startup (`ServerOptions`); `runtime.json` records the pid/port/apiVersion
  actually in use — the port can drift at startup (`PortPicker`), so never assume the configured
  port; read `runtime.json`. In containers the environment overrides config.json
  (`ServerOptions.ApplyEnvironment`: `ASTRA_HOST`, `ASTRA_PORT`, `ASTRA_PUBLIC_URL`,
  `ASTRA_STRICT_PORT`, `ASTRA_ADMIN_PASSWORD[_FILE]`; precedence flags > env > config.json) and
  `ASTRA_STRICT_PORT` disables the drift. `GatewayBaseUrl` is `ASTRA_PUBLIC_URL` when set, else
  `LocalUrl` (what `healthcheck` probes).
- Everything else lives in SQLite (`Astra.Data`, Dapper.AOT, WAL; see Native AOT).
  The one exception is `~/.astra/plugins/` (`AstraPaths.PluginsDir`, owner-only): magpie's plugin host
  and Bun need files — `plugin-auth.json` (plugin sign-ins in OpenCode's auth.json shape, plaintext by
  that format, never logged or returned by the API), `packages/` (`bun add` / npm tarball installs),
  `runtime/` (the downloaded Bun), `host/<hash>/` (the vendored host.js / pi.js). The installed-plugin
  list itself is SQLite (`plugins`, `plugin_provider_prefs`); the host gets it in its `init` call.
  `~/.astra/tools/` (`AstraPaths.ToolsDir`) likewise holds the downloaded `cloudflared-<version>/` and the
  running tunnel's `cloudflared.pid` (remote access; the tunnel settings themselves are `AppSettings.Tunnel`).
  Runtime-editable settings are one `AppSettings` JSON blob stored under key `"app"`
  (`SettingsService`); adding a setting is a property on `AppSettings` plus endpoint/UI wiring —
  no migration needed.
- Money is stored as INTEGER nanodollars (`Money` in `Astra.Core/Json.cs`, 1e9 per USD); unit
  prices are decimal strings inside pricing JSON. Never store money as a float.
- JSON options are deliberate (`Astra.Core/Json.cs`): `Json.Storage` (snake_case, omits nulls)
  for anything persisted; `Json.Api` (camelCase) for the admin HTTP surface, configured globally
  in `AstraApp`. Do not create ad-hoc `JsonSerializerOptions`. Every type crossing a JSON
  boundary must also be registered in the owning assembly's `JsonSerializerContext`
  (`JsonContexts` is the registry); under AOT an unregistered type throws (see Native AOT).

### Error and security boundaries

- Admin API: throw `AdminApiException(status, message)`; `AdminApiErrorFilter`
  (`Api/AdminApiErrorFilter.cs`) turns it (and IO/format errors) into JSON `{ error }`.
  Returning `Results.NotFound/BadRequest(new ErrorOnlyDto("…"))` (`Api/Dtos.cs`) is the older
  style and still normal where a handler returns its value; both are in use, so match the group
  you are editing instead of converting one to the other.
- `src/Astra.Server/Security/Security.cs` `SecurityMiddleware` enforces: loopback Host allowlist
  (DNS-rebinding defense), `X-Astra-Admin: 1` on every mutating `/api` call, and a session cookie
  for every request not from this machine except for an explicit "open" path list. Mutating endpoints get the
  header requirement automatically; adding a public read endpoint means deliberately adding it to
  that list. The web client already sends the header (`web/src/api/client.ts`, `api()`).
  Off-loopback (0.0.0.0 — the Settings page's LAN switch, `PUT /api/server/listen`, or a container), a request
  is from this machine only with a loopback peer, a loopback Host and no proxy header (incl. `X-Original-For`,
  which `ASPNETCORE_FORWARDEDHEADERS_ENABLED` leaves behind); everything else is marked network
  (`RequestOrigin.MarkNetwork`) and, like a remote request, needs a session for `/api` and a token for the
  gateway. Checks that mean "this machine only" use `RequestOrigin.IsLocal`, not `IsRemote`.
- Remote access (`src/Astra.Server/Tunnel/`): `TunnelService` runs cloudflared (a Cloudflare Tunnel)
  as a child process. Requests carrying `Cf-Ray` / `Cf-Connecting-IP`, or the tunnel's own hostname
  (the only non-loopback Host the allowlist admits), are marked remote (`Astra.Gateway/Http/RequestOrigin`):
  the gateway always wants a token from them (`RequireGatewayKey` only opens it to this machine), `/api`
  wants a session, and in loopback mode they get no `/api` beyond health/version/auth status unless
  `AppSettings.Tunnel.AllowAdmin`. In loopback mode the middleware also adopts Cloudflare's client
  IP and https scheme (per-IP rate limits, Secure cookie). `POST /api/auth/password`,
  `PUT /api/server/listen` and `PUT /api/tunnel`'s `cloudflaredPath` (a program for Astra to run)
  answer only callers from this machine.
  The request log records the origin (`requests.remote`, `requests.client_ip`, migration 0016;
  `?origin=remote|local` filter).
- Gateway: `GatewayException` is rendered in the inbound protocol's error shape. Client-facing
  gateway error messages are currently written in Chinese — match the surrounding file.
- Secrets (provider API keys, local client keys) go through `ISecretProtector` (DataProtection);
  DTOs expose only masks (`ProviderDto.HasApiKey` / `ApiKeyMasked`, `Api/ProviderEndpoints.cs`).
  Never log secrets or echo them back.
- Token-holder views (`src/Astra.Gateway/Http/KeyInfoEndpoints.cs`: `/v1/astra/key`, `/key/stats`, `/key/requests`,
  the legacy `/dashboard/billing/*`; the `/key` page) authenticate with the token itself and live under `/v1`, because
  `/api` needs an admin session off this machine. They show that token's own data only: request metadata and the model
  the client asked for (`requested_model`) — never a provider or account, the upstream or catalog model, `error_message`
  (map it through `RequestErrorCategories`), the client IP, the user agent, bodies or trace ids. So never hand them the
  admin request / stats DTOs or the `COALESCE(system_model_id, requested_model)` queries; `KeySelfServiceTests` and
  `TokenLimitTests` assert the raw JSON.

## Desktop app shell

`desktop/` is more than a window around `web/dist`; these pieces have their own rules.

- The renderer is served over the custom scheme `app://astra` (`desktop/src/appProtocol.ts`) with
  the CSP from `desktop/src/shared/csp.ts`. The CSP is a deliberate allowlist: a renderer feature
  that loads a new kind of resource (a remote image host, a frame, a websocket) needs a directive
  there. Add the narrowest one rather than widening an existing directive or dropping a
  restriction like `object-src 'none'`, and cover it in `desktop/tests/csp.test.ts`. Precedent:
  `img-src … https:` was added — and pinned by a test that plain `http:` stays out — for https
  plugin and market icons, the only scheme `PluginService.SafeIcon` admits.
- Main-process code can only be reached from the renderer through the preload bridge: a new IPC
  call is a linked update across `desktop/src/preload.ts` (injects `window.__ASTRA__`) and its
  `desktop/src/bridge.d.ts` declaration, the main-process handler (`main.ts` / `tray.ts` /
  `update.ts`), and the renderer's use of it. `window.__ASTRA__` is absent in a browser, so the
  web build must keep working without it (the shell falls back to the plain web app).

## Native AOT

`src/Astra.Server` publishes as a self-contained Native AOT binary (~40 MB executable, no .NET
runtime needed on the target).

- Publish: `dotnet publish src/Astra.Server -c Release -r <rid> --self-contained true
  -p:PublishAot=true -o out/publish` (what `release.yml` runs). Do **not** pass
  `-p:PublishSingleFile=true`: `src/Astra.Server/Astra.Server.csproj` keeps
  `PublishSingleFile`/`PublishReadyToRun`/`PublishTrimmed` behind a
  `Condition="'$(PublishAot)' != 'true'"` property group, and a command-line
  `-p:PublishSingleFile=true` overrides that group and breaks the AOT build. AOT output is
  multi-file by design — see the companion bullet below.
- Build gate: the seven shipped projects (`Astra.Core`, `Astra.Data`, `Astra.Clients`,
  `Astra.Providers`, `Astra.Plugins`, `Astra.Gateway`, `Astra.Server`) set
  `<IsAotCompatible>true</IsAotCompatible>`, so a plain `dotnet build -warnaserror` fails on
  AOT-hostile code (IL2026/IL3050 become errors via `TreatWarningsAsErrors`) — fix the code, do
  not weaken the gate. `Astra.Gateway` and `Astra.Server` also set
  `<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>`: the SDK only
  auto-enables the Request Delegate Generator when `PublishTrimmed`/`PublishAot` is set, so
  without it a plain build analyses differently from the publish.
- The one sanctioned exception is Jint's: its unused CLR-interop and System.Text.Json paths
  trip ILC's IL2026/IL2104/IL3053, which ILC cannot collapse for a package marked trimmable.
  `Astra.Server.csproj` passes `--nowarnaserr` for those three codes to the ILC step only (Roslyn's
  analyzer gate is untouched), `release.yml` runs `scripts/check-aot-warnings.mjs` over the
  publish log and fails on any IL warning not from Jint, then runs `astra-server plugin-selftest`
  on the binary (AOT can break at run time what the JIT never shows). Do not widen the exception
  to other codes or assemblies.
- JSON is source-generated: `JsonContexts` (`src/Astra.Core/JsonContexts.cs`) is the registry
  and every assembly contributes its own `JsonSerializerContext` via `[ModuleInitializer]`
  (`src/Astra.Core/JsonContext.cs`, `src/Astra.Providers/Templates/ProviderJsonContext.cs`,
  `src/Astra.Clients/Config/ClientJsonContext.cs`, `src/Astra.Server/Api/ServerJsonContext.cs`).
  The reflection fallback is pruned under AOT — an unregistered type throws there, while on the
  JIT it silently falls back. `JsonContexts.FallbackTypes` exposes what fell back, and
  `tests/Astra.Server.IntegrationTests/JsonRegistrationTests.cs` asserts the admin API resolves
  entirely from generated metadata. Trap: an interface-typed collection root
  (`IReadOnlyList<T>`, `IEnumerable<T>`) makes the serializer probe the whole interface graph
  and needs each interface registered — the established fix is to materialise a `List<T>` at
  the endpoint and register that.
- Dapper is source-generated (`src/Astra.Data`: Dapper.AOT with `[module: DapperAot]` plus a
  declarative `[module: TypeHandler(typeof(DateTimeOffset), typeof(DateTimeOffsetHandler))]`):
  - **Never call vanilla Dapper from a shipped assembly** — `SqlMapper.AsList` (and other
    `SqlMapper.*` / `DefaultTypeMap`) is not intercepted by Dapper.AOT, so it roots vanilla
    Dapper's reflection machinery into the AOT closure and ilc fails with `MSB3077`, an error
    whose message points at Dapper, not at your line. Use `.ToList()`.
  - **Private nested row types are silently not intercepted** (a no-warn "not supported" case),
    so row types are `internal` at namespace level; a `Query*` that silently stops being
    intercepted fails at runtime under AOT.
  - **Test assemblies opt out** with `[module: DapperAot(false)]` and then need the
    vanilla-Dapper globals in `tests/Astra.Data.Tests/VanillaDapper.cs` and
    `tests/Astra.Server.IntegrationTests/VanillaDapper.cs` — mirrored copies, keep them in sync.
- Native companion library: the publish output includes `libe_sqlite3.dylib` (macOS), `.so`
  (Linux), `e_sqlite3.dll` (Windows) beside the executable, loaded relative to the executable's
  directory — not embedded, unlike `PublishSingleFile`. Anything that copies the binary must
  copy the companions: `scripts/pack-platform.mjs` packs them into `npm/server-*/bin/` (the
  desktop installers pick them up via the existing `server-bundle` → `extraResources` chain),
  and `packages/update-core` (`staging.ts`/`swap.ts`/`apply.ts`) stages, commits and rolls them
  back with the same `.prev` pattern as the binary.
- Known gap: `scripts/gen-manifest.mjs` hashes only the executable, so the companion carries no
  integrity hash in the update feed.

## Linked updates — things that must change together

1. **Version bump**: one version everywhere — `cli/package.json` is the source of truth; every
   `npm/*/package.json`, `desktop/package.json` (electron-builder stamps it into the app — the
   installers' version and electron-updater's compare), the cli `optionalDependencies` pins, and
   `<Version>` in `Directory.Build.props` must match. Run `node scripts/sync-versions.mjs` (write mode) instead
   of editing them by hand, then `pnpm install` (the lockfile records the cli's platform-package
   specifiers); `release.yml` fails the publish if any drift or a tag/version mismatch remains.
   Add a `## [x.y.z] - date` section to `CHANGELOG.md` in the same change: pushing the bump to
   `main` triggers `release.yml`, which refuses to publish without it and uses it as the GitHub
   Release, download-page and update-manifest notes. The version picks the release channel
   (`x.y.z` stable, `x.y.z-preview.N` preview; `.agents/skills/release/references/channels.md`):
   it decides the npm dist-tag, the feed directory, the GitHub prerelease flag and the Docker tags,
   so there is no separate channel switch to set. `ServerOptions.ApiVersion` ("1.0") is
   deliberately **not** synced — it is a
   protocol version, and bumping its major breaks the desktop handshake
   (`desktop/src/shared/version.ts`, `EXPECTED_API_MAJOR`).
2. **New admin endpoint**: `Astra.Server/Api/*Endpoints.cs` (`MapXxxEndpoints` + register in
   `AstraApp.Create`) plus `web/src/api` hooks, `web/src/api/types.ts`, and i18n keys.
3. **Schema change**: add a new `src/Astra.Data/Migrations/NNNN_name.sql` (the numbered pattern
   is required by the embedded-resource glob in the csproj and `MigrationRunner`). Never edit an
   already-applied migration — `MigrationRunner` applies pending files in order and backs up the
   database first, but edits to old files are invisible to existing installs. Add repository
   methods and tests (`tests/Astra.Data.Tests/TestDb.cs`).
4. **New UI string**: add the key to **both** `zh` and `en` in `web/src/i18n/messages.ts`. `zh`
   defines the key set (`MessageKey = keyof typeof zh`) and `web/src/test/i18n.test.ts` enforces
   key and placeholder parity, so missing translations are caught by tests — still add both.
5. **Data-dir paths**: the `~/.astra` layout is defined in three places that must stay in sync:
   `src/Astra.Core/AstraPaths.cs`, `cli/src/lib/paths.ts`, `desktop/src/shared/paths.ts`.
6. **Client config take-over** (`Astra.Clients`): preserve the restore invariants — per-keyPath
   original values recorded in `client_config_state`, first-write backups kept permanently plus
   20 rolling backups (`ClientConfigApplier`), restore skips values the user changed (drift).
   `astra-server restore-all` is an offline subcommand that depends on this data.
   Files a client parses as strict JSON are an adapter's `StrictJsonFiles()`; `ClientConfigApplier.Repair` strips their
   comments/trailing commas (`StrictJson`, backed up like any write, no key state), and a later restore must not
   byte-restore the unparseable original (`ReconcileToOriginal` skips that case). Adapter tests
   must use a fake home (`ClientEnvironment`, `tests/Astra.Clients.Tests/TestHome.cs`) — never
   the developer's real client configs.
   Client-specific options (`ClientOptions` in `Astra.Core/Clients`, stored under `extras.options` in
   `clients.extra_json`, no migration): a new option is one `ClientOptionDescriptor` plus its consumer — the adapter
   (scope `config`, also add its key path to `ClientOptions.ConfigKeyPaths` so unsetting/disable restore it) or the
   gateway (scope `gateway`, read via `route.Client.ExtraJson`) — and zh/en `clients.option.<key>[.hint]` keys; the web
   panel renders from the descriptors the server reports on `ClientInfoDto.Options`.
   Per-model protocols: `ClientService.ContextAsync` adds each model's `protocols` hint (`GatewayRouter.PassthroughProtocols`,
   the protocols the gateway passes it through untranslated); an adapter picks with `ClientAdapterBase.ModelProtocols`
   (the user's `extras.protocolOverrides`, else the first native one it speaks, else its default). `ClientProtocols.For`
   (`Astra.Core/Clients`) lists what each adapter can write and drives the override validation and the web panel — teaching
   an adapter a protocol means updating it there too.
7. **A new JSON type crossing a boundary**: register it in the owning assembly's
   `JsonSerializerContext` (see Native AOT); the guard test
   (`tests/Astra.Server.IntegrationTests/JsonRegistrationTests.cs`) fails otherwise.
8. **Bun version** (`BunRuntime.Version` + its SHA-256 table, from the release's SHASUMS256.txt) and
   `ci.yml`'s `oven-sh/setup-bun` `bun-version` move together.
9. **Server startup configuration or release packaging**: the container surface is the env vars in
   `ServerOptions.ApplyEnvironment` (tests: `ServerOptionsEnvironmentTests`), the `healthcheck`
   subcommand, `Dockerfile` / `compose.yaml`, the `docker` job in `release.yml`, and
   `docs/content/docs/guide/docker.mdx`. Change them together; the job's smoke test expects
   `/api/health`, `/api/auth/status` (`required: true`) and the SPA at `/`.
10. **Plugin market contract**: the registry the docs site serves (`docs/lib/plugin-market/`, routes
    `docs/app/api/plugin-market/**`, document shape in `types.ts`) is parsed by `PluginMarket.Parse`
    (`src/Astra.Plugins/Market/PluginMarket.cs`) and shown by `web/src/api/types.ts` (`MarketListing`,
    `MarketSearchResult`) and `cli/src/commands/plugin.ts` — a field added on one side is added on all of them,
    and `docs/content/docs/advanced/plugin-market.mdx` lists the fields. The client reads only the docs registry
    (default `PluginMarket.DefaultUrl`; `ASTRA_PLUGIN_MARKET` > the `pluginMarketUrl` setting > default) and never
    asks npm or GitHub itself (search goes through the registry's `/search`). `docs/lib/plugin-market/upstream-fallback.json`
    is a copy of `Market/market.json` (the docs image build context has no `src/`): update both together. A hosted
    package's tarball address and hash come from the registry only (`PluginService.Add` never takes them from the
    caller; the download follows redirects by hand — https only, at most 5 hops — because a GitHub Release asset always
    redirects, and the sha256 from the registry decides whether what arrived is installed), and `PluginSources.MarketPackage` entries update through the market, not npm.
11. **Alert event** (`.plan/alerts-webhooks-token-limits.md`): a descriptor in `AlertEvents.All` and its evaluation in
    `AlertConditions` (`src/Astra.Core/Alerts/AlertContracts.cs`), the observation its subject type reports
    (`AlertEngine.SweepAsync`, `TokenLimiter.Observation`), its zh/en message in `src/Astra.Server/Alerts/AlertMessages.cs`,
    web i18n `alerts.event.<id>[.hint]` (plus `alerts.param.*` / `alerts.choice.*` for new condition fields), and the event
    table in `docs/content/docs/console/notifications.mdx`. The webhook payload shape is a public contract documented there.
12. **cloudflared version** (`CloudflaredRuntime.Version` + its SHA-256 table, from the release assets' digests —
    `gh api repos/cloudflare/cloudflared/releases/tags/<v>`) move together. The asset names `CloudflaredRuntime.Asset`
    returns are also the docs site mirror's allowlist (`CLOUDFLARED_ASSETS` in `docs/lib/tools-mirror.ts`, served at
    `/api/tools/cloudflared/<version>/<asset>`, cached under `${DATA_DIR}/tools/`): change them together. The remote access page and
    `docs/content/docs/guide/remote-access.mdx` describe what remote requests may reach — keep them in step with
    `SecurityMiddleware`.
13. **Release publishing** pairs a version bump with `release.yml`: the manifest the CLI and
    desktop read (`packages/update-core/src/check.ts`, `manifest.ts`, `DEFAULT_FEED_URL`) is served
    by the docs site from bytes `release.yml` uploads to `${DATA_DIR}/client-releases/<channel>/`
    through `docs/app/api/client-releases/` (storage and hashing: `docs/lib/releases.ts`, which
    owns `CLIENT_UPLOAD_TOKEN`). The upload step warns and exits 0 when `DOCS_RELEASE_URL` /
    `DOCS_RELEASE_TOKEN` are unset — a green run with a missing secret publishes nothing to the
    site, so check that step's log rather than assuming a release reached the feed. `release.yml`
    is also **two-phase**: a `main` push whose `cli/package.json` version is already on npm stops
    after the `version` job, so the `build` and `publish` jobs never run in that case.
    Channels: uploads carry `x-astra-channel` (`stable` | `preview`, refused when it disagrees with
    the version), and the `preview` feed offers preview releases plus every stable (`FEED_SOURCES`,
    `docs/lib/releases.ts`). The installers go up before npm, the server-update `latest.json` only
    after npm serves every package it names; the routes serve the newest release that has the file
    asked for. Clients follow the server's `updateChannel` setting (`UpdateChannels`, default
    stable): the server check, the desktop (manifest + DMG feed, `desktop/src/shared/updateChannel.ts`)
    and `astra update` (`--channel` overrides once; the self-update installs the manifest's exact
    version). The version ordering is semver precedence in four places that move together:
    `scripts/release-channel.mjs`, `UpdateCheckService.IsNewer`, `packages/update-core/src/version.ts`
    and `docs/lib/releases.ts` `compareVersions`.
14. **A `docs/` page or sidebar entry**: the folder's `meta.json` carries `title`/`description`/
    `icon`/`defaultOpen` and lists its `pages` (with `---[Icon]Name---` separators between
    sub-groups); a new page is added there and, when it belongs to a module, to the root
    `docs/content/docs/meta.json` order as well.
15. **Trace spans** (`.plan/observability.md`): span names are `TraceSpanNames` (`src/Astra.Core/Requests/TraceSpan.cs`),
    recorded by `GatewayPipeline` through `src/Astra.Gateway/Tracing` (`RequestTrace`; the agent links — session, turn,
    parent call — from `TraceKeys` + `TraceLinker`) and written to `trace_spans` with the request row
    (`RequestRepository.InsertBatchAsync`, migration 0017). The names and the attribute keys the read side depends on
    (`http.response.status_code`, `astra.account.id`, `astra.provider.id`, `astra.attempt.retry`, `astra.ratelimit.*`,
    `astra.response.*`, …) are also matched by SQL in `TraceRepository` and `RequestRepository.ProviderStatsAsync`, by
    `/api/traces` (`TraceEndpoints`) and by the web views (`web/src/components/traces/model.ts`
    `SPAN` and the inspectors): renaming one changes all of them, and `docs/content/docs/console/traces.mdx` lists what is
    recorded. Upstream requests never carry trace context: `GatewayHttpClients` sets a no-output
    `ActivityHeadersPropagator`, and `UpstreamTraceHeadersTests` checks it with the real handler (the integration
    fixture swaps the handler out, so it cannot).

16. **Install scripts** (`docs/public/install.sh`, `install.ps1`): they consume the release feed
    (`<channel>/latest.json`, `releases.json`, `/v/<version>/<file>` names from `canonicalFileName`;
    channel `stable` unless `--preview` / `ASTRA_CHANNEL=preview`), the npm server
    package layout (`bin/astra-server`, companions, `wwwroot/`) and the managed layout `update-core` owns
    (`~/.astra/server/astra-server-<version>`, `install.json` — they preserve its `desktop*` fields). Change
    them together with those, and with `docs/content/docs/guide/install.mdx`, both READMEs and the download page.
17. **Server autostart entries**: `astra autostart` (`cli/src/lib/autostart.ts`) and the settings page's switch
    (`src/Astra.Server/Autostart/AutostartService.cs`, `GET`/`PUT /api/server/autostart`) write the same entries —
    LaunchAgent `io.astra.server`, systemd user unit `astra.service`, Run key value `Astra Server` (the CLI's old
    `schtasks` task is only found and removed) — running `<server> serve --started-by autostart`. Names, paths,
    argv and file formats change in both (`AutostartTests.Entry_formats_match_the_cli` and `cli/test/autostart.test.ts`
    pin the text), plus `docs/content/docs/console/settings.mdx` and `cli/service.mdx`. The desktop app's own login
    item is separate (`desktop/src/shared/loginItem.ts`; Linux uses an XDG autostart entry).
18. **Token provider pools** (GitHub issue #2): `TokenPool` (`src/Astra.Core/Tokens/TokenPool.cs`, stored in
    `tokens.pool_json`, migration 0018) applies to bare-token direct calls only. `TokenService.ApplyPoolAsync` stores it
    canonically — one member is just `provider_id` / `account_id`, two or more also mirror the first member there so an
    older binary still routes; keep that. A new strategy is a `TokenPoolStrategies` constant, its branch in
    `ProviderPoolScheduler` (`Tiers` / `Pick` / `Rest`), the web editor's `POOL_STRATEGIES` and `expectedShares`
    (`web/src/components/TokenPoolEditor.tsx`, which mirrors the scheduler), zh/en `tokens.strategy.<id>[.hint]`, and the
    strategy table in `docs/content/docs/console/tokens.mdx`; what fails over (`ProviderFailover.Classify`) and how long a
    provider cools (`ProviderHealth`) are the failover table there — change them together. In `GatewayPipeline`, while
    another pool attempt may follow, never rewrite the client's guarded tree in place and never re-read the request
    bytes (they are the unredacted original once the privacy guard rewrote the tree): `PrepareAsync` clones instead.

## Conventions

- C#: file-scoped namespaces, nullable enabled, records and primary constructors, XML doc
  comments on public types.
- TypeScript is `strict` everywhere, but `noUncheckedIndexedAccess` is not: `tsconfig.base.json`
  sets it, `cli/tsconfig.json` and `packages/*` extend the base, `desktop/tsconfig.json` repeats
  the flag, and **`web/tsconfig.json` extends the base and turns it off** (`docs` uses Next's own
  config and is outside the base entirely). Index an array or object without a guard freely in the
  web UI; in the CLI, desktop and shared packages a non-null assertion like `arr[0]!` is the
  expected form.
- Formatting: root `.prettierrc` (100 cols, single quotes, semicolons, trailing commas). There
  is no repo-wide format command — match the surrounding code. ESLint: `web/` and `desktop/`
  have their own flat configs; `cli/` and `packages/*` inherit the root `eslint.config.js`.
- Tests: vitest with per-package configs — `cli/test`, `desktop/tests`, `packages/update-core/test`
  (node env, `include: ['test/**/*.test.ts']`), `web/src/test` (jsdom + testing-library, chosen in
  `web/vite.config.ts`). xunit on the .NET side, one test project
  per src project.
- The web UI is bilingual (zh/en); any user-visible string belongs in the i18n catalogs, not
  inline in components. The gateway is the exception: its client-facing error messages are Chinese
  (see Error and security boundaries).

## Generated files — do not edit by hand

- `web/dist`, `desktop/renderer`, `src/Astra.Server/wwwroot` — web build artifacts (all
  gitignored). `wwwroot` is not an SDK output: the server serves the UI from
  `<executable dir>/wwwroot` (`AstraApp.cs`), and `scripts/pack-platform.mjs --web-dist web/dist`
  is what puts it beside a published binary. A `pnpm --filter @aidotnet/web build` followed by a
  plain `dotnet run` therefore serves no UI.
- `desktop/server-bundle/` — server staged for the desktop installers by `release.yml`
  (gitignored).
- `npm/server-*/bin/`, `npm/desktop-*/app/` — outputs of `scripts/pack-platform.mjs` /
  `scripts/pack-desktop.mjs`.
- `docker-bin/<amd64|arm64>/` — the unpacked Linux server package staged by the `docker` job in
  `release.yml` for the root `Dockerfile` to `COPY` (gitignored). `docker/data/.gitkeep` is the
  only tracked file under `docker/`; it seeds the image's `/data` volume.
- `src/Astra.Core/Seed/models.json` — generated by `scripts/gen-seed.mjs` from models.dev.
  Regenerate rather than hand-edit; bump `SEED_VERSION` in the script when the seed changes.
  `SeedImporter` skips the import entirely when the stored seed version matches and never
  overwrites user-modified fields.
- `pnpm-lock.yaml` — change via `pnpm install`, never by hand.

## Known asymmetries and traps

- Server-binary resolution is close to aligned now, but not identical: the CLI resolves
  env > install.json > repo dev build > platform package (`cli/src/lib/binary.ts`, install path
  passed in from `server-lifecycle.ts`); desktop resolves install.json > `ASTRA_SERVER_BIN` >
  bundled server > dev build (`desktop/src/shared/resolveServerBinary.ts`, no platform-package
  fallback; install.json loses to the bundled server when its `serverVersion` is older). The
  desktop owns the service version at runtime: `ensureRunning` (`desktop/src/service.ts`) adopts a
  running server only when its version matches the resolved binary's recorded version
  (`shouldReplaceRunningServer`); a mismatching server — an older `astra serve`, a previous
  install — is stopped and replaced, so a packaged app always serves with its own bits (one it
  cannot stop is adopted, never doubled by a second server on a drifted port). Env/dev
  targets and unreadable versions adopt instead of killing. The
  standalone installers (dmg / NSIS / AppImage) ship the server under `resources/server`:
  `release.yml` stages `npm/server-*/bin` into `desktop/server-bundle`, which
  `electron-builder.yml` copies via `extraResources`. `scripts/pack-desktop.mjs` drops it from
  the Windows/Linux npm desktop packages (npm rejects tarballs over ~200 MB with E413; the npm
  track uses the CLI-managed server); the signed macOS `.app` keeps it. After an
  update, the executor repoints `install.json` at the managed binary
  (`~/.astra/server/astra-server-<version>`), which is what makes both sides pick it up — keep
  that invariant when touching either resolver.
- `@aidotnet/update-core` is a source-only workspace package (`main`/`exports` point at
  `src/index.ts`). Consumers must bundle it (tsup/esbuild), not import it as compiled JS — both
  cli and desktop do this today via their devDependency.
- Release publish order matters: `@aidotnet/server-*` and `@aidotnet/desktop-*` packages must
  be on npm before the `astragate` / `@aidotnet/astra-gate` CLI tarballs, because the CLI's `optionalDependencies` resolve
  at install time (`.github/workflows/release.yml`), and the server-update manifest goes to the feed after all of
  them. Every package is published with `--tag <latest|preview>`: a prerelease published without a tag becomes
  `latest`. Scoped tarballs are named without `@`/`+`
  (`aidotnet-server-*.tgz`) and the workflow globs rely on that. The `docker` job runs after
  `publish` (it unpacks the `pkg-linux-*` artifacts), so an image never exists for an unreleased version.
- Update flow ownership: the server only checks the feed (`UpdateCheckWorker`,
  `GET /api/update/status`, `POST /api/update/check`); applies happen out of process in the CLI
  or desktop via `packages/update-core` (staged download → sha256 → swap → restart → rollback,
  state in `~/.astra/update-state.json`, executor never replaces a running binary in place).
  The desktop surfaces this to its web UI through the `updates` bridge (`astra:update-check` /
  `astra:update-apply` → `UpdateController.check` / `applyFromUi` in `desktop/src/update.ts`):
  the npm track's server-update check is gated on the running SERVER's version, not the app's (the
  apply gate always was), so a current app with an old server still offers the update. The dmg and
  installer tracks ship the server inside the app, so they never update a server on its own: they check
  the app (electron-updater / the download page) and the new app's bundled server replaces the old one
  at launch (`resolveServerBinary` drops a stale managed `install.json`).

## Ground rules for edits

- Keep diffs minimal and scoped to the task. Do not refactor, reformat, or "clean up" code your
  task did not touch; existing oddities are not license for unrelated changes. Higher-priority
  instructions and the task's own scope win over this file; inside that scope, keep the invariants
  above and prefer the smallest change that satisfies both. If the two cannot both hold, say so
  before changing code.
- Read current file contents before editing; this repo has scripts, CI, and packages that
  reference files across boundaries, so stale copies cause silent drift. That matters most in
  files a second consumer reads or a script writes (`cli/package.json`, the `npm/*` manifests,
  `packages/update-core`'s source-only entry points, the two market registry copies,
  `pnpm-lock.yaml`): a small edit there is rarely small, and the copy you did not touch is the
  one that ships. Reading the current contents and preserving edits you did not make is the only
  thing that makes concurrent work safe — a small diff does not.
- If a task cannot be done without violating an invariant above (for example, editing an applied
  migration), stop and explain the conflict before changing code.
- If you change a documented command, boundary, or linked-update step in this file's scope,
  update the corresponding guidance here in the same change. Do not commit, push or publish
  without being asked.

## Repository map

The detailed map is in `.agents/codemap/` (generated at 6324e30; it can lag behind the code — when
they disagree, trust the code and fix the page). Read the matching page before you change:
- a gateway request, its protocol conversion or billing → `.agents/codemap/flows/gateway-request.md`
- the trace spans, agent-turn linking or the 链路追踪 views → `.agents/codemap/flows/request-tracing.md`
- an admin `/api` call, its auth or the console client → `.agents/codemap/flows/admin-api-request.md`
- server startup, the port or `runtime.json` → `.agents/codemap/flows/server-startup.md`
- taking over or restoring a local AI client's config → `.agents/codemap/flows/client-config-takeover.md`
- installing or running a plugin → `.agents/codemap/flows/plugin-install-and-run.md`
- a release, the update feed or the desktop/CLI updater → `.agents/codemap/flows/release-and-update.md`
- unsure where new code belongs → `.agents/codemap/architecture.md` (ownership table)
- a business term you do not recognise → `.agents/codemap/domain.md`

When you change a module's entry points, contracts or extension steps, update its page in the same change.

## Repository memory

`.agents/memory/` records context the code does not show: corrected approaches, decisions and their
reasons, traps, external resources. It is background; the rules are in this file.

**Before starting a task:** read `.agents/memory/MEMORY.md` and open the entries relevant to the task.
An entry describes the time it was written: verify any path, symbol or command it cites before relying
on it. If it conflicts with the code or this file, follow the code and this file, and fix or delete the
entry.

**Write an entry when it happens, not at the end of the task:**
- The user corrects your approach, or confirms a non-obvious one → `feedback`
- A decision is made or explained that affects later changes, or an approach is rejected → `project`
- You find a trap that is hard to see in the code and is not covered here → `project`
- The user mentions an external resource (dashboard, ticket, doc, monitoring) → `reference`

**Do not record:** what the code, git history, this file or `.agents/codemap/` already say; one-off task
state; secrets or personal data; unverified guesses.

**How:** update an existing entry before creating a new one; one fact per file, format in
`.agents/memory/README.md`; absolute dates; cite evidence (`path:Symbol`, commit or link); keep the
`MEMORY.md` line in sync; delete entries that turn out to be wrong. Anything about your own habits as an
individual goes in `.next-cowork/memory/` (not committed).

**Promotion:** when a `feedback` entry is confirmed again and holds for every contributor, suggest adding
it to this file — do not rewrite the rules here yourself. Memory changes appear in the diff; do not commit
them separately unless asked.
