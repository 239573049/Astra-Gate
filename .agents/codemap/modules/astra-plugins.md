---
module: astra-plugins
sources: ["src/Astra.Plugins/**"]
commit: 6324e30
updated: 2026-10-14
---
# astra-plugins

Runs magpie-compatible plugins in three shapes: gateway middleware and agent plugins compiled on Jint inside
Astra's own process (`Middleware/`, `Agents/`, `Js/`), OpenCode/pi provider plugins driven as a Bun sidecar
through magpie's vendored `host.js` (`Host/`, `Runtime/`, `Packages/`), and the plugin catalog, market and
installer that feed both (`PluginCatalog.cs`, `Market/`, `Packages/`, `Diagnostics/`).
**Not responsible for:** the plugin list rows and provider `plugin-<id>` mirroring (SQLite +
`src/Astra.Server/Api/PluginProviderSync.cs`), the `/api/plugins*` surface (`src/Astra.Server/Api/PluginEndpoints.cs`),
client config writes for agent-plugin clients (`astra-clients`), plugin contracts and `~/.astra` paths
(`astra-core`), and the market registry the docs site serves (`docs-site`).

## Entry points
| Symbol | File | Role |
|---|---|---|
| `PluginCatalog.SnapshotAsync` | `src/Astra.Plugins/PluginCatalog.cs:37` | The installed list at one generation: entry + target + `PackageManifest`, read from the store once per generation; `Off` (`ASTRA_PLUGINS=off`) forces every entry disabled. |
| `PluginCatalog.Invalidate` / `Changed` | `src/Astra.Plugins/PluginCatalog.cs:63`, `:24` | Bumps the generation; the middleware set, agent catalog, plugin host and provider sync reload on it. |
| `MiddlewareEngine.BeginAsync` | `src/Astra.Plugins/Middleware/MiddlewareEngine.cs:32` | Starts one request's middleware run — `null` when no plugin has middleware, so a plugin-free gateway pays nothing. |
| `AgentPluginCatalog.RefreshAsync` / `Connect` | `src/Astra.Plugins/Agents/AgentPluginCatalog.cs:43`, `:66` | Loads `magpie.agent` modules once a second at most; runs an agent's `connect` with the gateway/model input. |
| `PluginHost.CallAsync` / `FetchAsync` / `RestartAsync` | `src/Astra.Plugins/Host/PluginHost.cs:54`, `:60`, `:67` | The host as the rest of Astra uses it: lazily started, restarted when the list or the proxy moved, old host retired. |
| `PluginInstaller.AddAsync` / `AddHostedAsync` / `UpdateAsync` / `RemoveAsync` | `src/Astra.Plugins/Packages/PluginInstaller.cs:37`, `:46`, `:136`, `:191` | npm/git/path/market installs, updates, removals — the store entry is what they write. |
| `PluginMarket.ListAsync` / `SearchAsync` / `FindHostedAsync` | `src/Astra.Plugins/Market/PluginMarket.cs:77`, `:139`, `:98` | The registry (docs site by default), its search endpoint, and the tarball a hosted package must be fetched from. |
| `BunRuntime.EnsureAsync` | `src/Astra.Plugins/Runtime/BunRuntime.cs:65` | The Bun the host and `bun add` run on: `ASTRA_BUN` if set, else the pinned download. |
| `PluginFetchHandler.SendAsync` | `src/Astra.Plugins/Host/PluginFetchHandler.cs:46` | `HttpMessageHandler` for `plugin://<provider>/…`: resolves the URL/model/session, sends through the host, strips `X-Magpie-Sign-In`. |
| `PluginSelfTest.Run` | `src/Astra.Plugins/Diagnostics/PluginSelfTest.cs:23` | What `astra-server plugin-selftest` runs on each AOT binary. |

