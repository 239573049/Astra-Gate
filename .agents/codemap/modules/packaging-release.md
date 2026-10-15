---
module: packaging-release
sources: [".github/workflows/**", "scripts/**", "npm/**", "Dockerfile", "compose.yaml"]
commit: 6324e30
updated: 2026-10-14
---
# packaging-release

Everything between "the code builds" and "a user gets it": the three GitHub Actions pipelines (`ci.yml`, `release.yml`, `docs.yml`), the `scripts/*.mjs` that pack the published server binary and the electron-builder output into npm platform packages, the version-sync gate + `CHANGELOG.md` release notes, and the container surface (root `Dockerfile`, `compose.yaml`, `docker/data`).
**Not responsible for:** what the AOT binary contains (`astra-server`, `Astra.Server.csproj`), the web build (`web-ui`), electron-builder signing/targets and the renderer build (`desktop-app`), the update algorithm that consumes the manifest (`update-core`), the feed/market/tools HTTP surface on the site (`docs-site`), the CLI tarball's contents and its self-update name (`cli`).

## Entry points
| Symbol | File | Role |
|---|---|---|
| `version` job | `.github/workflows/release.yml:31` | Resolves the version from `cli/package.json`, derives the channel with `scripts/release-channel.mjs` (fails outside `x.y.z` / `x.y.z-preview.N`), gates tag↔version, "already on npm", and a non-empty CHANGELOG section; outputs `version`, `publish`, `channel`, `npm_tag`, `tag_preview`. |
| `build` job (6-row matrix) | `.github/workflows/release.yml:74` | Per RID: AOT publish → AOT-warning gate → `plugin-selftest` → version check → manifest partial → server pkg → `server-bundle` → desktop build → `pack-desktop` → `npm pack`; uploads `pkg-<rid>` + `feed-<rid>`. |
| `publish` job | `.github/workflows/release.yml:341` | Merges partials into `out/feed/<channel>/latest.json`, uploads the installers to the docs site, publishes platform packages then the CLI to npm under `--tag <npm_tag>`, then uploads the manifest (after npm), creates the `v<version>` GitHub Release (`--prerelease` for a preview). |
| `docker` job | `.github/workflows/release.yml:531` | Unpacks `pkg-linux-*` into `docker-bin/{amd64,arm64}`, smoke-tests an amd64 image, pushes multi-arch `ghcr.io/<owner>/<repo>`. |
| `dotnet` / `node` jobs | `.github/workflows/ci.yml:13`,`:35` | PR + main CI: `dotnet build -warnaserror` + `dotnet test --no-build`; `pnpm -r --if-present lint/typecheck/test/build`. |
| `build` job | `.github/workflows/docs.yml:31` | Path-filtered build+push of the docs image to `ghcr.io/<owner>/<repo>/docs`. |
| `pack-platform.mjs <main>` | `scripts/pack-platform.mjs:28` | `--rid --publish-dir --web-dist` → `npm/<server-*>/bin/{binary, libe_sqlite3.*, wwwroot}`. |
| `sync-versions.mjs <main>` | `scripts/sync-versions.mjs:30` | `--check` (read-only gate) or write mode: one version across `cli`, `npm/*`, `desktop`, `Directory.Build.props`. |

