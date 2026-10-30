---
flow: release-and-update
modules: [packaging-release, astra-server, cli, desktop-app, update-core, docs-site]
commit: 6324e30
updated: 2026-10-15
---
# release-and-update

**Trigger:** a version bump lands on `main` (`cli/package.json` changes → `release.yml`; or a `v*` tag;
or a manual `workflow_dispatch` for a build-only run). **Outcome:** npm carries the versioned platform
packages and CLI, `ghcr.io/<repo>` carries the matching Docker image, `v<version>` and its installers
exist as a GitHub Release, the docs site serves the installers + `latest.json` under
`/api/client-releases/<channel>/`, and an installed CLI or desktop app can find, stage, verify, swap and
(if the restart misbehaves) roll back to the previous server binary.

**Channels:** the version picks one — `x.y.z` → `stable`, `x.y.z-preview.N` → `preview`
(`scripts/release-channel.mjs`, run by the `version` job, outputs `channel` / `npm_tag` / `tag_preview`).
The channel decides the npm dist-tag, the feed directory, the GitHub prerelease flag and the Docker tags;
the `preview` feed also offers every stable. Clients follow the server's `updateChannel` setting
(default stable). The full table is `.agents/skills/release/references/channels.md`.

## Steps

### (a) Publishing

1. `scripts/sync-versions.mjs` — write mode (`node scripts/sync-versions.mjs [--version x.y.z]`)
   takes `cli/package.json` as the source of truth and rewrites every `npm/*/package.json`,
   `desktop/package.json`, the CLI's `optionalDependencies` pins and `<Version>` in
   `Directory.Build.props:9`; `--check` validates only, and also runs inside every build matrix row
   (`packaging-release`)
2. `scripts/changelog-section.mjs` — prints the body of the `## [x.y.z] - date` section of
   `CHANGELOG.md` up to the next `## ` heading, or nothing (exit 0) when absent (`packaging-release`)
3. ⇢ crosses **network** boundary via a push to `main` touching only `cli/package.json` (or tag `v*`),
   which starts `.github/workflows/release.yml`
4. `.github/workflows/release.yml:version` job — reads `cli/package.json`; `workflow_dispatch` ⇒
   `publish=false` and everything downstream is skipped; a tag whose name ≠ the version fails the run;
   `npm view @aidotnet/astra-gate@$VER` non-empty ⇒ `publish=false` (the **two-phase shortcut**: `build`
   and `publish` both `if: needs.version.outputs.publish == 'true'`, so a `main` push for an
   already-published version stops here); finally the CHANGELOG gate — an empty section is an
   `::error::` that fails the job (`release.yml:44-72`) (`packaging-release`)
5. `release.yml:build` — 6-row matrix (osx-arm64/osx-x64 on `macos-15`, linux-x64, linux-arm64 on
   `ubuntu-24.04-arm`, win-x64, win-arm64); `actions/setup-dotnet` runs once with
   `continue-on-error: true` and is repeated only when it failed (that retry is commit `6324e30`); mac
   rows fail fast on missing `CSC_LINK`/`APPLE_ID`/… secrets; `pnpm install` retried up to 3×; builds
   `web/dist`; selects the newest Xcode 2x for the AOT link (`release.yml:176-186`) (`packaging-release`)
6. `release.yml:188-212` — `dotnet publish src/Astra.Server -c Release -r $RID --self-contained true
   -p:PublishAot=true` (never `-p:PublishSingleFile=true`), then `scripts/check-aot-warnings.mjs`,
   `astra-server plugin-selftest` (every row but cross-built osx-x64), and
   `node scripts/sync-versions.mjs --check` as an in-CI drift gate (`packaging-release`)
7. `scripts/gen-manifest.mjs:partial` — sha256 + byte size of the freshly published executable →
   `out/feed/partial-<rid>.json`; the manifest names the npm package via `scripts/rid-map.mjs`
   (`packaging-release`). *Known gap: only the executable is hashed — the native companion
   (`libe_sqlite3.*`) carries no integrity hash.*