## Key types and functions
| Symbol | File | Role |
|---|---|---|
| `PackageManifest.Read` | `src/Astra.Plugins/Packages/PackageManifest.cs:68` | Reads the plugin's `package.json` (or a `*.middleware.js` / `*.agent.js` file): `magpie.middleware`, `magpie.agent`, `magpie.options`, `magpie.icon`, `main`/`exports`/index files, pi detection, `InProcessOnly`, `NotPluginReason`. |
| `PluginSpec` (`IsPath`/`IsGit`/`IsPackageName`/`Name`/`Target`/`ShortName`) | `src/Astra.Plugins/Store/PluginSpec.cs:10` | magpie's spec rules: what a spec names, where a plugin loads from, how a short name is derived. |
| `MiddlewareModule.Load` | `src/Astra.Plugins/Middleware/MiddlewareModule.cs:47` | Evaluates the module once, requires at least one of `onRequest`/`onEvent`/`onResponse`, reads the `events` export, keeps the engine pool. |
| `MiddlewareRun.Request` / `Event` / `Response` | `src/Astra.Plugins/Middleware/MiddlewareEngine.cs:165`, `:187`, `:209` | The three hooks over JSON in/out, in plugin order; `ctx.reject(status, message)` turns a request away. |
| `JsRuntime` / `JsModuleLoader` | `src/Astra.Plugins/Js/JsRuntime.cs:19`, `src/Astra.Plugins/Js/JsModuleLoader.cs:16` | The Jint engine (deadline, recursion/memory/regex limits, `console` as the only global) and the ESM loader that keeps imports inside the plugin root. |
| `PluginHostClient.Start` / `CallAsync` / `FetchAsync` | `src/Astra.Plugins/Host/PluginHostClient.cs:72`, `:113`, `:136` | Newline-delimited JSON over the host's stdin/stdout, with the credit-windowed fetch streaming. |
| `PluginProviders.ListAsync` / `LoaderBaseAsync` / `ProtocolOf` | `src/Astra.Plugins/Host/PluginProviders.cs:79`, `:101`, `:129` | The host's provider listing (cached per host), each account's loader base URL, and the wire API a model's AI SDK package speaks. |
| `HostAssets.Ensure` | `src/Astra.Plugins/Runtime/HostAssets.cs:20` | Writes the embedded `host.js`/`pi.js` into `~/.astra/plugins/host/<sha256[..12]>/`. |
| `ChildEnvironment.Build` / `Proxy` | `src/Astra.Plugins/Runtime/ChildEnvironment.cs:22`, `:58` | The Bun child's environment: Astra's minus every `ASTRA_*`, with the proxy as `MAGPIE_*_PROXY` for the host. |
| `NpmTarballFetcher.ResolveAsync` / `InstallAsync` / `DownloadVerifiedAsync` | `src/Astra.Plugins/Packages/NpmTarballFetcher.cs:27`, `:45`, `:71` | npm-registry tarball install (no Bun, SRI-checked, regular files only, paths kept inside the package) and the market's sha256/sha512-verified download. |
| `PluginJobs.Start` | `src/Astra.Plugins/Packages/PluginJobs.cs:17` | Install/update/remove/runtime-download as jobs with a 400-line log the plugins page follows. |
| `PluginsJsonContext` | `src/Astra.Plugins/PluginsJsonContext.cs:25` | Source-generated metadata for every type this assembly hands to the admin API, self-registered into `JsonContexts`. |