## Key types and functions
| Symbol | File | Role |
|---|---|---|
| `ridToServerDir` / `ridToDesktopDir` / `ALL_RIDS` | `scripts/rid-map.mjs:17`,`:26`,`:14` | The single RID → `npm/` directory map (`osx-arm64` → `server-darwin-arm64` → `desktop-darwin-arm64`); throws on an unknown RID. Inlined into the workflow with `node -e "import('./scripts/rid-map.mjs')…"` (`.github/workflows/release.yml:232`,`:276`). |
| `NATIVE_LIBRARY` scan | `scripts/pack-platform.mjs:67` | Copies every `*.dylib|*.so*|*.dll` beside the binary; warns when none was found (AOT keeps SQLite external). |
| `findMacAppBundles` / `findUnpackedDir` | `scripts/pack-desktop.mjs:39`,`:57` | Locates `mac*/*.app` or `{win,linux}[-<arch>]-unpacked` under electron-builder's output dir. |
| `keep` filter | `scripts/pack-desktop.mjs:96` | Drops `resources/server` from the Windows/Linux npm desktop package (npm E413); macOS keeps it. |
| `partial` mode | `scripts/gen-manifest.mjs:38` | Writes `{rid, server, serverSha256, serverSize}` for one RID, hashing **only the executable**. |
| `merge` mode | `scripts/gen-manifest.mjs:60` | Merges partials into `{version, apiVersion, releasedAt, notes, platforms}` — the `latest.json` contract. |
| `changelog-section` | `scripts/changelog-section.mjs:32` | `^## \[?v?<ver>\]?` body up to the next `## `; prints nothing (exit 0) when absent, so callers can test for empty. |
| `check-aot-warnings` | `scripts/check-aot-warnings.mjs:21` | Fails on any `IL\d{4}` warning/error line that is not Jint's; de-dupes MSBuild's repeated lines. |
| `publish-feed` | `scripts/publish-feed.mjs:41`,`:69` | rsync/copy to a local dir or `user@host:/path`: versioned artifacts first, `latest*.json|yml` pointers last; never `--delete`. |
| `Upload platform packages` step | `.github/workflows/release.yml:285` | Publishes `pkg-<rid>` (the tgz is the artifact, not the directory). |
| `Publish platform packages first, then the CLI` step | `.github/workflows/release.yml:458` | `publish()` (skip-if-on-npm, `--tag $NPM_TAG`) + `wait_for()` (90 min) then the CLI, twice (renamed), a `wait_for` on both CLIs, and the `preview` dist-tag move for a stable with `tag_preview`. |
| `channelOf` / `compareReleaseVersions` | `scripts/release-channel.mjs` | The release version rule (`x.y.z` stable, `x.y.z-preview.N` preview) and its ordering; imported by `sync-versions.mjs`, run as a CLI by the `version` job. |
| `Dockerfile` | `Dockerfile:19` | `runtime-deps:10.0` + `COPY docker-bin/${TARGETARCH}/`; `/data` owned by `app`, healthcheck via the `healthcheck` subcommand. |

## Dependencies
- Uses: `astra-server` via build output (files) — `dotnet publish src/Astra.Server … -p:PublishAot=true -o out/publish` (`.github/workflows/release.yml:193`), whose log the AOT gate reads (`:200`) and whose binary+companions `scripts/pack-platform.mjs:61`,`:71` carry; the ILC allow-list is `src/Astra.Server/Astra.Server.csproj:52`.
- Uses: `web-ui` via files (build output) — `pnpm --filter @aidotnet/web build` (`.github/workflows/release.yml:169`) then `--web-dist web/dist` → `bin/wwwroot` (`scripts/pack-platform.mjs:81`).
- Uses: `desktop-app` via shell (build + packager) — `pnpm --filter @aidotnet/desktop build` and `electron-builder … --config.directories.output=../out/desktop` (`.github/workflows/release.yml:257`,`:264`); `extraResources: server-bundle → server` (`desktop/electron-builder.yml:15`).
- Uses: `cli` via npm pack — `npm pack ./cli` then the in-place rename + rebuild as `@aidotnet/astra-gate` (`.github/workflows/release.yml:495`,`:502`); the version source is `cli/package.json:2`.
- Uses: `update-core` via a JSON contract — `latest.json` written by `scripts/gen-manifest.mjs:83` and fetched from `${feed}/<channel>/latest.json` (`packages/update-core/src/manifest.ts:49`).
- Uses: `docs-site` via HTTP — `POST ${DOCS_RELEASE_URL}/api/client-releases/upload` with `x-astra-version|channel|platform|kind|notes-b64` (installers before npm, the manifest after; accepted by `docs/app/api/client-releases/upload/route.ts`).
- Used by: `cli` via npm — `@aidotnet/server-*` are `optionalDependencies` resolved at install time (`cli/package.json:33`, `.github/workflows/release.yml:483`).
- Used by: `desktop-app` via npm package contents — `npm/desktop-*/app` (`scripts/pack-desktop.mjs:62`).
- Used by: `docs-site` via the image it serves + the uploaded files (`.github/workflows/docs.yml:28`, `docs/lib/releases.ts:25`).
- Used by: `astra-server` via container env — `ASTRA_HOME/ASTRA_HOST/ASTRA_PORT/ASTRA_STRICT_PORT` (`Dockerfile:30`, matching `ServerOptions.ApplyEnvironment`).