8. `scripts/pack-platform.mjs` — copies the binary + companions (`NATIVE_LIBRARY` regex at
   `pack-platform.mjs:67`) + `web/dist` → `bin/wwwroot` into `npm/server-<platform>-<arch>/bin/`;
   warns when no companions are found (`packaging-release`)
9. `release.yml:225-283` — `npm/<server-dir>/bin` copied to `desktop/server-bundle` (shipped as
   `resources/server` by `desktop/electron-builder.yml:15-17`), `pnpm --filter @aidotnet/desktop build`
   + `electron-builder` for that OS/arch (`--publish never`), `scripts/pack-desktop.mjs`, then
   `npm pack` of the server and (when produced) desktop platform packages; artifacts uploaded as
   `pkg-<rid>` and `feed-<rid>` (`packaging-release`)
10. `release.yml:publish` — rebuilds the CLI (`pnpm --filter astragate build`), merges the partials with
    `scripts/gen-manifest.mjs:merge` → `out/feed/<channel>/latest.json`
    (`{version, apiVersion, releasedAt, notes, platforms}`, `apiVersion` defaulting to `"1.0"` —
    deliberately a protocol version, not the release version), extracts `out/release-notes.md`
    (`packaging-release`)
11. `release.yml:380-386` — optional legacy host: `scripts/publish-feed.mjs` rsyncs `out/feed` to
    `FEED_UPLOAD_TARGET`, artifacts first and the `latest.json`/`latest-mac.yml` pointers last, so no
    client ever sees a pointer to a missing file (`packaging-release`)
