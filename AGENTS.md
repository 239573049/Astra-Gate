# AGENTS.md

Guidance for coding agents working in this repository. It covers the whole repo; no scoped
AGENTS.md files exist yet. Design rationale lives in `.plan/*.md` (local planning docs,
gitignored) — code comments cite them as "plan §N"; keep such comments true when you change
the behavior they describe.

## What this is

Astra (`astragate`) — a local AI gateway. One product, two toolchains:

- **.NET 10** (`Astra.sln`; `global.json` pins SDK 10.0.x): `src/Astra.Server` builds
  `astra-server`, an ASP.NET Core host published as a self-contained single file. The other
  `src/Astra.*` projects are its libraries. Tests live in `tests/Astra.*.Tests` (xunit).
- **pnpm workspace** (`pnpm@9.15.0`, `pnpm-workspace.yaml`; Node >= 18 per `cli`, CI runs Node 24):
  `cli/` (npm package `astragate`, bin `astra`; `release.yml` also rebuilds and publishes it as
  `@aidotnet/astra-gate`, whose self-update targets its own baked-in name), `web/` (`@aidotnet/web`, Vite + React admin UI),
  `desktop/` (`@aidotnet/desktop`, Electron shell), `docs/` (`@aidotnet/docs`, Next.js/Fumadocs
  documentation site), `npm/*` (platform packages `@aidotnet/server-*` / `@aidotnet/desktop-*`),
  `packages/update-core` (shared update engine).

`docs/` is the fumadocs documentation site (`pnpm --filter @aidotnet/docs dev` / `build`; content in
`docs/content/docs`, split into modules by root folders, each with a `meta.json` with `"root": true`).
It doubles as the official website and ships as its own Docker image: `.github/workflows/docs.yml`
builds `docs/Dockerfile` (Next standalone output; the build context is the repo root) and pushes to
`ghcr.io/<owner>/<repo>/docs` on every `main` push that touches `docs/**` (or the lockfile / workflow).
Tutorial screenshots in `docs/public/screenshots/` are captured from the live UI (`web` dev server on
:5173 + a running astra-server) via `node docs/scripts/snap.mjs`; re-run it after UI changes instead of
editing PNGs. `login.png` is the only mockup-rendered shot (`shots/login.html`, loopback never shows a
login page). `docs` has no `lint`/`test` script, so `pnpm -r --if-present …` skips it silently; its
`build` is a full Next.js build and fails if the shell exports a non-standard `NODE_ENV` (unset it first).

Distribution couples the sides: the server binary and `web/dist` are packed into `npm/server-*`
(`scripts/pack-platform.mjs`), and `web/dist` is copied into `desktop/renderer`
(`desktop/scripts/copy-web.mjs`). Packaging changes can therefore span toolchains.

## Commands

.NET, from the repo root (the single `Astra.sln` is picked up automatically):

- Build: `dotnet build -warnaserror` — warnings are errors (`Directory.Build.props` sets
  `TreatWarningsAsErrors`; CI adds the flag too). New warnings break the build.
- Test all: `dotnet test --no-build` (after building). Targeted: `dotnet test tests/Astra.Gateway.Tests`.
- Run: `dotnet run --project src/Astra.Server -- serve` — subcommands are listed in the
  `Program.cs` header comment (`serve`, `migrate`, `restore-all`, `set-password`, `version`).

Node (run `pnpm install` first; CI uses `pnpm install --frozen-lockfile`):

- All packages: `pnpm -r --if-present lint` / `typecheck` / `test` / `build`.
- One package: `pnpm --filter astragate test`, `pnpm --filter @aidotnet/web dev`, etc.
- Version gate: `node scripts/sync-versions.mjs --check` (read-only); write mode also rewrites
  `<Version>` in `Directory.Build.props`.

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
Astra.Core  <-  Astra.Data, Astra.Clients, Astra.Providers  <-  Astra.Gateway  <-  Astra.Server
(domain, zero      (persistence / client config                (pipeline,         (host, wires
 NuGet refs)        take-over, provider templates)              protocol IR)       everything)