## Dependencies
- Uses: `astra-core` via import — `PluginEntry`/`IPluginListStore`/`PluginKinds` and `AgentPluginDescriptor` (`src/Astra.Plugins/PluginCatalog.cs:14`, `src/Astra.Plugins/Agents/AgentPluginCatalog.cs:20`), `AstraPaths` (`src/Astra.Plugins/Runtime/HostAssets.cs:12`), `SystemProxy` (`src/Astra.Plugins/Runtime/ChildEnvironment.cs:62`), `JsonContexts` (`src/Astra.Plugins/PluginsJsonContext.cs:33`).
- Uses: `astra-data` via DB — the plugin list comes only from `IPluginListStore` (`src/Astra.Plugins/PluginCatalog.cs:46`), written through `AddAsync`/`ReplaceAsync` (`src/Astra.Plugins/Packages/PluginInstaller.cs:115`).
- Used by: `astra-gateway` via import — `MiddlewareEngine`/`MiddlewareRun` in the pipeline before the privacy guard (`src/Astra.Gateway/Pipeline/GatewayPipeline.cs:119`).
- Used by: `astra-providers` via HTTP — a plugin provider's `plugin://` endpoints are sent through `PluginFetchHandler`, which is registered as that route's `HttpMessageHandler` (`src/Astra.Plugins/Host/PluginFetchHandler.cs:12`).
- Used by: `astra-clients` via import — the agent descriptors become `plugin-<id>` adapters (`src/Astra.Clients/Adapters/ClientAdapterRegistry.cs:18`, `src/Astra.Clients/Adapters/PluginClientAdapter.cs:18`).
- Used by: `astra-server` via import — composition (`src/Astra.Server/Hosting/AstraApp.cs:140`–`:185`), `/api/plugins*` (`src/Astra.Server/Api/PluginEndpoints.cs:14`), `plugin-<id>` provider mirroring (`src/Astra.Server/Api/PluginProviderSync.cs:19`), `plugin-selftest` (`src/Astra.Server/Program.cs:61`).
- Uses/Used by: `docs-site` via HTTP — the registry address `PluginMarket.DefaultUrl` is the docs site's `/api/plugin-market/registry.json`, whose search endpoint is derived from it (`src/Astra.Plugins/Market/PluginMarket.cs:47`, `:160`).
- Used by: `cli`, `web-ui`, `desktop-app` via HTTP — the plugins page and the CLI talk to `astra-server`'s `/api/plugins*` and `/api/plugin-providers/{id}/*`, never to this assembly directly.

## State it owns
- SQLite: the installed list (`plugins`) and the per-provider picks (`plugin_provider_prefs`), read/written only
  through `IPluginListStore` (`src/Astra.Plugins/PluginCatalog.cs:14`); re-adding a plugin under a new spec
  keeps its options and enabled flag (`src/Astra.Plugins/Packages/PluginInstaller.cs:110`) and `ReplaceAsync`
  moves the provider picks to the new spec (`src/Astra.Core/Plugins/PluginContracts.cs:80`).
- Files under `~/.astra/plugins/` (`src/Astra.Core/AstraPaths.cs:33`), owner-only: `plugin-auth.json` — the
  sign-ins, OpenCode's `auth.json` shape, read and written **by the host** (`src/Astra.Plugins/Host/PluginHost.cs:146`,
  `src/Astra.Plugins/Host/host.js:654`); `packages/` — what `bun add` created, including `package.json`,
  `node_modules` and `bun.lock`; `runtime/bun-<version>/bun` (`src/Astra.Plugins/Runtime/BunRuntime.cs:55`);
  `host/<hash>/host.js|pi.js` (`src/Astra.Plugins/Runtime/HostAssets.cs:22`); `models-dev.json`
  (`src/Astra.Plugins/Runtime/ModelsDevCatalog.cs:18`); `market/registry.json`
  (`src/Astra.Plugins/Market/PluginMarket.cs:56`); `market-packages/<name>/<version>.tgz`
  (`src/Astra.Plugins/Packages/PluginInstaller.cs:30`); `.staging/<guid>/` during installs.
- Derived in-memory caches, never sources of truth: `PluginCatalog` snapshot/generation
  (`src/Astra.Plugins/PluginCatalog.cs:18`), the middleware set keyed by a disk stamp looked at once a second
  (`src/Astra.Plugins/Middleware/MiddlewareEngine.cs:51`), the agent set (`src/Astra.Plugins/Agents/AgentPluginCatalog.cs:33`),
  the host's provider listing and loader base URLs (`src/Astra.Plugins/Host/PluginProviders.cs:65`), the market
  registry + search results (`src/Astra.Plugins/Market/PluginMarket.cs:53`), job records (`src/Astra.Plugins/Packages/PluginJobs.cs:14`).