12. `release.yml` "Upload the installers to the docs site" (before npm) and "Publish the update
    manifest to the docs site" (**after** npm, because `latest.json` names npm packages) — `curl -X POST
    ${DOCS_RELEASE_URL}/api/client-releases/upload` with `Authorization: Bearer
    ${DOCS_RELEASE_TOKEN || CLIENT_UPLOAD_TOKEN}` and `x-astra-version` / `x-astra-channel` /
    `x-astra-platform` (`darwin-arm64|…`, or `manifest` for the JSON) / `x-astra-kind`
    (`dmg|zip|exe|appimage|json`) / `x-astra-notes-b64`; unset secrets ⇒ `::warning::` + `exit 0` (a green
    run that publishes nothing). The legacy `FEED_UPLOAD_TARGET` step (11) also runs after npm
    (`packaging-release` → `docs-site`)
13. ⇢ crosses **network** boundary via `POST /api/client-releases/upload`
14. `docs/app/api/client-releases/upload/route.ts:POST` — `verifyUploadToken` (constant-time bearer,
    503 when `CLIENT_UPLOAD_TOKEN` is unset, `docs/lib/releases.ts:34`), header sanitizing and semver
    validation, a 400 when `x-astra-channel` is unknown or disagrees with the version (`channelOfVersion`),
    then `docs/lib/releases.ts:saveUpload` (`docs-site`)
15. `docs/lib/releases.ts:saveUpload` — streams to `${DATA_DIR}/client-releases/<channel>/<version>/
    Astra-<ver>-<platform>.<ext>` while computing sha256 **and** sha512 (base64, which
    electron-updater wants), tmp + rename, then merges into that channel's `index.json`
    (`docs/lib/releases.ts:134`) (`docs-site`)
16. `docs/app/api/client-releases/[channel]/[...path]/route.ts:GET` — public read surface over
    `readFeed(channel)` (the channel's index merged with those in `FEED_SOURCES`: preview = preview +
    stable, each entry remembering its storage channel; unknown channel ⇒ 404, `beta` reads as preview):
    `/latest.json` (newest release **with** a `kind=json` file — installers uploaded ahead of the
    manifest are skipped), `/latest-mac.yml` (newest with a zip, `renderLatestMacYml`), `/releases.json`
    (semver-sorted, `channel` per version), `/v/<version>/<file>` and a bare filename looked up across
    the feed (`docs-site`)
17. `docs/app/(home)/download/page.tsx:DownloadPage` — the download page renders `readFeed('stable')` and,
    in a folded section, the newest preview while it is newer than that stable, straight off those
    directories, so the upload step is what populates the site
    (`docs-site`); `docs/proxy.ts:config` deliberately excludes `/api` (the proxy body cap of 10 MB
    truncated installers)
18. `release.yml` "Publish platform packages first, then the CLI" — npm publish order is a contract, every
    package under `--tag $NPM_TAG` (`latest` | `preview`): server tarballs → desktop tarballs →
    `wait_for` each id to become visible (`npm view … --prefer-online`, 90-minute deadline) → pack and
    publish `astragate` → rewrite `cli/package.json`'s name to `@aidotnet/astra-gate`, rebuild with tsup
    (it inlines `package.json`, and the CLI's self-update uses the baked-in name) and publish that
    **last** — which is exactly the marker the `version` job looks for — then `wait_for` both CLIs and, for
    a stable with `tag_preview=true`, `npm dist-tag add … preview` (`packaging-release`)
19. `release.yml` "Create the GitHub Release" — `gh release create v$VER` (or `edit` + `upload --clobber`
    on a re-run) with the installers and the changelog notes, `--prerelease` on the preview channel
    (`packaging-release`)
20. `release.yml:docker` (`needs: [version, publish]`) — unpacks `pkg-linux-*` into
    `docker-bin/{amd64,arm64}`, builds amd64 for a smoke test (`/api/health`, `/api/auth/status`
    `required:true`, the SPA at `/`), then buildx-pushes linux/amd64 + linux/arm64 to
    `ghcr.io/<repo>` — tags `latest`/`x.y`/`x.y.z` for stable, `x.y.z-preview.N` for a preview, plus
    `preview` when `tag_preview` (`packaging-release`)

### (b) Updating — the server only checks

21. `src/Astra.Server/Hosting/UpdateCheckService.cs:CheckAsync` — GET
    `{EffectiveFeedUrl}/{UpdateChannels.Normalize(UpdateChannel)}/latest.json` with User-Agent
    `Astra/<ServerOptions.Version>` through the `update-check` HttpClient (15 s timeout, `AstraApp.cs:216`),
    compares with `IsNewer` (semver precedence: a release beats its previews, so nothing ever downgrades)
    against `ServerOptions.Version`, persists `UpdateCheckState` under settings key
    `update.state`, keeps the previous result and records `Error` on failure; `UpdateFeedUrl`
    `""` disables the feed (`feed-not-configured`) (`astra-server`)
22. `src/Astra.Server/Hosting/UpdateCheckWorker.cs:ExecuteAsync` — background service: 30 s initial
    delay, 12 h interval doubling (cap 48 h) after a failed check; every failure is caught and logged
    (`astra-server`)
23. `src/Astra.Server/Api/UpdateEndpoints.cs:MapUpdateEndpoints` — `GET /api/update/status`
    (unauthenticated read-only tier) and `POST /api/update/check` (inside the `/api/update` group with
    `AdminApiErrorFilter`, so the mutating-call rules apply) both return the same `Status(...)`
    payload; commit `415d99ddd5ae5bfbd2ca78b966fee82d6f7b2ac0` ("fix(update): make POST /api/update/check
    return the status payload shape") extracted that builder because the web UI caches the check
    response as the status (`astra-server`). The prompt's `415d99d` is this commit; the prompt's
    `c521a689` is wrong — the fix commit is `c21a68995b2631bb85c8717f81e09365ecace6fd`
24. `web/src/api/hooks.ts:238-248` — `useUpdateStatus` (5-minute refetch) and `useCheckUpdate`, whose
    `onSuccess` writes the POST result into the same query key; `web/src/api/types.ts:806`
    (`UpdateStatus`) and `web/src/pages/SettingsPage.tsx` (About section) consume it (`web-ui`)
25. ⇢ crosses **IPC** boundary via `window.__ASTRA__.updates.check()/apply()` (`desktop/src/preload.ts:52`,
    `desktop/src/main.ts:680-686`, declared in `web/src/shell/bridge.ts:67`) — the renderer falls back to
    a hint ("Use `astra update`") when the bridge is absent (web build) (`web-ui` → `desktop-app`)

### (b) Updating — applying is out of process

26. `cli/src/commands/desktop.ts:runUpdate` — `astra update [--check] [--client] [--retry] [--channel]`:
    `resolveChannel` (`--channel`, else the running server's `/api/update/status` channel, else stable),
    `fetchManifest` for it (UA `Astra-CLI/<version>`), `updateServer` (confirmation prompt, then
    `applyServerUpdate` with a `ServerControl` over `server-lifecycle`), then an
    `npm install -g <package>@<manifest.version>` self-update (never `@latest`) and the desktop install;
    `reportCheck` sets exit code 1 when a version is available (`cli`)
27. `desktop/src/update.ts:UpdateController` — three surfaces: electron-updater DMG (macOS installs
    outside the npm prefix), macOS npm self-replace, and the server binary via `update-core`;
    `resolveUpdateTrack` (`desktop/src/shared/updateTrack.ts:17`) picks the track, `start()` schedules a
    launch-time check + 12 h interval (`desktop-app`)
28. `desktop/src/update.ts:runCheck` / `checkServerAndManifest` — POSTs `/api/update/check` to refresh the
    server's cached status (`desktop/src/api.ts:refreshUpdateCheck`, whose answer names the channel; the DMG
    track then points electron-updater at that channel via `setFeedURL` + `shared/updateChannel.ts`), then
    gates, on the npm track only, on the **SERVER's** version (the dmg and installer tracks bundle the
    server, so they check just the app: `checkDesktopDmg` / `checkInstallerManifest`, and the installer
    track's `applyFromUi` opens the download page, outcome `download`; the dmg track never has a pending
    manifest, so while electron-updater is still downloading the new app `applyFromUi` answers
    `downloading` — not `none`, which the Settings page words as "up to date" — and `update-downloaded`
    then reminds; `UiApplyOutcome` is mirrored in `web/src/shell/bridge.ts`):
    `control.probeVersion()` (which is `/api/version` through `desktop/src/main.ts:119`) with
    `app.getVersion()` only as the fallback when the server is down; the status only carries a version,
    so the manifest is fetched directly, from the channel that answered, for the checksum/platform info
    the executor needs (`desktop-app`)
29. `desktop/src/update.ts:applyServerUpdate` — `currentVersion: (await control.probeVersion()) ??
    '0.0.0'` and `gate()` comparing `manifest.apiVersion`'s major with `EXPECTED_API_MAJOR = 1`
    (`desktop/src/shared/version.ts:6`); `platformKey` is `osx-arm64|linux-x64|win-arm64`, **not**
    `process.platform` (`update.ts:342`) (`desktop-app`)
30. `desktop/src/update.ts:applyFromUi` / `backgroundCheck` / `claimWhenFree` — single-flight lock as a
    `symbol` token; `applyFromUi` claims the lock and then calls the **lock-free** `runCheck()`, because
    the public `check()` would refuse (its guard sees the lock), leave `pendingManifest` null and
    silently report "up to date" when the user clicked Update before this run's first check finished;
    a background check *waits* for the lock and holds it for the whole apply, releasing before
    `maybeRemind()` so a dialog's Update button can apply (commit
    `c21a68995b2631bb85c8717f81e09365ecace6fd`) (`desktop-app`)
30a. The Avalonia app (what `release.yml` ships) — `src/Astra.Desktop.Core/Update/UpdateCoordinator.cs` keeps the
    track rule (`UpdateTracks.Resolve`, port of `updateTrack.ts`; picked in `src/Astra.Desktop/Platform/SelfUpdate.cs`):
    dmg and installer apps check **their own** version against `<feed>/<channel>/releases.json`
    (`DesktopReleaseFeed`: newest release with a file tagged `darwin|win32|linux-arm64|x64` + `zip|exe|appimage`,
    the tags the upload step sends) and replace themselves (`IDesktopUpdater`: `MacAppUpdater` zip → ditto →
    bundle id + signing team check → `/bin/sh` swap after exit; `WindowsInstallerUpdater` setup.exe
    `/S --updated --force-run` started by PowerShell after exit; `AppImageUpdater` renames over `$APPIMAGE`);
    the host then quits. They never swap the server alone — before 2026-10-30 they did, on every track, and a dmg
    app stayed at 0.13.0-preview.1 while its server reached 0.13.3. The npm track and dev runs still use
    `ServerUpdater` (the C# port of `apply.ts`). Settings shows the host's answer (`IDesktopHost.LastUpdateCheck`)
    over the server's status and flags an app/server version mismatch on the desktop row (`desktop-app`)
31. `packages/update-core/src/apply.ts:applyServerUpdate` — the state machine
    `downloading → swapping → restarting → idle` with a `rolling-back` leg; refusals first: the
    consecutive-failure lock (`MAX_CONSECUTIVE_FAILURES = 2`, `state.ts:30`, bypassed by `force` /
    `--retry`), `isNewerVersion`, and the apiVersion major check; every phase is persisted to
    `~/.astra/update-state.json` (`paths.ts:24`) so the tray/UI can render progress across processes
    (`update-core`)
32. `packages/update-core/src/staging.ts:stageServerBinary` — `~/.astra/updates/staging` is wiped and
    refilled with `npm install --prefix <staging> <serverPackage>@<version>` (`npm-runner.ts:221`,
    login-shell PATH handling for GUI launches), the binary is sha256-checked against
    `manifest.platforms[platformKey].serverSha256` **before** anything on the live install is touched,
    and companion libraries are collected (`update-core`)
33. `packages/update-core/src/swap.ts` — `backupCurrentBinary` (rolling `~/.astra/backups/server/`,
    `MAX_BACKUPS = 3`, companions snapshotted into `<name>-libs`), `installManagedBinary` →
    `~/.astra/server/astra-server-<version>[.exe]` (a fresh file per version, so Windows locks never
    matter), companions kept as `<name>.prev`; `install-info.ts:applyServerInfo` repoints
    `~/.astra/install.json` (`serverPath`/`serverVersion`) — the cross-process anchor both resolvers
    read (`update-core`)
34. `apply.ts:152-201` — stop the server, `installManagedWebRoot` (swap `serverDir/wwwroot`, previous
    kept as `wwwroot.prev`; best effort), start, then `control.probeVersion()` must equal
    `manifest.version` or the run throws (`probeRestartedVersion`, `apply.ts:228`, retries a null or
    throwing probe for up to 30 s — a server still busy starting is not a failed update); on success `commitNativeCompanions` +
    `commitManagedWebRoot` + staging removed + state reset to `idle` (`update-core`)
35. `apply.ts:202-250` — failure path: `rolling-back` → repoint `install.json` at
    `previousInstall.serverPath` (or `clearServerInfo` so a standalone installer falls back to its
    bundled server), stop unconditionally (a busy failed server can fail `isRunning()` and would
    otherwise keep the port), restore `wwwroot.prev` and companions, restart, then record
    `phase: 'failed'` with `failedCount + 1` (`update-core`)
36. Resolution after the swap: `cli/src/lib/binary.ts:resolveServerBinary` (env > install.json >
    repo dev build > platform package) and `desktop/src/shared/resolveServerBinary.ts:resolveServerBinary`
    (install.json unless older than the bundled server > `ASTRA_SERVER_BIN` > bundled > dev) both honour
    the new `install.json`; `desktop/src/shared/resolveServerBinary.ts:shouldReplaceRunningServer` makes
    `desktop/src/service.ts` adopt a running server only when its version matches the resolved target
    (`cli` / `desktop-app`)

## Sequence

```mermaid
sequenceDiagram
  participant DEV as packaging-release
  participant GH as GitHub Actions
  participant NPM as npm registry
  participant DOCS as docs-site
  participant CLI as cli
  participant DESK as desktop-app
  participant CORE as update-core
  participant SRV as astra-server

  DEV->>DEV: sync-versions.mjs, CHANGELOG section
  DEV->>GH: push main (cli/package.json) / tag v*
  GH->>NPM: npm view @aidotnet/astra-gate@VER (already released?)
  GH->>GH: build matrix: AOT publish, gen-manifest partial, pack-platform, electron-builder
  GH->>DOCS: POST /api/client-releases/upload (installers, x-astra-channel)
  GH->>NPM: publish server-* → desktop-* → astragate → @aidotnet/astra-gate (last), --tag latest|preview
  GH->>DOCS: POST /api/client-releases/upload (latest.json, after npm)
  DOCS->>DOCS: saveUpload → DATA_DIR/client-releases/<channel>/<ver>/ + index.json
  GH->>GH: gh release create vVER; docker: smoke test + ghcr push

  SRV->>DOCS: 12 h poll GET /api/client-releases/<channel>/latest.json (UpdateCheckWorker)
  SRV->>SRV: UpdateCheckState under settings key "update.state"
  CLI->>SRV: GET /api/update/status (checkForServerUpdate)
  DESK->>SRV: POST /api/update/check (refresh), GET /api/version (probeVersion)
  CLI->>DOCS: fetchManifest (CLI/desktop direct, fallback path)
  DESK->>DESK: pendingManifest, gate apiVersion major, single-flight lock
  DESK->>CORE: applyServerUpdate(manifest, platformKey, control)
  CLI->>CORE: applyServerUpdate(manifest, control)
  CORE->>NPM: npm install --prefix staging @aidotnet/server-<p>-<a>@ver
  CORE->>CORE: sha256 verify → backup → installManagedBinary → install.json
  CORE->>SRV: stop → swap wwwroot → start → probeVersion
  SRV-->>CORE: version == manifest.version (else rolling-back)