```

- Domain logic (billing, pricing, models, privacy) belongs in `Astra.Core`; persistence in
  `Astra.Data` repositories; HTTP and protocol handling in `Astra.Gateway`; endpoints in
  `Astra.Server/Api`.
- `src/Astra.Server/Hosting/AstraApp.cs` is the composition root: DI registrations, middleware
  order, and every `Map*Endpoints()` call. New services, hosted workers, and endpoint groups are
  registered there. Integration tests reuse `AstraApp.Create` via `BuildOptions`
  (`InternalsVisibleTo Astra.Server.IntegrationTests`).

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
  port; read `runtime.json`.
- Everything else lives in SQLite (`Astra.Data`, Dapper, WAL). Runtime-editable settings are one
  `AppSettings` JSON blob stored under key `"app"` (`SettingsService`); adding a setting is a
  property on `AppSettings` plus endpoint/UI wiring — no migration needed.
- Money is stored as INTEGER nanodollars (`Money` in `Astra.Core/Json.cs`, 1e9 per USD); unit
  prices are decimal strings inside pricing JSON. Never store money as a float.
- JSON options are deliberate (`Astra.Core/Json.cs`): `Json.Storage` (snake_case, omits nulls)
  for anything persisted; `Json.Api` (camelCase) for the admin HTTP surface, configured globally
  in `AstraApp`. Do not create ad-hoc `JsonSerializerOptions`.

### Error and security boundaries

- Admin API: throw `AdminApiException(status, message)`; `AdminApiErrorFilter` turns it (and
  IO/format errors) into JSON `{ error }`. `src/Astra.Server/Security/Security.cs`
  `SecurityMiddleware` enforces: loopback Host allowlist (DNS-rebinding defense),
  `X-Astra-Admin: 1` on every mutating `/api` call, and a session cookie when listening
  off-loopback except for an explicit "open" path list. Mutating endpoints get the header
  requirement automatically; adding a public read endpoint means deliberately adding it to that
  list. The web client already sends the header (`web/src/api/client.ts`, `api()`).
- Gateway: `GatewayException` is rendered in the inbound protocol's error shape. Client-facing
  gateway error messages are currently written in Chinese — match the surrounding file.
- Secrets (provider API keys, local client keys) go through `ISecretProtector` (DataProtection);
  DTOs expose only masks (see `ProviderDto.HasApiKey` / `ApiKeyMasked`). Never log secrets or
  echo them back.

## Linked updates — things that must change together

1. **Version bump**: one version everywhere — `cli/package.json` is the source of truth; every
   `npm/*/package.json`, the cli `optionalDependencies` pins, and `<Version>` in
   `Directory.Build.props` must match. Run `node scripts/sync-versions.mjs` (write mode) instead
   of editing them by hand, then `pnpm install` (the lockfile records the cli's platform-package
   specifiers); `release.yml` fails the publish if any drift or a tag/version mismatch remains.
   Add a `## [x.y.z] - date` section to `CHANGELOG.md` in the same change: pushing the bump to
   `main` triggers `release.yml`, which refuses to publish without it and uses it as the GitHub
   Release, download-page and update-manifest notes. `ServerOptions.ApiVersion` ("1.0") is
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
   `astra-server restore-all` is an offline subcommand that depends on this data. Adapter tests
   must use a fake home (`ClientEnvironment`, `tests/Astra.Clients.Tests/TestHome.cs`) — never
   the developer's real client configs.

## Conventions

- C#: file-scoped namespaces, nullable enabled, records and primary constructors, XML doc
  comments on public types. `noUncheckedIndexedAccess` is also on for all TypeScript
  (`tsconfig.base.json`); packages extend it (`cli`) or replicate it (`web`, `desktop`).
- Formatting: root `.prettierrc` (100 cols, single quotes, semicolons, trailing commas). There
  is no repo-wide format command — match the surrounding code. ESLint: `web/` and `desktop/`
  have their own flat configs; `cli/` and `packages/*` inherit the root `eslint.config.js`.
- Tests: vitest with per-package configs — `cli/test`, `desktop/tests`, `packages/update-core`
  (node env), `web/src/test` (jsdom + testing-library). xunit on the .NET side, one test project
  per src project.
- The web UI is bilingual (zh/en); any user-visible string belongs in the i18n catalogs, not
  inline in components.

## Generated files — do not edit by hand

- `web/dist`, `desktop/renderer`, `src/Astra.Server/wwwroot` — web build artifacts (all
  gitignored).
- `desktop/server-bundle/` — server staged for the desktop installers by `release.yml`
  (gitignored).
- `npm/server-*/bin/`, `npm/desktop-*/app/` — outputs of `scripts/pack-platform.mjs` /
  `scripts/pack-desktop.mjs`.
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
  standalone installers (dmg / NSIS / AppImage) ship the server under `resources/server`:
  `release.yml` stages `npm/server-*/bin` into `desktop/server-bundle`, which
  `electron-builder.yml` copies via `extraResources`. After an
  update, the executor repoints `install.json` at the managed binary
  (`~/.astra/server/astra-server-<version>`), which is what makes both sides pick it up — keep
  that invariant when touching either resolver.
- `@aidotnet/update-core` is a source-only workspace package (`main`/`exports` point at
  `src/index.ts`). Consumers must bundle it (tsup/esbuild), not import it as compiled JS — both
  cli and desktop do this today via their devDependency.
- Release publish order matters: `@aidotnet/server-*` and `@aidotnet/desktop-*` packages must
  be on npm before the `astragate` / `@aidotnet/astra-gate` CLI tarballs, because the CLI's `optionalDependencies` resolve
  at install time (`.github/workflows/release.yml`). Scoped tarballs are named without `@`/`+`
  (`aidotnet-server-*.tgz`) and the workflow globs rely on that.
- Update flow ownership: the server only checks the feed (`UpdateCheckWorker`,
  `GET /api/update/status`, `POST /api/update/check`); applies happen out of process in the CLI
  or desktop via `packages/update-core` (staged download → sha256 → swap → restart → rollback,
  state in `~/.astra/update-state.json`, executor never replaces a running binary in place).

## Ground rules for edits

- Keep diffs minimal and scoped to the task. Do not refactor, reformat, or "clean up" code your
  task did not touch; existing oddities are not license for unrelated changes.
- Read current file contents before editing; this repo has scripts, CI, and packages that
  reference files across boundaries, so stale copies cause silent drift.
- If a task cannot be done without violating an invariant above (for example, editing an applied
  migration), stop and explain the conflict before changing code.
- If you change a documented command, boundary, or linked-update step in this file's scope,
  update the corresponding guidance here in the same change.