## Contracts it exposes
- **Plugin manifest** (`PackageManifest.Read`): `name`, `version`, `main`/`exports` or an index file
  (`index.ts|tsx|js|mjs|cjs`), `magpie.middleware`, `magpie.agent`, `magpie.options` (the options editor's
  starting point), `magpie.icon` (`https:`/`data:` only is what the server lets through), pi via a `pi` object,
  the `pi-package` keyword or a pi dependency (`src/Astra.Plugins/Packages/PackageManifest.cs:114`).
- **Names plugins see and may not lose**: `magpie.*` in `package.json`, `x-magpie-*` headers
  (`X-Magpie-Conversation` / `X-Magpie-Sign-In`, read and stripped in
  `src/Astra.Plugins/Host/PluginFetchHandler.cs:21`, `:24`; `x-magpie-work-dir` / `x-magpie-work-git` in
  `src/Astra.Plugins/Host/host.js:508`), and `MAGPIE_HTTPS_PROXY`/`MAGPIE_HTTP_PROXY`/`MAGPIE_NO_PROXY`
  (`src/Astra.Plugins/Runtime/ChildEnvironment.cs:39`, applied per fetch in `src/Astra.Plugins/Host/host.js:302`).
- **In-process middleware module**: named exports (or members of `export default`) `onRequest`, `onEvent`,
  `onResponse`, optional `events` array; `ctx` = `protocol`, `model`, `stream`, `path`, `agent`, `options`,
  `state` (per request), `reject(status, message)`, and `status` on `onResponse`
  (`src/Astra.Plugins/Middleware/MiddlewareEngine.cs:283`, `:144`).
- **Agent plugin module**: `export const agent` = `{id, name, config, format, model, prefix, bin, dir, ua, icon,
  notice, library{mcp, mcpFormat}}` plus optional `connect(input)`
  (`src/Astra.Plugins/Agents/AgentPluginCatalog.cs:148`, descriptor in `src/Astra.Core/Plugins/AgentPluginContracts.cs:11`).
- **Host RPC** (newline-delimited JSON): `{id, method, params}` → `{id, result}` / `{id, error}`; the host's
  methods are `init`, `providers`, `prompt`, `validate`, `authorize`, `callback`, `apiKey`, `load`, `usage`,
  `checkin`, `check`, `import`, `take`, `signOut`, `reload`, plus the `fetch` stream and the `credit`/`abort`
  control messages (`src/Astra.Plugins/Host/host.js:1969`–`:2047`, dispatch at `:2094`; client side
  `src/Astra.Plugins/Host/PluginHostClient.cs:113`). `init` carries `authPath`, `modelsDevPath`, `piPath`,
  `directory`, `config`, `prefer`, `plugins[{spec,target,options}]` and answers `{plugins:[…]}`
  (`src/Astra.Plugins/Host/PluginHost.cs:144`). Unsolicited events: `auth`, `signIn`, `log`, `toast`,
  `recovered` (`src/Astra.Plugins/Host/PluginHost.cs:209`).
- **Market document**: `{version, plugins:[{package, name, icon, kind, providers, replaces, summary{lang},
  latest, description, license, spec, origin, author, homepage, repository, tags, minAstraVersion,
  tarballs[{version, url, sha256, sha512, size}]}]}` — parsed in `src/Astra.Plugins/Market/PluginMarket.cs:219`
  / `:250`, typed by `MarketListing` (`:20`); `/search` answers
  `[{spec, name, description, version, source, kind, stars}]` (`:165`).
- **Install layout**: npm/git/path packages resolve to `PluginSpec.Target` — the path itself, or
  `<plugins>/packages/node_modules/<name>` (`src/Astra.Plugins/Store/PluginSpec.cs:45`); a market-hosted
  tarball is kept at `market-packages/<name with / → _>/<version>.tgz` and recorded in the packages folder's
  `package.json` (`src/Astra.Plugins/Packages/PluginInstaller.cs:407`).
- **Admin JSON**: every type this assembly returns is registered in `PluginsJsonContext`
  (`src/Astra.Plugins/PluginsJsonContext.cs:13`) — under Native AOT an unregistered type throws.