```

## Errors and cancellation

- **CI gates, all before anything is published**: tag/version mismatch and a missing CHANGELOG section
  fail the `version` job (`release.yml:54-70`); `check-aot-warnings.mjs` and the version-drift check
  fail a build row; mac rows fail early on missing signing secrets. `concurrency: release-publish`
  (non-cancelling) serializes tag and branch publishes so only one of them can publish a version.
- **Late, silent failure**: the docs-site upload step warns and `exit 0` when
  `DOCS_RELEASE_URL`/`DOCS_RELEASE_TOKEN` (or `CLIENT_UPLOAD_TOKEN`) are unset — the run is green, npm
  and GitHub are updated, and the feed the CLI/desktop read is *not*. Read that step's log, not the run
  status. Likewise `FEED_UPLOAD_TARGET` is optional.
- **Idempotent re-runs**: the publish step skips ids already on npm, `gh release edit/upload --clobber`
  updates an existing release, and the docs upload overwrites a version directory by name. The one
  hard deadline is `wait_for` (90 min) before the CLI is published.
- **Upload route**: 503 when uploads are disabled, 401 on a bad bearer (constant-time compare), 400 on
  a non-semver version / missing platform / kind / empty body; `saveUpload` writes tmp + rename and only
  then merges the index, so a killed upload never leaves a partial file or a mutated `index.json`.
- **Runtime checks never propagate**: `UpdateCheckService.CheckAsync` catches everything except
  cancellation with the caller's token, keeps the previous `UpdateCheckState` and records the error;
  `UpdateCheckWorker` backs off (12 h → 48 h) and never touches request handling;
  `checkForServerUpdate` falls back to fetching the feed directly (3 s status timeout, 15 s manifest
  timeout) and `fetchManifest` throws `UpdateFeedError` for timeouts, non-2xx, invalid JSON or a
  missing `version` (`packages/update-core/src/manifest.ts:46-78`).
- **Apply failures**: any throw inside `applyServerUpdate` goes to the rollback leg (see step 35) and
  increments `failedCount`; after 2 consecutive failures further runs refuse until `--retry`
  (`force`). A failed probe after restart is the main real-world trigger. A rollback that itself throws
  is only logged — `astra-server restore-all` is the offline escape hatch. Cancellation: the state file
  is written per phase, so a killed process leaves a truthful `downloading`/`swapping` phase rather than
  a half-applied install.
- **Desktop**: `check()` / `runCheck()` never throw (failures become the dialog message);
  `applyFromUi()` throws and the tray/Settings surfaces show it; the reminder is recorded *before* being
  shown, so a crash mid-dialog still counts.

## Where to hook in

- **A new manifest field**: three readers must move together — `scripts/gen-manifest.mjs:merge` (writer),
  `packages/update-core/src/manifest.ts:UpdateManifest` (CLI + desktop) and
  `src/Astra.Server/Hosting/UpdateCheckService.cs:UpdateManifest`. `minUpdateable` exists in the .NET and
  TS types but nothing reads it, and the docs-side `index.json` entry (`docs/lib/releases.ts:60`) is a
  fourth copy of the same idea.
- **A new platform / RID**: `scripts/rid-map.mjs` + an `npm/server-*` and `npm/desktop-*` package +
  the `release.yml` matrix + the desktop's `serverPackage` string (`desktop/src/main.ts:113`) and
  `cli/src/lib/platform.ts:platformInfo`.
- **A new artifact kind on the download page**: `EXT_BY_KIND` (`docs/lib/releases.ts:72`) + the
  filename→`os/arch/kind` mapping in the upload step (`release.yml:404-419`) + the download panel. The
  Avalonia self-update reads the same `platform`/`kind` tags (`DesktopReleaseFeed.PlatformTag` /
  `KindForThisOs`), so renaming a tag or an installer kind changes what installed apps can find.
- **The install scripts** (`docs/public/install.sh` / `install.ps1`, served at `/install.sh` / `/install.ps1`):
  they read `latest.json` (version + `platforms[<rid>].server/serverSha256`) and `releases.json`
  (`versions[].files[].name/sha256`), download the server tarball straight from the npm registry and
  desktop installers from `/v/<version>/Astra-<version>-<os>-<arch>.<ext>` — so a change to those feed
  shapes, to `canonicalFileName` or to the managed layout (`~/.astra/server/astra-server-<version>`,
  `install.json`) must be mirrored there. They parse JSON with grep/sed (bash), so keep the feed flat.
- **A new update setting**: an `AppSettings` property (no migration — `src/Astra.Core/AppSettings.cs:29`)
  + `/api/settings`/UI wiring; the desktop reads `autoCheck` straight off `/api/update/status`
  (`desktop/src/api.ts:60`) and passes no `feedUrl`, so it always follows the built-in feed.
- **Tempting but wrong**: applying the update inside the server (the running binary and its `wwwroot`
  are what get replaced — applying is deliberately out of process); `-p:PublishSingleFile=true` in any
  publish command; publishing the CLI before its platform packages; renaming `cli/package.json` to
  `@aidotnet/astra-gate` without the tsup rebuild that bakes the name into the self-updater; adding
  `/api` to the `docs/proxy.ts` matcher (installers get truncated at 10 MB); editing an
  already-applied update by hand instead of going through `applyServerUpdate` (the `.prev`/`install.json`
  protocol is what makes rollback work). Also keep the duplicated `NATIVE_LIBRARY` regex in sync —
  `scripts/pack-platform.mjs:67` and `packages/update-core/src/staging.ts:45`.

## Related

- Modules: [packaging-release](../modules/packaging-release.md), [astra-server](../modules/astra-server.md),
  [cli](../modules/cli.md), [desktop-app](../modules/desktop-app.md),
  [update-core](../modules/update-core.md), [docs-site](../modules/docs-site.md),
  [web-ui](../modules/web-ui.md)