## State it owns
- **Version constants** (one number, five places): `cli/package.json:2` is the source of truth; `scripts/sync-versions.mjs:38` rewrites every `npm/*/package.json`, `desktop/package.json` (`:47`), the CLI's `optionalDependencies` pins (`:68`) and `<Version>` in `Directory.Build.props:9`.
- `CHANGELOG.md` — the only release-note source, read by `scripts/changelog-section.mjs:25`; a missing `## [x.y.z]` section fails the `version` job (`.github/workflows/release.yml:67`).
- **Generated, do not edit:** `npm/server-*/bin/`, `npm/desktop-*/app/` (`scripts/pack-platform.mjs:59`, `scripts/pack-desktop.mjs:66`), `desktop/server-bundle/` (`.github/workflows/release.yml:233`), `docker-bin/{amd64,arm64}/` (`.github/workflows/release.yml:563`), `src/Astra.Server/wwwroot/`, `out/feed`, `out/artifacts`, `out/pkg` — all gitignored (`.gitignore:12`–`:26`).
- **Published state outside the repo:** the npm names `astragate`, `@aidotnet/astra-gate`, `@aidotnet/{server,desktop}-<os>-<arch>`, the tags `latest` / `<major>.<minor>` / `<version>` on `ghcr.io/<owner>/<repo>` and `/docs`, and the GitHub Release `v<version>` with the installers attached.
- `docker/data/.gitkeep` is the only tracked file under `docker/`; it seeds the image's `/data` volume (`Dockerfile:28`).

## Contracts it exposes
- **Platform packages:** `@aidotnet/server-<os>-<arch>` with `os`/`cpu`, `bin.astra-server` (`bin/astra-server.exe` on win32), `files: ["bin/"]`, `preferUnplugged: true` (`npm/server-win32-x64/package.json:12`); `@aidotnet/desktop-<os>-<arch>` with `files: ["app/"]` and no `bin` (`npm/desktop-darwin-arm64/package.json:12`).
- **Tarball names:** npm drops `@` and `+` from a scoped tarball, so the artifacts are `aidotnet-server-*.tgz` / `aidotnet-desktop-*.tgz` / `astragate-<v>.tgz` / `aidotnet-astra-gate-<v>.tgz` (`.github/workflows/release.yml:485`,`:486`,`:489`,`:496`,`:505`).
- **Feed layout:** `out/feed/stable/latest.json` (manifest), plus `latest-mac.yml`, `*.dmg`, `*.zip` for `scripts/publish-feed.mjs` (`.github/workflows/release.yml:375`–`:377`); the docs site re-serves the manifest at `/api/client-releases/stable/latest.json` from a file uploaded with `platform=manifest`, `kind=json` (`docs/app/api/client-releases/[channel]/[...path]/route.ts:80`).
- **Manifest schema:** `{version, apiVersion, releasedAt, notes?, platforms: {<rid>: {server, serverSha256, serverSize}}}` (`scripts/gen-manifest.mjs:83`); the TS mirror also declares an optional `desktop` field (`packages/update-core/src/manifest.ts:9`) that `gen-manifest.mjs` never writes.
- **Workflow artifacts:** `pkg-<rid>` = `out/pkg/*.tgz`, `feed-<rid>` = `out/feed-upload/*` (`.github/workflows/release.yml:288`,`:311`).
- **Env vars / secrets:** `NPM_TOKEN` (`.github/workflows/release.yml:450`), `CSC_LINK`/`CSC_KEY_PASSWORD`/`APPLE_ID`/`APPLE_APP_SPECIFIC_PASSWORD`/`APPLE_TEAM_ID` (`:132`–`:136`), `ASTRA_FEED_URL` (default `https://astra-gate.si/api/client-releases/stable`, `:254`), `FEED_UPLOAD_TARGET` (`:384`), `DOCS_RELEASE_URL` + `DOCS_RELEASE_TOKEN` (falls back to `CLIENT_UPLOAD_TOKEN`, `:395`), `GITHUB_TOKEN` (`:578`).
- **Container surface:** image `ghcr.io/<owner>/<repo>`, port `17321`, `VOLUME /data`, non-root `app`, `ENTRYPOINT ["/app/astra-server"]` + `CMD ["serve"]` (`Dockerfile:36`–`:45`); `compose.yaml:14` requires `ASTRA_ADMIN_PASSWORD` to be set.
- **Docs image contract:** `ghcr.io/<owner>/<repo>/docs` tagged `latest`(default branch) / long-sha / semver (`.github/workflows/docs.yml:50`–`:54`), built from `docs/Dockerfile` with the repo root as context (`:58`).