## How to extend
- To add a plugin kind: extend `PackageManifest.ReadFolder`/`ReadFile` (a new field, e.g. another `magpie.*`
  key) (`src/Astra.Plugins/Packages/PackageManifest.cs:114`), its chip in `InstalledPlugin.Kinds`
  (`src/Astra.Plugins/PluginCatalog.cs:94`), the constant in `astra-core`'s `PluginKinds`, and its loader
  (a new `MiddlewareEngine`-like engine or a host-side path). Missing the `Kinds` entry means the plugin
  shows no chip; missing the manifest read means it reads as `NotPluginReason` and an install is rolled back
  (`src/Astra.Plugins/Packages/PluginInstaller.cs:94`).
- To add a host RPC method: call it via `PluginHost.CallAsync` and add the handler to `host.js`'s `handlers`
  table by **re-vendoring** (`node scripts/vendor-magpie-host.mjs <commit>`) — a method the vendored host does
  not know answers `no method <x>` and the call throws `PluginHostException`
  (`src/Astra.Plugins/Host/host.js:2155`).
- To add a market field: `PluginMarket.ToListing`/`ToTarball` (`src/Astra.Plugins/Market/PluginMarket.cs:234`),
  the record `MarketListing` (`:20`), `web/src/api/types.ts`, `cli/src/commands/plugin.ts` and
  `docs/content/docs/advanced/plugin-market.mdx` — the registry, client and docs are one contract (AGENTS.md
  linked update 10); missing the client side silently drops the field.
- To add an agent descriptor field: `AgentPluginDescriptor` (astra-core), `AgentPluginCatalog.Check`'s
  validation, `PluginClientAdapter`'s plan, and the snapshot the client row keeps — `restore-all` reads only
  that snapshot (`src/Astra.Server/Commands.cs:28`), so a field not in it is missing offline.
- To add a `PluginCatalog`-visible JSON type: register it in `PluginsJsonContext`
  (`src/Astra.Plugins/PluginsJsonContext.cs:13`); missing it only fails in
  `tests/Astra.Server.IntegrationTests/JsonRegistrationTests.cs` and on AOT.

## Invariants and traps
- In-process JS never gets CLR objects: host interop is JSON text and `JsValue` only, no `AllowClr`, no
  interop wrappers, `console` the only global — that is what keeps the assembly `IsAotCompatible`
  (`src/Astra.Plugins/Js/JsRuntime.cs:12`, `src/Astra.Plugins/Astra.Plugins.csproj:4`). A plugin that wants a
  package must bundle it: imports must be `./`/`../` inside the plugin root, symlinks resolve out of it too
  (`src/Astra.Plugins/Js/JsModuleLoader.cs:48`, `:53`).
- A hook that throws or runs past its limit leaves its input exactly as it was (`onRequest` 250 ms, `onEvent`
  50 ms, load 1 s — `src/Astra.Plugins/Middleware/MiddlewareEngine.cs:19`); one engine is taken per request
  for all its hooks, so `ctx.state` is shared and module globals persist across requests
  (`src/Astra.Plugins/Middleware/MiddlewareModule.cs:10`).
- Middleware sees the agent's body in the client's own API and runs **before** the privacy guard, on agent
  requests only (`src/Astra.Gateway/Pipeline/GatewayPipeline.cs:116`). `onEvent` is offered only a
  single-`data:`-line JSON-object event; anything else passes through untouched
  (`src/Astra.Plugins/Middleware/SseEventFramer.cs:26`).
- The vendored host files are byte-for-byte magpie's; `HostAssetsTests` fails if they drift from `VENDORED.md`
  (`tests/Astra.Plugins.Tests/HostAssetsTests.cs:13`). Never hand-edit `Host/host.js` or `Host/pi.js` —
  re-vendor, then read the diff, because that file runs with the user's privileges
  (`src/Astra.Plugins/Host/VENDORED.md:9`).
- The host protocol is bounded: 64 KiB frames, a 512 KiB credit window per fetch, 64 MiB of streams and 64
  RPC slots, 32 MiB lines; breaking it (a second head, an oversized or over-credit chunk) gives that call up
  rather than buffering (`src/Astra.Plugins/Host/PluginHostClient.cs:33`, `:375`). A retired host finishes what
  is in flight for up to 10 minutes before it is disposed (`src/Astra.Plugins/Host/PluginHost.cs:202`).
- The host reads the proxy once at start, so a proxy change (or one appearing after login) restarts it —
  checked every 15 s, every 2 s while it has none (`src/Astra.Plugins/Host/PluginHost.cs:192`).
- No `ASTRA_*` variable ever reaches a Bun child (a container's `ASTRA_ADMIN_PASSWORD` must not), and for the
  host the proxy goes in as `MAGPIE_*_PROXY` so a provider set to "direct" can still go around it
  (`src/Astra.Plugins/Runtime/ChildEnvironment.cs:22`, `:39`).
- Downloads follow redirects by hand because the shared clients refuse to: Bun (https only, ≤ 5 hops,
  sha256 from `Sums` decides, `.part` + move) (`src/Astra.Plugins/Runtime/BunRuntime.cs:92`) and market
  tarballs (https, or http on loopback; ≤ 5 hops; sha256 always, sha512 when given)
  (`src/Astra.Plugins/Packages/NpmTarballFetcher.cs:116`). The tarball URL and hash come **only** from the
  registry (`src/Astra.Plugins/Packages/PluginInstaller.cs:46`, `src/Astra.Server/Api/PluginService.cs:94`).
- Bun version + its SHA-256 table + `ci.yml`'s `setup-bun` move together (`src/Astra.Plugins/Runtime/BunRuntime.cs:17`);
  a plugin pinned to a version is installed as `name@<resolved version>`, never as a tag, because Bun resolves
  tags by its own config (`src/Astra.Plugins/Packages/PluginInstaller.cs:245`).
- A plugin a Bun install never listed (bun installed it but the packages `package.json` does not name it) is
  an error, not a silent success (`src/Astra.Plugins/Packages/PluginInstaller.cs:77`); a package that names no
  file to load is removed again right after being installed (`src/Astra.Plugins/Packages/PluginInstaller.cs:94`).
- An agent whose id is a built-in client kind's, or `astra` itself, is refused
  (`src/Astra.Plugins/Agents/AgentPluginCatalog.cs:117`); duplicate agent ids are refused too (`:115`).
- `plugin-auth.json` is plaintext by OpenCode's format — it is never returned by the API and never logged; the
  whole `~/.astra/plugins/` folder is owner-only (`src/Astra.Core/AstraPaths.cs:35`, `:60`).
- Agent plugins write every key through `ClientConfigApplier`; disabling is planned from recorded state alone,
  so `restore-all` works with no plugin code at all (`src/Astra.Clients/Adapters/PluginClientAdapter.cs:11`).

## Tests
`tests/Astra.Plugins.Tests/**` — xunit, one class per file named `<Thing>Tests.cs`, `[Fact]`/`[Theory]`;
`TestPlugins` is the shared fixture (temp home + `MemoryPluginStore` + `PluginCatalog`, plus `Fixture`/`Folder`
helpers for plugin sources), and `MarketTestSupport.StubHttp` is the scripted HttpClient for npm/the market.
Middlewares and agents are tested against the magpie fixtures in
`tests/Astra.Plugins.Tests/Fixtures/magpie/` (`word-guard`, `agent-aider`, `fake`), copied to the output by
`tests/Astra.Plugins.Tests/Astra.Plugins.Tests.csproj:12`.
Tests that need a real Bun use `[BunFact]` (`tests/Astra.Plugins.Tests/PluginHostBunTests.cs:11`), which skips
unless `ASTRA_TEST_BUN` or a `bun` on `PATH` exists — the integration suite has its own copy of that attribute
(`tests/Astra.Server.IntegrationTests/ProviderPluginGatewayTests.cs:17`).
Run one file: `dotnet test tests/Astra.Plugins.Tests --filter FullyQualifiedName~MiddlewareEngineTests`
(all: `dotnet test --no-build`, after `dotnet build -warnaserror`).

## Related flows
- [plugin-install-and-run](../flows/plugin-install-and-run.md)
- [gateway-request](../flows/gateway-request.md)