## How to extend
- To add a platform (RID): add the entry to `scripts/rid-map.mjs:5`, a new `npm/server-<dir>/package.json` + `npm/desktop-<dir>/package.json` (copy an existing pair; `name`/`os`/`cpu`/`bin.files`), a row in the `build` matrix (`.github/workflows/release.yml:85`) and, if it is mac, extend the Xcode/`Check macOS signing credentials` conditions (`:130`,`:180`). Missing the `rid-map` entry makes every `npm pack`/staging step exit 1 with `Unknown RID`.
- To cut a release: `node scripts/sync-versions.mjs --version x.y.z`, add a `## [x.y.z] - date` section to `CHANGELOG.md`, `pnpm install`, push to `main`. Missing the changelog section fails the `version` job (`.github/workflows/release.yml:67`); a version already on npm silently stops after `version` (`:60`).
- To add a new file kind to the feed: extend the `platform`/`kind` mapping in the docs-upload loop (`.github/workflows/release.yml:408`–`:418`) and `EXT_BY_KIND` (`docs/lib/releases.ts:72`). A kind the site does not know lands as `.bin`.
- To change a version number by hand anywhere: run the sync script instead — a hand edit can drift `desktop/package.json` and every installer then reports a stale version (`scripts/sync-versions.mjs:44`).
- To add a post-publish step: add it to the `publish` job after `.github/workflows/release.yml:448`; the `docker` job already depends on `publish` completing (`:535`).

## Invariants and traps
- **Publish order is load-bearing**: `@aidotnet/server-*` and `@aidotnet/desktop-*` must be installable before the CLI tarball, because the CLI's `optionalDependencies` resolve at install time — a CLI that becomes `latest` first installs without a server and the dependency "silently fails" (`.github/workflows/release.yml:467`–`:494`). npm's publish-processing lag is why `wait_for` polls up to 90 minutes (`:473`).
- **The two-phase release**: a `main` push whose `cli/package.json` version is already on npm stops after the `version` job, so `build`/`publish` never run for that push (`cli/package.json` paths filter `.github/workflows/release.yml:11`, check `:60`). The marker is `@aidotnet/astra-gate` because it is published last (`:58`).
- **The CLI is packed, renamed, rebuilt and packed again**: tsup inlines `package.json`, and the CLI's self-update targets the baked-in name, so skipping the rebuild publishes a `@aidotnet/astra-gate` tarball that updates `astragate` (`.github/workflows/release.yml:499`).
- **The feed manifest hashes the executable only** — `libe_sqlite3.*` is copied but carries no integrity hash: a known gap (`scripts/gen-manifest.mjs:50`, `scripts/pack-platform.mjs:71`).
- **Never pass `-p:PublishSingleFile=true`**: a command-line `-p:` overrides the csproj's conditional group and re-enables single-file under AOT (`.github/workflows/release.yml:171`).
- **macOS rows need macos-15 + an explicit Xcode 2x**: macos-14 tops out at Xcode 16.2, whose SDK lacks the Swift shims the .NET 10 AOT runtime pack auto-links (undefined `__swift_FORCE_LOAD_*`), and whether the link passed depended on the agent; `macos-15` defaults to Xcode 16.4, hence the `Select an Xcode 26 toolchain` step (`.github/workflows/release.yml:86`, `:176`). Commit 82fa73b.
- **osx-x64 is cross-built on Apple silicon and never executed**, so its `plugin-selftest` is skipped (`.github/workflows/release.yml:204`).
- **Both docs/feed upload steps can "succeed" while publishing nothing**: the client-release upload warns and `exit 0` when its secrets are missing (`.github/workflows/release.yml:400`), and the feed step is skipped when `FEED_UPLOAD_TARGET` is unset because `secrets` is unavailable in a step-level `if` (`:380`). Check the step log, not the run colour.
- **`build` re-runs `Check macOS signing credentials` before the long build** because unsigned mac artifacts pass CI but break Gatekeeper and Squirrel.Mac's designated-requirement check (`.github/workflows/release.yml:126`).
- **The docs site synthesizes `latest-mac.yml` itself** (`renderLatestMacYml`, `docs/app/api/client-releases/[channel]/[...path]/route.ts:7`): the upload loop only ships `.dmg/.zip/.exe/.AppImage` + the manifest, so the `latest-mac.yml` produced by electron-builder reaches the feed host only through `publish-feed` (`.github/workflows/release.yml:377`).
- **`docker` is gated on `publish` completing**, so a failed npm publish never leaves an image for a version that was never released (`.github/workflows/release.yml:533`).
- **The image is assembled, never compiled**: `Dockerfile:23` copies `docker-bin/${TARGETARCH}/`, and the glibc `runtime-deps` base is deliberate — the release RIDs are `linux-x64`/`linux-arm64`, not `linux-musl-*` (`Dockerfile:17`). `Dockerfile.dockerignore` is the server image's context filter (only `docker-bin`, `docker/data`); the root `.dockerignore` belongs to `docs/Dockerfile` (`Dockerfile.dockerignore:2`).
- **Dropping `resources/server` is what keeps the Windows/Linux npm desktop tarball under npm's size limit** (E413); macOS keeps it because its `Resources` are sealed by the code signature (`scripts/pack-desktop.mjs:91`), and the pack step then skips a desktop package that has no `app/` at all (`.github/workflows/release.yml:279`).
- **Version drift is not checked on PRs**: `sync-versions.mjs --check` runs only inside the release `build` matrix (`.github/workflows/release.yml:210`), whereas `ci.yml` runs no version gate at all.
- **`TreatWarningsAsErrors`** (`Directory.Build.props:7`) plus `dotnet build -warnaserror` (`.github/workflows/ci.yml:30`) means a new warning breaks CI even though nothing else about the build failed.
- `.npmrc:1` links workspace packages when a dependency version matches exactly — the reason a version-skewed local install still resolves the *published* package; its comment still says `@astragate/server-*` while the packages are `@aidotnet/server-*` (stale comment).
- `docs/` has no `lint`/`test`/`typecheck` script (its check is `types:check`), so `pnpm -r --if-present …` in `ci.yml` skips it silently — the site is only really verified by `docs.yml`'s image build.

## Tests
There is no unit-test suite in this module; the gates are commands the workflows run:
- `node scripts/check-aot-warnings.mjs out/publish.log` — fails on any non-Jint IL warning (`.github/workflows/release.yml:200`).
- `out/publish/astra-server[.exe] plugin-selftest` — run-time AOT check on the freshly published binary, all rows except osx-x64 (`.github/workflows/release.yml:208`).
- `node scripts/sync-versions.mjs --check` — read-only version gate, exits 1 on any mismatch (`.github/workflows/release.yml:212`).
- `node scripts/changelog-section.mjs <ver>` — non-empty output is required to release (`.github/workflows/release.yml:67`).
- `docker` job smoke test: `docker run` + health polling, then `/api/health`, `/api/auth/status` must contain `"required":true`, and `/` must serve HTML (`.github/workflows/release.yml:590`–`:604`).
- Locally: `node scripts/check-aot-warnings.mjs <log>`, `node scripts/sync-versions.mjs --check`, `node scripts/changelog-section.mjs 0.9.2`. Full CI parity: `dotnet build -warnaserror && dotnet test --no-build` and `pnpm -r --if-present lint|typecheck|test|build` (`.github/workflows/ci.yml`).

## Related flows
- [release-and-update](../flows/release-and-update.md)
- [server-startup](../flows/server-startup.md)
